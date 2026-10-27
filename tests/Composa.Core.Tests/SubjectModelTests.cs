// Ported from Lolly (github.com/lolly-tools/lolly, shells/web/src/lib/matter.test.ts and matte-models.test.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.Core.Tests;

public class SubjectModelTests
{
    [Fact]
    public void A_landscape_picture_is_centered_with_padding_above_and_below()
    {
        var plan = Matting.PlanLetterbox(200, 100, 320);
        Assert.Equal(320 / 200.0, plan.Scale);
        Assert.Equal((320, 160), (plan.ContentWidth, plan.ContentHeight));
        Assert.Equal((0, 80), (plan.OffsetX, plan.OffsetY));
    }

    [Fact]
    public void A_square_picture_fills_the_square()
    {
        var plan = Matting.PlanLetterbox(512, 512, 1024);
        Assert.Equal((1024, 1024, 0, 0), (plan.ContentWidth, plan.ContentHeight, plan.OffsetX, plan.OffsetY));
    }

    [Fact]
    public void Modnet_is_normalized_to_minus_one_to_one_and_u2net_to_imagenet()
    {
        // The footgun both ported tests pin: a wrong mean or std does not crash, it quietly ruins the matte.
        var modnet = SubjectModels.ModNet;
        Assert.Equal([0.5f, 0.5f, 0.5f], modnet.Mean);
        Assert.Equal([0.5f, 0.5f, 0.5f], modnet.Std);
        var grey = Matting.PackNchwNormalized([128, 128, 128, 255], 1, 4, modnet);
        Assert.Equal((128 / 255f - 0.5f) / 0.5f, grey[0], 1e-6f);
        var white = Matting.PackNchwNormalized([255, 255, 255, 255], 1, 4, modnet);
        Assert.Equal(1f, white[0], 1e-6f);

        var red = Matting.PackNchwNormalized([255, 0, 0, 255], 1, 4, SubjectModels.U2NetP);
        Assert.Equal((1 - 0.485f) / 0.229f, red[0], 1e-6f);
        Assert.Equal(-0.456f / 0.224f, red[1], 1e-6f);
        Assert.Equal(-0.406f / 0.225f, red[2], 1e-6f);
    }

    [Fact]
    public void Planes_are_all_red_then_all_green_then_all_blue_and_the_row_stride_is_honoured()
    {
        var model = SubjectModels.U2NetP with { Mean = [0f, 0f, 0f], Std = [1f, 1f, 1f] };
        // Two pixels per row, a stride of twelve bytes: the four at the end of each row are padding.
        byte[] rgba = [10, 20, 30, 255, 40, 50, 60, 255, 9, 9, 9, 9, 70, 80, 90, 255, 100, 110, 120, 255, 9, 9, 9, 9];
        var t = Matting.PackNchwNormalized(rgba, 2, 12, model);
        Assert.Equal(12, t.Length);
        Assert.Equal([10 / 255f, 40 / 255f, 70 / 255f, 100 / 255f], t[..4]);
        Assert.Equal([20 / 255f, 50 / 255f, 80 / 255f, 110 / 255f], t[4..8]);
        Assert.Equal([30 / 255f, 60 / 255f, 90 / 255f, 120 / 255f], t[8..]);
    }

    [Fact]
    public void Minmax_stretches_a_bounded_head_and_a_flat_head_is_all_backdrop()
    {
        var m = Matting.ActivateMask([0.2f, 0.4f, 0.6f, 0.8f], MaskActivation.MinMax);
        Assert.Equal(0f, m[0]);
        Assert.Equal(1f, m[3]);
        Assert.Equal(1 / 3f, m[1], 1e-6f);
        Assert.Equal([0f, 0f, 0f], Matting.ActivateMask([0.5f, 0.5f, 0.5f], MaskActivation.MinMax));
    }

    [Fact]
    public void Sigmoid_squashes_logits()
    {
        var m = Matting.ActivateMask([0f, 8f, -8f], MaskActivation.Sigmoid);
        Assert.Equal(0.5f, m[0], 1e-6f);
        Assert.True(m[1] > 0.999f);
        Assert.True(m[2] < 0.001f);
    }

    [Fact]
    public void Both_shipped_heads_are_bounded_so_both_are_minmax()
    {
        // The sigmoid branch stays implemented and tested although nothing uses it: the next logit-head model
        // would otherwise be minmaxed silently, which washes the matte with no crash.
        Assert.Equal(MaskActivation.MinMax, SubjectModels.U2NetP.Activation);
        Assert.Equal(MaskActivation.MinMax, SubjectModels.ModNet.Activation);
    }

    [Fact]
    public void Unpadding_cuts_the_content_out_of_the_square_and_clamps()
    {
        var plan = Matting.PlanLetterbox(4, 2, 4); // Content 4×2 at offset (0, 1).
        var square = new float[16];
        for (var i = 0; i < 16; i++) square[i] = i / 10f - 0.3f;
        var content = Matting.UnpadMask(square, plan);
        Assert.Equal(8, content.Length);
        Assert.Equal(square[4], content[0]);
        Assert.Equal(square[11], content[7]);
        Assert.Equal(0f, Matting.UnpadMask([-1f, -1f, -1f, -1f], Matting.PlanLetterbox(2, 2, 2))[0]);
        Assert.Equal(1f, Matting.UnpadMask([2f, 2f, 2f, 2f], Matting.PlanLetterbox(2, 2, 2))[3]);
    }

    [Fact]
    public void Every_model_has_a_file_a_size_a_hash_and_a_permissive_licence()
    {
        Assert.Equal(2, SubjectModels.All.Count);
        foreach (var model in SubjectModels.All)
        {
            Assert.EndsWith(".onnx", model.File);
            Assert.True(model.Bytes > 1_000_000);
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.Contains(model.Licence, new[] { "Apache-2.0", "MIT", "BSD-3-Clause" });
            Assert.StartsWith("https://", model.Url);
            Assert.Equal(3, model.Mean.Length);
            Assert.Equal(3, model.Std.Length);
            Assert.Equal(0, model.InputSize % 32);
        }
        Assert.Equal(SubjectModels.U2NetP, SubjectModels.Find("u2netp"));
        Assert.Null(SubjectModels.Find("rmbg"));
    }

    [Fact]
    public void The_bundled_model_is_installed_and_is_the_file_the_catalog_names()
    {
        // u2netp.onnx is in git and copied beside the binaries, so every build carries it.
        Assert.True(SubjectModels.U2NetP.IsInstalled, SubjectModels.U2NetP.Path);
        Assert.True(SubjectModels.U2NetP.Verify());
        Assert.False((SubjectModels.U2NetP with { Sha256 = new string('0', 64) }).Verify());
        Assert.False((SubjectModels.U2NetP with { Bytes = 1 }).Verify());
        Assert.False((SubjectModels.U2NetP with { File = "nothing-here.onnx" }).Verify());
    }

    [Fact]
    public void A_swapped_or_damaged_file_is_refused_rather_than_run()
    {
        var swapped = SubjectModels.U2NetP with { Id = "swapped", Sha256 = new string('f', 64) };
        var error = Assert.Throws<InvalidDataException>(() => ModelRunner.Run(swapped, new float[3 * 320 * 320], 320));
        Assert.Contains("不是 Composa 构建时所用的", error.Message);
        var missing = SubjectModels.U2NetP with { Id = "missing", File = "absent.onnx" };
        Assert.Contains("未安装", Assert.Throws<InvalidDataException>(() => ModelRunner.Run(missing, new float[3 * 320 * 320], 320)).Message);
    }

    [Fact]
    public void The_box_filter_averages_over_clipped_windows_and_resampling_keeps_a_constant_field()
    {
        var field = new float[5 * 5];
        field[12] = 25f; // One bright pixel in the middle.
        var box = GuidedFilter.Box(field, 5, 5, 1);
        Assert.Equal(25f / 9, box[12], 1e-5f);
        Assert.Equal(25f / 9, box[6], 1e-5f);  // Diagonal neighbour: a full 3×3 window that holds the pixel.
        Assert.Equal(0f, box[0]);              // The corner window (2×2) does not reach it.
        var constant = Enumerable.Repeat(0.7f, 9).ToArray();
        Assert.All(GuidedFilter.Box(constant, 3, 3, 1), v => Assert.Equal(0.7f, v, 1e-6f));
        Assert.All(GuidedFilter.Resample(constant, 3, 3, 10, 7), v => Assert.Equal(0.7f, v, 1e-6f));
        Assert.Same(constant, GuidedFilter.Resample(constant, 3, 3, 3, 3));
    }

    [Fact]
    public void Upsampling_follows_the_pictures_edge_instead_of_blurring_the_mattes()
    {
        // A dark left half and a bright right half meeting at x = 130 of 200; the coarse matte is a blurry version
        // of the same split on a 10-wide grid. The result must change sides where the picture does, within a pixel
        // or two, not over the dozen pixels a plain scale-up would smear it across.
        using var guide = Pixels.NewColor(200, 100);
        for (var y = 0; y < 100; y++) for (var x = 0; x < 200; x++) guide.SetPixel(x, y, x < 130 ? new SKColor(30, 30, 30) : new SKColor(230, 230, 230));
        var coarse = new float[10 * 5];
        for (var y = 0; y < 5; y++) for (var x = 0; x < 10; x++) coarse[y * 10 + x] = x < 6 ? 0.1f : x == 6 ? 0.5f : 0.95f;
        using var matte = GuidedFilter.Upsample(coarse, 10, 5, guide);
        Assert.Equal((200, 100), (matte.Width, matte.Height));
        var row = Enumerable.Range(0, 200).Select(x => (int)matte.GetPixel(x, 50).Alpha).ToArray();
        Assert.True(row[120] < 60, $"left of the edge: {row[120]}");
        Assert.True(row[140] > 190, $"right of the edge: {row[140]}");
        Assert.True(row[131] - row[128] > 100, $"the step is sharp: {row[128]} to {row[131]}");
    }

    [Fact]
    public void Transparent_pixels_are_backdrop_whatever_the_matte_says()
    {
        using var guide = Pixels.NewColor(40, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 40; x++) if (x >= 20) guide.SetPixel(x, y, SKColors.White);
        var coarse = Enumerable.Repeat(1f, 4 * 4).ToArray();
        using var matte = GuidedFilter.Upsample(coarse, 4, 4, guide);
        Assert.Equal(0, matte.GetPixel(5, 20).Alpha);
        Assert.True(matte.GetPixel(35, 20).Alpha > 240);
    }

    [Fact]
    public void U2net_finds_a_bright_subject_on_a_dark_ground_and_leaves_transparency_alone()
    {
        // A red disc on a dark, noisy ground, with a transparent band along the bottom. Not a photo, but a shape the
        // saliency net finds every time, and that pins the whole pipeline: letterbox, normalization, run,
        // activation, unpadding and the guided upsampling back to 400×300.
        using var scene = Scene(400, 300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var matte = SubjectMatting.Matte(scene, SubjectModels.U2NetP);
        watch.Stop();
        Assert.Equal((400, 300), (matte.Width, matte.Height));
        Assert.True(matte.GetPixel(200, 130).Alpha > 200, $"centre {matte.GetPixel(200, 130).Alpha}");
        Assert.True(matte.GetPixel(20, 20).Alpha < 40, $"corner {matte.GetPixel(20, 20).Alpha}");
        Assert.Equal(0, matte.GetPixel(200, 290).Alpha);
        var bounds = SelectionMask.Bounds(matte, 128);
        Assert.InRange(bounds.Left, 120, 150);
        Assert.InRange(bounds.Right, 250, 280);
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"took {watch.ElapsedMilliseconds} ms");

        // The same picture through the finder, and the finder's fallback when a model is not there.
        using var found = SubjectFinder.Matte(scene, SubjectDetect.Any);
        Assert.NotNull(found);
        Assert.True(found!.GetPixel(200, 130).Alpha > 200);
        Assert.Equal(SubjectDetect.Backdrop, SubjectFinder.Resolve(SubjectDetect.Backdrop));
        Assert.Null(SubjectFinder.FallbackReason(SubjectDetect.Backdrop));
        Assert.Equal(SubjectDetect.Any, SubjectFinder.Resolve(SubjectDetect.Any));
    }

    [Fact]
    public void Modnet_runs_when_it_is_installed_and_falls_back_when_it_is_not()
    {
        if (!SubjectModels.ModNet.IsInstalled)
        {
            Assert.Equal(SubjectDetect.Backdrop, SubjectFinder.Resolve(SubjectDetect.Person));
            Assert.Contains("未安装", SubjectFinder.FallbackReason(SubjectDetect.Person));
            Assert.Contains("modnet.onnx", SubjectFinder.FallbackReason(SubjectDetect.Person));
            return; // Run scripts/models/fetch.sh to exercise the model itself.
        }
        Assert.True(SubjectModels.ModNet.Verify());
        using var scene = Scene(300, 400);
        using var matte = SubjectMatting.Matte(scene, SubjectModels.ModNet);
        Assert.Equal((300, 400), (matte.Width, matte.Height));
        Assert.Equal(0, matte.GetPixel(150, 395).Alpha);
    }

    [Fact]
    public void A_cancelled_run_stops_without_a_result()
    {
        using var scene = Scene(200, 150);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SubjectMatting.Matte(scene, SubjectModels.U2NetP, cancelled.Token));
    }

    /// <summary>A red disc in the middle of a dark, noisy ground, transparent along the bottom twentieth.</summary>
    private static SKBitmap Scene(int width, int height)
    {
        var bitmap = Pixels.NewColor(width, height);
        var random = new Random(7);
        int cx = width / 2, cy = (int)(height * 0.43), r = Math.Min(width, height) / 4;
        var opaqueTo = height - height / 20;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (y >= opaqueTo) continue;
            var inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r;
            var n = (byte)random.Next(0, 24);
            bitmap.SetPixel(x, y, inside ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        return bitmap;
    }
}
