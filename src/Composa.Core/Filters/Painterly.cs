using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

public enum PainterlyStyle { Impressionist, Expressionist, ColoristWash, Pointillist }

/// <summary>What the person chooses for the Painterly filter; the style decides the rest (<see cref="Painterly"/>).</summary>
public sealed record PainterlySettings
{
    public PainterlyStyle Style { get; init; } = PainterlyStyle.Impressionist;
    /// <summary>Diameter of the largest brush in pixels; 0 sizes it from the picture, a fiftieth of its shorter side.</summary>
    public double BrushSize { get; init; }
    /// <summary>How many brushes are used, each half the size of the last, 1 to 4.</summary>
    public int Passes { get; init; } = 3;
    /// <summary>
    /// How closely the strokes follow the picture, 0 to 100. A pass only paints where the picture still differs from
    /// what is on the canvas by more than a threshold; Detail lowers that threshold, so more of the picture is painted
    /// again with the smaller brushes.
    /// </summary>
    public double Detail { get; init; } = 50;

    public PainterlySettings Normalized() => this with
    {
        BrushSize = double.IsFinite(BrushSize) ? Math.Clamp(BrushSize, 0, 500) : 0, Passes = Math.Clamp(Passes, 1, 4), Detail = double.IsFinite(Detail) ? Math.Clamp(Detail, 0, 100) : 50
    };

    public static string DisplayName(PainterlyStyle style) => style switch
    {
        PainterlyStyle.Impressionist => "印象派",
        PainterlyStyle.Expressionist => "表现派",
        PainterlyStyle.ColoristWash => "色彩晕染",
        _ => "点彩派"
    };
}

/// <summary>
/// Paints a picture in brush strokes, after Hertzmann's "Painterly Rendering with Curved Brush Strokes of Multiple
/// Sizes" (1998). The largest brush paints first: the picture is softened by the brush's size, the canvas is compared
/// with it cell by cell, and wherever they differ by more than the style's threshold a stroke starts at the worst
/// pixel, takes that pixel's color, and runs along the picture's edges (perpendicular to the brightness gradient)
/// until the canvas already matches the picture better than the stroke would, or the edge fades out. Each smaller
/// brush then repaints only what the last one left too rough, so flat areas stay loose while eyes and mouths are
/// painted finely. Strokes are painted in random order with the brush engine's falloff, and everything a stroke did
/// not reach stays transparent. The same seed paints the same strokes.
/// </summary>
public static unsafe class Painterly
{
    /// <summary>
    /// A style, as the paper tabulates them: the error a cell may keep before it is painted again (a color distance,
    /// 0 to 441), how much a stroke bends with the edge it follows (1) or keeps going straight (0), the blur before
    /// each pass as a fraction of the brush radius, the stroke's opacity and hardness, its length in brush radii, and
    /// how much each stroke's color may wander in hue, saturation and value.
    /// </summary>
    private readonly record struct Style(double Threshold, double Curvature, double Blur, double Opacity, double Hardness, int MinLength, int MaxLength, double HueJitter, double SaturationJitter, double ValueJitter);

    private static Style Parameters(PainterlyStyle style) => style switch
    {
        PainterlyStyle.Expressionist => new(50, 0.25, 0.5, 0.7, 0.85, 10, 16, 0, 0, 0.5),
        PainterlyStyle.ColoristWash => new(200, 1, 0.5, 0.5, 0.7, 4, 16, 0.1, 0.3, 0.3),
        PainterlyStyle.Pointillist => new(100, 1, 0.5, 1, 0.95, 0, 0, 0.3, 0, 1),
        _ => new(100, 1, 0.5, 1, 0.9, 4, 16, 0, 0, 0)
    };

    private sealed record Stroke(float Red, float Green, float Blue, float Radius, List<SKPoint> Points)
    {
        /// <summary>The rows the stroke touches with a brush reaching <paramref name="reach"/> past its points.</summary>
        public (int Top, int Bottom) Rows(float reach)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in Points) { min = Math.Min(min, p.Y); max = Math.Max(max, p.Y); }
            return ((int)MathF.Floor(min - reach), (int)MathF.Ceiling(max + reach));
        }
    }

    /// <summary>The softened picture as straight colors, with a brightness plane for the gradients that steer the strokes.</summary>
    private sealed class Reference
    {
        public readonly int Width, Height;
        public readonly float[] Red, Green, Blue, Alpha, Luminance;

        public Reference(SKBitmap softened)
        {
            Width = softened.Width; Height = softened.Height;
            var count = Width * Height;
            Red = new float[count]; Green = new float[count]; Blue = new float[count]; Alpha = new float[count]; Luminance = new float[count];
            var src = (byte*)softened.GetPixels();
            var stride = softened.RowBytes;
            Parallel.For(0, Height, y =>
            {
                var row = src + (long)y * stride;
                for (var x = 0; x < Width; x++)
                {
                    var p = row + x * 4;
                    var i = y * Width + x;
                    if (p[3] == 0) continue;
                    var a = p[3] / 255f;
                    Red[i] = Math.Min(255, p[0] / a); Green[i] = Math.Min(255, p[1] / a); Blue[i] = Math.Min(255, p[2] / a);
                    Alpha[i] = a;
                    Luminance[i] = (0.2126f * Red[i] + 0.7152f * Green[i] + 0.0722f * Blue[i]) * a;
                }
            });
        }

        public int Index(float x, float y) => Math.Clamp((int)y, 0, Height - 1) * Width + Math.Clamp((int)x, 0, Width - 1);

        /// <summary>The brightness gradient at a pixel, by Sobel, with the picture's edges continued outward.</summary>
        public (float X, float Y) Gradient(int x, int y)
        {
            float At(int px, int py) => Luminance[Math.Clamp(py, 0, Height - 1) * Width + Math.Clamp(px, 0, Width - 1)];
            var gx = At(x + 1, y - 1) + 2 * At(x + 1, y) + At(x + 1, y + 1) - At(x - 1, y - 1) - 2 * At(x - 1, y) - At(x - 1, y + 1);
            var gy = At(x - 1, y + 1) + 2 * At(x, y + 1) + At(x + 1, y + 1) - At(x - 1, y - 1) - 2 * At(x, y - 1) - At(x + 1, y - 1);
            return (gx, gy);
        }
    }

    private const float MaxDistance = 441.68f;      // Black to white in RGB, the error of a pixel nothing has painted yet.

    public static SKBitmap Paint(SKBitmap source, PainterlySettings settings, uint seed)
    {
        settings = settings.Normalized();
        int w = source.Width, h = source.Height;
        var result = Pixels.NewColor(w, h);
        if (w == 0 || h == 0) return result;
        var style = Parameters(settings.Style);
        var largest = settings.BrushSize > 0 ? settings.BrushSize / 2 : Math.Max(2, Math.Min(w, h) / 100.0);
        // Detail 50 is the style's own threshold; every 25 points halves or doubles it.
        var threshold = (float)(style.Threshold * Math.Pow(2, (50 - settings.Detail) / 25));
        var random = new Random(unchecked((int)seed));
        var radii = new List<float>();
        for (var pass = 0; pass < settings.Passes; pass++)
        {
            var radius = (float)(largest / Math.Pow(2, pass));
            if (radius < 1 && radii.Count > 0) break;
            radii.Add(Math.Max(1, radius));
        }
        foreach (var radius in radii)
        {
            using var softened = Pixels.Clone(source);
            ImageFilters.BlurInPlace(softened, (float)(radius * style.Blur));
            var reference = new Reference(softened);
            var strokes = Plan(reference, result, radius, threshold, style);
            Shuffle(strokes, random);
            PaintAll(result, strokes, new Brush(radius, (float)style.Hardness), style, random);
        }
        Pixels.Invalidate(result);
        return result;
    }

    /// <summary>The strokes one pass wants, from the cells of the canvas that still differ from the picture. One per cell at most, starting at its worst pixel.</summary>
    private static List<Stroke> Plan(Reference reference, SKBitmap canvas, float radius, float threshold, Style style)
    {
        int w = reference.Width, h = reference.Height;
        var grid = Math.Max(1, (int)Math.Round(radius));
        var rows = (h + grid - 1) / grid;
        var perRow = new List<Stroke>[rows];
        var dst = (byte*)canvas.GetPixels();
        var stride = canvas.RowBytes;
        Parallel.For(0, rows, row =>
        {
            var found = new List<Stroke>();
            for (var x0 = 0; x0 < w; x0 += grid)
            {
                float sum = 0, worst = -1;
                int count = 0, worstX = 0, worstY = 0;
                for (var y = row * grid; y < Math.Min(h, row * grid + grid); y++)
                    for (var x = x0; x < Math.Min(w, x0 + grid); x++)
                    {
                        var i = y * w + x;
                        if (reference.Alpha[i] <= 0.01f) continue;
                        var error = Error(reference, i, dst + (long)y * stride + x * 4);
                        sum += error;
                        count++;
                        if (error > worst) { worst = error; worstX = x; worstY = y; }
                    }
                if (count == 0 || sum / count <= threshold) continue;
                found.Add(MakeStroke(reference, canvas, worstX, worstY, radius, style));
            }
            perRow[row] = found;
        });
        var strokes = new List<Stroke>();
        foreach (var found in perRow) if (found != null) strokes.AddRange(found);
        return strokes;
    }

    /// <summary>How far a canvas pixel is from the picture: the color distance, or the whole range where nothing has been painted yet.</summary>
    private static float Error(Reference reference, int i, byte* canvasPixel)
    {
        var a = canvasPixel[3] / 255f;
        if (a <= 0) return MaxDistance;
        float r = canvasPixel[0] / a, g = canvasPixel[1] / a, b = canvasPixel[2] / a;
        return Distance(reference.Red[i], reference.Green[i], reference.Blue[i], r, g, b) * a + MaxDistance * (1 - a);
    }

    private static float Distance(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        float dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>
    /// A stroke from a pixel: it takes the picture's color there and steps a brush radius at a time along the edge
    /// through each point, bending with it as the style allows, until it has gone far enough and the canvas already
    /// matches the picture better than the stroke's color would, or the picture goes flat or transparent.
    /// </summary>
    private static Stroke MakeStroke(Reference reference, SKBitmap canvas, int x0, int y0, float radius, Style style)
    {
        int w = reference.Width, h = reference.Height;
        var start = y0 * w + x0;
        float red = reference.Red[start], green = reference.Green[start], blue = reference.Blue[start];
        var points = new List<SKPoint> { new(x0 + 0.5f, y0 + 0.5f) };
        float x = x0 + 0.5f, y = y0 + 0.5f, lastDx = 0, lastDy = 0;
        var dst = (byte*)canvas.GetPixels();
        for (var i = 1; i <= style.MaxLength; i++)
        {
            int px = (int)x, py = (int)y;
            var at = py * w + px;
            if (i > style.MinLength)
            {
                var pixel = dst + (long)py * canvas.RowBytes + px * 4;
                var painted = pixel[3] / 255f;
                var canvasDistance = painted <= 0 ? MaxDistance
                    : Distance(reference.Red[at], reference.Green[at], reference.Blue[at], pixel[0] / painted, pixel[1] / painted, pixel[2] / painted);
                if (canvasDistance < Distance(reference.Red[at], reference.Green[at], reference.Blue[at], red, green, blue)) break;
            }
            var (gx, gy) = reference.Gradient(px, py);
            var magnitude = MathF.Sqrt(gx * gx + gy * gy);
            if (magnitude < 1e-3f) break;
            float dx = -gy / magnitude, dy = gx / magnitude;
            if (lastDx * dx + lastDy * dy < 0) { dx = -dx; dy = -dy; }
            var curvature = (float)style.Curvature;
            dx = curvature * dx + (1 - curvature) * lastDx;
            dy = curvature * dy + (1 - curvature) * lastDy;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6f) break;
            dx /= length; dy /= length;
            x += radius * dx; y += radius * dy;
            lastDx = dx; lastDy = dy;
            if (x < 0 || y < 0 || x >= w || y >= h || reference.Alpha[reference.Index(x, y)] <= 0.01f) break;
            points.Add(new SKPoint(x, y));
        }
        return new Stroke(red, green, blue, radius, points);
    }

    private static void Shuffle(List<Stroke> strokes, Random random)
    {
        for (var i = strokes.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (strokes[i], strokes[j]) = (strokes[j], strokes[i]);
        }
    }

    /// <summary>
    /// The brush's falloff for one pass, tabulated by squared distance so a dab costs no square root or cosine per
    /// pixel: the brush engine's shape, a hard disc softened past <c>hardness</c> of the radius.
    /// </summary>
    private sealed class Brush
    {
        private const int Steps = 4096;
        private readonly float[] table = new float[Steps + 1];
        private readonly float scale;
        public readonly float Radius, Reach;

        public Brush(float radius, float hardness)
        {
            Radius = radius;
            Reach = radius + 1;
            scale = Steps / (Reach * Reach);
            var inner = radius * hardness;
            for (var i = 0; i <= Steps; i++)
            {
                var distance = MathF.Sqrt(i / scale);
                var edge = Math.Clamp(radius - distance + 0.5f, 0, 1);
                if (hardness >= 0.995f || distance <= inner) table[i] = edge;
                else
                {
                    var t = Math.Clamp((distance - inner) / Math.Max(1e-3f, radius - inner), 0, 1);
                    table[i] = 0.5f * (1 + MathF.Cos(MathF.PI * t)) * edge;
                }
            }
        }

        public float At(float distanceSquared)
        {
            var i = (int)(distanceSquared * scale);
            return i >= Steps ? 0 : table[i];
        }
    }

    /// <summary>
    /// Paints the strokes in their shuffled order, in parallel where that cannot show: the canvas is cut into bands
    /// taller than any stroke, a stroke that lies within one band is painted with that band's, and the strokes that
    /// cross a band edge are painted last, one after the other. Colors are drawn first, in order, so the same seed
    /// gives the same picture however the work is split.
    /// </summary>
    private static void PaintAll(SKBitmap canvas, List<Stroke> strokes, Brush brush, Style style, Random random)
    {
        var colors = new (float, float, float)[strokes.Count];
        for (var i = 0; i < strokes.Count; i++) colors[i] = Jitter(strokes[i], style, random);
        var bandHeight = Math.Max(64, (int)Math.Ceiling(brush.Radius * (style.MaxLength + 2) + 4));
        var bands = (canvas.Height + bandHeight - 1) / bandHeight;
        var perBand = new List<int>[bands];
        for (var b = 0; b < bands; b++) perBand[b] = [];
        var crossing = new List<int>();
        for (var i = 0; i < strokes.Count; i++)
        {
            var (top, bottom) = strokes[i].Rows(brush.Reach);
            var band = Math.Clamp(top / bandHeight, 0, bands - 1);
            if (top >= 0 && bottom <= (band + 1) * bandHeight) perBand[band].Add(i); else crossing.Add(i);
        }
        Parallel.For(0, bands, band =>
        {
            var coverage = new float[1024];
            foreach (var i in perBand[band]) PaintStroke(canvas, strokes[i], colors[i], brush, style, ref coverage);
        });
        var shared = new float[1024];
        foreach (var i in crossing) PaintStroke(canvas, strokes[i], colors[i], brush, style, ref shared);
    }

    /// <summary>
    /// Lays the stroke down: dabs along its points into one coverage map, as the brush engine does, so a stroke that
    /// crosses itself never builds up past its opacity, then the stroke's color over the canvas at that coverage.
    /// </summary>
    private static void PaintStroke(SKBitmap canvas, Stroke stroke, (float Red, float Green, float Blue) color, Brush brush, Style style, ref float[] coverage)
    {
        var (red, green, blue) = color;
        var reach = brush.Reach;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in stroke.Points) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        int left = Math.Max(0, (int)MathF.Floor(minX - reach)), top = Math.Max(0, (int)MathF.Floor(minY - reach));
        int right = Math.Min(canvas.Width, (int)MathF.Ceiling(maxX + reach)), bottom = Math.Min(canvas.Height, (int)MathF.Ceiling(maxY + reach));
        if (right <= left || bottom <= top) return;
        int width = right - left, height = bottom - top;
        if (coverage.Length < width * height) coverage = new float[width * height * 2];
        Array.Clear(coverage, 0, width * height);
        var spacing = Math.Max(0.5f, brush.Radius * 0.4f);
        Dab(coverage, width, height, left, top, stroke.Points[0], brush);
        var residual = 0f;
        for (var i = 1; i < stroke.Points.Count; i++)
        {
            SKPoint from = stroke.Points[i - 1], to = stroke.Points[i];
            float dx = to.X - from.X, dy = to.Y - from.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0) continue;
            var travelled = spacing - residual;
            while (travelled <= length)
            {
                var t = travelled / length;
                Dab(coverage, width, height, left, top, new SKPoint(from.X + dx * t, from.Y + dy * t), brush);
                travelled += spacing;
            }
            residual = length - (travelled - spacing);
        }
        var opacity = (float)style.Opacity;
        var dst = (byte*)canvas.GetPixels();
        for (var y = 0; y < height; y++)
        {
            var row = dst + (long)(top + y) * canvas.RowBytes;
            for (var x = 0; x < width; x++)
            {
                var a = coverage[y * width + x] * opacity;
                if (a <= 0) continue;
                var p = row + (left + x) * 4;
                var keep = 1 - a;
                p[0] = (byte)Math.Min(255, red * a + p[0] * keep + 0.5f);
                p[1] = (byte)Math.Min(255, green * a + p[1] * keep + 0.5f);
                p[2] = (byte)Math.Min(255, blue * a + p[2] * keep + 0.5f);
                p[3] = (byte)Math.Min(255, 255 * a + p[3] * keep + 0.5f);
            }
        }
    }

    /// <summary>One round mark of the brush into the coverage map, keeping the higher coverage where dabs overlap.</summary>
    private static void Dab(float[] coverage, int width, int height, int left, int top, SKPoint center, Brush brush)
    {
        var reach = brush.Reach;
        int x0 = Math.Max(0, (int)MathF.Floor(center.X - reach) - left), y0 = Math.Max(0, (int)MathF.Floor(center.Y - reach) - top);
        int x1 = Math.Min(width, (int)MathF.Ceiling(center.X + reach) - left), y1 = Math.Min(height, (int)MathF.Ceiling(center.Y + reach) - top);
        for (var y = y0; y < y1; y++)
        {
            var dy = top + y + 0.5f - center.Y;
            var row = y * width;
            for (var x = x0; x < x1; x++)
            {
                var dx = left + x + 0.5f - center.X;
                var c = brush.At(dx * dx + dy * dy);
                if (c > coverage[row + x]) coverage[row + x] = c;
            }
        }
    }

    /// <summary>The stroke's color, wandered by the style's jitter: up to a quarter turn of hue, half the range of saturation and value.</summary>
    private static (float, float, float) Jitter(Stroke stroke, Style style, Random random)
    {
        if (style.HueJitter <= 0 && style.SaturationJitter <= 0 && style.ValueJitter <= 0) return (stroke.Red, stroke.Green, stroke.Blue);
        var color = new SKColor((byte)Math.Clamp(stroke.Red + 0.5f, 0, 255), (byte)Math.Clamp(stroke.Green + 0.5f, 0, 255), (byte)Math.Clamp(stroke.Blue + 0.5f, 0, 255));
        color.ToHsv(out var hue, out var saturation, out var value);
        hue = (hue + (float)((random.NextDouble() * 2 - 1) * style.HueJitter * 90) + 360) % 360;
        saturation = Math.Clamp(saturation + (float)((random.NextDouble() * 2 - 1) * style.SaturationJitter * 50), 0, 100);
        value = Math.Clamp(value + (float)((random.NextDouble() * 2 - 1) * style.ValueJitter * 50), 0, 100);
        var jittered = SKColor.FromHsv(hue, saturation, value);
        return (jittered.Red, jittered.Green, jittered.Blue);
    }
}
