using Composa.IO.Xcf;
using Composa.Model;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>
/// Files GIMP 3.2.6 itself wrote (<c>Fixtures/Xcf/make-fixtures.py</c>), so the reader is held to what GIMP saves and
/// not only to the test project's own writer: its precisions, its folders and masks, a text layer in the GIMP 3 form,
/// a layer with an effect, and 3.2's vector and link layers.
/// </summary>
public class XcfFixtureTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xcf", name);

    private static XcfImport Load(string name) => XcfImport.Load(Fixture(name));

    private static List<string> Messages(XcfImport import) => import.Conversions.Select(c => c.Message).ToList();

    [Theory]
    [InlineData("basic-8bit.xcf", false)]
    [InlineData("deep-16bit.xcf", true)]
    [InlineData("deep-float-linear.xcf", true)]
    public void The_basic_document_reads_the_same_in_every_precision(string name, bool deep)
    {
        var import = Load(name);
        Assert.Equal((64, 48, 144d), (import.Width, import.Height, import.Resolution));
        Assert.Equal(["Background", "Red square", "Half blue"], import.Layers.Select(l => l.Name));
        AssertColor(new SKColor(0xF9, 0xF3, 0xE7), import.Layers[0].Pixels!.GetPixel(32, 24));
        var red = import.Layers[1];
        Assert.Equal((10d, 8d, 20, 20), (red.Transform.X, red.Transform.Y, red.Pixels!.Width, red.Pixels.Height));
        AssertColor(new SKColor(0xED, 0x7C, 0x6C), red.Pixels.GetPixel(10, 10));
        var blue = import.Layers[2];
        Assert.Equal((BlendMode.Multiply, false), (blue.Blend, blue.Visible));
        Assert.Equal(0.6, blue.Opacity, 2);
        AssertColor(new SKColor(0x59, 0x95, 0xF3), blue.Pixels!.GetPixel(12, 8));
        Assert.Equal(deep, Messages(import).Any(m => m.Contains("损失了部分精度")));
        // GIMP picks Clip to backdrop for Multiply itself; only its linear blending is worth a line, and only on the Multiply layer.
        Assert.Contains(import.Conversions, c => c.LayerName == "Half blue" && c.Message.Contains("线性光"));
        Assert.DoesNotContain(import.Conversions, c => c.Message.Contains("剪贴到背景"));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Red square");
    }

    [Fact]
    public void Grayscale_and_indexed_files_become_rgb()
    {
        var gray = Load("gray.xcf");
        AssertColor(new SKColor(0x9E, 0x9E, 0x9E), gray.Layers[1].Pixels!.GetPixel(10, 10));
        Assert.DoesNotContain(gray.Conversions, c => c.Message.Contains("调色板"));
        var indexed = Load("indexed.xcf");
        Assert.Equal(["Background", "Dot"], indexed.Layers.Select(l => l.Name));
        AssertColor(new SKColor(0x7C, 0xCB, 0x95), indexed.Layers[0].Pixels!.GetPixel(5, 5));
        AssertColor(new SKColor(0xF3, 0xF3, 0x59), indexed.Layers[1].Pixels!.GetPixel(5, 5));
        Assert.Contains(Messages(indexed), m => m.Contains("索引调色板"));
    }

    [Fact]
    public void Folders_masks_guides_text_and_a_path_come_across_as_they_should()
    {
        var import = Load("groups-masks-text.xcf");
        Assert.Equal((96, 64), (import.Width, import.Height));
        Assert.Equal([(GuideAxis.Vertical, 48d), (GuideAxis.Horizontal, 16d)], import.Guides.Select(g => (g.Axis, g.Position)));
        Assert.Equal(["Background", "Group", "Masked", "Grain", "Hello GIMP"], import.Layers.Select(l => l.Name));
        var group = import.Layers[1];
        Assert.True(group.IsGroup && group.Collapsed, "a folded folder");
        Assert.Equal(["Inside", "Nested"], group.Children.Select(l => l.Name));
        Assert.Equal(BlendMode.Screen, group.Children[0].Blend);
        var deep = Assert.Single(group.Children[1].Children);
        Assert.Equal(("Deep", 0.5, 60d, 30d), (deep.Name, deep.Opacity, deep.Transform.X, deep.Transform.Y));
        var masked = import.Layers[2];
        Assert.True(masked.MaskEnabled);
        Assert.Equal((30, 30), (masked.Mask!.Width, masked.Mask.Height));
        Assert.Equal(0, masked.Mask.GetPixelSpan()[5]);                                   // The left half was painted black,
        Assert.Equal(255, masked.Mask.GetPixelSpan()[25]);                                // the right half left white.
        Assert.Equal(BlendMode.GrainMerge, import.Layers[3].Blend);
        // GIMP's mode, exactly: the only note left on that layer is that GIMP blends it in linear light.
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Grain" && c.Message.Contains("没有完全对应"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Grain" && c.Message.Contains("线性光"));
        Assert.Contains(Messages(import), m => m.Contains("路径被舍弃"));

        // GIMP 3 names the font inside a GimpFont form and writes the color as linear floats.
        var text = import.Layers[4];
        var style = text.Text!;
        Assert.Equal("Hello GIMP", style.Text);
        Assert.Equal("DejaVu Sans", style.FontFamily);
        Assert.True(style.Bold && !style.Italic, "bold, not italic");
        Assert.Equal(18, style.Size);
        var color = new SKColor(style.Color);
        Assert.InRange(color.Red, 85, 93);      // 0.1 linear is about 0.35 in sRGB,
        Assert.InRange(color.Blue, 184, 192);   // 0.5 linear about 0.74.
        Assert.Equal(TextAlignment.Left, style.Alignment);
        Assert.Null(style.BoxWidth);
        Assert.Equal((4 - (double)Composa.Text.TextLayout.Padding, 40 - (double)Composa.Text.TextLayout.Padding), (text.Transform.X, text.Transform.Y));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Hello GIMP" && c.Message.Contains("已按像素导入"));
    }

    [Fact]
    public void A_layer_with_an_effect_keeps_its_own_pixels_and_the_effect_is_reported()
    {
        // The square has a 6-pixel Gaussian blur as a GIMP 3 effect; the stored pixels are sharp to the edge, so the
        // effect is not in them, and the layer comes in as GIMP would show it with the effect switched off.
        var import = Load("effect.xcf");
        var square = import.Layers[1];
        Assert.Equal("Sharp square", square.Name);
        AssertColor(SKColors.Black, square.Pixels!.GetPixel(0, 12), 0);
        AssertColor(SKColors.Black, square.Pixels.GetPixel(23, 23), 0);
        Assert.Contains(import.Conversions, c => c.LayerName == "Sharp square" && c.Message.Contains("GIMP 滤镜"));
    }

    [Fact]
    public void Vector_and_link_layers_of_gimp_3_2_arrive_as_pixels_and_say_so()
    {
        var vector = Load("vector-layer.xcf");
        Assert.Equal(["Background", "Shape"], vector.Layers.Select(l => l.Name));
        Assert.NotNull(vector.Layers[1].Pixels);
        Assert.Contains(vector.Conversions, c => c.LayerName == "Shape" && c.Message.Contains("矢量图层"));

        // GIMP 3.2.6 declares the link property four bytes short; the reader steps over that and reads the layer.
        var link = Load("link-layer.xcf");
        Assert.Equal(["Background", "linked.png"], link.Layers.Select(l => l.Name));
        var linked = link.Layers[1];
        Assert.Equal((16, 16), (linked.Pixels!.Width, linked.Pixels.Height));
        AssertColor(new SKColor(0x95, 0xE7, 0x95), linked.Pixels.GetPixel(8, 8), 4);   // 0.3 and 0.8 linear, as GIMP's color setter takes them.
        Assert.Contains(link.Conversions, c => c.LayerName == "linked.png" && c.Message.Contains("链接图层"));
    }
}
