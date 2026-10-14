using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.Model;

public enum GuideAxis { Horizontal, Vertical }

/// <summary>A user-placed alignment line. Horizontal guides sit at a document Y; vertical ones at a document X.</summary>
public sealed record Guide(Guid Id, GuideAxis Axis, double Position)
{
    public Guide Offset(double dx, double dy) => this with { Position = Position + (Axis == GuideAxis.Vertical ? dx : dy) };

    public Guide Scaled(double sx, double sy) => this with { Position = Position * (Axis == GuideAxis.Vertical ? sx : sy) };

    /// <summary>Mirrors the guide when it runs across the flip, so it stays on the same content.</summary>
    public Guide Mirrored(bool horizontally, double center) =>
        (horizontally && Axis == GuideAxis.Vertical) || (!horizontally && Axis == GuideAxis.Horizontal) ? this with { Position = 2 * center - Position } : this;

    /// <summary>The guide after a quarter turn of the canvas: axes swap, positions follow the pixels.</summary>
    public Guide Turned(bool clockwise, int width, int height) => Axis == GuideAxis.Vertical
        ? this with { Axis = GuideAxis.Horizontal, Position = clockwise ? Position : width - Position }
        : this with { Axis = GuideAxis.Vertical, Position = clockwise ? height - Position : Position };

    public bool IsValid => double.IsFinite(Position) && Math.Abs(Position) <= 1_000_000;
}

/// <summary>
/// Non-printing layout grid (View > Show > Grid): a major line every <see cref="Spacing"/> pixels, each square split
/// into <see cref="Subdivisions"/>. Set through View > Grid Settings; the person's, not the project's.
/// </summary>
public sealed record LayoutGrid
{
    public const int MinSpacing = 2, MaxSpacing = 4096, DefaultSpacing = 64;
    public const int MinSubdivisions = 1, MaxSubdivisions = 64, DefaultSubdivisions = 8;

    /// <summary>Pixels between major lines.</summary>
    public int Spacing { get; init; } = DefaultSpacing;
    /// <summary>Parts each major square is split into; never finer than a pixel.</summary>
    public int Subdivisions { get; init; } = DefaultSubdivisions;

    [JsonIgnore]
    public bool IsValid => Spacing is >= MinSpacing and <= MaxSpacing && Subdivisions is >= MinSubdivisions and <= MaxSubdivisions && Subdivisions <= Spacing;

    /// <summary>The grid within its limits, which is what a settings file or a field outside them falls back to.</summary>
    public LayoutGrid Normalized()
    {
        var spacing = Math.Clamp(Spacing, MinSpacing, MaxSpacing);
        return new LayoutGrid { Spacing = spacing, Subdivisions = Math.Clamp(Subdivisions, MinSubdivisions, Math.Min(MaxSubdivisions, spacing)) };
    }

    /// <summary>Pixels between subdivision lines.</summary>
    [JsonIgnore]
    public double Step { get { var grid = Normalized(); return (double)grid.Spacing / grid.Subdivisions; } }

    /// <summary>
    /// Every grid line along a document edge, including subdivisions, in whole pixels. Counted from the origin rather
    /// than added up, so an uneven step does not drift off the majors.
    /// </summary>
    public IEnumerable<double> Lines(double length)
    {
        if (!(length >= 0)) yield break;
        var step = Step;
        var count = (int)Math.Floor(length / step + 0.001);
        for (var i = 0; i <= count; i++) yield return Math.Round(i * step);
    }

    public bool IsMajor(double value) => Math.Abs(Math.Round(value) % Normalized().Spacing) < 0.001;
}

/// <summary>The layout grid's colors, after Photoshop's Guides, Grid & Slices preferences. Custom uses the appearance's own color.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GridColorPreset>))]
public enum GridColorPreset { LightGray, LightBlue, LightRed, Green, MediumBlue, Yellow, Magenta, Cyan, Black, Custom }

/// <summary>The major lines' pattern; subdivisions stay dotted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GridStyle>))]
public enum GridStyle { Lines, DashedLines, Dots }

/// <summary>How the layout grid is drawn (View > Grid Settings). Majors at the chosen opacity, subdivisions dotted and fainter.</summary>
public sealed record GridAppearance
{
    public const int MinOpacity = 1, MaxOpacity = 100, DefaultOpacity = 45;
    public const uint DefaultCustomColor = 0xFFB3B3B3;

    public GridColorPreset Preset { get; init; } = GridColorPreset.LightGray;
    /// <summary>Used while <see cref="Preset"/> is Custom, as ARGB; kept when another preset is chosen so switching back finds it.</summary>
    public uint CustomColor { get; init; } = DefaultCustomColor;
    public GridStyle Style { get; init; } = GridStyle.Lines;
    /// <summary>The major lines' opacity, in percent.</summary>
    public int Opacity { get; init; } = DefaultOpacity;

    public GridAppearance Normalized() => this with { Opacity = Math.Clamp(Opacity, MinOpacity, MaxOpacity) };

    /// <summary>The color the grid is drawn in: the preset's, or the custom color.</summary>
    [JsonIgnore]
    public SKColor Color => PresetColor(Preset) ?? new SKColor(CustomColor);

    [JsonIgnore]
    public double MajorAlpha => Math.Clamp(Opacity, MinOpacity, MaxOpacity) / 100.0;
    /// <summary>Subdivisions at a little over half the majors' opacity: 28% beside the default 45%.</summary>
    [JsonIgnore]
    public double SubdivisionAlpha => MajorAlpha * 28 / 45;

    /// <summary>A preset's color, or null for Custom.</summary>
    public static SKColor? PresetColor(GridColorPreset preset) => preset switch
    {
        GridColorPreset.LightGray => new SKColor(179, 179, 179),
        GridColorPreset.LightBlue => new SKColor(74, 199, 255),
        GridColorPreset.LightRed => new SKColor(255, 102, 102),
        GridColorPreset.Green => new SKColor(64, 204, 64),
        GridColorPreset.MediumBlue => new SKColor(51, 102, 255),
        GridColorPreset.Yellow => new SKColor(255, 255, 0),
        GridColorPreset.Magenta => new SKColor(255, 0, 255),
        GridColorPreset.Cyan => new SKColor(0, 255, 255),
        GridColorPreset.Black => SKColors.Black,
        _ => null
    };

    public static string DisplayName(GridColorPreset preset) => preset switch
    {
        GridColorPreset.LightGray => "浅灰",
        GridColorPreset.LightBlue => "浅蓝",
        GridColorPreset.LightRed => "浅红",
        GridColorPreset.Green => "绿色",
        GridColorPreset.MediumBlue => "中蓝",
        GridColorPreset.Yellow => "黄色",
        GridColorPreset.Magenta => "洋红",
        GridColorPreset.Cyan => "青色",
        GridColorPreset.Black => "黑色",
        _ => "自定义"
    };

    public static string DisplayName(GridStyle style) => style switch
    {
        GridStyle.Lines => "实线",
        GridStyle.DashedLines => "虚线",
        _ => "点"
    };

    /// <summary>On and off lengths of the major lines in screen points; empty for a solid line.</summary>
    public static float[] Dashes(GridStyle style) => style switch
    {
        GridStyle.DashedLines => [4, 3],
        GridStyle.Dots => [1, 2],
        _ => []
    };
}
