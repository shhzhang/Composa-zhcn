using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;
using TextAlignment = Composa.Model.TextAlignment;

namespace Composa.App;

public sealed partial class MainWindow
{
    private static readonly Composa.Vision.SubjectDetect[] Detects = [Composa.Vision.SubjectDetect.Any, Composa.Vision.SubjectDetect.Person, Composa.Vision.SubjectDetect.Backdrop];

    /// <summary>Rebuilds the bar under the tabs with the current tool's settings.</summary>
    private void RebuildOptions()
    {
        refreshOptions = null;
        if (session == null) { optionsHost.Child = null; return; }
        var s = session;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "options" } };
        void Add(params Control[] controls) => row.Children.AddRange(controls);
        Control Title(string text) => Ui.Label(text, weight: Avalonia.Media.FontWeight.SemiBold);
        // A tool that comes in a group is picked from its rail button, so the bar only names the one in use.
        string Chosen() => toolButtons[s.Tool].Current!.Name;

        switch (s.Tool)
        {
            case Tool.Move:
                Add(Title("移动"));
                BuildTransformFields(row);
                break;
            case Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear:
                Add(Title(s.Tool switch { Tool.SpotHealing => "污点修复", Tool.CloneStamp => "仿制图章", _ => Chosen() }));
                var size = Ui.SliderField("大小", s.Brush.Size, 1, 500, v => s.Brush = s.Brush with { Size = v });
                var hardness = Ui.SliderField("硬度", s.Brush.Hardness * 100, 0, 100, v => s.Brush = s.Brush with { Hardness = v / 100 });
                Add(size, hardness);
                Action<double>? setOpacity = null;
                if (s.Tool != Tool.SpotHealing)
                {
                    var opacity = Ui.SliderField(s.Tool == Tool.Smear ? "强度" : "不透明度", s.Brush.Opacity * 100, 1, 100, v => s.Brush = s.Brush with { Opacity = v / 100 });
                    setOpacity = v => opacity.Value = v;
                    Add(opacity);
                }
                Action<double>? setSmoothing = null;
                if (s.Tool == Tool.Brush)
                {
                    // Healing, cloning and smearing keep their own feel; only Paint and Erase trail the pointer.
                    var smoothing = Ui.SliderField("平滑", s.Brush.Smoothing, 0, 100, v => s.Brush = s.Brush with { Smoothing = v });
                    setSmoothing = v => smoothing.Value = v;
                    Add(smoothing);
                }
                if (s.Tool == Tool.CloneStamp)
                    Add(Ui.Check("对齐", s.CloneAligned, v => s.CloneAligned = v), Ui.Check("对所有图层取样", s.SampleAllLayers, v => s.SampleAllLayers = v));
                refreshOptions = () =>
                {
                    size.Value = Math.Min(500, s.Brush.Size);
                    hardness.Value = s.Brush.Hardness * 100;
                    setOpacity?.Invoke(s.Brush.Opacity * 100);
                    setSmoothing?.Invoke(s.Brush.Smoothing);
                };
                break;
            case Tool.Marquee or Tool.Lasso or Tool.Wand:
                Add(Title(Chosen()));
                if (s.Tool == Tool.Wand)
                {
                    if (s.WandMode == WandMode.Wand)
                        Add(Ui.SliderField("容差", s.WandTolerance, 0, 255, v => s.WandTolerance = (int)v, width: 130), Ui.Check("连续的", s.WandContiguous, v => s.WandContiguous = v));
                    else
                    {
                        var edge = Ui.Number(s.ObjectEdgeOffset, -10, 10, v => s.ObjectEdgeOffset = (int)v, 1, "0", 52);
                        ToolTip.SetTip(edge, "正值使检测到的轮廓向内收紧；负值使其向外放松");
                        Add(Ui.Row(5, Ui.Scrub(Ui.Label("边缘", Palette.Secondary), edge), edge, Ui.Label("px", Palette.Secondary)));
                        var detect = Ui.Combo(Detects, s.Detect, Composa.Vision.SubjectFinder.DisplayName, v => { s.Detect = v; RememberToolSettings(); }, 128);
                        ToolTip.SetTip(detect, "主体如何被识别：在本机运行、适用于任意主体或人物的模型，或紧贴图片边缘的纯色背景。同一选择也用于「选择 > 主体」和「移除背景」。");
                        Add(Ui.Row(5, Ui.Label("检测", Palette.Secondary), detect));
                    }
                    Add(Ui.Check("对所有图层取样", s.SampleAllLayers, v => s.SampleAllLayers = v));
                }
                else Add(Ui.SliderField("羽化", s.Feather, 0, 100, v => s.Feather = v));
                Add(Ui.Separator());
                // Modify buttons with their amounts, as in the macOS tool bar; both need a selection.
                Control Modify(string title, Func<int> get, Action<int> set, int max, Action apply)
                {
                    var button = Flat(title, apply);
                    var amount = Ui.Number(get(), 1, max, v => set((int)v), 1, "0", 50);
                    var pair = Ui.Row(3, button, amount);
                    refreshOptions += () => button.IsEnabled = amount.IsEnabled = s.Selection != null;
                    return pair;
                }
                Add(Modify("扩展", () => s.SelectionExpandAmount, v => s.SelectionExpandAmount = v, 500, () => s.ExpandSelection(s.SelectionExpandAmount)),
                    Modify("收缩", () => s.SelectionContractAmount, v => s.SelectionContractAmount = v, 500, () => s.ContractSelection(s.SelectionContractAmount)),
                    Modify("羽化", () => s.SelectionFeatherAmount, v => s.SelectionFeatherAmount = v, 250, () => s.FeatherSelection(s.SelectionFeatherAmount)));
                Add(Ui.Separator(), Flat("全选", s.SelectAll), Flat("取消选择", s.Deselect), Flat("反选", s.InvertSelection));
                refreshOptions?.Invoke();
                break;
            case Tool.Gradient:
                Add(Title("渐变"),
                    Ui.Combo(new[] { "线性", "径向" }, s.GradientRadial ? "径向" : "线性", v => v, v => s.GradientRadial = v == "径向", 90),
                    Ui.Check("前景色到透明", s.GradientToTransparent, v => { s.GradientToTransparent = v; UpdateStatus(); }));
                var gradientOpacity = Ui.SliderField("不透明度", s.GradientOpacity * 100, 1, 100, v => s.GradientOpacity = v / 100);
                Add(gradientOpacity);
                refreshOptions = () => gradientOpacity.Value = s.GradientOpacity * 100;
                break;
            case Tool.Shape:
                Add(Title(Chosen()));
                if (s.ShapeKind == ShapeKind.Line) Add(Ui.SliderField("宽度", s.ShapeLineWidth, 1, 100, v => s.ShapeLineWidth = v));
                else if (s.ShapeKind == ShapeKind.RoundedRectangle) Add(Ui.SliderField("圆角半径", s.ShapeCornerRadius, 0, 400, v => s.ShapeCornerRadius = v, width: 150));
                Add(Ui.Label(s.ShapeKind == ShapeKind.Line ? "使用前景色绘制 · Shift 吸附到 45°" : "使用前景色填充", Palette.Secondary));
                break;
            case Tool.Text:
                Add(Title("文字"));
                BuildTextFields(row);
                break;
            case Tool.Crop:
                Add(Title("裁剪"));
                var ratio = Ui.Combo(EditorSession.CropRatios, s.CropRatio, r => r, r => { s.CropRatio = r; canvas.ChangeCropRatio(); }, 100);
                ToolTip.SetTip(ratio, "拖动时裁剪框保持的形状");
                Add(Ui.Row(6, Ui.Label("比例", Palette.Secondary), ratio));
                var readout = Ui.Label("在画布上拖动以选择要保留的区域", Palette.Secondary);
                var apply = Ui.TextButton("应用", canvas.ApplyCrop, accent: true);
                var cancel = Ui.TextButton("取消", canvas.CancelCrop);
                Add(readout, apply, cancel, Ui.Separator(), Flat("修边透明边缘", () => { if (!s.Trim()) ShowProblem("没有可修边的内容：没有边缘是透明的。"); else canvas.Fit(); }));
                refreshOptions = () =>
                {
                    apply.IsEnabled = cancel.IsEnabled = canvas.HasCrop;
                    readout.Text = canvas.CropRect is { } crop ? $"{Math.Round(crop.Width)} × {Math.Round(crop.Height)} px" : "在画布上拖动以选择要保留的区域";
                };
                refreshOptions();
                break;
            case Tool.Eyedropper:
                Add(Title("吸管"), Ui.Label("对合并后的图像取样", Palette.Secondary));
                break;
            case Tool.Hand or Tool.Zoom:
                Add(Title(s.Tool == Tool.Hand ? "抓手" : "缩放"), Flat("适合屏幕", canvas.Fit), Flat("100%", () => canvas.ZoomTo(1)), Flat("200%", () => canvas.ZoomTo(2)));
                break;
        }
        optionsHost.Child = row;
    }

    /// <summary>The Type bar: font, size, style, color, alignment and spacing for the text being typed (or the next text).</summary>
    private void BuildTextFields(StackPanel row)
    {
        var s = session!;
        var updating = false;
        void Change(Func<TextStyle, TextStyle> change)
        {
            if (!updating) s.ChangeTextStyle(change);
        }
        var style = s.CurrentTextStyle;
        var families = EditorSession.FontFamilies;
        var family = families.Contains(style.FontFamily) ? style.FontFamily : families.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? families.FirstOrDefault() ?? style.FontFamily;
        // A long font name is cut off rather than widening the bar.
        // While typing, the family, Bold and Italic land on the selected letters only, as the color does.
        void ChangeFace(Func<TextFace, TextFace> change) { if (!updating) s.SetTextFace(change); }
        var font = Ui.Combo(families, family, f => f, f => ChangeFace(face => face with { FontFamily = f }), 190);
        font.MaxWidth = 190;
        var size = Ui.Number(style.Size, 1, 2000, v => Change(st => st with { Size = v }), 1, "0.#", 64);
        var bold = Ui.Check("粗体", style.Bold, v => ChangeFace(face => face with { Bold = v }));
        var italic = Ui.Check("斜体", style.Italic, v => ChangeFace(face => face with { Italic = v }));
        var swatch = new Border { Width = 34, Height = 22, CornerRadius = new Avalonia.CornerRadius(3), BorderBrush = Avalonia.Media.Brushes.White, BorderThickness = new Avalonia.Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        ToolTip.SetTip(swatch, "文字颜色");
        swatch.PointerPressed += async (_, _) =>
        {
            // The picker's working color shows on the canvas as it changes. Text being typed takes it as any bar change;
            // a text layer that is not open for typing shows it inside an edit that is taken back when the dialog
            // closes, and the pick then goes through Change so it undoes as one step like any other bar change.
            // While typing, the color goes on the selected letters only (or all of them when nothing is selected).
            var original = s.CurrentTextStyle;
            var editing = s.TextEdit;
            var layer = editing == null && s.ActiveLayer is { Text: not null } active ? active : null;
            if (layer != null) s.Begin("更改文字样式");
            void Preview(SKColor color)
            {
                var opaque = (uint)color | 0xFF000000;
                if (editing != null && s.TextEdit == editing) s.SetTextColor(opaque);
                else if (layer != null) s.PreviewTextStyle(layer, st => st.WithColor(opaque, 0, 0));
            }
            var picked = await Dialogs.Prompts.Color(this, "文字颜色", new SKColor(s.CurrentTextColor), editing != null || layer != null ? Preview : null);
            if (layer != null) s.Cancel();
            if (picked is not { } color)
            {
                if (editing != null && s.TextEdit == editing) s.RestoreTextColors(original);
                return;
            }
            if (!updating) s.SetTextColor((uint)color | 0xFF000000);
            // The text color is the foreground color: picking one in the Type bar moves the swatch too.
            s.Foreground = color;
            UpdateColors();
            refreshOptions?.Invoke();
        };
        var alignments = new Dictionary<TextAlignment, ToggleButton>();
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (alignment, icon) in new[] { (TextAlignment.Left, Icons.AlignLeft), (TextAlignment.Center, Icons.AlignCenter), (TextAlignment.Right, Icons.AlignRight) })
        {
            var button = new ToggleButton { Classes = { "tool" }, Width = 30, Height = 26, Content = Icons.Create(icon, 15), IsChecked = style.Alignment == alignment };
            ToolTip.SetTip(button, "Align " + alignment.ToString().ToLowerInvariant());
            button.Click += (_, _) => { Change(st => st with { Alignment = alignment }); refreshOptions?.Invoke(); };
            alignments[alignment] = button;
            alignRow.Children.Add(button);
        }
        var tracking = Ui.Number(style.Tracking, -100, 1000, v => Change(st => st with { Tracking = v }), 1, "0", 58);
        ToolTip.SetTip(tracking, "字距：每个字符之后额外的间距，以像素为单位");
        var leading = Ui.Number(style.Leading, 0, 5000, v => Change(st => st with { Leading = v }), 1, "0", 58);
        ToolTip.SetTip(leading, "行距：基线到基线的行高，以像素为单位。0 为自动：大小的 120%");
        var done = Ui.TextButton("完成", () => { s.FinishText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); }, accent: true);
        var cancel = Ui.TextButton("取消", () => { s.CancelText(); canvas.Focus(); RebuildOptions(); UpdateStatus(); });
        var edit = Ui.TextButton("编辑文字", () => { if (s.ActiveLayer is { Text: not null } layer) BeginTextEdit(layer); });
        foreach (var button in new[] { done, cancel, edit }) button.MinWidth = 0;
        row.Children.AddRange([font, Ui.Row(4, size, Ui.Scrub(Ui.Label("px", Palette.Secondary), size)), bold, italic, swatch, alignRow,
            Ui.Row(5, Ui.Scrub(Ui.Label("字距", Palette.Secondary), tracking), tracking), Ui.Row(5, Ui.Scrub(Ui.Label("行距", Palette.Secondary), leading), leading), Ui.Separator()]);
        if (s.IsEditingText) row.Children.AddRange([done, cancel]);
        else { edit.IsEnabled = s.ActiveLayer?.Text != null; row.Children.Add(edit); }
        refreshOptions = () =>
        {
            var current = s.CurrentTextStyle;
            updating = true;
            size.Value = (decimal)current.Size;
            tracking.Value = (decimal)current.Tracking;
            leading.Value = (decimal)current.Leading;
            var face = s.CurrentTextFace;
            bold.IsChecked = face.Bold;
            italic.IsChecked = face.Italic;
            // Selected letters in more than one family: the menu says so instead of naming one.
            var uniform = s.CurrentUniformTextFamily;
            var index = uniform == null ? -1 : families.ToList().IndexOf(uniform);
            font.PlaceholderText = uniform ?? "(多种)";
            if (font.SelectedIndex != index) font.SelectedIndex = index;
            swatch.Background = new Avalonia.Media.SolidColorBrush(new SKColor(s.CurrentTextColor).ToAvalonia());
            foreach (var (alignment, button) in alignments) button.IsChecked = current.Alignment == alignment;
            updating = false;
        };
        refreshOptions();
    }

    private static Button Flat(string text, Action action)
    {
        var button = Ui.TextButton(text, action);
        button.MinWidth = 0;
        return button;
    }

    private void BuildTransformFields(StackPanel row)
    {
        var s = session!;
        var layer = s.ActiveLayer;
        var autoSelect = Ui.Check("自动选择", canvas.AutoSelect, v => { canvas.AutoSelect = v; RememberToolSettings(); });
        ToolTip.SetTip(autoSelect, "单击图层的像素即可选择该图层。关闭时，拖动可从任意位置移动当前图层；Ctrl+单击仍可选取。");
        row.Children.Add(autoSelect);
        row.Children.Add(Ui.Check("变换控件", canvas.ShowTransformControls, v => { canvas.ShowTransformControls = v; canvas.InvalidateVisual(); RememberToolSettings(); }));
        if (layer?.Pixels == null)
        {
            row.Children.Add(Ui.Label(layer == null ? "未选择图层" : layer.IsGroup ? "移动文件夹中的所有图层" : "此图层没有像素", Palette.Secondary));
            return;
        }
        var updating = false;
        NumericUpDown Field(string label, Func<LayerTransform, double> get, Func<LayerTransform, double, LayerTransform> set, double min, double max, string format)
        {
            var box = Ui.Number(get(layer.Transform), min, max, v =>
            {
                if (updating || s.Document.Find(layer.Id) is not { } live) return;
                s.SetTransform(live, set(live.Transform, v));
            }, 1, format, 74);
            row.Children.Add(Ui.Row(5, Ui.Scrub(Ui.Label(label, Palette.Secondary), box), box));
            return box;
        }
        var x = Field("X", t => t.X, (t, v) => t with { X = v }, -100000, 100000, "0.#");
        var y = Field("Y", t => t.Y, (t, v) => t with { Y = v }, -100000, 100000, "0.#");
        var w = Field("W", t => t.Width, (t, v) => t with { Width = Math.Max(1, v), Height = Math.Max(1, t.Height * v / Math.Max(1e-6, t.Width)) }, 1, 100000, "0.#");
        var h = Field("H", t => t.Height, (t, v) => t with { Height = Math.Max(1, v) }, 1, 100000, "0.#");
        var angle = Field("∠", t => t.Rotation, (t, v) => t with { Rotation = v }, -360, 360, "0.##");
        refreshOptions = () =>
        {
            if (s.Document.Find(layer.Id) is not { } live) return;
            updating = true;
            x.Value = (decimal)live.Transform.X; y.Value = (decimal)live.Transform.Y;
            w.Value = (decimal)live.Transform.Width; h.Value = (decimal)live.Transform.Height;
            angle.Value = (decimal)live.Transform.Rotation;
            updating = false;
        };
    }
}
