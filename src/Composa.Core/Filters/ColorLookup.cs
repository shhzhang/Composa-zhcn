// Ported from Lolly (github.com/lolly-tools/lolly, engine/src/grade.ts and community/darkroom/hooks.js at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Composa.Model;

namespace Composa.Filters;

/// <summary>
/// A color lookup table as a <c>.cube</c> or <c>.3dl</c> file holds it: a cube of <see cref="Size"/>³ RGB points
/// that maps every input color to an output color by tetrahedral interpolation, optionally preceded by per-channel
/// curves of <see cref="CurveSize"/> points (a <c>.cube</c> with <c>LUT_1D_SIZE</c>, alone or as the shaper before its
/// <c>LUT_3D_SIZE</c> table). Inputs are read over the domain the file declares, which is 0 to 1 unless it says
/// otherwise. The lattice is immutable and travels with the document: an adjustment holds it, undo snapshots share
/// it, and the project file writes it once by its <see cref="Id"/>. The cube is red-fastest, the order the
/// <c>.cube</c> format uses, so a <c>.3dl</c> (blue-fastest) is reordered as it is read.
///
/// Where Lolly reads any three numbers as a color and skips what it cannot read, this reader refuses a line it
/// cannot make sense of and names it, and it routes a file by its extension instead of trying both formats in turn,
/// so a broken <c>.cube</c> reports its own line rather than "not a .3dl".
/// </summary>
public sealed class ColorLattice
{
    private readonly float[]? curves;   // CurveSize points, three floats each: red, green, blue
    private readonly float[]? cube;     // Size³ points, three floats each, red-fastest

    /// <summary>Points per axis of the 3D cube; 0 when the table is curves only.</summary>
    public int Size { get; }
    /// <summary>Points of the per-channel curves; 0 when there are none.</summary>
    public int CurveSize { get; }
    public (float R, float G, float B) DomainMin { get; }
    public (float R, float G, float B) DomainMax { get; }
    /// <summary>The <c>TITLE</c> of the file, or empty.</summary>
    public string Title { get; }
    /// <summary>SHA-256 of the lattice's contents, in hex. Two lattices with the same id are the same table.</summary>
    public string Id { get; }

    private ColorLattice(int size, float[]? cube, int curveSize, float[]? curves, (float, float, float) domainMin, (float, float, float) domainMax, string title)
    {
        if (cube == null && curves == null) throw new ArgumentException("A lattice needs a cube or curves.");
        if (cube != null && (size < 2 || size > DocumentLimits.MaxLookupSize || cube.Length != (long)size * size * size * 3)) throw new ArgumentException("The cube does not match its size.");
        if (curves != null && (curveSize < 2 || curveSize > DocumentLimits.MaxLookupCurveSize || curves.Length != curveSize * 3)) throw new ArgumentException("The curves do not match their size.");
        if (domainMax.Item1 <= domainMin.Item1 || domainMax.Item2 <= domainMin.Item2 || domainMax.Item3 <= domainMin.Item3) throw new ArgumentException("The domain is empty.");
        Size = cube != null ? size : 0;
        this.cube = cube;
        CurveSize = curves != null ? curveSize : 0;
        this.curves = curves;
        DomainMin = domainMin;
        DomainMax = domainMax;
        Title = title;
        Id = Hash();
    }

    /// <summary>The cube's points, red-fastest, three floats each. Empty when the table is curves only.</summary>
    public ReadOnlySpan<float> Cube => cube;
    /// <summary>The curves' points, three floats each. Empty when there are none.</summary>
    public ReadOnlySpan<float> Curves => curves;
    /// <summary>Whether a 3D cube is present, as opposed to curves alone.</summary>
    public bool HasCube => cube != null;

    /// <summary>A cube of <paramref name="size"/> points per axis from <paramref name="points"/>, red-fastest, three floats each.</summary>
    public static ColorLattice FromCube(int size, ReadOnlySpan<float> points, string title = "") =>
        new(size, points.ToArray(), 0, null, (0, 0, 0), (1, 1, 1), title);

    /// <summary>The lattice that changes nothing, at <paramref name="size"/> points per axis.</summary>
    public static ColorLattice Identity(int size)
    {
        var data = new float[size * size * size * 3];
        var k = 0;
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    data[k++] = r / (float)(size - 1);
                    data[k++] = g / (float)(size - 1);
                    data[k++] = b / (float)(size - 1);
                }
        return new ColorLattice(size, data, 0, null, (0, 0, 0), (1, 1, 1), "");
    }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>Reads a LUT file by its extension: <c>.3dl</c> as Autodesk's format, anything else as a <c>.cube</c>.</summary>
    public static ColorLattice Parse(string text, string? fileName = null) =>
        fileName != null && fileName.EndsWith(".3dl", StringComparison.OrdinalIgnoreCase) ? Parse3dl(text) : ParseCube(text);

    /// <summary>Reads a file from disk; the name decides the format and becomes the title when the file has none.</summary>
    public static ColorLattice Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"文件超过 {MaxFileBytes / (1024 * 1024)} MB，没有查找表需要这么大。");
        return Parse(File.ReadAllText(path), info.Name);
    }

    /// <summary>The largest file worth reading as text: a 144-point cube with six decimals is about 90 MB.</summary>
    public const long MaxFileBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Adobe/IRIDAS <c>.cube</c>: <c>TITLE</c>, <c>LUT_1D_SIZE</c> and/or <c>LUT_3D_SIZE</c>, <c>DOMAIN_MIN</c> and
    /// <c>DOMAIN_MAX</c>, then one color per line, the 1D rows first when both sizes are given. Blank lines, comments,
    /// CRLF and other <c>LUT_</c> keywords are allowed; anything else is refused with its line number.
    /// </summary>
    public static ColorLattice ParseCube(string text)
    {
        int size = 0, curveSize = 0;
        var title = "";
        (float, float, float) domainMin = (0, 0, 0), domainMax = (1, 1, 1);
        float[]? curves = null, cube = null;
        var read = 0;                  // colors read so far, curves first
        var early = 0;                 // the line of a color met before any size, refused once a size turns up
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var number = i + 1;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0].ToUpperInvariant();
            if (keyword == "TITLE")
            {
                var rest = line[5..].Trim();
                title = rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"' ? rest[1..^1] : rest;
                continue;
            }
            if (keyword is "LUT_1D_SIZE" or "LUT_3D_SIZE")
            {
                if (early > 0) throw Refuse(early, $"颜色出现在 {keyword} 之前。");
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) throw Refuse(number, $"{keyword} 需要一个整数。");
                if (keyword == "LUT_3D_SIZE")
                {
                    if (cube != null) throw Refuse(number, "LUT_3D_SIZE 出现了两次。");
                    if (n < 2 || n > DocumentLimits.MaxLookupSize) throw Refuse(number, $"LUT_3D_SIZE 必须在 2 到 {DocumentLimits.MaxLookupSize} 之间。");
                    size = n;
                    cube = new float[n * n * n * 3];
                }
                else
                {
                    if (curves != null) throw Refuse(number, "LUT_1D_SIZE 出现了两次。");
                    if (n < 2 || n > DocumentLimits.MaxLookupCurveSize) throw Refuse(number, $"LUT_1D_SIZE 必须在 2 到 {DocumentLimits.MaxLookupCurveSize} 之间。");
                    curveSize = n;
                    curves = new float[n * 3];
                }
                continue;
            }
            if (keyword is "DOMAIN_MIN" or "DOMAIN_MAX")
            {
                if (parts.Length != 4 || !TryColor(parts, 1, out var domain)) throw Refuse(number, $"{keyword} 需要三个数字。");
                if (keyword == "DOMAIN_MIN") domainMin = domain; else domainMax = domain;
                continue;
            }
            if (keyword is "LUT_1D_INPUT_RANGE" or "LUT_3D_INPUT_RANGE")
            {
                // Resolve's spelling of the domain: one range for all three channels.
                if (parts.Length != 3 || !TryNumber(parts[1], out var low) || !TryNumber(parts[2], out var high)) throw Refuse(number, $"{keyword} 需要两个数字。");
                domainMin = (low, low, low);
                domainMax = (high, high, high);
                continue;
            }
            if (keyword.StartsWith("LUT_", StringComparison.Ordinal)) continue;   // a keyword this reader does not know
            if (parts.Length != 3 || !TryColor(parts, 0, out var color))
                throw Refuse(number, char.IsLetter(line[0]) ? $"「{parts[0]}」不是 .cube 关键字。" : "颜色应为三个数字。");
            if (cube == null && curves == null) { if (early == 0) early = number; continue; }
            var total = curveSize + (cube != null ? size * size * size : 0);
            if (read >= total) throw Refuse(number, $"颜色比尺寸要求的 {total} 个还多。");
            var target = read < curveSize ? curves! : cube!;
            var at = (read < curveSize ? read : read - curveSize) * 3;
            target[at] = color.Item1;
            target[at + 1] = color.Item2;
            target[at + 2] = color.Item3;
            read++;
        }
        if (cube == null && curves == null) throw new InvalidDataException("不是 .cube 文件：缺少 LUT_3D_SIZE 或 LUT_1D_SIZE 行。");
        var expected = curveSize + (cube != null ? size * size * size : 0);
        if (read < expected) throw new InvalidDataException($"文件提前结束：尺寸要求 {expected} 个颜色，只读到 {read} 个。");
        if (domainMax.Item1 <= domainMin.Item1 || domainMax.Item2 <= domainMin.Item2 || domainMax.Item3 <= domainMin.Item3)
            throw new InvalidDataException("每个通道的 DOMAIN_MAX 都必须大于 DOMAIN_MIN。");
        return new ColorLattice(size, cube, curveSize, curves, domainMin, domainMax, title);
    }

    /// <summary>
    /// Autodesk <c>.3dl</c>: an optional <c>Mesh</c> header naming the input and output bit depths, a line of input
    /// levels whose count is the size, then size³ whole-number colors, blue-fastest, on the output depth's scale. A
    /// file without the header is scaled by its largest value (8, 10, 12 or 16 bits), as Lolly does. The input levels
    /// are taken as evenly spaced, which every file in circulation has.
    /// </summary>
    public static ColorLattice Parse3dl(string text)
    {
        var lines = text.Split('\n');
        int[]? mesh = null;
        var rows = new List<(int R, int G, int B)>();
        var outputBits = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var number = i + 1;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var values = new int[parts.Length];
            var numeric = true;
            for (var p = 0; p < parts.Length && numeric; p++)
                numeric = int.TryParse(parts[p], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[p]) && values[p] >= 0;
            if (!numeric)
            {
                if (!line.Any(char.IsLetter)) throw Refuse(number, "应为整数。");
                // "Mesh 4 12": input bits (the levels line has 2^4 + 1 entries) and output bits. 3DMESH and other headers are skipped.
                if (parts[0].Equals("Mesh", StringComparison.OrdinalIgnoreCase) && parts.Length == 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) && bits is >= 1 and <= 16)
                    outputBits = bits;
                continue;
            }
            if (mesh == null && rows.Count == 0 && values.Length > 3) { mesh = values; continue; }
            if (values.Length != 3) throw Refuse(number, "颜色应为三个整数。");
            if (mesh != null && rows.Count >= (long)mesh.Length * mesh.Length * mesh.Length) throw Refuse(number, $"颜色比 {mesh.Length} 级输入要求的还多。");
            rows.Add((values[0], values[1], values[2]));
        }
        var size = mesh?.Length ?? (int)Math.Round(Math.Cbrt(rows.Count));
        if (size < 2 || rows.Count < (long)size * size * size)
            throw new InvalidDataException(mesh == null
                ? $"不是 .3dl 文件：{rows.Count} 个颜色构不成完整的立方体。"
                : $"文件提前结束：{size} 级输入要求 {(long)size * size * size} 个颜色，只读到 {rows.Count} 个。");
        if (size > DocumentLimits.MaxLookupSize) throw new InvalidDataException($"查找表每轴 {size} 个点；最多支持 {DocumentLimits.MaxLookupSize} 个。");
        if (rows.Count > size * size * size) throw new InvalidDataException($"不是 .3dl 文件：{rows.Count} 个颜色构不成完整的立方体。");
        var peak = 0;
        foreach (var row in rows) peak = Math.Max(peak, Math.Max(row.R, Math.Max(row.G, row.B)));
        var scale = outputBits > 0 ? (1 << outputBits) - 1 : peak > 4095 ? 65535 : peak > 1023 ? 4095 : peak > 255 ? 1023 : 255;
        var data = new float[size * size * size * 3];
        var k = 0;
        for (var r = 0; r < size; r++)
            for (var g = 0; g < size; g++)
                for (var b = 0; b < size; b++)
                {
                    var row = rows[k++];
                    var at = ((b * size + g) * size + r) * 3;   // red-fastest destination
                    data[at] = row.R / (float)scale;
                    data[at + 1] = row.G / (float)scale;
                    data[at + 2] = row.B / (float)scale;
                }
        return new ColorLattice(size, data, 0, null, (0, 0, 0), (1, 1, 1), "");
    }

    private static InvalidDataException Refuse(int line, string problem) => new($"第 {line} 行：{problem}");

    private static bool TryNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static bool TryColor(string[] parts, int from, out (float, float, float) color)
    {
        color = default;
        if (!TryNumber(parts[from], out var r) || !TryNumber(parts[from + 1], out var g) || !TryNumber(parts[from + 2], out var b)) return false;
        color = (r, g, b);
        return true;
    }

    // ── writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The lattice as <c>.cube</c> text with six decimals, the precision LUT files are exchanged at, under
    /// <paramref name="title"/> and any <paramref name="comments"/> as <c>#</c> lines.
    /// </summary>
    public string ToCube(string? title = null, params string[] comments)
    {
        var text = new StringBuilder();
        foreach (var comment in comments) text.Append("# ").Append(comment).Append('\n');
        var name = (title ?? Title).Replace('"', '\'');
        if (name.Length > 0) text.Append("TITLE \"").Append(name).Append("\"\n");
        if (curves != null) text.Append("LUT_1D_SIZE ").Append(CurveSize).Append('\n');
        if (cube != null) text.Append("LUT_3D_SIZE ").Append(Size).Append('\n');
        text.Append("DOMAIN_MIN ").Append(Six(DomainMin.R)).Append(' ').Append(Six(DomainMin.G)).Append(' ').Append(Six(DomainMin.B)).Append('\n');
        text.Append("DOMAIN_MAX ").Append(Six(DomainMax.R)).Append(' ').Append(Six(DomainMax.G)).Append(' ').Append(Six(DomainMax.B)).Append('\n');
        Rows(curves);
        Rows(cube);
        return text.ToString();

        void Rows(float[]? data)
        {
            if (data == null) return;
            for (var i = 0; i < data.Length; i += 3)
                text.Append(Six(data[i])).Append(' ').Append(Six(data[i + 1])).Append(' ').Append(Six(data[i + 2])).Append('\n');
        }
    }

    private static string Six(float value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    // ── sampling ─────────────────────────────────────────────────────────────

    /// <summary>Looks up one color, all channels 0 to 1 (the domain maps them onto the table); the result is not clamped.</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        r = Shape(0, r);
        g = Shape(1, g);
        b = Shape(2, b);
        if (cube == null) return (r, g, b);
        var n1 = Size - 1;
        float x = Math.Clamp(r, 0, 1) * n1, y = Math.Clamp(g, 0, 1) * n1, z = Math.Clamp(b, 0, 1) * n1;
        int x0 = Math.Min((int)x, Size - 2), y0 = Math.Min((int)y, Size - 2), z0 = Math.Min((int)z, Size - 2);
        Tetrahedral(cube, Size, x0, y0, z0, x - x0, y - y0, z - z0, out r, out g, out b);
        return (r, g, b);
    }

    /// <summary>Maps one channel's input onto the domain and through the curves, if any, to 0..1.</summary>
    private float Shape(int channel, float value)
    {
        var (min, max) = channel switch { 0 => (DomainMin.R, DomainMax.R), 1 => (DomainMin.G, DomainMax.G), _ => (DomainMin.B, DomainMax.B) };
        value = Math.Clamp((value - min) / (max - min), 0, 1);
        if (curves == null) return value;
        var x = value * (CurveSize - 1);
        var i0 = (int)x;
        var f = x - i0;
        var i1 = Math.Min(i0 + 1, CurveSize - 1);
        return curves[i0 * 3 + channel] * (1 - f) + curves[i1 * 3 + channel] * f;
    }

    /// <summary>
    /// Tetrahedral interpolation inside the cell at (x0, y0, z0) with fractions (fx, fy, fz): the standard for grading,
    /// because it is exact on the grid and keeps grey grey along the diagonal, where trilinear drifts. The six cases
    /// and their order are Lolly's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Tetrahedral(float[] d, int n, int x0, int y0, int z0, float fx, float fy, float fz, out float r, out float g, out float b)
    {
        const int sx = 3;
        int sy = n * 3, sz = n * n * 3;
        var i000 = ((z0 * n + y0) * n + x0) * 3;
        var i111 = i000 + sx + sy + sz;
        float w0, w1, w2, w3;
        int ia, ib;
        if (fx >= fy)
        {
            if (fy >= fz) { w0 = 1 - fx; w1 = fx - fy; w2 = fy - fz; w3 = fz; ia = i000 + sx; ib = i000 + sx + sy; }
            else if (fx >= fz) { w0 = 1 - fx; w1 = fx - fz; w2 = fz - fy; w3 = fy; ia = i000 + sx; ib = i000 + sx + sz; }
            else { w0 = 1 - fz; w1 = fz - fx; w2 = fx - fy; w3 = fy; ia = i000 + sz; ib = i000 + sx + sz; }
        }
        else
        {
            if (fz >= fy) { w0 = 1 - fz; w1 = fz - fy; w2 = fy - fx; w3 = fx; ia = i000 + sz; ib = i000 + sy + sz; }
            else if (fz >= fx) { w0 = 1 - fy; w1 = fy - fz; w2 = fz - fx; w3 = fx; ia = i000 + sy; ib = i000 + sy + sz; }
            else { w0 = 1 - fy; w1 = fy - fx; w2 = fx - fz; w3 = fz; ia = i000 + sy; ib = i000 + sx + sy; }
        }
        r = w0 * d[i000] + w1 * d[ia] + w2 * d[ib] + w3 * d[i111];
        g = w0 * d[i000 + 1] + w1 * d[ia + 1] + w2 * d[ib + 1] + w3 * d[i111 + 1];
        b = w0 * d[i000 + 2] + w1 * d[ia + 2] + w2 * d[ib + 2] + w3 * d[i111 + 2];
    }

    /// <summary>
    /// The per-pixel operation for 8-bit pixels at <paramref name="amount"/> (0 to 1) of the look. The domain and the
    /// curves are functions of one channel, so they collapse into one table per channel from a byte to a cell and the
    /// fraction inside it, built here once; a table without a cube collapses all the way to three byte tables.
    /// </summary>
    internal PixelOp CreateOp(float amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        if (cube == null)
        {
            byte[] red = new byte[256], green = new byte[256], blue = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                var (r, g, b) = Sample(i / 255f, i / 255f, i / 255f);
                red[i] = Mix(i, r, t);
                green[i] = Mix(i, g, t);
                blue[i] = Mix(i, b, t);
            }
            return Lut.Op(red, green, blue);
        }
        var n = Size;
        var cells = new int[3][];
        var fractions = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            cells[c] = new int[256];
            fractions[c] = new float[256];
            for (var i = 0; i < 256; i++)
            {
                var x = Math.Clamp(Shape(c, i / 255f), 0, 1) * (n - 1);
                var cell = Math.Min((int)x, n - 2);
                cells[c][i] = cell;
                fractions[c][i] = x - cell;
            }
        }
        int[] cellR = cells[0], cellG = cells[1], cellB = cells[2];
        float[] fracR = fractions[0], fracG = fractions[1], fracB = fractions[2];
        var data = cube;
        return [MethodImpl(MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) =>
        {
            Tetrahedral(data, n, cellR[r], cellG[g], cellB[b], fracR[r], fracG[g], fracB[b], out var nr, out var ng, out var nb);
            r = Mix(r, nr, t);
            g = Mix(g, ng, t);
            b = Mix(b, nb, t);
        };
    }

    /// <summary>Blends a looked-up value (0 to 1, unclamped) into the original byte by <paramref name="t"/>, as Lolly's frame apply does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Mix(int original, float looked, float t) =>
        (byte)Math.Clamp((int)MathF.Round(original + (looked * 255 - original) * t), 0, 255);

    // ── the project file's entry ─────────────────────────────────────────────

    /// <summary>The most bytes a stored lattice can take: the largest cube and curves as floats, with room for the header.</summary>
    public const long MaxEntryBytes = ((long)DocumentLimits.MaxLookupSize * DocumentLimits.MaxLookupSize * DocumentLimits.MaxLookupSize * 3 + DocumentLimits.MaxLookupCurveSize * 3L) * 4 + 64 * 1024;

    private static ReadOnlySpan<byte> Magic => "CLUT"u8;

    /// <summary>
    /// Writes the lattice as the project file stores it: a header (the sizes, the domain and the title) and the
    /// floats as they lie in memory, little-endian, which is what every platform Composa runs on is. Exact, unlike
    /// the six decimals of <see cref="ToCube"/>.
    /// </summary>
    public void WriteTo(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(1);   // the entry's own version
        writer.Write(Size);
        writer.Write(CurveSize);
        writer.Write(DomainMin.R); writer.Write(DomainMin.G); writer.Write(DomainMin.B);
        writer.Write(DomainMax.R); writer.Write(DomainMax.G); writer.Write(DomainMax.B);
        writer.Write(Title);
        writer.Flush();
        if (curves != null) stream.Write(MemoryMarshal.AsBytes(curves.AsSpan()));
        if (cube != null) stream.Write(MemoryMarshal.AsBytes(cube.AsSpan()));
    }

    /// <summary>Reads what <see cref="WriteTo"/> wrote, checking the sizes against the ceilings before anything is allocated.</summary>
    public static ColorLattice ReadFrom(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw Damaged();
            if (reader.ReadInt32() != 1) throw new InvalidDataException("该查找表由更新版本的 Composa 写入。");
            var size = reader.ReadInt32();
            var curveSize = reader.ReadInt32();
            if (size != 0 && (size < 2 || size > DocumentLimits.MaxLookupSize)) throw Damaged();
            if (curveSize != 0 && (curveSize < 2 || curveSize > DocumentLimits.MaxLookupCurveSize)) throw Damaged();
            if (size == 0 && curveSize == 0) throw Damaged();
            var domainMin = (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var domainMax = (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var title = reader.ReadString();
            if (title.Length > 1024) throw Damaged();
            float[]? curves = null, cube = null;
            if (curveSize > 0) stream.ReadExactly(MemoryMarshal.AsBytes((curves = new float[curveSize * 3]).AsSpan()));
            if (size > 0) stream.ReadExactly(MemoryMarshal.AsBytes((cube = new float[size * size * size * 3]).AsSpan()));
            if ((curves != null && curves.Any(float.IsNaN)) || (cube != null && cube.Any(float.IsNaN))) throw Damaged();
            return new ColorLattice(size, cube, curveSize, curves, domainMin, domainMax, title);
        }
        catch (Exception error) when (error is EndOfStreamException or ArgumentException)
        {
            throw Damaged();
        }

        static InvalidDataException Damaged() => new("项目内的查找表已损坏。");
    }

    // ── identity ─────────────────────────────────────────────────────────────

    private string Hash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<float> header = [Size, CurveSize, DomainMin.R, DomainMin.G, DomainMin.B, DomainMax.R, DomainMax.G, DomainMax.B];
        hash.AppendData(MemoryMarshal.AsBytes(header));
        if (curves != null) hash.AppendData(MemoryMarshal.AsBytes(curves.AsSpan()));
        if (cube != null) hash.AppendData(MemoryMarshal.AsBytes(cube.AsSpan()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>
/// Color Lookup: every color replaced by what a lookup table says, mixed back into the original by
/// <see cref="Amount"/>. The lattice is not part of the JSON (the project file stores it as its own entry, under
/// <see cref="LatticeId"/>), and until one is chosen the adjustment changes nothing.
/// </summary>
public sealed record ColorLookupAdjustment : Adjustment
{
    private readonly string? latticeId;

    [JsonIgnore] public ColorLattice? Lattice { get; init; }
    /// <summary>The lattice's <see cref="ColorLattice.Id"/>, which is what the manifest carries and what tells two adjustments apart.</summary>
    public string? LatticeId { get => Lattice?.Id ?? latticeId; init => latticeId = value; }
    /// <summary>What the lattice came from: a bundled look's name or a file's name, for the layer's name and the dialog.</summary>
    public string Source { get; init; } = "";
    /// <summary>How much of the look shows, 0 to 100.</summary>
    public double Amount { get; init; } = 100;
    public override AdjustmentKind Kind => AdjustmentKind.ColorLookup;
    public override string DisplayName => "颜色查找";
    public override bool IsIdentity => Lattice == null || Amount <= 0;

    internal override PixelOp CreateOp() => Lattice!.CreateOp((float)(Amount / 100));
}
