// Ported from Lolly (github.com/lolly-tools/lolly, engine/src/xcf.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Xcf;

/// <summary>A GIMP file that cannot be read, with the reason in words a user can act on.</summary>
public sealed class XcfException(string message) : IOException(message)
{
    public static XcfException NotXcf() => new("这不是 GIMP 文件。");
    public static XcfException Truncated() => new("无法读取该 GIMP 文件。它可能已损坏或不完整。");
    public static XcfException TooLarge() => new($"该 GIMP 文件超出了 Composa 的容纳范围：每边 {DocumentLimits.MaxSide:N0} 像素、单个图层 {DocumentLimits.MaxSurfaceMegapixels} 百万像素、全部图层共 {DocumentLimits.DocumentBudgetMegapixels} 百万像素。");
}

/// <summary>
/// How a file stores each sample. GIMP 2.10 and later keep integers of 8, 16 or 32 bits and floats of 16, 32 or 64,
/// each either linear or gamma-encoded (the sRGB curve, the one Composa's pixels use). The numbers that name them
/// changed twice while 2.10 was in development: versions 5 and 6 used the same numbers as 7 and later except that
/// 400 and 450 were the half floats, and version 4 counted from 0.
/// </summary>
internal readonly record struct XcfPrecision(int BytesPerSample, bool IsFloat, bool Linear, string Name)
{
    public static readonly XcfPrecision Gamma8 = new(1, false, false, "8 位");

    /// <summary>The precision a header code means for a file of <paramref name="version"/>, or null when the code is unknown.</summary>
    public static XcfPrecision? FromCode(int version, uint code)
    {
        if (version == 4)
            return code switch
            {
                0 => Gamma8, 1 => new(2, false, false, "16 位"), 2 => new(4, false, true, "32 位线性"),
                3 => new(2, true, true, "16 位浮点线性"), 4 => new(4, true, true, "32 位浮点线性"), _ => null
            };
        if (version is 5 or 6 && code is 400 or 450) return new(2, true, code == 400, code == 400 ? "16 位浮点线性" : "16 位浮点");
        if (version is 5 or 6 && code is 500 or 550) return new(4, true, code == 500, code == 500 ? "32 位浮点线性" : "32 位浮点");
        return code switch
        {
            100 => new(1, false, true, "8 位线性"), 150 => Gamma8,
            200 => new(2, false, true, "16 位线性"), 250 => new(2, false, false, "16 位"),
            300 => new(4, false, true, "32 位线性"), 350 => new(4, false, false, "32 位"),
            500 => new(2, true, true, "16 位浮点线性"), 550 => new(2, true, false, "16 位浮点"),
            600 => new(4, true, true, "32 位浮点线性"), 650 => new(4, true, false, "32 位浮点"),
            700 => new(8, true, true, "64 位浮点线性"), 750 => new(8, true, false, "64 位浮点"),
            _ => null
        };
    }

    /// <summary>Whether converting to Composa's 8-bit gamma pixels loses anything the file had.</summary>
    public bool Deep => BytesPerSample > 1 || IsFloat;
}

/// <summary>The whole file as read: the canvas, its properties and its layers bottom to top.</summary>
internal sealed class XcfFile
{
    public int Version;
    public int Width;
    public int Height;
    /// <summary>0 RGB, 1 grayscale, 2 indexed.</summary>
    public int BaseType;
    public XcfPrecision Precision = XcfPrecision.Gamma8;
    /// <summary>0 none, 1 GIMP's run-length scheme, 2 zlib.</summary>
    public int Compression = 1;
    /// <summary>RGB triples for an indexed file; null otherwise.</summary>
    public byte[]? Colormap;
    public double Resolution = 72;
    public List<(int Position, bool Vertical)> Guides = [];
    /// <summary>The file carries an ICC profile parasite, which Composa cannot yet apply.</summary>
    public bool HasIccProfile;
    /// <summary>Channels of the image itself (saved selections and alpha channels), which have no place here.</summary>
    public int ChannelCount;
    /// <summary>Paths (<c>PROP_VECTORS</c>, <c>PROP_PATHS</c>, or the vectors list of XCF 18 and later), which have no place here.</summary>
    public int PathCount;
    public List<XcfLayer> Layers = [];
}

/// <summary>A layer as the file describes it, with its pixels decoded.</summary>
internal sealed class XcfLayer
{
    public string Name = "";
    /// <summary>The pixel rectangle as it is imported: offsets and size, after any cropping to the canvas.</summary>
    public int Left, Top, Width, Height;
    /// <summary>What the file says, before cropping.</summary>
    public int SourceLeft, SourceTop, SourceWidth, SourceHeight;
    /// <summary>0 RGB, 1 RGBA, 2 gray, 3 gray with alpha, 4 indexed, 5 indexed with alpha.</summary>
    public int Type;
    public double Opacity = 1;
    public bool Visible = true;
    public int Mode;
    public bool IsGroup;
    /// <summary>The group is shown folded in GIMP's layer list.</summary>
    public bool Collapsed;
    /// <summary>The layer's address in the tree: an index at every depth, root first.</summary>
    public int[]? ItemPath;
    public bool HasMask;
    public bool ApplyMask = true;
    /// <summary>The spaces and compositing the layer asks for; 0 when the file says nothing (auto).</summary>
    public int BlendSpace, CompositeSpace, CompositeMode;
    /// <summary>The <c>gimp-text-layer</c> parasite, when the layer is text GIMP can still edit.</summary>
    public byte[]? TextParasite;
    public int EffectCount;
    public bool IsVectorLayer, IsLinkLayer;
    public bool Cropped;
    /// <summary>Tiles that could not be decoded and were left transparent.</summary>
    public int DamagedTiles;
    /// <summary>Premultiplied RGBA over <see cref="Left"/>, <see cref="Top"/>; null for a group or an empty layer.</summary>
    public SKBitmap? Image;
    /// <summary>Alpha8 over the same rectangle; null without a mask.</summary>
    public SKBitmap? MaskImage;

    internal long HierarchyPointer, MaskPointer;
    /// <summary>Which part of the layer's own pixel grid is read, once cropped; the whole of it when null.</summary>
    internal SKRectI? Crop;

    public bool HasAlpha => Type is 1 or 3 or 5;
    public int Channels => Type switch { 0 => 3, 1 => 4, 2 => 1, 3 => 2, 4 => 1, _ => 2 };
    public bool IsIndexed => Type >= 4;
}

/// <summary>Big-endian reads over the file's bytes, refusing to run past the end; pointers are 4 bytes until XCF 11 and 8 from then on.</summary>
internal ref struct XcfCursor(ReadOnlySpan<byte> data, bool widePointers)
{
    private readonly ReadOnlySpan<byte> data = data;
    public readonly bool WidePointers = widePointers;
    public int Offset;

    public int Length => data.Length;
    public int Remaining => data.Length - Offset;
    public int PointerSize => WidePointers ? 8 : 4;

    private void Need(long count)
    {
        if (count < 0 || Offset < 0 || Offset + count > data.Length) throw XcfException.Truncated();
    }

    public void Skip(long count) { Need(count); Offset += (int)count; }
    public void Seek(long offset) { if (offset < 0 || offset > data.Length) throw XcfException.Truncated(); Offset = (int)offset; }

    public byte U8() { Need(1); return data[Offset++]; }
    public uint U32() { Need(4); var v = (uint)data[Offset] << 24 | (uint)data[Offset + 1] << 16 | (uint)data[Offset + 2] << 8 | data[Offset + 3]; Offset += 4; return v; }
    public int I32() => (int)U32();
    public float F32() => BitConverter.Int32BitsToSingle(I32());
    public ulong U64() { var hi = U32(); var lo = U32(); return (ulong)hi << 32 | lo; }

    /// <summary>A file offset; refused when it does not fit the file (a pointer past the end can never be followed).</summary>
    public long Pointer()
    {
        var value = WidePointers ? U64() : U32();
        if (value > (ulong)data.Length) throw XcfException.Truncated();
        return (long)value;
    }

    /// <summary>A pointer as written, however large; for the tile table, where a bad one costs a tile rather than the file.</summary>
    public ulong RawPointer() => WidePointers ? U64() : U32();

    public ReadOnlySpan<byte> Bytes(long count) { Need(count); var slice = data.Slice(Offset, (int)count); Offset += (int)count; return slice; }

    /// <summary>A string as the file stores it: a 4-byte length that counts the terminating NUL, then UTF-8; a zero length is the empty string.</summary>
    public string String()
    {
        var length = U32();
        if (length == 0) return "";
        if (length > 1_000_000) throw XcfException.Truncated();
        var bytes = Bytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes[..^1]).TrimEnd('\0');
    }
}
