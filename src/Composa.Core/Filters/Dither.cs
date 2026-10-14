using SkiaSharp;

namespace Composa.Filters;

/// <summary>Filter > Dither's looks, grouped as the panel's menu lists them.</summary>
public enum DitherStyle { Atkinson, FloydSteinberg, Bayer2, Bayer4, Bayer8, HalftoneDots, HalftoneLines, HalftoneDiamonds, MacPatterns, Ascii }

/// <summary>How a chunky pixel is drawn: a solid square, or a round dot with the dark color around it, like a dot-matrix or LED screen.</summary>
public enum DitherPixelShape { Square, Dot }

public enum DitherColors { BlackWhite, TwoColors, Original }

/// <summary>What the person chooses for the Dither filter (<see cref="DitherPixels"/> does the work).</summary>
public sealed record DitherSettings
{
    public const int MinPixelSize = 1, MaxPixelSize = 32, DefaultPixelSize = 2;
    public const int MinCellSize = 4, MaxCellSize = 64, DefaultCellSize = 8;
    public const int MinTextSize = 6, MaxTextSize = 64, DefaultTextSize = 14;
    public const int MinLevels = 2, MaxLevels = 8;
    public const int MaxCharacters = 64;
    public const string DefaultCharacters = " .:-=+*#%@";

    public DitherStyle Style { get; init; } = DitherStyle.Atkinson;
    /// <summary>Each dithered pixel covers this many layer pixels on a side, for chunky old-screen pixels.</summary>
    public int PixelSize { get; init; } = DefaultPixelSize;
    public DitherPixelShape PixelShape { get; init; } = DitherPixelShape.Square;
    /// <summary>Halftone screen cells, in dithered pixels.</summary>
    public int CellSize { get; init; } = DefaultCellSize;
    /// <summary>ASCII's line height in layer pixels; the characters are about six tenths as wide.</summary>
    public int TextSize { get; init; } = DefaultTextSize;
    /// <summary>Halftone screen angle in degrees, -90 to 90.</summary>
    public double Angle { get; init; } = 45;
    /// <summary>Tones per channel for diffusion and ordered styles; 2 is 1-bit.</summary>
    public int Levels { get; init; } = MinLevels;
    /// <summary>How much of the error diffusion passes on, 0 to 100 percent. Less gives flatter, posterized areas.</summary>
    public double Diffusion { get; init; } = 100;
    /// <summary>-100 to 100: more ink (darker) or less, and flatter or punchier, before dithering.</summary>
    public double Density { get; init; }
    public double Contrast { get; init; }
    public DitherColors Colors { get; init; } = DitherColors.BlackWhite;
    /// <summary>The two colors, as ARGB, used while <see cref="Colors"/> is Two Colors.</summary>
    public uint Dark { get; init; } = 0xFF000000;
    public uint Light { get; init; } = 0xFFFFFFFF;
    /// <summary>
    /// Marks stand for the light tones, drawn in the light color on the dark: glowing dots on a black screen. On by
    /// default; it only affects halftone, patterns and ASCII.
    /// </summary>
    public bool LightOnDark { get; init; } = true;
    /// <summary>ASCII's characters, in any order: they are sorted by how much ink each one has.</summary>
    public string Characters { get; init; } = DefaultCharacters;

    /// <summary>Error diffusion: each pixel's rounding error is passed to its neighbors.</summary>
    public bool Diffuses => Style is DitherStyle.Atkinson or DitherStyle.FloydSteinberg;
    /// <summary>Diffusion and ordered styles quantize to a number of tones; the rest draw marks in two.</summary>
    public bool HasTones => Diffuses || Style is DitherStyle.Bayer2 or DitherStyle.Bayer4 or DitherStyle.Bayer8;
    public bool IsHalftone => Style is DitherStyle.HalftoneDots or DitherStyle.HalftoneLines or DitherStyle.HalftoneDiamonds;
    /// <summary>Halftone shapes, patterns and characters mark one tone on the other, so which one is the mark matters.</summary>
    public bool DrawsMarks => !HasTones;

    public DitherSettings Normalized() => this with
    {
        PixelSize = Math.Clamp(PixelSize, MinPixelSize, MaxPixelSize),
        CellSize = Math.Clamp(CellSize, MinCellSize, MaxCellSize),
        TextSize = Math.Clamp(TextSize, MinTextSize, MaxTextSize),
        Angle = double.IsFinite(Angle) ? Math.Clamp(Angle, -90, 90) : 45,
        Levels = Math.Clamp(Levels, MinLevels, MaxLevels),
        Diffusion = double.IsFinite(Diffusion) ? Math.Clamp(Diffusion, 0, 100) : 100,
        Density = double.IsFinite(Density) ? Math.Clamp(Density, -100, 100) : 0,
        Contrast = double.IsFinite(Contrast) ? Math.Clamp(Contrast, -100, 100) : 0,
        Dark = Dark | 0xFF000000,
        Light = Light | 0xFF000000,
        Characters = new string((Characters ?? "").Where(c => c != '\n' && c != '\r').Take(MaxCharacters).ToArray())
    };

    /// <summary>The styles as the menu lists them: diffusion, ordered, halftone, then patterns and text.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<DitherStyle>> Groups =
    [
        [DitherStyle.Atkinson, DitherStyle.FloydSteinberg],
        [DitherStyle.Bayer2, DitherStyle.Bayer4, DitherStyle.Bayer8],
        [DitherStyle.HalftoneDots, DitherStyle.HalftoneLines, DitherStyle.HalftoneDiamonds],
        [DitherStyle.MacPatterns, DitherStyle.Ascii]
    ];

    public static string DisplayName(DitherStyle style) => style switch
    {
        DitherStyle.Atkinson => "Atkinson（经典 Mac）",
        DitherStyle.FloydSteinberg => "Floyd-Steinberg",
        DitherStyle.Bayer2 => "Bayer 2 × 2",
        DitherStyle.Bayer4 => "Bayer 4 × 4",
        DitherStyle.Bayer8 => "Bayer 8 × 8",
        DitherStyle.HalftoneDots => "半调圆点",
        DitherStyle.HalftoneLines => "半调线条",
        DitherStyle.HalftoneDiamonds => "半调菱形",
        DitherStyle.MacPatterns => "Mac 图案",
        DitherStyle.Ascii => "ASCII",
        _ => style.ToString()
    };

    public static string DisplayName(DitherColors colors) => colors switch
    {
        DitherColors.BlackWhite => "黑白",
        DitherColors.TwoColors => "双色",
        _ => "原色"
    };

    public static string DisplayName(DitherPixelShape shape) => shape switch
    {
        DitherPixelShape.Dot => "圆点",
        _ => "方形"
    };

    /// <summary>A style by its display name or enum name, ignoring case, spaces, punctuation and the Mac note.</summary>
    public static bool TryParseStyle(string text, out DitherStyle style)
    {
        var key = Key(text);
        foreach (var candidate in Enum.GetValues<DitherStyle>())
        {
            if (Key(candidate.ToString()) == key || Key(DisplayName(candidate)) == key) { style = candidate; return true; }
        }
        style = default;
        return false;

        static string Key(string s) => new(s.ToLowerInvariant().Replace("(classic mac)", "").Replace("×", "x").Where(char.IsLetterOrDigit).ToArray());
    }

    internal static (byte R, byte G, byte B) Bytes(uint argb) => ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
