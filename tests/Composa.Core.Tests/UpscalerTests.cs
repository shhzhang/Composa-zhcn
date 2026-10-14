// Ported from Lolly (github.com/lolly-tools/lolly, shells/web/src/lib/upscale-models.test.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Rendering;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class UpscalerTests
{
    /// <summary>A stand-in model: four-times nearest neighbour on 0 to 1 planes, which tiling must reproduce exactly.</summary>
    private static float[] NearestFour(float[] planes, int width, int height, CancellationToken _)
    {
        int ow = width * 4, oh = height * 4;
        var output = new float[ow * oh * 3];
        for (var c = 0; c < 3; c++)
        for (var y = 0; y < oh; y++)
        for (var x = 0; x < ow; x++)
            output[c * ow * oh + y * ow + x] = planes[c * width * height + (y / 4) * width + x / 4];
        return output;
    }

    [Fact]
    public void Tiles_cover_the_picture_once_with_overlapping_windows_clipped_at_the_edges()
    {
        var tiles = Upscaling.PlanTiles(600, 300, 256, 16);
        Assert.Equal(6, tiles.Count); // 3 across, 2 down.
        Assert.Equal(new TilePlan(0, 0, 256, 256, 0, 0, 272, 272), tiles[0]);
        Assert.Equal(new TilePlan(256, 0, 256, 256, 240, 0, 288, 272), tiles[1]);
        Assert.Equal(new TilePlan(512, 256, 88, 44, 496, 240, 104, 60), tiles[5]);
        Assert.Equal(600 * 300, tiles.Sum(t => t.CoreWidth * t.CoreHeight));
        Assert.Single(Upscaling.PlanTiles(10, 10));
    }

    [Fact]
    public void Planes_become_rgb_bytes_clamped_and_cropped()
    {
        // A 2×2 plane set: red ramps, green all 1.2 (clamps to 255), blue negative (clamps to 0).
        float[] planes = [0f, 0.5f, 1f, 0.25f, 1.2f, 1.2f, 1.2f, 1.2f, -1f, -1f, -1f, -1f];
        var destination = new byte[4 * 3 * 2];
        Upscaling.PlanesToRgb(planes, 2, 2, 1, 0, 1, 2, destination, 12, 2, 0);
        Assert.Equal([128, 255, 0], destination[6..9]);   // (1,0) placed at destination x 2, row 0
        Assert.Equal([64, 255, 0], destination[18..21]);  // (1,1) at row 1
    }

    [Fact]
    public void Tiled_enlarging_equals_untiled_exactly_and_keeps_alpha_apart()
    {
        // 300×140: two tiles across, overlap clipped at the left edge, odd remainder on the right; a gradient so any
        // misplaced core shows, and a transparent band that the model never sees.
        using var source = Pixels.NewColor(300, 140);
        for (var y = 0; y < 140; y++) for (var x = 0; x < 300; x++)
            if (y < 120) source.SetPixel(x, y, new SKColor((byte)(x * 255 / 299), (byte)(y * 2), (byte)((x + y) % 256)));
        using var tiled = Upscaler.Enlarge(source, 4, NearestFour);
        Assert.Equal((1200, 560), (tiled.Width, tiled.Height));
        for (var y = 0; y < 480; y += 7) for (var x = 0; x < 1200; x += 13)
        {
            var expected = source.GetPixel(x / 4, y / 4);
            var actual = tiled.GetPixel(x, y);
            Assert.True(Math.Abs(expected.Red - actual.Red) <= 1 && Math.Abs(expected.Green - actual.Green) <= 1 && Math.Abs(expected.Blue - actual.Blue) <= 1, $"at {x},{y}: {expected} vs {actual}");
            Assert.Equal(255, actual.Alpha);
        }
        Assert.Equal(0, tiled.GetPixel(600, 540).Alpha);
    }

    [Fact]
    public void Transparent_pixels_take_a_neighbours_color_before_the_model_sees_them()
    {
        // A red square with transparent surroundings. Without spreading, the window's transparent pixels would be
        // black and a model would darken the square's edge; with it, what the model sees at the edge is red.
        using var source = Pixels.NewColor(40, 40);
        for (var y = 10; y < 30; y++) for (var x = 10; x < 30; x++) source.SetPixel(x, y, SKColors.Red);
        float[]? seen = null;
        using var result = Upscaler.Enlarge(source, 4, (planes, w, h, ct) => { seen = planes; return NearestFour(planes, w, h, ct); });
        Assert.NotNull(seen);
        var page = 40 * 40;
        Assert.Equal(1f, seen![5 * 40 + 9]);          // Five pixels out: red spread there.
        Assert.Equal(1f, seen[8 * 40 + 20]);          // Two above the square.
        Assert.Equal(0f, seen[page + 20 * 40 + 20]);  // Green of the square itself.
        Assert.Equal(1f, seen[0]);                    // The far corner: the average color, red.
        // The result's alpha follows the source: inside opaque red, outside clear.
        Assert.Equal(new SKColor(255, 0, 0, 255), result.GetPixel(80, 80));
        Assert.Equal(0, result.GetPixel(10, 10).Alpha);
    }

    [Fact]
    public void The_general_model_is_installed_and_enlarges_a_small_picture_four_times()
    {
        Assert.True(UpscaleModels.General.IsInstalled, UpscaleModels.General.Path);
        Assert.True(UpscaleModels.General.Verify());
        Assert.True(UpscaleModels.IsAvailable);
        Assert.Null(UpscaleModels.UnavailableReason);
        Assert.Equal("BSD-3-Clause", UpscaleModels.General.Licence);
        Assert.Equal(4, UpscaleModels.General.Scale);

        using var source = Pixels.NewColor(48, 32);
        for (var y = 0; y < 32; y++) for (var x = 0; x < 48; x++) source.SetPixel(x, y, x < 24 ? new SKColor(200, 60, 40) : new SKColor(40, 80, 200));
        var reports = new List<(int, int)>();
        var progress = new Progress<(int Done, int Total)>();
        using var result = Upscaler.Enlarge(source, UpscaleModels.General, new SyncProgress(reports.Add));
        Assert.Equal((192, 128), (result.Width, result.Height));
        Assert.Equal([(0, 1), (1, 1)], reports);
        // The colors survive the trip through the model: left still red, right still blue, edges opaque.
        var left = result.GetPixel(40, 64); var right = result.GetPixel(150, 64);
        Assert.True(left.Red > 150 && left.Blue < 100, left.ToString());
        Assert.True(right.Blue > 150 && right.Red < 100, right.ToString());
        Assert.Equal(255, result.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void A_cancelled_enlargement_stops_before_its_first_tile()
    {
        using var source = Pixels.NewColor(40, 40);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Upscaler.Enlarge(source, 4, NearestFour, null, cancelled.Token));
    }

    [Fact]
    public void A_picture_whose_enlargement_would_pass_the_surface_ceiling_is_refused_before_running()
    {
        using var large = Pixels.NewColor(5000, 3000); // 15 megapixels, 240 at four times, over the 200 ceiling.
        var error = Assert.Throws<InvalidOperationException>(() => Upscaler.Enlarge(large, 4, NearestFour));
        Assert.Contains($"{Model.DocumentLimits.MaxSurfaceMegapixels} 百万像素", error.Message);
    }

    private sealed class SyncProgress(Action<(int, int)> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value);
    }
}
