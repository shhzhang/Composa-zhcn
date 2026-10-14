using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>The Object Selection tool's dragged box: the model looks at the box alone.</summary>
public class ObjectBoxTests
{
    /// <summary>A wide dark scene with a small red disc at (520, 90), radius 18: a few pixels to a whole-picture matte, a whole picture to a box.</summary>
    private static EditorSession Scene()
    {
        var session = EditorSession.NewCanvas(640, 200, SKColors.Transparent);
        var photo = Pixels.NewColor(640, 200);
        var random = new Random(11);
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 640; x++)
        {
            var inside = (x - 520) * (x - 520) + (y - 90) * (y - 90) < 18 * 18;
            var n = (byte)random.Next(0, 24);
            photo.SetPixel(x, y, inside ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        session.AddImageLayer("photo", photo, new SKPoint(320, 100));
        session.Detect = SubjectDetect.Any;
        return session;
    }

    [Fact]
    public async Task A_box_around_a_small_object_selects_it_and_nothing_outside_the_box()
    {
        var session = Scene();
        await session.SelectObjectInBoxAsync(new SKRectI(470, 40, 570, 140));
        Assert.NotNull(session.Selection);
        Assert.Equal("对象选择", session.History.UndoName);
        var bounds = SelectionMask.Bounds(session.Selection!, 128);
        Assert.InRange(bounds.Left, 490, 510);
        Assert.InRange(bounds.Right, 530, 550);
        Assert.InRange(bounds.Top, 60, 80);
        Assert.InRange(bounds.Bottom, 100, 120);
        Assert.Equal(0, session.Selection!.GetPixel(100, 100).Alpha);
        Assert.Equal(0, session.Selection.GetPixel(469, 90).Alpha); // Nothing outside the box, whatever the model would say there.
    }

    [Fact]
    public async Task A_box_with_nothing_in_it_deselects_and_a_tiny_or_outside_box_does_nothing()
    {
        var session = Scene();
        session.SelectAll();
        await session.SelectObjectInBoxAsync(new SKRectI(700, 0, 800, 100)); // Entirely off the canvas.
        Assert.NotNull(session.Selection);
        await session.SelectObjectInBoxAsync(new SKRectI(10, 10, 11, 11));
        Assert.NotNull(session.Selection);
        await session.SelectObjectInBoxAsync(new SKRectI(20, 20, 220, 180), SelectionMode.Add); // Plain noise: nothing stands out, and Add keeps what was there.
        Assert.NotNull(session.Selection);
        Assert.Equal("全选", session.History.UndoName);
    }

    [Fact]
    public async Task A_document_that_changes_while_the_model_runs_is_left_alone_and_cancel_stops_it()
    {
        var session = Scene();
        var running = session.SelectObjectInBoxAsync(new SKRectI(470, 40, 570, 140));
        session.AddImageLayer("late", Pixels.NewColor(4, 4), new SKPoint(2, 2));
        await running;
        Assert.Null(session.Selection);
        Assert.Equal("添加图像", session.History.UndoName);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SelectObjectInBoxAsync(new SKRectI(470, 40, 570, 140), SelectionMode.Replace, cancelled.Token));
        Assert.Null(session.Selection);
    }

    [Fact]
    public async Task The_plain_backdrop_method_works_in_a_box_too()
    {
        var session = EditorSession.NewCanvas(300, 200, SKColors.White);
        session.AddImageLayer("dot", TestImages.Solid(20, 20, SKColors.Blue), new SKPoint(250, 50));
        session.Detect = SubjectDetect.Backdrop;
        session.SampleAllLayers = true;
        await session.SelectObjectInBoxAsync(new SKRectI(220, 20, 280, 80));
        Assert.Equal(new SKRectI(240, 40, 260, 60), SelectionMask.Bounds(session.Selection!, 128));
    }
}
