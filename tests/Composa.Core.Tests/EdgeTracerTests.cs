using Composa.Editing;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>Edges traced for drawing by hand, and strokes painted many at a time.</summary>
public class EdgeTracerTests
{
    private static SKBitmap SquareOnWhite()
    {
        var bitmap = Solid(100, 100, SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(30, 30, 40, 40, paint);
        Pixels.Invalidate(bitmap);
        return bitmap;
    }

    [Fact]
    public void A_square_traces_as_one_outline_with_its_corners()
    {
        using var source = SquareOnWhite();
        var edges = EdgeTracer.Trace(source, detail: 50, minLength: 20, simplify: 1.5);
        var outline = Assert.Single(edges);
        Assert.InRange(outline.Count, 4, 12);                                              // Four corners, perhaps the closing point and a kink or two.
        float left = outline.Min(p => p.X), right = outline.Max(p => p.X), top = outline.Min(p => p.Y), bottom = outline.Max(p => p.Y);
        Assert.InRange(left, 27, 33); Assert.InRange(right, 67, 73); Assert.InRange(top, 27, 33); Assert.InRange(bottom, 67, 73);
    }

    [Fact]
    public void A_flat_picture_has_no_edges_and_a_region_limits_the_search()
    {
        using var flat = Solid(60, 60, SKColors.Gray);
        Assert.Empty(EdgeTracer.Trace(flat));
        using var source = SquareOnWhite();
        var left = EdgeTracer.Trace(source, minLength: 10, region: new SKRectI(0, 0, 50, 100));
        Assert.NotEmpty(left);
        Assert.All(left.SelectMany(e => e), p => Assert.InRange(p.X, 0, 50));
        Assert.True(left.SelectMany(e => e).Any(p => p.X > 28), "the square's left side lies inside the region");
    }

    [Fact]
    public void Faint_edges_need_more_detail_and_scraps_are_dropped()
    {
        using var source = Solid(100, 100, new SKColor(128, 128, 128));
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = new SKColor(150, 150, 150) })
            canvas.DrawRect(30, 30, 40, 40, paint);
        Pixels.Invalidate(source);
        Assert.Empty(EdgeTracer.Trace(source, detail: 0));
        Assert.NotEmpty(EdgeTracer.Trace(source, detail: 100));
        Assert.Empty(EdgeTracer.Trace(source, detail: 100, minLength: 1000));
    }

    [Fact]
    public void Many_strokes_paint_as_one_undoable_step_and_stop_at_the_first_that_cannot()
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        var brush = new BrushSettings { Size = 10, Hardness = 1 };
        var (painted, problem) = session.PaintStrokes(
        [
            new PlannedStroke([new SKPoint(10, 20), new SKPoint(90, 20)], brush, SKColors.Red),
            new PlannedStroke([new SKPoint(10, 50), new SKPoint(90, 50)], brush, SKColors.Blue),
            new PlannedStroke([new SKPoint(10, 80)], brush, SKColors.Lime)
        ]);
        Assert.Null(problem);
        Assert.Equal(3, painted);
        AssertColor(SKColors.Red, session.Composite().GetPixel(50, 20));
        AssertColor(SKColors.Blue, session.Composite().GetPixel(50, 50));
        AssertColor(SKColors.Lime, session.Composite().GetPixel(10, 80));
        Assert.Equal("画笔笔触", session.History.UndoName);
        session.Undo();
        AssertColor(SKColors.White, session.Composite().GetPixel(50, 20));
        AssertColor(SKColors.White, session.Composite().GetPixel(50, 50));
        Assert.False(session.CanUndo);

        session.AddText(new SKPoint(10, 10), session.TextDefaults with { Text = "Live" });
        var (before, refused) = session.PaintStrokes([new PlannedStroke([new SKPoint(10, 20)], brush, SKColors.Red)]);
        Assert.Equal(0, before);
        Assert.Contains("文字", refused);
    }
}
