using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>One effect's controls, bound to the layer that opened the panel. Changes preview on the canvas.</summary>
public static class EffectsDialog
{
    /// <summary>
    /// Edits one effect of a layer. Every change is written to the layer at once so the canvas follows; the caller
    /// wraps the call in Begin/Commit (or Cancel), so the whole dialog undoes as one step.
    /// </summary>
    public static async Task<bool> Edit(Window owner, EditorSession session, Layer layer, LayerEffectKind kind)
    {
        var effects = layer.Effects ?? LayerEffects.Empty;
        if (!effects.Contains(kind)) return false;
        void Set(LayerEffects next) { effects = next; session.SetEffects(layer, next); }
        var rows = new StackPanel { Spacing = 10 };
        const double fieldWidth = 330;
        Control Slider(string label, double value, double min, double max, Action<double> changed, double step = 1, string format = "0") =>
            Ui.SliderField(label, value, min, max, changed, step, format, fieldWidth);
        // Photoshop's dial for the light's direction, with the field beside it for an exact number; each follows the other.
        Control Angle(double value, Action<double> changed) => Ui.AngleField("角度", value, -180, 180, changed, fieldWidth);
        Control Swatch()
        {
            var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            void Paint() => swatch.Background = new SolidColorBrush(new SKColor(effects.ColorOf(kind) ?? 0xFF000000).ToAvalonia());
            Paint();
            ToolTip.SetTip(swatch, LayerEffects.DisplayName(kind) + "颜色");
            swatch.PointerPressed += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(swatch) is not Window window || await Prompts.Color(window, LayerEffects.DisplayName(kind) + "颜色", new SKColor(effects.ColorOf(kind) ?? 0xFF000000)) is not { } picked) return;
                Set(effects.WithColor(kind, (uint)picked | 0xFF000000));
                Paint();
            };
            return Ui.Row(10, Ui.Label("颜色", Palette.Secondary, weight: FontWeight.Normal) is var l ? Width(l, 70) : l, swatch);
        }

        switch (kind)
        {
            case LayerEffectKind.Stroke:
                var stroke = effects.Stroke!;
                rows.Children.Add(Ui.Row(10, Width(Ui.Label("位置", Palette.Secondary), 70),
                    Ui.Combo(new[] { "外部", "内部" }, stroke.Inside ? "内部" : "外部", v => v, v => Set(effects with { Stroke = effects.Stroke! with { Inside = v == "内部" } }), 120)));
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("大小", stroke.Size, 0, 100, v => Set(effects with { Stroke = effects.Stroke! with { Size = v } })));
                rows.Children.Add(Slider("不透明度", stroke.Opacity * 100, 0, 100, v => Set(effects with { Stroke = effects.Stroke! with { Opacity = v / 100 } })));
                break;
            case LayerEffectKind.DropShadow:
            case LayerEffectKind.InnerShadow:
                var inner = kind == LayerEffectKind.InnerShadow;
                var shadow = inner ? effects.InnerShadow! : effects.Shadow!;
                void SetShadow(Func<ShadowEffect, ShadowEffect> change) => Set(inner ? effects with { InnerShadow = change(effects.InnerShadow!) } : effects with { Shadow = change(effects.Shadow!) });
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("不透明度", shadow.Opacity * 100, 0, 100, v => SetShadow(s => s with { Opacity = v / 100 })));
                rows.Children.Add(Angle(shadow.Angle, v => SetShadow(s => s with { Angle = v })));
                rows.Children.Add(Slider("距离", shadow.Distance, 0, inner ? 100 : 250, v => SetShadow(s => s with { Distance = v })));
                rows.Children.Add(Slider("模糊", shadow.Blur, 0, 250, v => SetShadow(s => s with { Blur = v })));
                break;
            case LayerEffectKind.ColorOverlay:
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("不透明度", effects.ColorOverlay!.Opacity * 100, 0, 100, v => Set(effects with { ColorOverlay = effects.ColorOverlay! with { Opacity = v / 100 } })));
                break;
            case LayerEffectKind.OuterGlow:
                var glow = effects.OuterGlow!;
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("不透明度", glow.Opacity * 100, 0, 100, v => Set(effects with { OuterGlow = effects.OuterGlow! with { Opacity = v / 100 } })));
                rows.Children.Add(Slider("大小", glow.Size, 0, 250, v => Set(effects with { OuterGlow = effects.OuterGlow! with { Size = v } })));
                break;
            case LayerEffectKind.InnerGlow:
                var innerGlow = effects.InnerGlow!;
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("不透明度", innerGlow.Opacity * 100, 0, 100, v => Set(effects with { InnerGlow = effects.InnerGlow! with { Opacity = v / 100 } })));
                rows.Children.Add(Slider("大小", innerGlow.Size, 0, 100, v => Set(effects with { InnerGlow = effects.InnerGlow! with { Size = v } })));
                break;
        }
        var dialog = new DialogWindow(LayerEffects.DisplayName(kind), rows);
        return await dialog.Ask(owner);
    }

    private static Control Width(TextBlock label, double width)
    {
        label.Width = width;
        return label;
    }
}
