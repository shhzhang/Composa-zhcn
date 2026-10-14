using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The Detect choice through the window: the options bar, a model-backed click and command, the progress window and the Remove Background dialog.</summary>
public class SubjectDetectUiTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public SubjectDetectUiTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(320, 240, SKColors.Transparent);
        // A dark, noisy photo with a red disc at (160, 100), which U²-Net finds every time.
        var photo = Pixels.NewColor(320, 240);
        var random = new Random(3);
        for (var y = 0; y < 240; y++)
        for (var x = 0; x < 320; x++)
        {
            var inside = (x - 160) * (x - 160) + (y - 100) * (y - 100) < 60 * 60;
            var n = (byte)random.Next(0, 24);
            photo.SetPixel(x, y, inside ? new SKColor(225, 40, 30) : new SKColor((byte)(35 + n), (byte)(40 + n), (byte)(45 + n)));
        }
        session.AddImageLayer("photo", photo, new SKPoint(160, 120));
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    /// <summary>Runs queued UI work until the condition holds, for work that comes back from another thread.</summary>
    private static void PumpUntil(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "the awaited work did not finish in time");
    }

    [AvaloniaFact]
    public void The_object_selection_bar_offers_the_detect_choice_and_the_settings_remember_it()
    {
        window.SelectTool(Tool.Wand);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WandMode.Object, session.WandMode);
        Assert.Equal(SubjectDetect.Any, session.Detect);
        var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Width == 128);
        Assert.Equal(["任何主体", "人物", "纯色背景"], ((IEnumerable<string>)combo.ItemsSource!).ToList());
        Assert.Equal(0, combo.SelectedIndex);
        Screenshots.Save(window, "80-object-selection-detect");
        combo.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SubjectDetect.Backdrop, session.Detect);
        Assert.Equal(SubjectDetect.Backdrop, window.Settings.Detect);

        // A second document starts from the choice, as the other tool settings do.
        var another = EditorSession.NewCanvas(100, 100, SKColors.White);
        window.AddSession(another);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SubjectDetect.Backdrop, another.Detect);
    }

    [AvaloniaFact]
    public void Remove_background_sits_in_the_image_menu_and_not_among_the_filters()
    {
        var items = window.GetLogicalDescendants().OfType<MenuItem>().ToList();
        var removeBackground = items.Single(m => m.Header as string == "移除背景…");
        Assert.Equal("图像(_I)", (removeBackground.Parent as MenuItem)?.Header as string);
        var filter = items.Single(m => m.Header as string == "滤镜(_I)");
        Assert.DoesNotContain(filter.Items.OfType<MenuItem>(), m => (m.Header as string)?.StartsWith("移除背景") == true);
    }

    [AvaloniaFact]
    public void A_click_with_the_model_selects_the_object_once_the_model_has_answered()
    {
        window.SelectTool(Tool.Wand);
        session.WandMode = WandMode.Object;
        session.Detect = SubjectDetect.Any;
        window.MouseDown(At(160, 100), MouseButton.Left);
        window.MouseUp(At(160, 100), MouseButton.Left);
        PumpUntil(() => session.Selection != null);
        Assert.Equal("对象选择", session.History.UndoName);
        var bounds = SelectionMask.Bounds(session.Selection!, 128);
        Assert.InRange(bounds.Left, 85, 115);
        Assert.InRange(bounds.Right, 205, 235);
        Assert.Equal(0, session.Selection!.GetPixel(10, 10).Alpha);

        // The backdrop deselects, through the cached matte, so without waiting on the model.
        window.MouseDown(At(10, 10), MouseButton.Left);
        window.MouseUp(At(10, 10), MouseButton.Left);
        PumpUntil(() => session.Selection == null);
        Screenshots.Save(window, "81-object-selection-model");
    }

    [AvaloniaFact]
    public void A_box_dragged_with_the_object_tool_runs_the_model_on_the_box_alone()
    {
        window.SelectTool(Tool.Wand);
        session.WandMode = WandMode.Object;
        session.Detect = SubjectDetect.Any;
        window.MouseDown(At(90, 30), MouseButton.Left);
        window.MouseMove(At(230, 170));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Canvas.IsDragging);
        Screenshots.Save(window, "86-object-box-drag");
        window.MouseUp(At(230, 170), MouseButton.Left);
        PumpUntil(() => session.Selection != null);
        Assert.Equal("对象选择", session.History.UndoName);
        var bounds = SelectionMask.Bounds(session.Selection!, 128);
        Assert.InRange(bounds.Left, 85, 115);
        Assert.InRange(bounds.Right, 205, 235);
        Assert.Equal(0, session.Selection!.GetPixel(60, 20).Alpha); // Inside the box but backdrop.
        Assert.Equal(0, session.Selection.GetPixel(300, 200).Alpha); // Outside the box.
    }

    [AvaloniaFact]
    public void Select_subject_shows_the_progress_window_while_the_model_runs_and_selects_the_disc()
    {
        var delay = ProgressWindow.Delay;
        ProgressWindow.Delay = TimeSpan.FromMilliseconds(1);
        try
        {
            session.Detect = SubjectDetect.Any;
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control | RawInputModifiers.Alt);
            PumpUntil(() => window.OwnedWindows.OfType<ProgressWindow>().Any(w => w.IsVisible), 5);
            Screenshots.Save(window.OwnedWindows.OfType<ProgressWindow>().Single(), "82-finding-the-subject");
            PumpUntil(() => session.Selection != null);
            Assert.Equal("选择主体", session.History.UndoName);
            Assert.True(session.Selection!.GetPixel(160, 100).Alpha > 200);
            Assert.True(session.Selection.GetPixel(10, 10).Alpha < 40);
            Assert.Empty(window.OwnedWindows.OfType<ProgressWindow>());
        }
        finally { ProgressWindow.Delay = delay; }
    }

    [AvaloniaFact]
    public void A_cancelled_progress_window_leaves_the_selection_alone()
    {
        var delay = ProgressWindow.Delay;
        ProgressWindow.Delay = TimeSpan.FromMilliseconds(1);
        try
        {
            session.Detect = SubjectDetect.Any;
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control | RawInputModifiers.Alt);
            PumpUntil(() => window.OwnedWindows.OfType<ProgressWindow>().Any(w => w.IsVisible), 5);
            var progress = window.OwnedWindows.OfType<ProgressWindow>().Single();
            progress.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); // Cancel is the dialog's cancel button; Escape presses it.
            PumpUntil(() => !window.OwnedWindows.OfType<ProgressWindow>().Any(), 10);
            // The model's answer, if it still came, must not have been applied.
            Thread.Sleep(1500);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(session.Selection);
            Assert.False(session.CanUndo && session.History.UndoName == "选择主体");
        }
        finally { ProgressWindow.Delay = delay; }
    }

    [AvaloniaFact]
    public async Task The_remove_background_dialog_offers_detect_and_tolerance_follows_it()
    {
        var previews = new List<FilterSettings>();
        _ = AdjustmentDialogs.EditFilter(window, new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Any }, previews.Add);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Last();
        var combo = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        var tolerance = dialog.GetVisualDescendants().OfType<Controls.SliderField>().Single();
        Assert.Equal(0, combo.SelectedIndex);
        Assert.False(tolerance.IsEnabled);
        Screenshots.Save(dialog, "83-remove-background-detect");
        combo.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.True(tolerance.IsEnabled);
        for (var i = 0; i < 5; i++) { await Task.Delay(60); Dispatcher.UIThread.RunJobs(); } // The preview timer needs the loop to run.
        Assert.Equal(SubjectDetect.Backdrop, previews.Last().Detect);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        // Editing a mask, only the plain backdrop applies.
        _ = AdjustmentDialogs.EditFilter(window, new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = SubjectDetect.Backdrop }, _ => { }, canDetect: false);
        Dispatcher.UIThread.RunJobs();
        dialog = window.OwnedWindows.Last();
        Assert.False(dialog.GetVisualDescendants().OfType<ComboBox>().Single().IsEnabled);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
