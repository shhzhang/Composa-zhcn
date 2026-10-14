using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>The Painterly filter: strokes that come from the picture, so the result is still that picture.</summary>
public class PainterlyTests
{
    private static SKBitmap Paint(SKBitmap source, PainterlySettings? settings = null, uint seed = 7) =>
        ImageFilters.Run(source, new FilterSettings { Kind = FilterKind.Painterly, Painterly = settings ?? new(), Seed = seed }).Result;

    private static (double Coverage, SKColor Mean) Sample(SKBitmap bitmap, SKRectI area)
    {
        long r = 0, g = 0, b = 0, opaque = 0, count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = bitmap.GetPixel(x, y);
                count++;
                if (p.Alpha < 250) continue;
                opaque++; r += p.Red; g += p.Green; b += p.Blue;
            }
        return opaque == 0 ? (0, SKColors.Transparent) : ((double)opaque / count, new SKColor((byte)(r / opaque), (byte)(g / opaque), (byte)(b / opaque)));
    }

    [Fact]
    public void A_flat_picture_is_covered_in_its_own_color()
    {
        using var source = Solid(120, 90, new SKColor(200, 60, 30));
        using var result = Paint(source, new PainterlySettings { BrushSize = 16 });
        var (coverage, mean) = Sample(result, new SKRectI(10, 10, 110, 80));
        Assert.True(coverage > 0.97, $"only {coverage:P0} of the middle was painted");
        AssertColor(new SKColor(200, 60, 30), mean, 4);
    }

    [Fact]
    public void Each_side_keeps_its_color_and_the_strokes_follow_the_edge_between_them()
    {
        using var source = Pixels.NewColor(160, 120);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(new SKColor(30, 60, 200));
            using var red = new SKPaint { Color = new SKColor(220, 40, 40) };
            canvas.DrawRect(0, 0, 80, 120, red);
        }
        using var result = Paint(source, new PainterlySettings { BrushSize = 12, Passes = 3 });
        var (leftCoverage, left) = Sample(result, new SKRectI(8, 8, 60, 112));
        var (rightCoverage, right) = Sample(result, new SKRectI(100, 8, 152, 112));
        Assert.True(leftCoverage > 0.95 && rightCoverage > 0.95, $"coverage {leftCoverage:P0} / {rightCoverage:P0}");
        Assert.True(left.Red > 180 && left.Blue < 90, $"left side {left} should be red");
        Assert.True(right.Blue > 160 && right.Red < 90, $"right side {right} should be blue");
        // The smallest brush repaints the boundary, so it stays where it was to within a brush.
        var (_, edge) = Sample(result, new SKRectI(84, 8, 96, 112));
        Assert.True(edge.Blue > edge.Red, $"just right of the edge {edge} should already be blue");
    }

    [Fact]
    public void Transparent_pixels_get_no_strokes()
    {
        using var source = Pixels.NewColor(100, 100);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.Green })
            canvas.DrawRect(0, 0, 100, 50, paint);
        using var result = Paint(source, new PainterlySettings { BrushSize = 10 });
        var (topCoverage, _) = Sample(result, new SKRectI(5, 5, 95, 40));
        var (bottomCoverage, _) = Sample(result, new SKRectI(5, 65, 95, 95));
        Assert.True(topCoverage > 0.95, $"the picture was painted {topCoverage:P0}");
        Assert.Equal(0, bottomCoverage);
    }

    [Fact]
    public void The_same_seed_paints_the_same_strokes_and_another_seed_others()
    {
        using var source = Gradient(80, 60);
        using var first = Paint(source, seed: 3);
        using var again = Paint(source, seed: 3);
        using var other = Paint(source, seed: 4);
        Assert.Equal(first.Bytes, again.Bytes);
        Assert.NotEqual(first.Bytes, other.Bytes);
    }

    [Fact]
    public void Every_style_paints_and_the_brush_fits_a_small_picture_by_itself()
    {
        using var source = Gradient(40, 30);
        foreach (var style in Enum.GetValues<PainterlyStyle>())
        {
            using var result = Paint(source, new PainterlySettings { Style = style });
            var (coverage, _) = Sample(result, new SKRectI(4, 4, 36, 26));
            Assert.True(coverage > 0.5, $"{style} painted only {coverage:P0}");
        }
    }

    [Fact]
    public void The_filter_is_one_undoable_step_on_the_layer()
    {
        var session = EditorSession.NewCanvas(60, 40, new SKColor(10, 200, 10));
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.Painterly, Painterly = new PainterlySettings { BrushSize = 8 } });
        Assert.Equal("绘画", session.History.UndoName);
        var (coverage, mean) = Sample(session.ActiveLayer!.Pixels!, new SKRectI(6, 6, 54, 34));
        Assert.True(coverage > 0.95);
        AssertColor(new SKColor(10, 200, 10), mean, 4);
    }
}
