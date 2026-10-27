// Ported from Lolly (github.com/lolly-tools/lolly, tests/xcf.test.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Editing;
using Composa.IO;
using Composa.IO.Xcf;
using Composa.Model;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class XcfTests
{
    private static XcfWriter Writer(int version, int width, int height, int compression = 1) => new() { Version = version, Width = width, Height = height, Compression = compression };

    private static XcfImport Load(XcfWriter writer, long? budget = null) => budget is { } b ? XcfImport.Load(writer.Build(), b) : XcfImport.Load(writer.Build());

    private static List<string> Messages(XcfImport import) => import.Conversions.Select(c => c.Message).ToList();

    /// <summary>Every pixel of the layer against the straight RGBA the fixture was built from, within a step.</summary>
    private static void AssertPixels(Layer layer, byte[] rgba, int width, int tolerance = 1)
    {
        var pixels = layer.Pixels!;
        Assert.Equal(rgba.Length / 4 / width, pixels.Height);
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                AssertColor(new SKColor(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]), pixels.GetPixel(x, y), tolerance);
            }
    }

    // ── recognising a file ───────────────────────────────────────────────────

    [Fact]
    public void A_file_is_known_by_its_signature_and_a_version_token()
    {
        Assert.True(XcfImport.IsXcf(Writer(1, 2, 2).Build()));
        Assert.True(XcfImport.IsXcf(Writer(11, 2, 2).Build()));
        Assert.True(XcfImport.IsXcf(Writer(0, 2, 2).Build()));
        var magic = Writer(1, 2, 2).Build();
        magic[0] = (byte)'G';
        Assert.False(XcfImport.IsXcf(magic));
        var token = Writer(1, 2, 2).Build();
        token[9] = (byte)'x';
        Assert.False(XcfImport.IsXcf(token));
        Assert.False(XcfImport.IsXcf(new byte[4]));
        Assert.Equal("这不是 GIMP 文件。", Assert.Throws<XcfException>(() => XcfImport.Load([1, 2, 3])).Message);
    }

    // ── versions, compressions, pixels ───────────────────────────────────────

    [Fact]
    public void Version_1_rle_keeps_geometry_order_opacity_mode_visibility_and_pixels()
    {
        var writer = Writer(1, 8, 8);
        var top = new XcfWriterLayer { Name = "top layer", Width = 6, Height = 4, X = -2, Y = 3, Opacity255 = 128, Mode = 30, Visible = false }.Patterned(3);
        var bottom = new XcfWriterLayer { Name = "bottom", Width = 8, Height = 8, Mode = 28 }.Patterned(1);
        writer.Layers.AddRange([top, bottom]);   // The file lists the top layer first.
        var import = Load(writer);
        Assert.Empty(import.Conversions);
        Assert.Equal((8, 8), (import.Width, import.Height));
        Assert.Equal(["bottom", "top layer"], import.Layers.Select(l => l.Name));   // Bottom to top here.
        var first = import.Layers[0];
        Assert.Equal((BlendMode.Normal, true, 1d), (first.Blend, first.Visible, first.Opacity));
        AssertPixels(first, bottom.Rgba!, 8);
        var second = import.Layers[1];
        Assert.Equal((-2d, 3d, 6d, 4d), (second.Transform.X, second.Transform.Y, second.Transform.Width, second.Transform.Height));
        Assert.Equal(128 / 255.0, second.Opacity, 6);
        Assert.Equal((BlendMode.Multiply, false), (second.Blend, second.Visible));
        AssertPixels(second, top.Rgba!, 6);
    }

    [Fact]
    public void Version_11_zlib_with_wide_pointers_and_float_opacity_spans_several_tiles()
    {
        var writer = Writer(11, 70, 65, compression: 2);
        var layer = new XcfWriterLayer { Name = "ζlib layer", Width = 70, Height = 65, FloatOpacity = 0.25f, Mode = 31 }.Patterned(7);
        writer.Layers.Add(layer);
        var import = Load(writer);
        var only = Assert.Single(import.Layers);
        Assert.Equal("ζlib layer", only.Name);
        Assert.Equal(0.25, only.Opacity, 6);
        Assert.Equal(BlendMode.Screen, only.Blend);
        AssertPixels(only, layer.Rgba!, 70);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(10, 2)]
    [InlineData(14, 0)]
    [InlineData(18, 1)]
    [InlineData(20, 2)]
    [InlineData(26, 1)]
    public void Every_version_family_and_compression_reads_the_same_pixels(int version, int compression)
    {
        var writer = Writer(version, 130, 70, compression);
        var layer = new XcfWriterLayer { Name = "raw", Width = 130, Height = 70, X = 5, Y = 3 }.Patterned(2);
        writer.Layers.Add(layer);
        writer.Channels = 1;
        writer.Paths = 2;
        var import = Load(writer);
        AssertPixels(Assert.Single(import.Layers), layer.Rgba!, 130);
        Assert.Contains(Messages(import), m => m.Contains("已保存的通道"));
        Assert.Contains(Messages(import), m => m.Contains("个路径"));
    }

    [Fact]
    public void Gray_indexed_and_their_alpha_variants_become_rgba()
    {
        byte[] gray = [10, 10, 10, 255, 200, 200, 200, 255, 90, 90, 90, 255, 0, 0, 0, 255];
        var writer = Writer(1, 2, 2);
        writer.BaseType = 1;
        writer.Layers.Add(new XcfWriterLayer { Name = "g", Width = 2, Height = 2, Type = 2, Rgba = gray });
        writer.Layers.Add(new XcfWriterLayer { Name = "ga", Width = 2, Height = 2, Type = 3, Rgba = [10, 10, 10, 255, 200, 200, 200, 128, 90, 90, 90, 0, 0, 0, 0, 255] });
        var import = Load(writer);
        AssertPixels(import.Layers[1], gray, 2);
        var ga = import.Layers[0].Pixels!;
        Assert.Equal(128, ga.GetPixel(1, 0).Alpha);
        Assert.Equal(0, ga.GetPixel(0, 1).Alpha);
        Assert.Empty(import.Conversions);

        var indexed = Writer(11, 2, 1);
        indexed.BaseType = 2;
        indexed.Colormap = [255, 0, 0, 0, 0, 255, 0, 255, 0];
        indexed.Layers.Add(new XcfWriterLayer { Name = "i", Width = 2, Height = 1, Type = 5, Rgba = [2, 0, 0, 255, 1, 0, 0, 100] });   // red = the index
        import = Load(indexed);
        var pixels = Assert.Single(import.Layers).Pixels!;
        AssertColor(new SKColor(0, 255, 0), pixels.GetPixel(0, 0), 0);
        AssertColor(new SKColor(0, 0, 255, 100), pixels.GetPixel(1, 0));
        Assert.Contains(Messages(import), m => m.Contains("索引调色板"));
    }

    [Theory]
    [InlineData(100, "8 位线性", true)]
    [InlineData(200, "16 位线性", true)]
    [InlineData(250, "16 位", true)]
    [InlineData(300, "32 位线性", true)]
    [InlineData(350, "32 位", true)]
    [InlineData(500, "16 位浮点线性", true)]
    [InlineData(550, "16 位浮点", true)]
    [InlineData(600, "32 位浮点线性", true)]
    [InlineData(650, "32 位浮点", true)]
    [InlineData(700, "64 位浮点线性", true)]
    [InlineData(750, "64 位浮点", true)]
    public void Every_precision_converts_to_eight_bit_srgb_and_says_so(uint precision, string name, bool noted)
    {
        var writer = Writer(11, 16, 4, compression: 0);
        writer.Precision = precision;
        var layer = new XcfWriterLayer { Name = "deep", Width = 16, Height = 4, Rgba = Ramp(16, 4) };
        layer.Mask = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
        layer.ApplyMask = true;
        writer.Layers.Add(layer);
        var import = Load(writer);
        var only = Assert.Single(import.Layers);
        // Half floats carry about three decimals, a step or two at the bright end; 8-bit linear has only a few steps for
        // the whole of the shadows, so its darks come back coarsely; the rest are exact to a step.
        AssertPixels(only, layer.Rgba!, 16, precision == 100 ? 8 : precision is 500 or 550 ? 2 : 1);
        for (var i = 0; i < 64; i++) Assert.InRange(only.Mask!.GetPixelSpan()[i % 16 + i / 16 * only.Mask.RowBytes], Math.Max(0, i * 4 - 1), Math.Min(255, i * 4 + 1));
        if (noted) Assert.Contains(Messages(import), m => m.Contains($"{name} 像素") || (precision == 100 && m.Contains("线性光像素")));
    }

    private static byte[] Ramp(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                rgba[i] = (byte)(x * 255 / (width - 1));
                rgba[i + 1] = (byte)(y * 255 / (height - 1));
                rgba[i + 2] = (byte)(255 - x * 255 / (width - 1));
                rgba[i + 3] = (byte)(x < width / 2 ? 255 : 128);
            }
        return rgba;
    }

    [Fact]
    public void The_development_precision_numbers_of_versions_4_to_6_are_read_too()
    {
        foreach (var (version, code, bytes) in new[] { (4, 1u, 2), (5, 400u, 2), (6, 250u, 2), (7, 250u, 2) })
        {
            var writer = Writer(version, 3, 3, compression: 0);
            writer.Precision = code;
            Assert.Equal(bytes, writer.BytesPerSample);
            var layer = new XcfWriterLayer { Name = "v", Width = 3, Height = 3 }.Patterned(4);
            writer.Layers.Add(layer);
            AssertPixels(Assert.Single(Load(writer).Layers), layer.Rgba!, 3, version == 5 ? 2 : 1);
        }
        var unknown = Writer(11, 2, 2);
        unknown.Precision = 999;
        Assert.Contains("精度", Assert.Throws<XcfException>(() => Load(unknown)).Message);
    }

    // ── groups, masks, guides ────────────────────────────────────────────────

    [Fact]
    public void Groups_follow_their_item_paths_with_masks_opacity_and_the_folded_state()
    {
        var writer = Writer(11, 4, 4, compression: 0);
        writer.Layers.Add(new XcfWriterLayer { Name = "Group", Width = 4, Height = 4, IsGroup = true, ItemPath = [0], Expanded = false, Opacity255 = 128, Mask = Enumerable.Repeat((byte)200, 16).ToArray(), ApplyMask = true });
        writer.Layers.Add(new XcfWriterLayer { Name = "Inner", Width = 4, Height = 4, IsGroup = true, ItemPath = [0, 0], Mode = 30 });
        writer.Layers.Add(new XcfWriterLayer { Name = "deep", Width = 4, Height = 4, ItemPath = [0, 0, 0] }.Patterned(2));
        writer.Layers.Add(new XcfWriterLayer { Name = "inside", Width = 4, Height = 4, ItemPath = [0, 1] }.Patterned(3));
        writer.Layers.Add(new XcfWriterLayer { Name = "root", Width = 4, Height = 4, ItemPath = [1] }.Patterned(4));
        var import = Load(writer);
        Assert.Equal(["root", "Group"], import.Layers.Select(l => l.Name));
        var group = import.Layers[1];
        Assert.True(group.IsGroup && group.Collapsed);
        Assert.Equal(128 / 255.0, group.Opacity, 6);
        Assert.NotNull(group.Mask);
        Assert.Equal((4, 4), (group.Mask!.Width, group.Mask.Height));   // A folder's mask covers the document.
        Assert.Equal(200, group.Mask.GetPixelSpan()[0]);
        Assert.Equal(["inside", "Inner"], group.Children.Select(l => l.Name));
        Assert.Equal(["deep"], group.Children[1].Children.Select(l => l.Name));
        Assert.Contains(import.Conversions, c => c.LayerName == "Inner" && c.Message.Contains("文件夹混合模式「正片叠底」"));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Group");

        // A pass-through folder, and a member whose group was never listed, land at the top level.
        var loose = Writer(11, 4, 4, compression: 0);
        loose.Layers.Add(new XcfWriterLayer { Name = "Pass", Width = 4, Height = 4, IsGroup = true, ItemPath = [0], Mode = 61 });
        loose.Layers.Add(new XcfWriterLayer { Name = "orphan", Width = 4, Height = 4, ItemPath = [5, 0] }.Patterned());
        import = Load(loose);
        Assert.Equal(["orphan", "Pass"], import.Layers.Select(l => l.Name));
        Assert.Empty(import.Conversions);
    }

    [Fact]
    public void A_layer_mask_stays_a_mask_and_apply_mask_off_disables_it()
    {
        var writer = Writer(1, 2, 2, compression: 0);
        writer.Layers.Add(new XcfWriterLayer { Name = "masked", Width = 2, Height = 2, Mask = [255, 128, 0, 64], ApplyMask = true }.Filled(SKColors.White));
        writer.Layers.Add(new XcfWriterLayer { Name = "off", Width = 2, Height = 2, Mask = [255, 128, 0, 64], ApplyMask = false }.Filled(SKColors.White));
        var import = Load(writer);
        var masked = import.Layers[1];
        Assert.True(masked.MaskEnabled);
        var mask = masked.Mask!;
        Assert.Equal([255, 128], mask.GetPixelSpan()[..2].ToArray());
        Assert.Equal([0, 64], mask.GetPixelSpan().Slice(mask.RowBytes, 2).ToArray());
        Assert.Equal(255, masked.Pixels!.GetPixel(1, 1).Alpha);   // The pixels keep their own alpha; the mask is the mask.
        Assert.False(import.Layers[0].MaskEnabled);
        Assert.NotNull(import.Layers[0].Mask);
    }

    [Fact]
    public void Guides_and_resolution_come_along_and_a_document_takes_them()
    {
        var writer = Writer(15, 40, 30, compression: 0);
        writer.Guides.AddRange([(10, false), (25, true), (-5, true)]);
        writer.Resolution = 300;
        writer.Layers.Add(new XcfWriterLayer { Name = "L", Width = 40, Height = 30 }.Filled(SKColors.Gray));
        var import = Load(writer);
        Assert.Equal(300, import.Resolution);
        Assert.Equal([(GuideAxis.Horizontal, 10d), (GuideAxis.Vertical, 25d), (GuideAxis.Vertical, -5d)], import.Guides.Select(g => (g.Axis, g.Position)));
        var session = EditorSession.OpenGimp(import, "shot");
        Assert.Equal(3, session.Document.Guides.Count);
        Assert.Equal(300, session.Document.Resolution);
        Assert.Equal("shot", session.Title);
    }

    // ── what is reported ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(28, BlendMode.Normal, null)]
    [InlineData(30, BlendMode.Multiply, null)]
    [InlineData(23, BlendMode.Overlay, null)]
    [InlineData(5, BlendMode.SoftLight, "没有完全对应")]
    [InlineData(46, BlendMode.GrainExtract, null)]
    [InlineData(21, BlendMode.GrainMerge, null)]
    [InlineData(48, BlendMode.VividLight, null)]
    [InlineData(1, BlendMode.Normal, "不受支持")]
    [InlineData(58, BlendMode.Normal, "不受支持")]
    [InlineData(99, BlendMode.Normal, "无法识别")]
    public void Blend_modes_map_where_the_math_agrees_and_are_reported_where_it_does_not(int mode, BlendMode expected, string? note)
    {
        var writer = Writer(11, 2, 2, compression: 0);
        writer.Layers.Add(new XcfWriterLayer { Name = "m", Width = 2, Height = 2, Mode = mode }.Filled(SKColors.Red));
        var import = Load(writer);
        Assert.Equal(expected, Assert.Single(import.Layers).Blend);
        if (note == null) Assert.Empty(import.Conversions);
        else Assert.Contains(Messages(import), m => m.Contains(note));
    }

    [Fact]
    public void Linear_blending_and_other_compositing_are_reported_only_where_they_show()
    {
        var writer = Writer(11, 2, 2, compression: 0);
        writer.Layers.Add(new XcfWriterLayer { Name = "plain", Width = 2, Height = 2, Mode = 28, BlendSpace = -1, CompositeSpace = -1, CompositeMode = -1 }.Filled(SKColors.Red));
        writer.Layers.Add(new XcfWriterLayer { Name = "faded", Width = 2, Height = 2, Mode = 28, Opacity255 = 100, BlendSpace = -1, CompositeSpace = -1 }.Filled(SKColors.Red));
        writer.Layers.Add(new XcfWriterLayer { Name = "lab", Width = 2, Height = 2, Mode = 30, BlendSpace = 3 }.Filled(SKColors.Red));
        writer.Layers.Add(new XcfWriterLayer { Name = "clipped", Width = 2, Height = 2, Mode = 28, CompositeMode = 3 }.Filled(SKColors.Red));
        var import = Load(writer);
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "plain");
        Assert.Contains(import.Conversions, c => c.LayerName == "faded" && c.Message.Contains("线性光"));
        Assert.Contains(import.Conversions, c => c.LayerName == "lab" && c.Message.Contains("LAB"));
        Assert.Contains(import.Conversions, c => c.LayerName == "clipped" && c.Message.Contains("剪贴到图层"));
    }

    [Fact]
    public void Text_effects_vector_and_link_layers_and_profiles_are_reported()
    {
        var writer = Writer(25, 4, 4, compression: 0);
        writer.IccProfile = true;
        writer.Layers.Add(new XcfWriterLayer { Name = "Title", Width = 4, Height = 4, Text = "(text \"Hi\")\n(font \"Sans\")" }.Filled(SKColors.Black));
        writer.Layers.Add(new XcfWriterLayer { Name = "Blurred", Width = 4, Height = 4, Effects = 2 }.Filled(SKColors.Black));
        writer.Layers.Add(new XcfWriterLayer { Name = "Shape", Width = 4, Height = 4, VectorLayer = true }.Filled(SKColors.Black));
        writer.Layers.Add(new XcfWriterLayer { Name = "Linked", Width = 4, Height = 4, LinkLayer = true }.Filled(SKColors.Black));
        var import = Load(writer);
        Assert.Equal(4, import.Layers.Count);
        Assert.Contains(import.Conversions, c => c.LayerName == "Title" && c.Message.Contains("已按像素导入"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Blurred" && c.Message.Contains("2 个效果"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Shape" && c.Message.Contains("矢量图层"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Linked" && c.Message.Contains("链接图层"));
        Assert.Contains(import.Conversions, c => c.LayerName == "图像" && c.Message.Contains("颜色配置文件"));
    }

    [Fact]
    public void A_newer_version_is_read_with_what_is_known_and_says_so()
    {
        var writer = Writer(27, 3, 3, compression: 0);
        var layer = new XcfWriterLayer { Name = "future", Width = 3, Height = 3 }.Patterned();
        writer.Layers.Add(layer);
        var import = Load(writer);
        Assert.Contains(Messages(import), m => m.Contains("格式 27"));
        AssertPixels(Assert.Single(import.Layers), layer.Rgba!, 3);
    }

    // ── damage, limits, budget ───────────────────────────────────────────────

    [Fact]
    public void Truncation_anywhere_is_refused_with_a_message_or_read_as_far_as_it_goes_never_a_crash()
    {
        var writer = Writer(1, 8, 8);
        writer.Layers.Add(new XcfWriterLayer { Name = "L", Width = 8, Height = 8 }.Patterned());
        var full = writer.Build();
        for (var cut = 14; cut < full.Length; cut += 3)
        {
            try { XcfImport.Load(full[..cut]).Discard(); }
            catch (XcfException error) { Assert.False(string.IsNullOrEmpty(error.Message)); }
        }
    }

    [Fact]
    public void A_tile_pointer_outside_the_file_leaves_that_tile_transparent_and_says_so()
    {
        var writer = Writer(1, 4, 4, compression: 0);
        writer.Layers.Add(new XcfWriterLayer { Name = "x", Width = 4, Height = 4, PoisonTilePointer = true }.Patterned());
        var import = Load(writer);
        var only = Assert.Single(import.Layers);
        Assert.Equal(0, only.Pixels!.GetPixel(1, 1).Alpha);
        Assert.Contains(import.Conversions, c => c.LayerName == "x" && c.Message.Contains("无法读取"));
    }

    [Fact]
    public void Lies_about_size_are_refused_as_too_large_and_an_unknown_base_type_as_not_gimp()
    {
        var big = Writer(1, 2, 2).Build();
        big[14] = 0; big[15] = 6; big[16] = 0x1A; big[17] = 0x80;   // 400,000 wide
        Assert.Contains("容纳范围", Assert.Throws<XcfException>(() => XcfImport.Load(big)).Message);
        var type = Writer(1, 2, 2).Build();
        type[25] = 7;
        Assert.Equal("这不是 GIMP 文件。", Assert.Throws<XcfException>(() => XcfImport.Load(type)).Message);
    }

    [Fact]
    public void A_file_past_the_budget_is_cropped_to_the_canvas_and_past_that_refused()
    {
        var writer = Writer(11, 10, 10, compression: 0);
        var wide = new XcfWriterLayer { Name = "wide", Width = 30, Height = 10, X = -10, Y = 0 }.Patterned(2);
        writer.Layers.Add(wide);
        writer.Layers.Add(new XcfWriterLayer { Name = "fits", Width = 10, Height = 10 }.Filled(SKColors.Blue));
        // Room for everything: nothing is cut.
        var whole = Load(writer, 1000);
        Assert.Equal(30, whole.Layers[1].Pixels!.Width);
        Assert.Equal(-10, whole.Layers[1].Transform.X);
        Assert.Empty(whole.Conversions);
        // Room for the canvas twice, not for the wide layer whole: it is cut to the canvas, the pixels inside it kept.
        var cropped = Load(writer, 200);
        var cut = cropped.Layers[1];
        Assert.Equal((10, 10, 0d, 0d), (cut.Pixels!.Width, cut.Pixels.Height, cut.Transform.X, cut.Transform.Y));
        Assert.Contains(cropped.Conversions, c => c.LayerName == "wide" && c.Message.Contains("已裁剪"));
        var expected = wide.Rgba!;
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
            {
                var i = (y * 30 + x + 10) * 4;
                AssertColor(new SKColor(expected[i], expected[i + 1], expected[i + 2], 255), cut.Pixels.GetPixel(x, y));
            }
        // No room even then.
        Assert.Contains("容纳范围", Assert.Throws<XcfException>(() => Load(writer, 150)).Message);
    }

    // ── text ─────────────────────────────────────────────────────────────────

    private const string Parasite = "(text \"Hello\\nWorld\")\n(font \"DejaVu Sans Bold Italic\")\n(font-size 24.000000)\n(font-size-unit points)\n(antialias yes)\n(language \"en-gb\")\n(base-direction ltr)\n(color (color-rgba 1.000000 0.000000 0.000000 1.000000))\n(justify center)\n(box-mode fixed)\n(box-width 200.000000)\n(box-height 80.000000)\n(box-unit pixels)\n(hinting yes)\n(letter-spacing 2.000000)\n(line-spacing 0.000000)\n";

    [Fact]
    public void Text_in_one_style_is_retyped_and_text_with_markup_stays_pixels()
    {
        var writer = Writer(19, 400, 300, compression: 0);
        writer.Resolution = 144;   // 24 points at 144 ppi are 48 pixels.
        writer.Layers.Add(new XcfWriterLayer { Name = "Title", Width = 200, Height = 80, X = 30, Y = 40, Text = Parasite, Opacity255 = 200, Mode = 30 }.Filled(SKColors.Black));
        writer.Layers.Add(new XcfWriterLayer { Name = "Fancy", Width = 50, Height = 20, Text = "(markup \"<markup>a<b>b</b></markup>\")\n(font \"Sans\")\n(font-size 20)\n" }.Filled(SKColors.Black));
        writer.Layers.Add(new XcfWriterLayer { Name = "Broken", Width = 50, Height = 20, Text = "(text \"unclosed" }.Filled(SKColors.Black));
        var import = Load(writer);
        var title = import.Layers[2];
        var style = title.Text!;
        Assert.Equal("Hello\nWorld", style.Text);
        Assert.True(style.Bold && style.Italic, "bold italic");
        Assert.Equal(48, style.Size);
        Assert.Equal(0xFFFF0000u, style.Color);
        Assert.Equal(TextAlignment.Center, style.Alignment);
        Assert.Equal((200d, 80d), (style.BoxWidth, style.BoxHeight));
        Assert.Equal(4, style.Tracking);   // Points too.
        Assert.Equal(0, style.Leading);
        Assert.Equal((30 - (double)Composa.Text.TextLayout.Padding, 40 - (double)Composa.Text.TextLayout.Padding), (title.Transform.X, title.Transform.Y));
        Assert.Equal((200 / 255.0, BlendMode.Multiply), (title.Opacity, title.Blend));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Title" && c.Message.Contains("已按像素导入"));
        Assert.Null(import.Layers[1].Text);
        Assert.Contains(import.Conversions, c => c.LayerName == "Fancy" && c.Message.Contains("混合了多种字体"));
        Assert.Null(import.Layers[0].Text);
        Assert.Contains(import.Conversions, c => c.LayerName == "Broken" && c.Message == XcfText.RasterizedNote);
    }

    [Fact]
    public void A_pango_font_description_splits_into_a_family_and_its_style()
    {
        Assert.Equal(("Liberation Serif", true, false), XcfText.Face("Liberation Serif Bold"));
        Assert.Equal(("Noto Sans", false, true), XcfText.Face("Noto Sans Italic"));
        Assert.Equal(("Nowhere Grotesk", true, true), XcfText.Face("Nowhere Grotesk Semi-Bold Oblique"));
        Assert.Equal((new TextStyle().FontFamily, false, false), XcfText.Face(""));   // A description with no family takes the default's.
        Assert.Equal("Comic", XcfText.Face("Comic 12").Family);   // Pango's trailing size is not part of the family.
        Assert.Equal("Bold", XcfText.Face("Bold").Family);        // A lone style word is a family, since nothing else is.
        Assert.False(XcfText.Face("Sans-serif").Family.Contains("serif", StringComparison.OrdinalIgnoreCase) && !EditorSession.FontFamilies.Contains(XcfText.Face("Sans-serif").Family));
        var parsed = XcfText.Expressions.Parse("(a \"x\\\"y\" 1.5 (b c)) ; comment\n(d)");
        Assert.Equal(2, parsed.Count);
        var first = (List<object>)parsed[0];
        Assert.Equal("x\"y", first[1]);
        Assert.Equal(1.5, first[2]);
        Assert.Equal("c", ((XcfText.Symbol)((List<object>)first[3])[1]).Name);
    }

    // ── compressed files ─────────────────────────────────────────────────────

    [Fact]
    public void A_gzipped_file_opens_and_bzip2_and_xz_are_refused_with_the_way_out()
    {
        var writer = Writer(11, 6, 6);
        var layer = new XcfWriterLayer { Name = "zipped", Width = 6, Height = 6 }.Patterned(5);
        writer.Layers.Add(layer);
        var plain = writer.Build();
        using var packed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(plain);
        var bytes = packed.ToArray();
        Assert.True(XcfImport.IsXcf(bytes));
        AssertPixels(Assert.Single(XcfImport.Load(bytes).Layers), layer.Rgba!, 6);

        var folder = Path.Combine(Path.GetTempPath(), "composa-xcfgz-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var gz = Path.Combine(folder, "picture.xcf.gz");
            File.WriteAllBytes(gz, bytes);
            Assert.True(XcfImport.IsXcf(gz));
            AssertPixels(Assert.Single(XcfImport.Load(gz).Layers), layer.Rgba!, 6);
            // A plain file wearing the .gz name is still known by its signature and opens as it is.
            var notGz = Path.Combine(folder, "plain.xcf.gz");
            File.WriteAllBytes(notGz, plain);
            Assert.True(XcfImport.IsXcf(notGz));
            Assert.Single(XcfImport.Load(notGz).Layers);
            var png = Path.Combine(folder, "other.xcf.gz");
            File.WriteAllBytes(png, [1, 2, 3]);
            Assert.False(XcfImport.IsXcf(png));
            foreach (var (suffix, name) in new[] { (".xcf.bz2", "bzip2"), (".xcf.xz", "xz") })
            {
                var refused = Path.Combine(folder, "deep" + suffix);
                File.WriteAllBytes(refused, [0x42, 0x5A, 0x68]);
                Assert.True(XcfImport.IsXcf(refused));
                var error = Assert.Throws<XcfException>(() => XcfImport.Load(refused));
                Assert.Contains(name, error.Message);
                Assert.Contains(".xcf.gz", error.Message);
            }
            // A cut-off archive unpacks to a cut-off file: refused with a message, or read with what it has and a note.
            var cut = Path.Combine(folder, "cut.xcf.gz");
            File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
            try { Assert.NotEmpty(XcfImport.Load(cut).Conversions); }
            catch (XcfException error) { Assert.NotEmpty(error.Message); }
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    // ── into a session ───────────────────────────────────────────────────────

    [Fact]
    public void Placing_a_gimp_file_into_a_document_folders_its_layers_as_one_step()
    {
        var writer = Writer(11, 20, 20, compression: 0);
        writer.Guides.Add((5, true));
        writer.Layers.Add(new XcfWriterLayer { Name = "A", Width = 10, Height = 10 }.Filled(SKColors.Red));
        writer.Layers.Add(new XcfWriterLayer { Name = "B", Width = 10, Height = 10, X = 10, Y = 10 }.Filled(SKColors.Blue));
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        var folder = session.PlaceGimp(XcfImport.Load(writer.Build()), "sketch", new SKPoint(50, 50));
        Assert.Equal("sketch", folder.Name);
        Assert.Equal(["B", "A"], folder.Children.Select(l => l.Name));
        // The two together span (0,0) to (20,20); centred on (50,50) that box starts at (40,40), so B at (10,10) lands at (50,50).
        Assert.Equal((50d, 50d), (folder.Children[0].Transform.X, folder.Children[0].Transform.Y));
        Assert.Equal((40d, 40d), (folder.Children[1].Transform.X, folder.Children[1].Transform.Y));
        Assert.Equal("导入 GIMP 文件", session.History.UndoName);
        Assert.Empty(session.Document.Guides);                                                      // Placing leaves the file's guides behind.
        session.Undo();
        Assert.Single(session.Document.Layers);
    }
}
