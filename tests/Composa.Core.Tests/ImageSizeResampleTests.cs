using Composa.Editing;
using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>Image Size's Resample choice: nearest-neighbour blocks, and the model's results handed in beforehand.</summary>
public class ImageSizeResampleTests
{
    private static SKBitmap PixelArt()
    {
        var art = Pixels.NewColor(4, 3);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Magenta, SKColors.Cyan, SKColors.Black, SKColors.White, SKColors.Gray, SKColors.Orange, SKColors.Purple, SKColors.Brown };
        for (var y = 0; y < 3; y++) for (var x = 0; x < 4; x++) art.SetPixel(x, y, colors[y * 4 + x]);
        return art;
    }

    [Fact]
    public void Nearest_neighbour_turns_every_pixel_into_an_exact_block()
    {
        var session = EditorSession.NewCanvas(4, 3, SKColors.Transparent);
        using var art = PixelArt();
        session.AddImageLayer("art", Pixels.Clone(art), new SKPoint(2, 1.5f));
        session.ResizeImage(40, 30, mode: ResampleMode.Nearest);
        var pixels = session.ActiveLayer!.Pixels!;
        Assert.Equal((40, 30), (pixels.Width, pixels.Height));
        for (var y = 0; y < 30; y++) for (var x = 0; x < 40; x++)
            Assert.True(art.GetPixel(x / 10, y / 10) == pixels.GetPixel(x, y), $"at {x},{y}");
        Assert.Equal("图像大小", session.History.UndoName);

        // Automatic smooths the same picture: a block edge is a blend, not a step.
        session.Undo();
        session.ResizeImage(40, 30);
        var smooth = session.ActiveLayer!.Pixels!;
        Assert.NotEqual(smooth.GetPixel(9, 5), smooth.GetPixel(10, 5));
        Assert.True(smooth.GetPixel(10, 5) != SKColors.Lime || smooth.GetPixel(9, 5) != SKColors.Red);
    }

    [Fact]
    public void Prepared_pixels_are_taken_only_while_the_layer_still_shows_what_they_were_made_from()
    {
        var session = EditorSession.NewCanvas(20, 10, SKColors.Transparent);
        session.AddImageLayer("photo", TestImages.Gradient(20, 10), new SKPoint(10, 5));
        var layer = session.ActiveLayer!;
        var marker = Pixels.NewColor(40, 20);
        marker.Erase(SKColors.Magenta);
        var enhanced = new EnhancedLayers(new Dictionary<Guid, (SKBitmap, SKBitmap)> { [layer.Id] = (layer.Pixels!, marker) });
        session.ResizeImage(40, 20, mode: ResampleMode.Enhance, enhanced: enhanced);
        Assert.Same(marker, session.ActiveLayer!.Pixels);
        Assert.Equal(SKColors.Magenta, session.ActiveLayer.Pixels!.GetPixel(5, 5));
        enhanced.DisposeUnused(session.Document); // The marker is in use, so it must survive this.
        Assert.Equal(SKColors.Magenta, session.ActiveLayer.Pixels!.GetPixel(5, 5));

        // The layer changed since the preparation: the stale result is ignored and the plain path runs.
        session.Undo();
        var stale = Pixels.NewColor(40, 20);
        var staleSet = new EnhancedLayers(new Dictionary<Guid, (SKBitmap, SKBitmap)> { [session.ActiveLayer!.Id] = (Pixels.Clone(session.ActiveLayer.Pixels!), stale) });
        session.ResizeImage(40, 20, mode: ResampleMode.Enhance, enhanced: staleSet);
        Assert.NotSame(stale, session.ActiveLayer!.Pixels);
        Assert.Equal((40, 20), (session.ActiveLayer.Pixels!.Width, session.ActiveLayer.Pixels.Height));
    }

    [Fact]
    public async Task Preparing_runs_the_model_over_the_raster_layers_that_grow_and_resize_uses_the_results()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Transparent);
        using var photo = Pixels.NewColor(60, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) photo.SetPixel(x, y, x < 30 ? new SKColor(200, 60, 40) : new SKColor(40, 80, 200));
        session.AddImageLayer("photo", Pixels.Clone(photo), new SKPoint(30, 20));
        // A turned layer is placed, not resampled, when the scale is even, so the model has nothing to do for it.
        session.AddImageLayer("turned", Pixels.NewColor(8, 8), new SKPoint(10, 10));
        session.Apply("turn", () => session.ActiveLayer!.Transform = session.ActiveLayer.Transform with { Rotation = 15 });
        var reports = new List<(int Done, int Total)>();
        var enhanced = await session.PrepareEnhancedAsync(150, 100, new SyncProgress(reports.Add));
        Assert.Equal(2, enhanced.Count); // The photo and the canvas's own Background layer; not the turned one.
        Assert.Equal((0, 2), reports.First());
        Assert.Equal((2, 2), reports.Last());
        var photoLayer = session.Document.AllLayers().Single(l => l.Name == "photo");
        var prepared = enhanced.For(photoLayer);
        Assert.NotNull(prepared);
        Assert.Equal((150, 100), (prepared!.Width, prepared.Height)); // Four times, then fitted to 2.5 times.
        session.ResizeImage(150, 100, mode: ResampleMode.Enhance, enhanced: enhanced);
        photoLayer = session.Document.AllLayers().Single(l => l.Name == "photo");
        Assert.Same(prepared, photoLayer.Pixels);
        var left = photoLayer.Pixels!.GetPixel(30, 50); var right = photoLayer.Pixels.GetPixel(120, 50);
        Assert.True(left.Red > 150 && right.Blue > 150, $"{left} {right}");
        Assert.Equal((150, 100), (session.Document.Width, session.Document.Height));
        enhanced.DisposeUnused(session.Document);
        session.Undo();
        Assert.Equal((60, 40), (session.Document.Width, session.Document.Height));

        // Shrinking prepares nothing; a cancelled preparation returns nothing and changes nothing.
        Assert.Equal(0, (await session.PrepareEnhancedAsync(30, 20)).Count);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareEnhancedAsync(150, 100, null, cancelled.Token));
        Assert.Equal((60, 40), (session.Document.Width, session.Document.Height));
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }
}
