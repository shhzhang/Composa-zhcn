using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>The session's Select Subject, Object Selection and Remove Background through the models, on a scene U²-Net finds every time.</summary>
public class SubjectSessionTests
{
    /// <summary>A 320×240 canvas with a dark noisy photo layer carrying a red disc at (160, 100), radius 60.</summary>
    private static EditorSession Scene(out SKBitmap photo)
    {
        var session = EditorSession.NewCanvas(320, 240, SKColors.Transparent);
        photo = Pixels.NewColor(320, 240);
        var random = new Random(3);
        for (var y = 0; y < 240; y++)
        for (var x = 0; x < 320; x++)
        {
            var inside = (x - 160) * (x - 160) + (y - 100) * (y - 100) < 60 * 60;
            var n = (byte)random.Next(0, 24);
            photo.SetPixel(x, y, inside ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        session.AddImageLayer("photo", photo, new SKPoint(160, 120)); // Centred on the canvas, so the layer covers it.
        return session;
    }

    [Fact]
    public async Task Select_subject_with_the_model_selects_the_disc_and_the_matte_is_reused()
    {
        var session = Scene(out _);
        session.Detect = SubjectDetect.Any;
        Assert.True(await session.SelectSubjectAsync());
        Assert.Equal("选择主体", session.History.UndoName);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.True(session.Selection.GetPixel(10, 10).Alpha < 40, $"corner {session.Selection.GetPixel(10, 10).Alpha}");
        var bounds = SelectionMask.Bounds(session.Selection, 128);
        Assert.InRange(bounds.Left, 85, 115);
        Assert.InRange(bounds.Right, 205, 235);

        // The same picture again costs no model run: the cached matte answers, and an object click over the whole picture shares it.
        var first = await session.FindSubjectAsync(wholePicture: true);
        Assert.Same(first, await session.FindSubjectAsync(wholePicture: true));
        session.SampleAllLayers = true;
        Assert.Same(first, await session.FindSubjectAsync());
        session.Deselect();
        await session.SelectObjectAsync(160, 100);
        Assert.Equal("对象选择", session.History.UndoName);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
        Assert.True(session.Selection.GetPixel(10, 10).Alpha == 0);
        await session.SelectObjectAsync(10, 10); // The backdrop: nothing to select.
        Assert.Null(session.Selection);
        session.ObjectEdgeOffset = -4;
        await session.SelectObjectAsync(160, 100);
        Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
    }

    [Fact]
    public async Task A_document_that_changes_while_the_model_runs_throws_the_result_away()
    {
        var session = Scene(out _);
        session.Detect = SubjectDetect.Any;
        var running = session.SelectSubjectAsync();
        session.AddImageLayer("late", Pixels.NewColor(4, 4), new SKPoint(0, 0));
        Assert.False(await running);
        Assert.Null(session.Selection);
        Assert.Equal("添加图像", session.History.UndoName);
    }

    [Fact]
    public async Task A_cancelled_selection_commits_nothing()
    {
        var session = Scene(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SelectSubjectAsync(SelectionMode.Replace, cancelled.Token));
        Assert.Null(session.Selection);
        Assert.False(session.CanUndo && session.History.UndoName == "选择主体");
    }

    [Fact]
    public async Task Remove_background_with_the_model_adds_a_mask_and_the_plain_method_erases()
    {
        var session = Scene(out var photo);
        var layer = session.ActiveLayer!;
        Assert.True(await session.ApplyRemoveBackgroundAsync(new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Any }));
        Assert.Equal("移除背景", session.History.UndoName);
        Assert.Same(photo, layer.Pixels); // The pixels are untouched; the mask does the hiding.
        Assert.NotNull(layer.Mask);
        Assert.True(layer.MaskEnabled);
        Assert.True(layer.Mask!.GetPixel(160, 100).Alpha > 200);
        Assert.True(layer.Mask.GetPixel(10, 10).Alpha < 40);
        session.Undo();
        layer = session.ActiveLayer!; // Undo restores a snapshot, whose layers are other objects.
        Assert.Null(layer.Mask);

        // An existing mask is met, not replaced: what it hid stays hidden.
        session.AddMask(layer);
        var existing = Pixels.Clone(layer.Mask!);
        using (var canvas = new SKCanvas(existing)) canvas.DrawRect(0, 0, 160, 240, new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src });
        session.Apply("half", () => layer.Mask = existing);
        session.EditingMask = false;
        Assert.True(await session.ApplyRemoveBackgroundAsync(new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Any }));
        Assert.Equal(0, layer.Mask!.GetPixel(120, 100).Alpha);
        Assert.True(layer.Mask.GetPixel(200, 100).Alpha > 200);
        Assert.True(layer.Mask.GetPixel(300, 20).Alpha < 40);

        // The plain backdrop erases pixels; the disc has no plain backdrop, so a near-empty tolerance removes little.
        session.Undo(); session.Undo(); session.Undo();
        layer = session.ActiveLayer!;
        Assert.Null(layer.Mask);
        Assert.True(await session.ApplyRemoveBackgroundAsync(new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Backdrop, Amount = 60 }));
        Assert.Null(layer.Mask);
        Assert.NotSame(photo, layer.Pixels);
        Assert.Equal(0, layer.Pixels!.GetPixel(5, 5).Alpha);
        Assert.Equal(255, layer.Pixels.GetPixel(160, 100).Alpha);
    }

    [Fact]
    public async Task Remove_background_on_a_layer_with_no_subject_changes_nothing()
    {
        var session = EditorSession.NewCanvas(200, 200, SKColors.Transparent);
        session.AddImageLayer("flat", TestImages.Solid(200, 200, new SKColor(90, 90, 90)), new SKPoint(0, 0));
        var before = session.History.CurrentId;
        Assert.False(await session.ApplyRemoveBackgroundAsync(new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Any }));
        Assert.Equal(before, session.History.CurrentId);
        Assert.Null(session.ActiveLayer!.Mask);
        Assert.False(session.IsPreviewing);
    }

    [Fact]
    public async Task The_preview_switches_between_a_mask_and_erased_pixels_and_cancel_restores_both()
    {
        var session = Scene(out var photo);
        var layer = session.ActiveLayer!;
        Assert.True(session.BeginFilter(FilterKind.RemoveBackground));
        var matte = await session.FindLayerSubjectAsync(SubjectDetect.Any);
        Assert.NotNull(matte);
        session.PreviewRemoveBackground(matte);
        Assert.NotNull(layer.Mask);
        Assert.Same(photo, layer.Pixels);
        session.PreviewRemoveBackground(null);
        session.PreviewFilter(new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Backdrop, Amount = 60 });
        Assert.Null(layer.Mask);
        Assert.NotSame(photo, layer.Pixels);
        session.PreviewRemoveBackground(matte);
        Assert.Same(photo, layer.Pixels);
        Assert.NotNull(layer.Mask);
        session.CancelPreview();
        layer = session.ActiveLayer!; // Cancel restores a snapshot, whose layers are other objects.
        Assert.Null(layer.Mask);
        Assert.Same(photo, layer.Pixels);
        Assert.False(session.CanUndo && session.History.UndoName == "移除背景");
    }
}
