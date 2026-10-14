// Ported from Lolly (github.com/lolly-tools/lolly, engine/src/raster-layers.ts at 12b26ff, the XCF mode table), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Xcf;

/// <summary>
/// A GIMP file rebuilt as this editor's layers, as far as they can carry it, with a report of everything that had to
/// be converted on the way. Nothing is applied to a document until the caller decides to, so the report can be shown
/// first. GIMP's format is an interchange format here: files are opened, never written.
/// </summary>
public sealed class XcfImport
{
    public int Width { get; }
    public int Height { get; }
    public double Resolution { get; }
    /// <summary>Root layers, bottom to top, with folders holding their children. Meant to be placed once.</summary>
    public List<Layer> Layers { get; }
    public IReadOnlyList<Guide> Guides { get; }
    public IReadOnlyList<ImportConversion> Conversions { get; }

    private XcfImport(int width, int height, double resolution, List<Layer> layers, List<Guide> guides, List<ImportConversion> conversions)
    {
        Width = width;
        Height = height;
        Resolution = resolution;
        Layers = layers;
        Guides = guides;
        Conversions = conversions;
    }

    /// <summary>
    /// GIMP files are known by their <c>gimp xcf </c> signature, whatever their extension, and a compressed one
    /// (<c>.xcf.gz</c>, or <c>.xcf.bz2</c> and <c>.xcf.xz</c>, which are refused with a message) by its name.
    /// </summary>
    public static bool IsXcf(string path) => XcfReader.Matches(path) || XcfReader.MatchesCompressed(path);
    public static bool IsXcf(ReadOnlySpan<byte> data) => XcfReader.Matches(data) || XcfReader.IsGzip(data);

    /// <summary>Reads a file that is to become a document of its own, so the whole document budget is its to use.</summary>
    public static XcfImport Load(string path) => Load(path, DocumentLimits.DocumentPixelBudget);
    public static XcfImport Load(byte[] data) => Load(data, DocumentLimits.DocumentPixelBudget);

    /// <param name="pixelBudget">How much raster the file may add: the document budget, less what the target document already holds.</param>
    public static XcfImport Load(string path, long pixelBudget)
    {
        var info = new FileInfo(path);
        if (info.Length > int.MaxValue) throw XcfException.TooLarge();
        foreach (var (suffix, name) in XcfReader.UnreadableCompression)
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new XcfException($"该 GIMP 文件使用 {name} 压缩，Composa 无法读取。请从 GIMP 另存为 .xcf 或 .xcf.gz。");
        return Load(File.ReadAllBytes(path), pixelBudget, info.Name);
    }

    public static XcfImport Load(byte[] data, long pixelBudget) => Load(data, pixelBudget, "图像");

    private static XcfImport Load(byte[] data, long pixelBudget, string fileName)
    {
        if (XcfReader.IsGzip(data)) data = XcfReader.Unpack(data);
        var file = XcfReader.Read(data, pixelBudget);
        try { return Build(file, fileName, pixelBudget); }
        catch
        {
            foreach (var layer in file.Layers) { layer.Image?.Dispose(); layer.MaskImage?.Dispose(); }
            throw;
        }
    }

    /// <summary>Frees the layers' pixels when the import is not going ahead.</summary>
    public void Discard()
    {
        foreach (var layer in Document.Flatten(Layers))
        {
            layer.Pixels?.Dispose();
            layer.Mask?.Dispose();
            layer.Pixels = null;
            layer.Mask = null;
        }
        Layers.Clear();
    }

    /// <summary>A new document holding the layers and guides, the topmost root layer active.</summary>
    public Document ToDocument()
    {
        var document = new Document(Width, Height) { Resolution = Resolution };
        document.Layers.AddRange(Layers);
        document.Guides.AddRange(Guides);
        document.SetActive(Layers.LastOrDefault()?.Id);
        return document;
    }

    private static XcfImport Build(XcfFile file, string fileName, long pixelBudget)
    {
        var conversions = new List<ImportConversion>();
        void Note(string layer, string message) => conversions.Add(new ImportConversion(layer, message));
        var canvas = new SKSizeI(file.Width, file.Height);
        // What text laid out afresh may still take, after the pixels the reader already holds.
        var remaining = pixelBudget - file.Layers.Sum(l => (long)(l.Image?.Width ?? 0) * (l.Image?.Height ?? 0) + (long)(l.MaskImage?.Width ?? 0) * (l.MaskImage?.Height ?? 0));

        if (file.Version > XcfReader.KnownVersion) Note(fileName, $"该文件由比此版本 Composa 所知更新的 GIMP 保存（格式 {file.Version}），其后新增的内容可能缺失。");
        if (file.Precision.Deep) Note(fileName, $"该文件以 {file.Precision.Name} 像素保存；Composa 每通道保留 8 位，因此损失了部分精度。");
        else if (file.Precision.Linear) Note(fileName, "该文件以线性光像素保存；已转换为 sRGB。");
        if (file.BaseType == 2) Note(fileName, "索引调色板已转换为 RGB 颜色；调色板本身不再保留。");
        if (file.HasIccProfile) Note(fileName, "该文件带有颜色配置文件，Composa 暂不应用；颜色按 sRGB 处理。");
        if (file.ChannelCount > 0) Note(fileName, file.ChannelCount == 1 ? "一个已保存的通道（选区或 Alpha 通道）被舍弃。" : $"{file.ChannelCount} 个已保存的通道（选区或 Alpha 通道）被舍弃。");
        if (file.PathCount > 0) Note(fileName, file.PathCount == 1 ? "一个路径被舍弃；Composa 没有路径。" : $"{file.PathCount} 个路径被舍弃；Composa 没有路径。");

        // Groups by their item path: a member's path is its group's path with one more index. The file lists layers
        // top to bottom and so do the paths, so each group's children are gathered top first and reversed. A layer
        // at the top level carries no path in GIMP's own files; its path is its place among the roots, top first.
        var records = Enumerable.Reverse(file.Layers).ToList();   // top first, as the paths count
        var roots = new List<Layer>();
        var groups = new Dictionary<string, Layer>();
        var childrenOf = new Dictionary<Layer, List<Layer>>();
        var built = new List<(XcfLayer Record, Layer Layer)>();
        foreach (var record in records)
        {
            var name = record.Name.Length == 0 ? (record.IsGroup ? "文件夹" : "图层") : record.Name;
            var layer = record.IsGroup ? BuildGroup(record, name, canvas, Note) : BuildLayer(record, name, canvas, file.Resolution, ref remaining, Note);
            built.Add((record, layer));
            var path = record.ItemPath is { Length: > 0 } own ? own : [roots.Count];
            if (record.IsGroup) groups[string.Join('/', path)] = layer;
            var parent = path.Length > 1 && groups.TryGetValue(string.Join('/', path[..^1]), out var group) ? group : null;
            if (parent == null) roots.Add(layer);
            else
            {
                if (!childrenOf.TryGetValue(parent, out var siblings)) childrenOf[parent] = siblings = [];
                siblings.Add(layer);
            }
        }
        foreach (var (group, children) in childrenOf) { children.Reverse(); group.Children.AddRange(children); }
        roots.Reverse();

        var guides = file.Guides.Select(g => new Guide(Guid.NewGuid(), g.Vertical ? GuideAxis.Vertical : GuideAxis.Horizontal, g.Position)).Where(g => g.IsValid).Take(1000).ToList();
        return new XcfImport(file.Width, file.Height, file.Resolution, roots, guides, conversions);
    }

    private static Layer BuildGroup(XcfLayer record, string name, SKSizeI canvas, Action<string, string> note)
    {
        var layer = Layer.Group(name);
        layer.Collapsed = record.Collapsed;
        layer.Visible = record.Visible;
        layer.Opacity = record.Opacity;
        var mode = XcfMode.Find(record.Mode);
        if (mode is { Blend: BlendMode.Normal, Supported: true } || record.Mode == XcfMode.PassThrough) layer.Blend = BlendMode.Normal;
        else note(name, $"文件夹混合模式「{mode?.Name ?? record.Mode.ToString()}」不受支持，该文件夹将按穿透处理。");
        ApplyMask(record, layer, canvas, note);
        return layer;
    }

    private static Layer BuildLayer(XcfLayer record, string name, SKSizeI canvas, double resolution, ref long remaining, Action<string, string> note)
    {
        if (record.Cropped) note(name, PsdImport.CroppedNote);
        if (record.DamagedTiles > 0) note(name, record.DamagedTiles == 1 ? "该图层有一个分块无法读取，现为透明。" : $"该图层有 {record.DamagedTiles} 个分块无法读取，现为透明。");
        if (record.TextParasite != null)
        {
            // Text in one style is laid out afresh and can be retyped; text with markup keeps GIMP's pixels.
            if (XcfText.Parse(record.TextParasite, resolution, out var why) is { } source && XcfText.Place(source, record, name, ref remaining) is { } text)
            {
                record.Image?.Dispose();
                record.MaskImage?.Dispose();
                foreach (var message in source.Notes) note(name, message);
                text.Visible = record.Visible;
                text.Opacity = record.Opacity;
                text.Blend = XcfMode.Find(record.Mode) is { Supported: true } textMode ? textMode.Blend : BlendMode.Normal;
                return text;
            }
            note(name, why);
        }
        if (record.EffectCount > 0) note(name, record.EffectCount == 1 ? "该图层的效果（GIMP 滤镜）无法在此应用，已被舍弃。" : $"该图层的 {record.EffectCount} 个效果（GIMP 滤镜）无法在此应用，已被舍弃。");
        if (record.IsVectorLayer) note(name, "矢量图层已按像素导入；其形状无法在此编辑。");
        if (record.IsLinkLayer) note(name, "链接图层已按像素导入；不再跟随其源文件。");

        // No pixel area (an empty or wholly off-canvas layer): an empty layer over the canvas.
        var layer = record.Image is { } image ? Layer.Raster(name, image, record.Left, record.Top) : Layer.Raster(name, Pixels.NewColor(canvas.Width, canvas.Height));
        layer.Visible = record.Visible;
        layer.Opacity = record.Opacity;
        var mode = XcfMode.Find(record.Mode);
        if (mode == null) note(name, $"混合模式 {record.Mode} 无法识别，将按「正常」应用。");
        else if (!mode.Supported) note(name, $"混合模式「{mode.Name}」不受支持，将按「正常」应用。");
        else if (mode.Approximate) note(name, $"混合模式「{mode.Name}」没有完全对应的模式；「{mode.Blend.DisplayName()}」最为接近。");
        layer.Blend = mode is { Supported: true } ? mode.Blend : BlendMode.Normal;

        // GIMP composites in linear light unless told otherwise; Composa composites in sRGB. A Normal layer at full
        // opacity looks the same either way, anything else does not. The value is negative when GIMP chose it itself.
        var blendSpace = Math.Abs(record.BlendSpace);
        var compositeSpace = Math.Abs(record.CompositeSpace);
        var changesColor = layer.Blend != BlendMode.Normal || layer.Opacity < 1 || (record.HasMask && record.ApplyMask);
        if (changesColor && (blendSpace == 1 || compositeSpace == 1)) note(name, "GIMP 以线性光混合此图层；此处以 sRGB 混合，结果可能略有差异。");
        else if (changesColor && (blendSpace == 3 || compositeSpace == 3)) note(name, "GIMP 以 LAB 混合此图层；此处以 sRGB 混合，结果可能有所差异。");
        // GIMP picks Clip to backdrop itself for most modes (the value is then negative), which differs from Union only
        // where the backdrop is transparent; only a compositing the person chose is worth a line.
        if (record.CompositeMode is 2 or 3 or 4) note(name, $"「{XcfMode.CompositeModeName(record.CompositeMode)}」合成方式不受支持；图层将按 GIMP 的「联合」方式合成。");

        ApplyMask(record, layer, canvas, note);
        return layer;
    }

    /// <summary>
    /// A layer's mask shares its pixel grid here; a folder's mask covers the document, so the folder's mask is placed
    /// into one that size (GIMP keeps it over the folder's own rectangle).
    /// </summary>
    private static void ApplyMask(XcfLayer record, Layer layer, SKSizeI canvas, Action<string, string> note)
    {
        if (record.MaskImage is not { } plane) return;
        if (layer.Pixels != null && plane.Width == layer.Pixels.Width && plane.Height == layer.Pixels.Height)
        {
            layer.Mask = plane;
        }
        else
        {
            var mask = Pixels.NewMask(canvas.Width, canvas.Height, 255);
            using (plane)
            using (var surface = new SKCanvas(mask))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                surface.DrawBitmap(plane, record.Left, record.Top, paint);
            Pixels.Invalidate(mask);
            layer.Mask = mask;
        }
        layer.MaskEnabled = record.ApplyMask;
    }
}

/// <summary>
/// GIMP's layer modes, both generations in one number space: 0 to 22 are the legacy modes, 23 to 27 the LCH ones
/// GIMP 2.10 added, 28 to 61 the current class. Each maps onto the blend mode here whose math agrees, or the closest
/// one with <see cref="Approximate"/> set; the modes with no counterpart (painting modes and GIMP's own compositing
/// tricks) fall back to Normal and say so.
/// </summary>
internal sealed record XcfMode(string Name, BlendMode Blend, bool Approximate = false, bool Supported = true)
{
    public const int PassThrough = 61;

    private static readonly Dictionary<int, XcfMode> Table = new()
    {
        [0] = new("正常（旧版）", BlendMode.Normal),
        [1] = new("溶解", BlendMode.Normal, Supported: false),
        [2] = new("后方", BlendMode.Normal, Supported: false),
        [3] = new("正片叠底（旧版）", BlendMode.Multiply),
        [4] = new("滤色（旧版）", BlendMode.Screen),
        [5] = new("叠加（旧版）", BlendMode.SoftLight, true),      // GIMP's old Overlay was soft light's math
        [6] = new("差值（旧版）", BlendMode.Difference),
        [7] = new("相加（旧版）", BlendMode.LinearDodge),
        [8] = new("减去（旧版）", BlendMode.Subtract),
        [9] = new("只变暗（旧版）", BlendMode.Darken),
        [10] = new("只变亮（旧版）", BlendMode.Lighten),
        [11] = new("HSV 色相（旧版）", BlendMode.Hue, true),
        [12] = new("HSV 饱和度（旧版）", BlendMode.Saturation, true),
        [13] = new("HSL 颜色（旧版）", BlendMode.Color, true),
        [14] = new("HSV 亮度（旧版）", BlendMode.Luminosity, true),
        [15] = new("划分（旧版）", BlendMode.Divide),
        [16] = new("减淡（旧版）", BlendMode.ColorDodge),
        [17] = new("加深（旧版）", BlendMode.ColorBurn),
        [18] = new("强光（旧版）", BlendMode.HardLight),
        [19] = new("柔光（旧版）", BlendMode.SoftLight, true),
        [20] = new("颗粒提取（旧版）", BlendMode.GrainExtract),
        [21] = new("颗粒合并（旧版）", BlendMode.GrainMerge),
        [22] = new("颜色擦除（旧版）", BlendMode.Normal, Supported: false),
        [23] = new("叠加", BlendMode.Overlay),
        [24] = new("LCH 色相", BlendMode.Hue, true),
        [25] = new("LCH 色度", BlendMode.Saturation, true),
        [26] = new("LCH 颜色", BlendMode.Color, true),
        [27] = new("LCH 亮度", BlendMode.Luminosity, true),
        [28] = new("正常", BlendMode.Normal),
        [29] = new("后方", BlendMode.Normal, Supported: false),
        [30] = new("正片叠底", BlendMode.Multiply),
        [31] = new("滤色", BlendMode.Screen),
        [32] = new("差值", BlendMode.Difference),
        [33] = new("相加", BlendMode.LinearDodge),
        [34] = new("减去", BlendMode.Subtract),
        [35] = new("只变暗", BlendMode.Darken),
        [36] = new("只变亮", BlendMode.Lighten),
        [37] = new("HSV 色相", BlendMode.Hue, true),
        [38] = new("HSV 饱和度", BlendMode.Saturation, true),
        [39] = new("HSL 颜色", BlendMode.Color, true),
        [40] = new("HSV 亮度", BlendMode.Luminosity, true),
        [41] = new("划分", BlendMode.Divide),
        [42] = new("减淡", BlendMode.ColorDodge),
        [43] = new("加深", BlendMode.ColorBurn),
        [44] = new("强光", BlendMode.HardLight),
        [45] = new("柔光", BlendMode.SoftLight, true),
        [46] = new("颗粒提取", BlendMode.GrainExtract),
        [47] = new("颗粒合并", BlendMode.GrainMerge),
        [48] = new("亮光", BlendMode.VividLight),
        [49] = new("点光", BlendMode.PinLight),
        [50] = new("线性光", BlendMode.LinearLight),
        [51] = new("实色混合", BlendMode.HardMix),
        [52] = new("排除", BlendMode.Exclusion),
        [53] = new("线性加深", BlendMode.LinearBurn),
        [54] = new("亮度只变暗", BlendMode.Darken, true),
        [55] = new("亮度只变亮", BlendMode.Lighten, true),
        [56] = new("明度", BlendMode.Luminosity, true),
        [57] = new("颜色擦除", BlendMode.Normal, Supported: false),
        [58] = new("擦除", BlendMode.Normal, Supported: false),
        [59] = new("合并", BlendMode.Normal, Supported: false),
        [60] = new("拆分", BlendMode.Normal, Supported: false),
        [61] = new("穿透", BlendMode.Normal)
    };

    public static XcfMode? Find(int mode) => Table.GetValueOrDefault(mode);

    public static string CompositeModeName(int mode) => mode switch { 2 => "剪贴到背景", 3 => "剪贴到图层", 4 => "交集", _ => "联合" };
}
