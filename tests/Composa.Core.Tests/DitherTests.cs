using Composa.Filters;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>The Dither filter: pixels quantized or marked, in two colors or the picture's own.</summary>
public class DitherTests
{
    private static SKBitmap Dithered(SKBitmap source, DitherSettings settings) =>
        ImageFilters.Run(source, new FilterSettings { Kind = FilterKind.Dither, Dither = settings }).Result;

    private static double WhiteShare(SKBitmap bitmap, SKRectI? area = null)
    {
        var rect = area ?? new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        long white = 0, count = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
            for (var x = rect.Left; x < rect.Right; x++) { count++; if (bitmap.GetPixel(x, y).Red > 127) white++; }
        return (double)white / count;
    }

    private static HashSet<SKColor> Colors(SKBitmap bitmap)
    {
        var colors = new HashSet<SKColor>();
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) colors.Add(bitmap.GetPixel(x, y));
        return colors;
    }

    [Fact]
    public void Diffusion_turns_mid_gray_into_a_balanced_mix_of_black_and_white()
    {
        using var gray = Solid(64, 64, new SKColor(128, 128, 128));
        foreach (var style in new[] { DitherStyle.Atkinson, DitherStyle.FloydSteinberg })
        {
            using var result = Dithered(gray, new DitherSettings { Style = style, PixelSize = 1 });
            Assert.Subset(new HashSet<SKColor> { SKColors.Black, SKColors.White }, Colors(result));
            var white = WhiteShare(result);
            Assert.InRange(white, 0.3, 0.7);
        }
        // More tones keep the gray itself; no diffusion leaves everything at the nearest tone (black, below the midpoint).
        using var toned = Dithered(gray, new DitherSettings { Style = DitherStyle.Atkinson, PixelSize = 1, Levels = 3 });
        Assert.All(Colors(toned), c => Assert.Equal(c.Red, c.Green));
        Assert.Contains(Colors(toned), c => c.Red is > 100 and < 156);
        using var flat = Dithered(gray, new DitherSettings { Style = DitherStyle.FloydSteinberg, PixelSize = 1, Diffusion = 0 });
        Assert.Single(Colors(flat));
    }

    [Fact]
    public void Ordered_dithering_follows_the_tone_and_density_darkens_it()
    {
        using var gradient = Gradient(64, 16); // Dark on the left, light on the right.
        using var result = Dithered(gradient, new DitherSettings { Style = DitherStyle.Bayer8, PixelSize = 1 });
        Assert.Subset(new HashSet<SKColor> { SKColors.Black, SKColors.White }, Colors(result));
        Assert.True(WhiteShare(result, new SKRectI(0, 0, 16, 16)) < WhiteShare(result, new SKRectI(48, 0, 64, 16)));
        using var gray = Solid(64, 64, new SKColor(160, 160, 160));
        using var plain = Dithered(gray, new DitherSettings { Style = DitherStyle.Bayer4, PixelSize = 1 });
        using var dense = Dithered(gray, new DitherSettings { Style = DitherStyle.Bayer4, PixelSize = 1, Density = 80 });
        using var punchy = Dithered(gray, new DitherSettings { Style = DitherStyle.Bayer4, PixelSize = 1, Contrast = 80 });
        Assert.True(WhiteShare(dense) < WhiteShare(plain));
        Assert.True(WhiteShare(punchy) > WhiteShare(plain)); // 160 is above mid gray, so contrast pushes it lighter.
        // Bayer 2 × 2 on mid gray is a checkerboard-like pattern that repeats every two pixels.
        using var mid = Solid(8, 8, new SKColor(128, 128, 128));
        using var bayer2 = Dithered(mid, new DitherSettings { Style = DitherStyle.Bayer2, PixelSize = 1 });
        for (var y = 0; y < 6; y++) for (var x = 0; x < 6; x++) Assert.Equal(bayer2.GetPixel(x, y), bayer2.GetPixel(x + 2, y + 2));
        Assert.Equal(2, Colors(bayer2).Count);
    }

    [Fact]
    public void Two_colors_and_original_colors_replace_black_and_white()
    {
        using var gray = Solid(32, 32, new SKColor(128, 128, 128));
        using var two = Dithered(gray, new DitherSettings { Style = DitherStyle.Atkinson, PixelSize = 1, Colors = DitherColors.TwoColors, Dark = 0xFF200040, Light = 0xFFFFE080 });
        Assert.Equal(new HashSet<SKColor> { new(0x20, 0x00, 0x40), new(0xFF, 0xE0, 0x80) }, Colors(two));
        // Original colors dither each channel on its own: a dark red becomes pure red and black.
        using var red = Solid(32, 32, new SKColor(140, 0, 0));
        using var original = Dithered(red, new DitherSettings { Style = DitherStyle.Bayer8, PixelSize = 1, Colors = DitherColors.Original });
        Assert.Subset(new HashSet<SKColor> { SKColors.Black, SKColors.Red }, Colors(original));
        Assert.Contains(SKColors.Red, Colors(original));
        // Halftone marks in Original mode take the pixel's own color on black.
        using var dots = Dithered(red, new DitherSettings { Style = DitherStyle.HalftoneDots, PixelSize = 1, Colors = DitherColors.Original, CellSize = 8 });
        Assert.Subset(new HashSet<SKColor> { SKColors.Black, new(140, 0, 0) }, Colors(dots));
        Assert.Equal(2, Colors(dots).Count);
    }

    [Fact]
    public void Chunky_pixels_are_uniform_blocks_and_dots_leave_dark_gaps()
    {
        using var gradient = Gradient(64, 32);
        using var square = Dithered(gradient, new DitherSettings { Style = DitherStyle.Atkinson, PixelSize = 4 });
        for (var y = 0; y < 32; y += 4)
            for (var x = 0; x < 64; x += 4)
            {
                var first = square.GetPixel(x, y);
                for (var dy = 0; dy < 4; dy++) for (var dx = 0; dx < 4; dx++) Assert.Equal(first, square.GetPixel(x + dx, y + dy));
            }
        using var white = Solid(16, 16, SKColors.White);
        using var dots = Dithered(white, new DitherSettings { Style = DitherStyle.Bayer8, PixelSize = 8, PixelShape = DitherPixelShape.Dot });
        Assert.Equal(SKColors.Black, dots.GetPixel(0, 0));      // The corner of a block is the gap.
        Assert.Equal(SKColors.White, dots.GetPixel(4, 4));      // Its middle is the lit pixel.
        using var picked = Dithered(white, new DitherSettings { Style = DitherStyle.Bayer8, PixelSize = 8, PixelShape = DitherPixelShape.Dot, Colors = DitherColors.TwoColors, Dark = 0xFF102030, Light = 0xFFFFFFFF });
        Assert.Equal(new SKColor(0x10, 0x20, 0x30), picked.GetPixel(0, 0));
    }

    [Fact]
    public void Halftone_marks_grow_with_the_tone_and_light_on_dark_swaps_the_ink()
    {
        using var light = Solid(48, 48, new SKColor(200, 200, 200));
        using var dark = Solid(48, 48, new SKColor(60, 60, 60));
        foreach (var style in new[] { DitherStyle.HalftoneDots, DitherStyle.HalftoneLines, DitherStyle.HalftoneDiamonds, DitherStyle.MacPatterns })
        {
            // Light on dark: the marks are light and there are more of them the lighter the tone; on light, the reverse reads the same.
            using var lit = Dithered(light, new DitherSettings { Style = style, PixelSize = 1, CellSize = 8 });
            using var dim = Dithered(dark, new DitherSettings { Style = style, PixelSize = 1, CellSize = 8 });
            Assert.True(WhiteShare(lit) > WhiteShare(dim), style.ToString());
            Assert.Subset(new HashSet<SKColor> { SKColors.Black, SKColors.White }, Colors(lit));
            using var onLight = Dithered(light, new DitherSettings { Style = style, PixelSize = 1, CellSize = 8, LightOnDark = false });
            Assert.True(WhiteShare(onLight) > 0.5, style.ToString());
        }
        using var black = Solid(16, 16, SKColors.Black);
        using var none = Dithered(black, new DitherSettings { Style = DitherStyle.HalftoneDots, PixelSize = 1 });
        Assert.Equal(new HashSet<SKColor> { SKColors.Black }, Colors(none)); // No light tone, no marks.
    }

    [Fact]
    public void Ascii_lays_the_picture_out_in_cells_of_text()
    {
        var glyphs = DitherPixels.Glyphs("@ .", 14);
        Assert.Equal(14, glyphs.Height);
        Assert.InRange(glyphs.Width, 5, 14);
        Assert.Equal(3, glyphs.Coverage.Length);
        Assert.Equal(0, glyphs.Coverage[0]);                            // The space has no ink,
        Assert.True(glyphs.Coverage[1] < glyphs.Coverage[2]);           // the period less than the at sign.
        using var white = Solid(64, 42, SKColors.White);
        using var text = Dithered(white, new DitherSettings { Style = DitherStyle.Ascii, TextSize = 14, Characters = "@ ." });
        // A uniform picture gets the same character in every cell, so the result repeats cell by cell.
        for (var y = 0; y < 28; y++) for (var x = 0; x < 64 - glyphs.Width; x++) Assert.Equal(text.GetPixel(x, y), text.GetPixel(x, y + 14));
        for (var y = 0; y < 42; y++) for (var x = 0; x < 64 - glyphs.Width; x++) Assert.Equal(text.GetPixel(x, y), text.GetPixel(x + glyphs.Width, y));
        Assert.InRange(WhiteShare(text), 0.05, 0.6);                    // The at sign, light on dark.
        using var black = Solid(64, 42, SKColors.Black);
        using var blank = Dithered(black, new DitherSettings { Style = DitherStyle.Ascii, TextSize = 14, Characters = "@ ." });
        Assert.Equal(0, WhiteShare(blank));                             // The space.
    }

    [Fact]
    public void Alpha_is_kept_and_transparent_pixels_are_left_alone()
    {
        using var source = Solid(16, 16, new SKColor(128, 128, 128));
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(128, 128, 128, 128), BlendMode = SKBlendMode.Src };
            canvas.DrawRect(new SKRect(0, 0, 8, 16), paint);
        }
        foreach (var style in new[] { DitherStyle.Atkinson, DitherStyle.Bayer4, DitherStyle.HalftoneDots, DitherStyle.MacPatterns })
        {
            using var result = Dithered(source, new DitherSettings { Style = style, PixelSize = 1 });
            for (var y = 0; y < 16; y++)
            {
                Assert.Equal(0u, (uint)result.GetPixel(12, y));
                Assert.Equal(128, result.GetPixel(3, y).Alpha);
            }
        }
    }

    [Fact]
    public void Settings_are_clamped_and_named()
    {
        var wild = new DitherSettings { PixelSize = 99, CellSize = 1, TextSize = 1000, Angle = 400, Levels = 1, Diffusion = -5, Density = 250, Contrast = double.NaN, Dark = 0x00123456, Characters = "a\nb\r" + new string('x', 100) };
        var clean = wild.Normalized();
        Assert.Equal((DitherSettings.MaxPixelSize, DitherSettings.MinCellSize, DitherSettings.MaxTextSize, 90d, DitherSettings.MinLevels), (clean.PixelSize, clean.CellSize, clean.TextSize, clean.Angle, clean.Levels));
        Assert.Equal((0d, 100d, 0d, 0xFF123456u), (clean.Diffusion, clean.Density, clean.Contrast, clean.Dark));
        Assert.Equal(DitherSettings.MaxCharacters, clean.Characters.Length);
        Assert.DoesNotContain('\n', clean.Characters);
        Assert.True(new DitherSettings { Style = DitherStyle.Atkinson }.Diffuses);
        Assert.True(new DitherSettings { Style = DitherStyle.Bayer4 }.HasTones);
        Assert.True(new DitherSettings { Style = DitherStyle.HalftoneLines }.IsHalftone);
        Assert.True(new DitherSettings { Style = DitherStyle.Ascii }.DrawsMarks);
        Assert.True(DitherSettings.TryParseStyle("floyd_steinberg", out var floyd) && floyd == DitherStyle.FloydSteinberg);
        Assert.True(DitherSettings.TryParseStyle("Halftone Dots", out var dots) && dots == DitherStyle.HalftoneDots);
        Assert.True(DitherSettings.TryParseStyle("atkinson", out var atkinson) && atkinson == DitherStyle.Atkinson);
        Assert.False(DitherSettings.TryParseStyle("blurry", out _));
        Assert.Equal(10, DitherSettings.Groups.Sum(g => g.Count));
        Assert.Equal("抖动", FilterSettings.DisplayName(FilterKind.Dither));
    }
}
