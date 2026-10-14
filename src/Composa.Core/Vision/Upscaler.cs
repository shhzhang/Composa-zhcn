// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/upscale.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>
/// Enlarges a picture by a model's multiple, tile by tile so memory stays bounded whatever the size. The model sees
/// straight RGB, so a transparent pixel, which has no color of its own, is given the color of its nearest neighbours
/// first, or the model would darken every soft edge; the alpha is enlarged on its own and put back afterwards. The
/// same input always gives the same output: tiles run one after another on a fixed number of threads.
/// </summary>
public static class Upscaler
{
    /// <summary>What a model does to one tile's window: plane-major 0 to 1 RGB in, the same out at the multiple. Tests pass a stand-in.</summary>
    public delegate float[] TileFunction(float[] planes, int width, int height, CancellationToken cancellation);

    /// <summary>
    /// The picture at <paramref name="model"/>'s multiple, premultiplied. <paramref name="progress"/> reports tiles
    /// done and tiles in all. Safe off the UI thread on a committed bitmap.
    /// </summary>
    public static SKBitmap Enlarge(SKBitmap source, UpscaleModel model, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default) =>
        Enlarge(source, model.Scale, (planes, w, h, ct) => ModelRunner.RunImage(model, planes, w, h, ct).Planes, progress, cancellation);

    public static unsafe SKBitmap Enlarge(SKBitmap source, int scale, TileFunction run, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellation = default)
    {
        int width = source.Width, height = source.Height;
        if ((long)width * scale * height * scale > Model.DocumentLimits.MaxSurfacePixels)
            throw new InvalidOperationException($"将 {width}×{height} 像素放大 {scale} 倍将产生超过 {Model.DocumentLimits.MaxSurfaceMegapixels} 百万像素的图层，这已是图层所能容纳的上限。");
        var (rgb, alpha, hasAlpha) = Straighten(source);
        var tiles = Upscaling.PlanTiles(width, height);
        int outWidth = width * scale, outHeight = height * scale;
        var outRgb = new byte[(long)outWidth * outHeight * 3];
        var done = 0;
        progress?.Report((0, tiles.Count));
        foreach (var tile in tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var window = Matting.PackNchw(Crop(rgb, width, tile), tile.PadWidth, tile.PadHeight, tile.PadWidth * 4, [0f, 0f, 0f], [1f, 1f, 1f]);
            var planes = run(window, tile.PadWidth, tile.PadHeight, cancellation);
            int planeW = tile.PadWidth * scale, planeH = tile.PadHeight * scale;
            if (planes.Length != planeW * planeH * 3) throw new InvalidDataException($"模型对 {tile.PadWidth}×{tile.PadHeight} 的分块返回了 {planes.Length} 个值，而非 {planeW * planeH * 3} 个。");
            Upscaling.PlanesToRgb(planes, planeW, planeH, (tile.CoreX - tile.PadX) * scale, (tile.CoreY - tile.PadY) * scale, tile.CoreWidth * scale, tile.CoreHeight * scale,
                outRgb, outWidth * 3, tile.CoreX * scale, tile.CoreY * scale);
            progress?.Report((++done, tiles.Count));
        }
        cancellation.ThrowIfCancellationRequested();

        // The alpha, enlarged smoothly on its own, premultiplies the model's colors back into Composa's pixels.
        var result = Pixels.NewColor(outWidth, outHeight);
        using var bigAlpha = hasAlpha ? Resize(alpha!, outWidth, outHeight) : null;
        var dst = (byte*)result.GetPixels();
        var a = bigAlpha == null ? null : (byte*)bigAlpha.GetPixels();
        var aStride = bigAlpha?.RowBytes ?? 0;
        Parallel.For(0, outHeight, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            var src = (long)y * outWidth * 3;
            for (var x = 0; x < outWidth; x++)
            {
                var p = row + x * 4;
                var alphaValue = a == null ? (byte)255 : a[(long)y * aStride + x];
                var s = src + x * 3;
                p[0] = (byte)((outRgb[s] * alphaValue + 127) / 255);
                p[1] = (byte)((outRgb[s + 1] * alphaValue + 127) / 255);
                p[2] = (byte)((outRgb[s + 2] * alphaValue + 127) / 255);
                p[3] = alphaValue;
            }
        });
        alpha?.Dispose();
        return result;
    }

    /// <summary>
    /// Straight RGBA bytes (alpha left 255 in the color buffer) and the alpha as a mask. Pixels with no coverage take
    /// the color of their nearest covered neighbours, spread outward a few pixels at a time, and the average color
    /// beyond that, so the model sees a continuous picture rather than black holes.
    /// </summary>
    private static unsafe (byte[] Rgb, SKBitmap? Alpha, bool HasAlpha) Straighten(SKBitmap source)
    {
        int width = source.Width, height = source.Height;
        var rgba = new byte[width * height * 4];
        var hasAlpha = false;
        SKBitmap? alpha = null;
        var src = (byte*)source.GetPixels();
        long sumR = 0, sumG = 0, sumB = 0, covered = 0;
        for (var y = 0; y < height; y++)
        {
            var row = src + (long)y * source.RowBytes;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                var at = (y * width + x) * 4;
                var av = p[3];
                if (av != 255) hasAlpha = true;
                if (av == 0) { rgba[at + 3] = 0; continue; }
                rgba[at] = (byte)Math.Min(255, (p[0] * 255 + av / 2) / av);
                rgba[at + 1] = (byte)Math.Min(255, (p[1] * 255 + av / 2) / av);
                rgba[at + 2] = (byte)Math.Min(255, (p[2] * 255 + av / 2) / av);
                rgba[at + 3] = 255;
                sumR += rgba[at]; sumG += rgba[at + 1]; sumB += rgba[at + 2]; covered++;
            }
        }
        if (hasAlpha)
        {
            alpha = Pixels.NewMask(width, height);
            var a = (byte*)alpha.GetPixels();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) a[(long)y * alpha.RowBytes + x] = src[(long)y * source.RowBytes + x * 4 + 3];
            SpreadColor(rgba, width, height, covered == 0 ? (byte)0 : (byte)(sumR / covered), covered == 0 ? (byte)0 : (byte)(sumG / covered), covered == 0 ? (byte)0 : (byte)(sumB / covered));
        }
        return (rgba, alpha, hasAlpha);
    }

    /// <summary>Fills uncovered pixels (alpha byte 0) from covered neighbours, eight passes outward; whatever is left takes the average.</summary>
    private static void SpreadColor(byte[] rgba, int width, int height, byte fillR, byte fillG, byte fillB)
    {
        var pending = new List<int>();
        for (var i = 0; i < width * height; i++) if (rgba[i * 4 + 3] == 0) pending.Add(i);
        for (var pass = 0; pass < 8 && pending.Count > 0; pass++)
        {
            var filled = new List<(int Index, byte R, byte G, byte B)>();
            foreach (var i in pending)
            {
                int x = i % width, y = i / width, r = 0, g = 0, b = 0, n = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var at = (ny * width + nx) * 4;
                    if (rgba[at + 3] == 0) continue;
                    r += rgba[at]; g += rgba[at + 1]; b += rgba[at + 2]; n++;
                }
                if (n > 0) filled.Add((i, (byte)(r / n), (byte)(g / n), (byte)(b / n)));
            }
            foreach (var (i, r, g, b) in filled) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255; }
            var done = filled.Select(f => f.Index).ToHashSet();
            pending.RemoveAll(done.Contains);
        }
        foreach (var i in pending) { rgba[i * 4] = fillR; rgba[i * 4 + 1] = fillG; rgba[i * 4 + 2] = fillB; rgba[i * 4 + 3] = 255; }
    }

    private static byte[] Crop(byte[] rgba, int width, TilePlan tile)
    {
        var window = new byte[tile.PadWidth * tile.PadHeight * 4];
        for (var y = 0; y < tile.PadHeight; y++)
            Buffer.BlockCopy(rgba, ((tile.PadY + y) * width + tile.PadX) * 4, window, y * tile.PadWidth * 4, tile.PadWidth * 4);
        return window;
    }

    private static SKBitmap Resize(SKBitmap mask, int width, int height)
    {
        var result = Pixels.NewMask(width, height);
        if (!mask.ScalePixels(result, new SKSamplingOptions(SKCubicResampler.CatmullRom))) throw new InvalidOperationException("无法重新取样 alpha 通道。");
        return result;
    }
}
