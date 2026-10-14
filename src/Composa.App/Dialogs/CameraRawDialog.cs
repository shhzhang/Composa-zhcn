using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>
/// The Camera Raw Filter's panel: a histogram of the graded layer, a thumbnail that doubles as the white-balance
/// eyedropper, then the groups Light, Color, Color Grading, Effects, Curve, Color Mixer, Detail, Optics and
/// Calibration, each collapsible and switchable off with an eye without clearing its sliders. Changes preview live
/// on the canvas through <paramref name="changed"/>; OK returns the grade as rendered (a hidden group contributes nothing).
/// </summary>
public static class CameraRawDialog
{
    private const double LabelWidth = 96, FieldWidth = 300;

    /// <param name="original">The layer's pixels before the filter, for the eyedropper and Auto.</param>
    /// <param name="graded">Reads the layer as currently previewed, for the histogram.</param>
    /// <param name="saveLookPath">Asks where to save the grade as a .cube; null hides Save Look.</param>
    /// <param name="title">The document's title, for the saved look's TITLE.</param>
    private static string GroupName(CameraRawGroup group) => group switch
    {
        CameraRawGroup.Light => "光线",
        CameraRawGroup.Color => "颜色",
        CameraRawGroup.Effects => "效果",
        CameraRawGroup.Curve => "曲线",
        CameraRawGroup.Mixer => "颜色混合器",
        CameraRawGroup.Grading => "颜色分级",
        CameraRawGroup.Detail => "细节",
        CameraRawGroup.Optics => "光学",
        _ => "校准"
    };

    public static async Task<CameraRawSettings?> Show(Window owner, CameraRawSettings initial, SKBitmap original, Action<CameraRawSettings> changed, Func<SKBitmap?> graded,
        Func<Task<string?>>? saveLookPath = null, string title = "")
    {
        var current = initial;
        var hidden = new HashSet<CameraRawGroup>();
        CameraRawSettings Rendered() => hidden.Aggregate(current, (settings, group) => settings.Without(group));
        var histogram = new HistogramView { Width = 300, Height = 90 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            changed(Rendered());
            if (graded() is { } pixels) { histogram.Histogram = Histogram.Of(pixels); histogram.InvalidateVisual(); }
        };
        var eyes = new Dictionary<CameraRawGroup, Button>();
        Button? saveLook = null;
        void Update(CameraRawSettings value)
        {
            current = value;
            foreach (var (group, eye) in eyes) eye.IsVisible = current.Adjusts(group);
            if (saveLook != null) saveLook.IsEnabled = !LookBake.ColorOnly(Rendered()).IsIdentity;
            timer.Stop();
            timer.Start();
        }

        // The thumbnail: click a pixel that should be neutral and Temperature and Tint follow.
        var thumb = Ui.ToAvaloniaBitmap(original, 300);
        var preview = new Image { Source = thumb, Width = thumb.PixelSize.Width, Height = thumb.PixelSize.Height, Stretch = Stretch.Uniform, Cursor = new Cursor(StandardCursorType.Cross) };
        ToolTip.SetTip(preview, "点击画面中应为中性色的像素，以其设置白平衡");
        var readout = Ui.Label("R —   G —   B —", Palette.Secondary);
        readout.FontSize = 11;
        Action<double>? setTemperature = null, setTint = null;
        ComboBox? balance = null;
        preview.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(preview);
            var color = CameraRawPixels.StraightColor(original, (int)(p.X / preview.Width * original.Width), (int)(p.Y / preview.Height * original.Height));
            readout.Text = color is { } c ? $"R {Math.Round(c.Red * 255)}   G {Math.Round(c.Green * 255)}   B {Math.Round(c.Blue * 255)}" : "R —   G —   B —";
        };
        preview.PointerPressed += (_, e) =>
        {
            var p = e.GetPosition(preview);
            if (CameraRawPixels.StraightColor(original, (int)(p.X / preview.Width * original.Width), (int)(p.Y / preview.Height * original.Height)) is not { } c) return;
            if (CameraRawSettings.NeutralizeSrgb(c.Red, c.Green, c.Blue) is not { } solved) return;
            Update(current with { Temperature = Math.Clamp(solved.Temperature, -100, 100), Tint = Math.Clamp(solved.Tint, -100, 100), WhiteBalance = CameraRawWhiteBalance.Custom });
            setTemperature?.Invoke(current.Temperature);
            setTint?.Invoke(current.Tint);
            if (balance != null) balance.SelectedIndex = 0;
        };

        var groups = new StackPanel { Spacing = 6 };
        Expander Group(CameraRawGroup group, string title, Control body, bool open = false)
        {
            var eye = new Button { Classes = { "flat" }, Padding = new Thickness(4), Content = Icons.Create(Icons.Eye, 13, Palette.Secondary), IsVisible = initial.Adjusts(group) };
            ToolTip.SetTip(eye, "在不清除滑块的情况下开启或关闭此分组");
            eye.Click += (_, _) =>
            {
                if (!hidden.Remove(group)) hidden.Add(group);
                eye.Content = Icons.Create(hidden.Contains(group) ? Icons.EyeOff : Icons.Eye, 13, Palette.Secondary);
                timer.Stop();
                timer.Start();
            };
            eyes[group] = eye;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = 288 };
            header.Children.Add(Ui.Label(title, weight: FontWeight.SemiBold));
            Grid.SetColumn(eye, 1);
            header.Children.Add(eye);
            body.Margin = new Thickness(8, 6, 0, 4);
            var expander = new Expander { Header = header, Content = body, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
            groups.Children.Add(expander);
            return expander;
        }
        // Reset puts a slider back to what a fresh grade has, read from the defaults with the same getter that reads the value.
        var defaults = new CameraRawSettings();
        Controls.SliderField Slider(string label, Func<CameraRawSettings, double> get, double min, double max, Func<CameraRawSettings, double, CameraRawSettings> set, double step = 1, string format = "0", string? tip = null, IReadOnlyList<Color>? track = null)
        {
            var field = Ui.SliderField(label, get(current), min, max, v => Update(set(current, v)), step, format, FieldWidth, track, get(defaults));
            // The field's own tip explains its gestures; what the slider adjusts goes above that.
            if (tip != null) ToolTip.SetTip(field, Ui.Column(6, Ui.Label(tip), (Control)ToolTip.GetTip(field)!));
            return field;
        }
        Control Column(params Control[] rows) => Ui.Column(6, rows);
        Control Heading(string text) { var label = Ui.Label(text, Palette.Secondary); label.Margin = new Thickness(0, 4, 0, 0); return label; }

        // Light.
        Group(CameraRawGroup.Light, "光线", Column(
            Slider("曝光度", s => s.Exposure, -5, 5, (s, v) => s with { Exposure = v }, 0.05, "0.00", "以曝光档数增亮或压暗整个画面"),
            Slider("对比度", s => s.Contrast, -100, 100, (s, v) => s with { Contrast = v }, tip: "增大或减小明暗色调的差异，主要影响中间调"),
            Slider("高光", s => s.Highlights, -100, 100, (s, v) => s with { Highlights = v }),
            Slider("阴影", s => s.Shadows, -100, 100, (s, v) => s with { Shadows = v }),
            Slider("白色", s => s.Whites, -100, 100, (s, v) => s with { Whites = v }, tip: "设置最亮的点"),
            Slider("黑色", s => s.Blacks, -100, 100, (s, v) => s with { Blacks = v }, tip: "设置最暗的点")), open: true);

        // Color, with Auto white balance from the layer's average.
        var temperature = Slider("色温", s => s.Temperature, -100, 100, (s, v) => s with { Temperature = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "将画面在偏蓝与偏黄之间移动", track: Controls.SliderTracks.Temperature);
        var tint = Slider("色调", s => s.Tint, -100, 100, (s, v) => s with { Tint = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "将画面在偏绿与偏洋红之间移动", track: Controls.SliderTracks.Tint);
        setTemperature = v => temperature.Value = v;
        setTint = v => tint.Value = v;
        var balanceLabel = Ui.Label("白平衡");
        balanceLabel.Width = LabelWidth;
        balance = Ui.Combo(new[] { "自定", "自动" }, initial.WhiteBalance == CameraRawWhiteBalance.Auto ? "自动" : "自定", c => c, choice =>
        {
            if (choice != "自动") { Update(current with { WhiteBalance = CameraRawWhiteBalance.Custom }); return; }
            // Auto balances the average color of the original layer; the sliders show what it chose.
            var solved = CameraRawPixels.AutoBalance(original);
            Update(current with { WhiteBalance = CameraRawWhiteBalance.Auto, Temperature = Math.Clamp(solved?.Temperature ?? 0, -100, 100), Tint = Math.Clamp(solved?.Tint ?? 0, -100, 100) });
            temperature.Value = current.Temperature;
            tint.Value = current.Tint;
        }, 120);
        ToolTip.SetTip(balance, "自动平衡平均颜色；自定跟随色温和色调。点击缩略图可从单个像素进行设置。");
        Group(CameraRawGroup.Color, "颜色", Column(
            Ui.Row(8, balanceLabel, balance), temperature, tint,
            Slider("自然饱和度", s => s.Vibrance, -100, 100, (s, v) => s with { Vibrance = v }, tip: "优先增强不鲜艳的颜色，并保护肤色", track: Controls.SliderTracks.Chroma),
            Slider("饱和度", s => s.Saturation, -100, 100, (s, v) => s with { Saturation = v }, track: Controls.SliderTracks.Chroma)), open: true);

        // Color Grading, directly under Color and open like it, as upstream shows it: four wheels as sliders, then blending and balance.
        var wheelRows = new List<Control>();
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            wheelRows.Add(Heading(CameraRawGrading.Names[i]));
            // Saturation runs from gray to the wheel's hue and follows the Hue slider as it turns.
            Controls.SliderField saturation = null!;
            var hue = Slider("色相", s => s.Grading.Wheels[index].Hue, 0, 360, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Hue = v }) }, tip: "围绕色轮取值", track: Controls.SliderTracks.Spectrum(180));
            hue.Changed += v => saturation.Track = Controls.SliderTracks.Saturation(v);
            saturation = Slider("饱和度", s => s.Grading.Wheels[index].Saturation, 0, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Saturation = v }) }, tip: "着色的强度；为 0 时此色轮不起作用", track: Controls.SliderTracks.Saturation(initial.Grading.Wheels[i].Hue));
            wheelRows.Add(hue);
            wheelRows.Add(saturation);
            wheelRows.Add(Slider("明亮度", s => s.Grading.Wheels[index].Luminance, -100, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Luminance = v }) }, track: Controls.SliderTracks.Lightness));
        }
        wheelRows.Add(Heading("重叠"));
        wheelRows.Add(Slider("混合", s => s.Grading.Blending, 0, 100, (s, v) => s with { Grading = s.Grading with { Blending = v } }, tip: "三个色调色轮相互重叠的程度"));
        wheelRows.Add(Slider("平衡", s => s.Grading.Balance, -100, 100, (s, v) => s with { Grading = s.Grading with { Balance = v } }, tip: "负值偏向阴影，正值偏向高光"));
        Group(CameraRawGroup.Grading, "颜色分级", Column(wheelRows.ToArray()), open: true);

        // Effects.
        var glowStyles = Enum.GetValues<CameraRawGlowStyle>();
        var vignetteStyles = Enum.GetValues<CameraRawVignetteStyle>();
        static string StyleName(Enum style) => style switch
        {
            CameraRawGlowStyle.Diffusion => "扩散", CameraRawGlowStyle.Bloom => "泛光", CameraRawGlowStyle.Halation => "光晕",
            CameraRawVignetteStyle.HighlightPriority => "高光优先", CameraRawVignetteStyle.ColorPriority => "颜色优先",
            CameraRawVignetteStyle.PaintOverlay => "绘画叠加", _ => style.ToString()
        };
        Control StyleRow(string label, Control combo) { var text = Ui.Label(label); text.Width = LabelWidth; return Ui.Row(8, text, combo); }
        Group(CameraRawGroup.Effects, "效果", Column(
            Slider("纹理", s => s.Texture, -100, 100, (s, v) => s with { Texture = v }, tip: "增强或柔化细小细节"),
            Slider("清晰度", s => s.Clarity, -100, 100, (s, v) => s with { Clarity = v }, tip: "增强或柔化较大形体上的对比"),
            Slider("去除薄雾", s => s.Dehaze, -100, 100, (s, v) => s with { Dehaze = v }, tip: "调高时去除薄雾，调低时增加薄雾"),
            Heading("辉光"),
            Slider("辉光", s => s.Glow, 0, 100, (s, v) => s with { Glow = v }, tip: "从明亮区域向外扩散辉光"),
            StyleRow("样式", Ui.Combo(glowStyles, initial.GlowStyle, v => StyleName(v), v => Update(current with { GlowStyle = v }), 140)),
            Slider("范围", s => s.GlowRange, -100, 100, (s, v) => s with { GlowRange = v }, tip: "区域需多亮才会发光；调高辉光前不起作用"),
            Slider("扩散", s => s.GlowSpread, -100, 100, (s, v) => s with { GlowSpread = v }, tip: "辉光延伸的距离；调高辉光前不起作用"),
            Slider("暖调", s => s.GlowWarmth, -100, 100, (s, v) => s with { GlowWarmth = v }, tip: "由冷到暖；Halation 保持红色", track: Controls.SliderTracks.Temperature),
            Heading("暗角"),
            Slider("数量", s => s.VignetteAmount, -100, 100, (s, v) => s with { VignetteAmount = v }, tip: "压暗或提亮边缘；中心不受影响"),
            StyleRow("样式", Ui.Combo(vignetteStyles, initial.VignetteStyle, v => StyleName(v), v => Update(current with { VignetteStyle = v }), 140)),
            Slider("中点", s => s.VignetteMidpoint, 0, 100, (s, v) => s with { VignetteMidpoint = v }),
            Slider("圆度", s => s.VignetteRoundness, -100, 100, (s, v) => s with { VignetteRoundness = v }),
            Slider("羽化", s => s.VignetteFeather, 0, 100, (s, v) => s with { VignetteFeather = v }),
            Slider("高光", s => s.VignetteHighlights, 0, 100, (s, v) => s with { VignetteHighlights = v }, tip: "在暗角压暗时保护明亮边缘（高光优先）"),
            Heading("颗粒"),
            Slider("数量", s => s.GrainAmount, 0, 100, (s, v) => s with { GrainAmount = v }),
            Slider("大小", s => s.GrainSize, 0, 100, (s, v) => s with { GrainSize = v }),
            Slider("粗糙度", s => s.GrainRoughness, 0, 100, (s, v) => s with { GrainRoughness = v })));

        // Curve: the parametric sliders and a point curve per channel.
        var curveEditor = new CurveEditor { Width = 240, Height = 240 };
        var channels = new[] { "RGB", "红", "绿", "蓝" };
        CurvesAdjustment ToEditor(CameraRawCurve c)
        {
            var curves = new CurvesAdjustment();
            CurvePoint[][] points = [c.Rgb, c.Red, c.Green, c.Blue];
            for (var i = 0; i < 4; i++) curves = curves.WithChannel(i, points[i].Select(p => new CurvePoint(p.X * 255, p.Y * 255)));
            return curves;
        }
        curveEditor.Curves = ToEditor(initial.Curve);
        curveEditor.Changed += curves =>
        {
            CurvePoint[] Points(int channel) => curves.Channels[channel].Select(p => new CurvePoint(p.X / 255, p.Y / 255)).ToArray();
            Update(current with { Curve = current.Curve with { Rgb = Points(0), Red = Points(1), Green = Points(2), Blue = Points(3) } });
        };
        var channelPicker = Ui.Combo(channels, "RGB", c => c, c => curveEditor.Channel = Array.IndexOf(channels, c), 100);
        var resetCurve = Ui.TextButton("重置", () => { Update(current with { Curve = new CameraRawCurve() }); curveEditor.Curves = ToEditor(current.Curve); });
        resetCurve.MinWidth = 0;
        Group(CameraRawGroup.Curve, "曲线", Column(
            Heading("参数"),
            Slider("高光", s => s.Curve.Highlights, -100, 100, (s, v) => s with { Curve = s.Curve with { Highlights = v } }),
            Slider("亮调", s => s.Curve.Lights, -100, 100, (s, v) => s with { Curve = s.Curve with { Lights = v } }),
            Slider("暗调", s => s.Curve.Darks, -100, 100, (s, v) => s with { Curve = s.Curve with { Darks = v } }),
            Slider("阴影", s => s.Curve.Shadows, -100, 100, (s, v) => s with { Curve = s.Curve with { Shadows = v } }),
            Slider("饱和度微调", s => s.Curve.RefineSaturation, -100, 100, (s, v) => s with { Curve = s.Curve with { RefineSaturation = v } }, tip: "曲线同时改变饱和度的程度"),
            Heading("点"),
            Ui.Row(8, Ui.Label("通道", Palette.Secondary), channelPicker, resetCurve),
            curveEditor));

        // Color Mixer: one tab of eight families at a time.
        var mixerTabs = new[] { "色相", "饱和度", "明亮度" };
        var mixerTab = 0;
        var mixerRows = new StackPanel { Spacing = 6 };
        void BuildMixer()
        {
            mixerRows.Children.Clear();
            for (var family = 0; family < 8; family++)
            {
                var (t, f) = (mixerTab, family);
                // The track shows the family's neighbours, its gray to color, or its dark to light, whichever the tab adjusts.
                var centre = CameraRawMixer.Centers[family];
                var track = t switch { 0 => Controls.SliderTracks.Hue(centre), 1 => Controls.SliderTracks.Saturation(centre), _ => Controls.SliderTracks.Luminance(centre) };
                mixerRows.Children.Add(Slider(CameraRawMixer.Names[family], s => s.Mixer.Get(t, f), -100, 100, (s, v) => s with { Mixer = s.Mixer.With(t, f, v) }, track: track));
            }
        }
        BuildMixer();
        Group(CameraRawGroup.Mixer, "颜色混合器", Column(
            Ui.Row(8, Ui.Label("调整", Palette.Secondary), Ui.Combo(mixerTabs, "色相", t => t, t => { mixerTab = Array.IndexOf(mixerTabs, t); BuildMixer(); }, 130)),
            mixerRows));


        // Detail.
        Group(CameraRawGroup.Detail, "细节", Column(
            Heading("锐化"),
            Slider("数量", s => s.Detail.SharpenAmount, 0, 150, (s, v) => s with { Detail = s.Detail with { SharpenAmount = v } }),
            Slider("半径", s => s.Detail.SharpenRadius, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenRadius = v } }),
            Slider("细节", s => s.Detail.SharpenDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenDetail = v } }),
            Slider("蒙版", s => s.Detail.SharpenMasking, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenMasking = v } }, tip: "将锐化限制在边缘"),
            Heading("降噪"),
            Slider("明亮度", s => s.Detail.NoiseLuminance, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminance = v } }),
            Slider("细节", s => s.Detail.NoiseLuminanceDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceDetail = v } }),
            Slider("对比度", s => s.Detail.NoiseLuminanceContrast, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceContrast = v } }),
            Slider("颜色", s => s.Detail.NoiseColor, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColor = v } }),
            Slider("细节", s => s.Detail.NoiseColorDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorDetail = v } }),
            Slider("平滑度", s => s.Detail.NoiseColorSmoothness, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorSmoothness = v } })));

        // Optics.
        var o = initial.Optics;
        Group(CameraRawGroup.Optics, "光学", Column(
            Ui.Check("去除色差", o.RemoveChromaticAberration, v => Update(current with { Optics = current.Optics with { RemoveChromaticAberration = v } })),
            Ui.Check("启用镜头配置文件校正", o.EnableLensProfile, v => Update(current with { Optics = current.Optics with { EnableLensProfile = v } })),
            Slider("扭曲", s => s.Optics.ProfileDistortion, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileDistortion = v } }, tip: "配置文件强度；渲染后的图层不含镜头数据，因此这里缩放通用校正"),
            Slider("暗角", s => s.Optics.ProfileVignetting, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileVignetting = v } }),
            Heading("手动"),
            Slider("扭曲", s => s.Optics.Distortion, -100, 100, (s, v) => s with { Optics = s.Optics with { Distortion = v } }, tip: "正值拉直向外弯曲的线条，负值拉直向内弯曲的线条"),
            Heading("去边"),
            Slider("紫色数量", s => s.Optics.PurpleAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { PurpleAmount = v } }),
            Slider("紫色色相下限", s => s.Optics.PurpleHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueLow = v } }),
            Slider("紫色色相上限", s => s.Optics.PurpleHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueHigh = v } }),
            Slider("绿色数量", s => s.Optics.GreenAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { GreenAmount = v } }),
            Slider("绿色色相下限", s => s.Optics.GreenHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueLow = v } }),
            Slider("绿色色相上限", s => s.Optics.GreenHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueHigh = v } }),
            Heading("暗角"),
            Slider("数量", s => s.Optics.VignetteAmount, -100, 100, (s, v) => s with { Optics = s.Optics with { VignetteAmount = v } }, tip: "提亮角落以补偿镜头暗角"),
            Slider("中点", s => s.Optics.VignetteMidpoint, 0, 100, (s, v) => s with { Optics = s.Optics with { VignetteMidpoint = v } })));

        // Calibration.
        var c = initial.Calibration;
        var processes = Enumerable.Range(1, 6).ToArray();
        var processSummary = new TextBlock { Text = CameraRawCalibration.ProcessSummary(c.Process), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 310, FontSize = 11 };
        Group(CameraRawGroup.Calibration, "校准", Column(
            StyleRow("流程", Ui.Combo(processes, c.Process, p => $"版本 {p}", p => { Update(current with { Calibration = current.Calibration with { Process = p } }); processSummary.Text = CameraRawCalibration.ProcessSummary(p); }, 130)),
            processSummary,
            Slider("阴影色调", s => s.Calibration.ShadowTint, -100, 100, (s, v) => s with { Calibration = s.Calibration with { ShadowTint = v } }, track: Controls.SliderTracks.Tint, tip: "阴影中由偏绿到偏洋红"),
            Heading("红原色"),
            Slider("色相", s => s.Calibration.RedHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedHue = v } }, track: Controls.SliderTracks.Hue(0)),
            Slider("饱和度", s => s.Calibration.RedSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedSaturation = v } }, track: Controls.SliderTracks.Saturation(0)),
            Heading("绿原色"),
            Slider("色相", s => s.Calibration.GreenHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenHue = v } }, track: Controls.SliderTracks.Hue(120)),
            Slider("饱和度", s => s.Calibration.GreenSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenSaturation = v } }, track: Controls.SliderTracks.Saturation(120)),
            Heading("蓝原色"),
            Slider("色相", s => s.Calibration.BlueHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueHue = v } }, track: Controls.SliderTracks.Hue(240)),
            Slider("饱和度", s => s.Calibration.BlueSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueSaturation = v } }, track: Controls.SliderTracks.Saturation(240))));

        var scroll = new ScrollViewer { Content = groups, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        var head = Ui.Column(6, histogram, Ui.Row(0, preview), readout);
        histogram.HorizontalAlignment = preview.HorizontalAlignment = HorizontalAlignment.Left;
        // Save Look: the grade's color stages as a .cube any editor can load. What reads neighbours or the position
        // (Effects, Detail, Optics) cannot go into a table and is named in the file and in the note beside the button.
        var saved = Ui.Label("", Palette.Secondary, 11);
        saved.VerticalAlignment = VerticalAlignment.Center;
        saved.TextTrimming = TextTrimming.CharacterEllipsis;
        saveLook = Ui.TextButton("存储外观…", async () =>
        {
            var grade = Rendered();
            if (saveLookPath == null || LookBake.ColorOnly(grade).IsIdentity || await saveLookPath() is not { } path) return;
            var leftOut = LookBake.LeftOutOf(grade).Select(GroupName).ToList();
            try
            {
                var comment = leftOut.Count > 0 ? $"从 Composa 的 Camera Raw 滤镜存储，不含 {string.Join(", ", leftOut)}（查找表无法容纳此项）" : "从 Composa 的 Camera Raw 滤镜存储";
                await Task.Run(() => File.WriteAllText(path, LookBake.Bake(grade, 33, title).ToCube(title, comment)));
                saved.Text = leftOut.Count > 0 ? $"已存储 {Path.GetFileName(path)}，不含 {string.Join(", ", leftOut)}" : $"已存储 {Path.GetFileName(path)}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await Prompts.Alert(owner, "无法存储外观", error.Message);
            }
        });
        saveLook.IsEnabled = !LookBake.ColorOnly(Rendered()).IsIdentity;
        ToolTip.SetTip(saveLook, "将调色存储为 .cube 查找表供其他编辑器使用。效果、细节和光学基于邻域或位置更改像素，因此不包含在内。");
        var footer = Ui.Row(8, saveLook, saved);
        footer.IsVisible = saveLookPath != null;
        var body = Ui.Column(10, head, Ui.Separator(false), scroll, footer);
        body.Width = 360;
        var dialog = new DialogWindow("Camera Raw 滤镜", body);
        dialog.Opened += (_, _) => { timer.Stop(); timer.Start(); };
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        return accepted ? Rendered() : null;
    }
}
