using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>Layer > Enhance Resolution: a layer placed small and scaled up gets pixels for its size on the canvas and keeps its place.</summary>
public class EnhanceResolutionTests
{
    private static (EditorSession Session, Layer Layer) PlacedSmall(double shownWidth, double shownHeight, double rotation = 0)
    {
        var session = EditorSession.NewCanvas(400, 300, SKColors.White);
        var small = Pixels.NewColor(40, 30);
        for (var y = 0; y < 30; y++) for (var x = 0; x < 40; x++) small.SetPixel(x, y, x < 20 ? new SKColor(200, 60, 40) : new SKColor(40, 80, 200));
        var layer = session.AddImageLayer("logo", small, new SKPoint(200, 150));
        session.Apply("place", () => layer.Transform = layer.Transform with { X = 100, Y = 75, Width = shownWidth, Height = shownHeight, Rotation = rotation });
        return (session, session.ActiveLayer!);
    }

    [Fact]
    public void Only_a_raster_layer_shown_larger_than_its_pixels_can_be_enhanced()
    {
        var (session, layer) = PlacedSmall(200, 150);
        Assert.True(session.CanEnhanceResolution(layer));
        Assert.Equal(new SKSizeI(160, 120), EditorSession.EnhancedResolutionSize(layer)); // Four times is the most it gives.
        session.Apply("shrink", () => layer.Transform = layer.Transform with { Width = 40, Height = 30 });
        Assert.False(session.CanEnhanceResolution(session.ActiveLayer));
        session.Apply("a little", () => session.ActiveLayer!.Transform = session.ActiveLayer.Transform with { Width = 100, Height = 75 });
        Assert.Equal(new SKSizeI(100, 75), EditorSession.EnhancedResolutionSize(session.ActiveLayer!)); // Its size on the canvas, exactly.
        Assert.False(session.CanEnhanceResolution(null));
        Assert.False(session.CanEnhanceResolution(session.Document.Layers[0])); // The background is shown at its own size.
    }

    [Fact]
    public async Task The_layer_takes_the_pixels_and_keeps_its_place_and_becomes_a_plain_placement_again()
    {
        var (session, layer) = PlacedSmall(100, 75);
        var boundsBefore = layer.Bounds;
        var enhanced = await session.PrepareEnhancedResolutionAsync(layer);
        Assert.Equal(1, enhanced.Count);
        Assert.True(session.EnhanceResolution(layer, enhanced));
        enhanced.DisposeUnused(session.Document);
        Assert.Equal("增强分辨率", session.History.UndoName);
        var after = session.ActiveLayer!;
        Assert.Equal((100, 75), (after.Pixels!.Width, after.Pixels.Height));
        Assert.Equal(boundsBefore, after.Bounds);
        Assert.True(after.Transform.IsPureTranslation(100, 75));
        var left = after.Pixels.GetPixel(20, 40); var right = after.Pixels.GetPixel(80, 40);
        Assert.True(left.Red > 150 && right.Blue > 150, $"{left} {right}");
        session.Undo();
        Assert.Equal((40, 30), (session.ActiveLayer!.Pixels!.Width, session.ActiveLayer.Pixels.Height));
        Assert.Equal(boundsBefore, session.ActiveLayer.Bounds);
    }

    [Fact]
    public async Task A_turned_layer_keeps_its_turn_and_its_mask_follows_the_new_pixels()
    {
        var (session, layer) = PlacedSmall(120, 90, rotation: 20);
        session.AddMask(layer);
        session.EditingMask = false;
        var enhanced = await session.PrepareEnhancedResolutionAsync(layer);
        Assert.True(session.EnhanceResolution(layer, enhanced));
        var after = session.ActiveLayer!;
        Assert.Equal((120, 90), (after.Pixels!.Width, after.Pixels.Height));
        Assert.Equal((120, 90), (after.Mask!.Width, after.Mask.Height));
        Assert.Equal(20, after.Transform.Rotation);
        Assert.Equal((100d, 75d, 120d, 90d), (after.Transform.X, after.Transform.Y, after.Transform.Width, after.Transform.Height));
    }

    [Fact]
    public async Task A_layer_that_changed_while_the_model_ran_is_left_alone()
    {
        var (session, layer) = PlacedSmall(100, 75);
        var enhanced = await session.PrepareEnhancedResolutionAsync(layer);
        session.Apply("repaint", () => layer.Pixels = Pixels.NewColor(40, 30));
        var state = session.History.CurrentId;
        Assert.False(session.EnhanceResolution(session.ActiveLayer!, enhanced));
        Assert.Equal(state, session.History.CurrentId);
        enhanced.DisposeUnused(session.Document);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareEnhancedResolutionAsync(session.ActiveLayer!, null, cancelled.Token));
        Assert.Equal(0, (await session.PrepareEnhancedResolutionAsync(session.Document.Layers[0])).Count); // Nothing to do for the background.
    }
}
