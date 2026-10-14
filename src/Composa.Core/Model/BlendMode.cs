using SkiaSharp;

namespace Composa.Model;

public enum BlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, ColorDodge, ColorBurn,
    SoftLight, HardLight, Exclusion, Hue, Saturation, Color, Luminosity,
    LinearBurn, LinearDodge, VividLight, LinearLight, PinLight, HardMix, Subtract, Divide,
    /// <summary>GIMP's frequency-separation pair: the difference from the layer around mid-gray, and that difference added back. Photoshop has neither.</summary>
    GrainExtract, GrainMerge
}

public static class BlendModeExtensions
{
    /// <summary>
    /// Photoshop's grouping: darkening modes together, then lightening, then contrast, then the comparative ones, then
    /// the component modes. Menus draw a line between each group. Darker Color and Lighter Color are left out: they
    /// compare a pixel's whole brightness rather than working a channel at a time. Grain Extract and Grain Merge are
    /// GIMP's and sit with the comparative modes, beside Subtract, whose arithmetic they share.
    /// </summary>
    public static readonly BlendMode[][] Groups =
    [
        [BlendMode.Normal],
        [BlendMode.Darken, BlendMode.Multiply, BlendMode.ColorBurn, BlendMode.LinearBurn],
        [BlendMode.Lighten, BlendMode.Screen, BlendMode.ColorDodge, BlendMode.LinearDodge],
        [BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.VividLight, BlendMode.LinearLight, BlendMode.PinLight, BlendMode.HardMix],
        [BlendMode.Difference, BlendMode.Exclusion, BlendMode.Subtract, BlendMode.Divide, BlendMode.GrainExtract, BlendMode.GrainMerge],
        [BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity]
    ];

    /// <summary>The modes Skia has no equivalent for; <see cref="Rendering.SeparableBlend"/> composites them per pixel instead.</summary>
    public static bool IsCustom(this BlendMode mode) => mode >= BlendMode.LinearBurn;

    /// <summary>Skia's blend for a mode it can draw; custom modes never reach a paint and map to source-over here.</summary>
    public static SKBlendMode ToSkia(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver
    };

    public static string DisplayName(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => "正常",
        BlendMode.Darken => "变暗",
        BlendMode.Multiply => "正片叠底",
        BlendMode.Lighten => "变亮",
        BlendMode.Screen => "滤色",
        BlendMode.Overlay => "叠加",
        BlendMode.Difference => "差值",
        BlendMode.Exclusion => "排除",
        BlendMode.Hue => "色相",
        BlendMode.Saturation => "饱和度",
        BlendMode.Color => "颜色",
        BlendMode.Luminosity => "亮度",
        BlendMode.ColorDodge => "颜色减淡",
        BlendMode.ColorBurn => "颜色加深",
        BlendMode.SoftLight => "柔光",
        BlendMode.HardLight => "强光",
        BlendMode.LinearBurn => "线性加深",
        BlendMode.LinearDodge => "线性减淡（添加）",
        BlendMode.VividLight => "亮光",
        BlendMode.LinearLight => "线性光",
        BlendMode.PinLight => "点光",
        BlendMode.HardMix => "实色混合",
        BlendMode.GrainExtract => "颗粒提取",
        BlendMode.GrainMerge => "颗粒合并",
        _ => mode.ToString()
    };
}
