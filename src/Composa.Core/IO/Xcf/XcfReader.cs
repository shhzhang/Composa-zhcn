// Ported from Lolly (github.com/lolly-tools/lolly, engine/src/xcf.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using System.IO.Compression;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Xcf;

/// <summary>
/// Reads GIMP's <c>.xcf</c> files following GIMP's published description of the format (developer.gimp.org, "XCF
/// file format"): the header, the property lists, the layer and channel structures, the hierarchy and level
/// structures, and the tiles in all three of their encodings. This is an original implementation written from that
/// document and from Lolly's reader; nothing here is taken from GIMP's own code. XCF 0 to 10 carry 4-byte pointers
/// and 11 onwards 8-byte ones; everything else newer versions added is a length-prefixed property, which a reader
/// that does not know it skips, so a file from a GIMP newer than this reader still opens with what it understands.
/// </summary>
internal static class XcfReader
{
    public const int MaxLayers = 10_000;
    /// <summary>The newest version this reader was written against (GIMP 3.2.6); a newer file is read as this one.</summary>
    public const int KnownVersion = 26;
    private const int MaxProperties = 4096;
    private const int Tile = 64;

    private static ReadOnlySpan<byte> Magic => "gimp xcf "u8;

    public static bool Matches(ReadOnlySpan<byte> data) => Version(data) != null;

    /// <summary>The compressed forms GIMP can save that the base library cannot unpack, with the name the message uses.</summary>
    public static readonly (string Suffix, string Name)[] UnreadableCompression = [(".xcf.bz2", "bzip2"), (".xcf.xz", "xz")];

    public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    /// <summary>A <c>.xcf.gz</c> that unpacks to an XCF, or a name in a compression that is refused later with its reason.</summary>
    public static bool MatchesCompressed(string path)
    {
        if (UnreadableCompression.Any(c => path.EndsWith(c.Suffix, StringComparison.OrdinalIgnoreCase))) return true;
        if (!path.EndsWith(".xcf.gz", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var unpacked = new GZipStream(stream, CompressionMode.Decompress);
            Span<byte> head = stackalloc byte[14];
            return unpacked.ReadAtLeast(head, 14, throwOnEndOfStream: false) == 14 && Matches(head);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>Unpacks a gzipped file into memory, refusing one that unpacks past what a file may be.</summary>
    public static byte[] Unpack(byte[] data)
    {
        try
        {
            using var unpacked = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[1 << 16];
            int read;
            while ((read = unpacked.Read(buffer)) > 0)
            {
                if (output.Length + read > int.MaxValue - 1024) throw XcfException.TooLarge();
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { throw XcfException.Truncated(); }
    }

    public static bool Matches(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[14];
            return stream.Read(head) == 14 && Matches(head);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The file's version: 0 for the first format ("file"), the number of a "vNNN" token otherwise; null when this is no XCF.</summary>
    public static int? Version(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14 || !data[..9].SequenceEqual(Magic) || data[13] != 0) return null;
        var token = data.Slice(9, 4);
        if (token.SequenceEqual("file"u8)) return 0;
        if (token[0] != 'v' || !char.IsAsciiDigit((char)token[1]) || !char.IsAsciiDigit((char)token[2]) || !char.IsAsciiDigit((char)token[3])) return null;
        return (token[1] - '0') * 100 + (token[2] - '0') * 10 + (token[3] - '0');
    }

    // Property ids, from GIMP's document.
    private const uint PropEnd = 0, PropColormap = 1, PropOpacity = 6, PropMode = 7, PropVisible = 8, PropApplyMask = 11, PropOffsets = 15,
        PropCompression = 17, PropGuides = 18, PropResolution = 19, PropParasites = 21, PropPaths = 23, PropVectors = 25, PropGroupItem = 29,
        PropItemPath = 30, PropGroupItemFlags = 31, PropFloatOpacity = 33, PropCompositeMode = 35, PropCompositeSpace = 36, PropBlendSpace = 37,
        PropVectorLayer = 47, PropLinkLayer = 48;

    private delegate void PropertyVisitor(uint id, ReadOnlySpan<byte> payload);

    public static XcfFile Read(ReadOnlySpan<byte> data, long pixelBudget)
    {
        var version = Version(data) ?? throw XcfException.NotXcf();
        var file = new XcfFile { Version = version };
        var cursor = new XcfCursor(data, version >= 11) { Offset = 14 };
        var width = cursor.I32();
        var height = cursor.I32();
        file.BaseType = cursor.I32();
        if (file.BaseType is < 0 or > 2) throw XcfException.NotXcf();
        // The canvas is a size, not an allocation: only the layers count against the budget.
        if (!DocumentLimits.FitsSurface(width, height)) throw XcfException.TooLarge();
        file.Width = width;
        file.Height = height;
        if (version >= 4)
            file.Precision = XcfPrecision.FromCode(version, cursor.U32()) ?? throw new XcfException("该 GIMP 文件存储像素的精度 Composa 无法读取。");

        ReadProperties(ref cursor, (id, payload) => ImageProperty(file, id, payload));
        if (file.Compression is < 0 or > 2) throw new XcfException("该 GIMP 文件使用的分块压缩方式 Composa 无法读取。");

        var pointers = new List<long>();
        while (true)
        {
            var pointer = cursor.Pointer();
            if (pointer == 0) break;
            if (pointers.Count >= MaxLayers) throw XcfException.TooLarge();
            pointers.Add(pointer);
        }
        // Image channels (saved selections) and, from XCF 18, paths: counted, never read. Older files keep their paths in
        // PROP_PATHS or PROP_VECTORS; XCF 18 still writes PROP_VECTORS beside the list, so only the list is counted there.
        file.ChannelCount = CountPointers(ref cursor);
        if (version >= 18) file.PathCount = CountPointers(ref cursor);

        // Records first, top to bottom as the file lists them; the pixels wait until the budget is settled.
        var records = new List<XcfLayer>();
        foreach (var pointer in pointers)
        {
            cursor.Seek(pointer);
            records.Add(ReadRecord(ref cursor, file));
        }
        records.Reverse();
        file.Layers = records;

        // A file whose layers fit imports whole. Only one that would be refused falls back: each layer reaching past
        // the canvas is cut to it and the budget checked again, decided from the records before a pixel is read.
        if (!FitsBudget(records, pixelBudget))
        {
            foreach (var layer in records) CropToCanvas(layer, width, height);
            if (!FitsBudget(records, pixelBudget)) throw XcfException.TooLarge();
        }
        var used = 0L;
        var samples = new Samples(file.Precision);
        foreach (var layer in records)
        {
            Decode(data, file, layer, samples, pixelBudget - used);
            if (layer.Image != null) used += (long)layer.Image.Width * layer.Image.Height;
            if (layer.MaskImage != null) used += (long)layer.MaskImage.Width * layer.MaskImage.Height;
        }
        return file;
    }

    private static int CountPointers(ref XcfCursor cursor)
    {
        var count = 0;
        while (cursor.Pointer() != 0)
            if (++count > MaxLayers) throw XcfException.TooLarge();
        return count;
    }

    /// <summary>Walks one property list to its <c>PROP_END</c>, handing each payload to <paramref name="visit"/>.</summary>
    private static void ReadProperties(ref XcfCursor cursor, PropertyVisitor visit)
    {
        for (var i = 0; i < MaxProperties; i++)
        {
            var id = cursor.U32();
            var length = cursor.U32();
            if (id == PropEnd) return;
            var payload = cursor.Bytes(length);
            visit(id, payload);
            // GIMP 3.2.6 declares PROP_LINK_LAYER four bytes short: flags, the path and a width are counted, the height
            // that follows them is not. Stepping over it keeps the list, and the pointers after it, in place.
            if (id == PropLinkLayer && LinkLayerUnderdeclared(payload)) cursor.Skip(4);
        }
        throw XcfException.Truncated();
    }

    private static bool LinkLayerUnderdeclared(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return false;
        var p = new XcfCursor(payload, false);
        p.Skip(4);
        var pathLength = p.U32();
        return payload.Length == 4 + 4 + pathLength + 4;
    }

    private static void ImageProperty(XcfFile file, uint id, ReadOnlySpan<byte> payload)
    {
        var p = new XcfCursor(payload, false);
        switch (id)
        {
            case PropColormap:
                if (payload.Length < 4) break;
                var count = p.U32();
                if (count is > 0 and <= 256 && payload.Length >= 4 + count * 3) file.Colormap = p.Bytes(count * 3).ToArray();
                break;
            case PropCompression:
                if (payload.Length >= 1) file.Compression = payload[0];
                break;
            case PropGuides:
                // An int32 position and a byte: 1 for a horizontal guide, 2 for a vertical one.
                for (var i = 0; i + 5 <= payload.Length && file.Guides.Count < 1000; i += 5)
                {
                    var position = p.I32();
                    var orientation = p.U8();
                    if (orientation is 1 or 2) file.Guides.Add((position, orientation == 2));
                }
                break;
            case PropResolution:
                if (payload.Length >= 8)
                {
                    var horizontal = p.F32();
                    file.Resolution = float.IsFinite(horizontal) && horizontal >= 1 ? Math.Min(9600, horizontal) : 72;
                }
                break;
            case PropParasites:
                foreach (var (name, _) in Parasites(payload))
                    if (name == "icc-profile") file.HasIccProfile = true;
                break;
            case PropPaths:
            case PropVectors:
                if (payload.Length >= 12) { p.Skip(4); if (id == PropVectors) p.Skip(4); file.PathCount += (int)Math.Min(p.U32(), 100_000); }
                else if (id == PropPaths && payload.Length >= 8) { p.Skip(4); file.PathCount += (int)Math.Min(p.U32(), 100_000); }
                break;
        }
    }

    /// <summary>The parasites in a <c>PROP_PARASITES</c> payload: a string name, flags, a length and that many bytes, repeated.</summary>
    private static List<(string Name, byte[] Data)> Parasites(ReadOnlySpan<byte> payload)
    {
        var result = new List<(string, byte[])>();
        try
        {
            var p = new XcfCursor(payload, false);
            while (p.Remaining >= 12 && result.Count < 1000)
            {
                var name = p.String();
                p.Skip(4);
                var length = p.U32();
                result.Add((name, p.Bytes(length).ToArray()));
            }
        }
        catch (XcfException) { /* A damaged parasite list loses the rest of its parasites, nothing more. */ }
        return result;
    }

    private static XcfLayer ReadRecord(ref XcfCursor cursor, XcfFile file)
    {
        var layer = new XcfLayer { Width = cursor.I32(), Height = cursor.I32(), Type = cursor.I32() };
        if (layer.Type is < 0 or > 5 || layer.Width < 0 || layer.Height < 0 || layer.Width > DocumentLimits.MaxSide || layer.Height > DocumentLimits.MaxSide) throw XcfException.Truncated();
        layer.Name = cursor.String();
        ReadProperties(ref cursor, (id, payload) => LayerProperty(layer, id, payload));
        (layer.SourceLeft, layer.SourceTop, layer.SourceWidth, layer.SourceHeight) = (layer.Left, layer.Top, layer.Width, layer.Height);
        layer.HierarchyPointer = cursor.Pointer();
        layer.MaskPointer = cursor.Pointer();
        layer.HasMask = layer.MaskPointer != 0;
        // From XCF 20 a layer lists its effects after its mask; a reader that cannot run them still has to step over them.
        if (file.Version >= 20)
            while (cursor.Pointer() != 0)
                if (++layer.EffectCount > 1000) throw XcfException.Truncated();
        return layer;
    }

    private static void LayerProperty(XcfLayer layer, uint id, ReadOnlySpan<byte> payload)
    {
        var p = new XcfCursor(payload, false);
        switch (id)
        {
            case PropOpacity when payload.Length >= 4: layer.Opacity = Math.Min(255, p.U32()) / 255.0; break;
            case PropFloatOpacity when payload.Length >= 4:
                var opacity = p.F32();
                if (float.IsFinite(opacity)) layer.Opacity = Math.Clamp(opacity, 0, 1);
                break;
            case PropVisible when payload.Length >= 4: layer.Visible = p.U32() != 0; break;
            case PropMode when payload.Length >= 4: layer.Mode = (int)p.U32(); break;
            case PropOffsets when payload.Length >= 8: layer.Left = p.I32(); layer.Top = p.I32(); break;
            case PropGroupItem: layer.IsGroup = true; break;
            case PropGroupItemFlags when payload.Length >= 4: layer.Collapsed = (p.U32() & 1) == 0; break;
            case PropItemPath:
                var depth = Math.Min(payload.Length / 4, 64);
                layer.ItemPath = new int[depth];
                for (var i = 0; i < depth; i++) layer.ItemPath[i] = (int)Math.Min(p.U32(), int.MaxValue);
                break;
            case PropApplyMask when payload.Length >= 4: layer.ApplyMask = p.U32() != 0; break;
            case PropBlendSpace when payload.Length >= 4: layer.BlendSpace = p.I32(); break;
            case PropCompositeSpace when payload.Length >= 4: layer.CompositeSpace = p.I32(); break;
            case PropCompositeMode when payload.Length >= 4: layer.CompositeMode = p.I32(); break;
            case PropParasites:
                foreach (var (name, data) in Parasites(payload))
                    if (name == "gimp-text-layer") layer.TextParasite = data;
                break;
            case PropVectorLayer: layer.IsVectorLayer = true; break;
            case PropLinkLayer: layer.IsLinkLayer = true; break;
        }
    }

    // ── budget and cropping ───────────────────────────────────────────────────

    private static bool FitsBudget(List<XcfLayer> layers, long pixelBudget)
    {
        var used = 0L;
        foreach (var layer in layers)
        {
            if (!FitsBudget(layer, pixelBudget - used)) return false;
            used += Needed(layer);
        }
        return true;
    }

    private static long Needed(XcfLayer layer)
    {
        var pixels = (long)layer.Width * layer.Height;
        return (layer.IsGroup ? 0 : pixels) + (layer.HasMask ? pixels : 0);
    }

    private static bool FitsBudget(XcfLayer layer, long remainingPixels)
    {
        if (layer.Width > 0 && layer.Height > 0 && (!layer.IsGroup || layer.HasMask) && !DocumentLimits.FitsSurface(layer.Width, layer.Height)) return false;
        return Needed(layer) <= Math.Max(0, remainingPixels);
    }

    /// <summary>Cuts the layer's rectangle to the canvas, remembering which part of its own grid that leaves.</summary>
    private static void CropToCanvas(XcfLayer layer, int canvasWidth, int canvasHeight)
    {
        var left = Math.Clamp(layer.Left, 0, canvasWidth);
        var top = Math.Clamp(layer.Top, 0, canvasHeight);
        var right = Math.Clamp(layer.Left + layer.Width, left, canvasWidth);
        var bottom = Math.Clamp(layer.Top + layer.Height, top, canvasHeight);
        if (left == layer.Left && top == layer.Top && right - left == layer.Width && bottom - top == layer.Height) return;
        layer.Crop = new SKRectI(left - layer.Left, top - layer.Top, right - layer.Left, bottom - layer.Top);
        layer.Left = left;
        layer.Top = top;
        layer.Width = right - left;
        layer.Height = bottom - top;
        layer.Cropped = true;
    }

    // ── pixels ────────────────────────────────────────────────────────────────

    private static void Decode(ReadOnlySpan<byte> data, XcfFile file, XcfLayer layer, Samples samples, long remainingPixels)
    {
        if (!FitsBudget(layer, remainingPixels)) throw XcfException.TooLarge();
        if (layer.Width <= 0 || layer.Height <= 0) return;
        if (!layer.IsGroup && layer.HierarchyPointer != 0)
        {
            var image = Pixels.NewColor(layer.Width, layer.Height);
            try
            {
                ReadHierarchy(data, file, layer, layer.HierarchyPointer, layer.Channels, samples, image, null);
                Pixels.Invalidate(image);
                layer.Image = image;
            }
            catch { image.Dispose(); throw; }
        }
        if (layer.HasMask)
        {
            var mask = Pixels.NewMask(layer.Width, layer.Height);
            try
            {
                ReadMask(data, file, layer, samples, mask);
                Pixels.Invalidate(mask);
                layer.MaskImage = mask;
            }
            catch { mask.Dispose(); throw; }
        }
    }

    /// <summary>A layer mask is a channel: the same size as its layer, a name, properties, and one gray hierarchy.</summary>
    private static void ReadMask(ReadOnlySpan<byte> data, XcfFile file, XcfLayer layer, Samples samples, SKBitmap mask)
    {
        var cursor = new XcfCursor(data, file.Version >= 11);
        cursor.Seek(layer.MaskPointer);
        var width = cursor.I32();
        var height = cursor.I32();
        if (width != layer.SourceWidth || height != layer.SourceHeight) throw XcfException.Truncated();
        _ = cursor.String();
        ReadProperties(ref cursor, (_, _) => { });
        var hierarchy = cursor.Pointer();
        if (hierarchy == 0) return;
        ReadHierarchy(data, file, layer, hierarchy, 1, samples, null, mask);
    }

    /// <summary>
    /// A hierarchy names its size and bytes per pixel and points at its first level, whose tiles hold the pixels; the
    /// levels after the first are unused reductions. Every tile is read into <paramref name="image"/> or
    /// <paramref name="mask"/>, converted to Composa's 8-bit samples on the way; a tile that cannot be read is left
    /// transparent and counted.
    /// </summary>
    private static void ReadHierarchy(ReadOnlySpan<byte> data, XcfFile file, XcfLayer layer, long pointer, int channels, Samples samples, SKBitmap? image, SKBitmap? mask)
    {
        var cursor = new XcfCursor(data, file.Version >= 11);
        cursor.Seek(pointer);
        var width = cursor.I32();
        var height = cursor.I32();
        var bytesPerPixel = cursor.I32();
        var bytesPerSample = layer.IsIndexed && image != null ? 1 : file.Precision.BytesPerSample;
        if (width != layer.SourceWidth || height != layer.SourceHeight || bytesPerPixel != channels * bytesPerSample) throw XcfException.Truncated();
        cursor.Seek(cursor.Pointer());
        if (cursor.I32() != width || cursor.I32() != height) throw XcfException.Truncated();

        int tilesAcross = (width + Tile - 1) / Tile, tilesDown = (height + Tile - 1) / Tile;
        // A tile pointer outside the file marks a damaged tile, not a damaged file: the tile is left transparent.
        var tiles = new long[tilesAcross * tilesDown];
        for (var i = 0; i < tiles.Length; i++)
        {
            var raw = cursor.RawPointer();
            tiles[i] = raw < (ulong)data.Length ? (long)raw : 0;
        }
        // A tile's bytes run to the next tile in the file, whichever that is; the last runs to the end of the file.
        var sorted = tiles.Where(t => t > 0).Distinct().Order().ToArray();
        var crop = layer.Crop ?? new SKRectI(0, 0, width, height);
        var tile = new byte[Tile * Tile * bytesPerPixel];
        for (var ty = 0; ty < tilesDown; ty++)
            for (var tx = 0; tx < tilesAcross; tx++)
            {
                var area = new SKRectI(tx * Tile, ty * Tile, Math.Min(width, (tx + 1) * Tile), Math.Min(height, (ty + 1) * Tile));
                var shown = SKRectI.Intersect(area, crop);
                if (shown.IsEmpty) continue;
                var start = tiles[ty * tilesAcross + tx];
                var next = Array.BinarySearch(sorted, start);
                var end = next >= 0 && next + 1 < sorted.Length ? sorted[next + 1] : data.Length;
                if (start == 0 || !DecodeTile(data, start, end, area.Width * area.Height, bytesPerPixel, file.Compression, tile))
                {
                    layer.DamagedTiles++;
                    continue;
                }
                if (image != null) samples.ToImage(tile, area, shown, crop, layer, file.Colormap, image);
                else samples.ToMask(tile, area, shown, crop, mask!);
            }
    }

    /// <summary>
    /// One tile's samples into <paramref name="output"/>, interleaved: raw bytes, zlib, or GIMP's run-length scheme,
    /// which encodes each byte of the pixel as its own stream (byte 0 of every pixel, then byte 1, and so on) with
    /// short runs (0 to 126: that many plus one copies of the next byte), long runs (127: a two-byte count and the
    /// byte), long literals (128: a two-byte count of bytes that follow) and short literals (129 to 255: 256 minus the
    /// opcode bytes follow). False when the bytes do not add up to a tile.
    /// </summary>
    private static bool DecodeTile(ReadOnlySpan<byte> data, long start, long end, int pixels, int bytesPerPixel, int compression, byte[] output)
    {
        var needed = pixels * bytesPerPixel;
        if (start < 0 || start >= data.Length || end > data.Length || end <= start) return false;
        var bytes = data[(int)start..(int)end];
        switch (compression)
        {
            case 0:
                if (bytes.Length < needed) return false;
                bytes[..needed].CopyTo(output);
                return true;
            case 2:
                try
                {
                    using var stream = new ZLibStream(new MemoryStream(bytes.ToArray()), CompressionMode.Decompress);
                    return stream.ReadAtLeast(output.AsSpan(0, needed), needed, throwOnEndOfStream: false) == needed;
                }
                catch (Exception error) when (error is InvalidDataException or IOException) { return false; }
            default:
            {
                var p = 0;
                for (var plane = 0; plane < bytesPerPixel; plane++)
                {
                    var written = 0;
                    while (written < pixels)
                    {
                        if (p >= bytes.Length) return false;
                        int op = bytes[p++], length;
                        if (op < 127)
                        {
                            length = op + 1;
                            if (p >= bytes.Length || written + length > pixels) return false;
                            var value = bytes[p++];
                            for (var i = 0; i < length; i++) output[(written + i) * bytesPerPixel + plane] = value;
                        }
                        else if (op == 127)
                        {
                            if (p + 3 > bytes.Length) return false;
                            length = bytes[p] << 8 | bytes[p + 1];
                            var value = bytes[p + 2];
                            p += 3;
                            if (length == 0 || written + length > pixels) return false;
                            for (var i = 0; i < length; i++) output[(written + i) * bytesPerPixel + plane] = value;
                        }
                        else
                        {
                            if (op == 128)
                            {
                                if (p + 2 > bytes.Length) return false;
                                length = bytes[p] << 8 | bytes[p + 1];
                                p += 2;
                            }
                            else length = 256 - op;
                            if (length == 0 || p + length > bytes.Length || written + length > pixels) return false;
                            for (var i = 0; i < length; i++) output[(written + i) * bytesPerPixel + plane] = bytes[p + i];
                            p += length;
                        }
                        written += length;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>
    /// Converts the file's samples to Composa's 8-bit gamma-encoded ones. Integer and float samples of every width
    /// are scaled to 0..1; linear ones then go through the sRGB curve, which gamma-encoded ones already carry. Alpha
    /// and masks are coverage, never gamma-encoded, so they are only scaled. The 8- and 16-bit conversions are tables.
    /// </summary>
    private sealed class Samples
    {
        private readonly XcfPrecision precision;
        private readonly byte[]? color8, alpha8;
        private readonly byte[]? color16, alpha16;

        public Samples(XcfPrecision precision)
        {
            this.precision = precision;
            if (precision.IsFloat) return;
            if (precision.BytesPerSample == 1)
            {
                color8 = new byte[256];
                alpha8 = new byte[256];
                for (var i = 0; i < 256; i++) { color8[i] = Encode(i / 255.0, precision.Linear); alpha8[i] = (byte)i; }
            }
            else if (precision.BytesPerSample == 2)
            {
                color16 = new byte[65536];
                alpha16 = new byte[65536];
                for (var i = 0; i < 65536; i++) { color16[i] = Encode(i / 65535.0, precision.Linear); alpha16[i] = (byte)Math.Round(i * 255.0 / 65535); }
            }
        }

        private static byte Encode(double unit, bool linear)
        {
            unit = Math.Clamp(double.IsFinite(unit) ? unit : 0, 0, 1);
            if (linear) unit = Filters.CameraRawPixels.LinearToSrgb(unit);
            return (byte)Math.Round(unit * 255);
        }

        /// <summary>The sample at <paramref name="at"/> as a byte: a color (gamma applied to linear files) or a coverage.</summary>
        private byte Sample(ReadOnlySpan<byte> raw, int at, int bytesPerSample, bool coverage)
        {
            switch (bytesPerSample)
            {
                case 1 when !precision.IsFloat: return coverage ? raw[at] : color8 != null ? color8[raw[at]] : raw[at];
                case 2 when !precision.IsFloat:
                {
                    var value = raw[at] << 8 | raw[at + 1];
                    return coverage ? alpha16![value] : color16![value];
                }
                case 4 when !precision.IsFloat:
                {
                    var value = (uint)raw[at] << 24 | (uint)raw[at + 1] << 16 | (uint)raw[at + 2] << 8 | raw[at + 3];
                    return Encode(value / 4294967295.0, !coverage && precision.Linear);
                }
                case 2:
                    return Encode((double)BitConverter.UInt16BitsToHalf((ushort)(raw[at] << 8 | raw[at + 1])), !coverage && precision.Linear);
                case 4:
                    return Encode(BitConverter.Int32BitsToSingle(raw[at] << 24 | raw[at + 1] << 16 | raw[at + 2] << 8 | raw[at + 3]), !coverage && precision.Linear);
                default:
                {
                    var bits = (long)raw[at] << 56 | (long)raw[at + 1] << 48 | (long)raw[at + 2] << 40 | (long)raw[at + 3] << 32 | (long)raw[at + 4] << 24 | (long)raw[at + 5] << 16 | (long)raw[at + 6] << 8 | raw[at + 7];
                    return Encode(BitConverter.Int64BitsToDouble(bits), !coverage && precision.Linear);
                }
            }
        }

        /// <summary>Writes the part of a decoded tile that lies inside the crop into the layer's premultiplied image.</summary>
        public unsafe void ToImage(byte[] tile, SKRectI area, SKRectI shown, SKRectI crop, XcfLayer layer, byte[]? colormap, SKBitmap image)
        {
            var channels = layer.Channels;
            var bytesPerSample = layer.IsIndexed ? 1 : precision.BytesPerSample;
            var bytesPerPixel = channels * bytesPerSample;
            var pixels = (byte*)image.GetPixels();
            for (var y = shown.Top; y < shown.Bottom; y++)
            {
                var row = pixels + (long)(y - crop.Top) * image.RowBytes;
                for (var x = shown.Left; x < shown.Right; x++)
                {
                    var at = ((y - area.Top) * area.Width + (x - area.Left)) * bytesPerPixel;
                    byte r, g, b, a = 255;
                    if (layer.IsIndexed)
                    {
                        var index = tile[at];
                        if (colormap != null && index * 3 + 2 < colormap.Length) { r = colormap[index * 3]; g = colormap[index * 3 + 1]; b = colormap[index * 3 + 2]; }
                        else r = g = b = index;
                        if (layer.HasAlpha) a = tile[at + 1];
                    }
                    else if (channels >= 3)
                    {
                        r = Sample(tile, at, bytesPerSample, false);
                        g = Sample(tile, at + bytesPerSample, bytesPerSample, false);
                        b = Sample(tile, at + 2 * bytesPerSample, bytesPerSample, false);
                        if (layer.HasAlpha) a = Sample(tile, at + 3 * bytesPerSample, bytesPerSample, true);
                    }
                    else
                    {
                        r = g = b = Sample(tile, at, bytesPerSample, false);
                        if (layer.HasAlpha) a = Sample(tile, at + bytesPerSample, bytesPerSample, true);
                    }
                    var p = row + (long)(x - crop.Left) * 4;
                    p[0] = Premultiply(r, a); p[1] = Premultiply(g, a); p[2] = Premultiply(b, a); p[3] = a;
                }
            }
        }

        public unsafe void ToMask(byte[] tile, SKRectI area, SKRectI shown, SKRectI crop, SKBitmap mask)
        {
            var bytesPerSample = precision.BytesPerSample;
            var pixels = (byte*)mask.GetPixels();
            for (var y = shown.Top; y < shown.Bottom; y++)
            {
                var row = pixels + (long)(y - crop.Top) * mask.RowBytes;
                for (var x = shown.Left; x < shown.Right; x++)
                    row[x - crop.Left] = Sample(tile, ((y - area.Top) * area.Width + (x - area.Left)) * bytesPerSample, bytesPerSample, true);
            }
        }

        private static byte Premultiply(byte value, byte alpha) => alpha == 255 ? value : (byte)((value * alpha + 127) / 255);
    }
}
