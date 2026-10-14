using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Select > Color Range: a panel beside the canvas while clicks on the canvas pick colors.</summary>
public class ColorRangeUiTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public ColorRangeUiTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        var picture = Rendering.Pixels.NewColor(600, 400);
        using (var canvas = new SKCanvas(picture))
        {
            using var red = new SKPaint { Color = new SKColor(200, 30, 30) };
            using var green = new SKPaint { Color = new SKColor(30, 200, 30) };
            canvas.DrawRect(new SKRect(0, 0, 300, 400), red);
            canvas.DrawRect(new SKRect(300, 0, 600, 400), green);
        }
        Rendering.Pixels.Invalidate(picture);
        session.AddImageLayer("picture", picture);
        window.AddSession(session);
        window.SelectTool(Tool.Brush);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    private void Click(float x, float y, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(At(x, y), MouseButton.Left, modifiers);
        window.MouseUp(At(x, y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private ColorRangeWindow Open()
    {
        window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "色彩范围…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return window.OwnedWindows.OfType<ColorRangeWindow>().Single();
    }

    [AvaloniaFact]
    public void Clicks_on_the_canvas_pick_colors_while_the_panel_is_open_and_ok_keeps_the_selection()
    {
        var panel = Open();
        Assert.NotNull(session.ColorRange);
        var preview = panel.GetVisualDescendants().OfType<Image>().Single();
        Assert.Null(preview.Source);
        // With the Brush chosen, a click picks a color instead of painting: the red half is selected, the green not.
        Click(100, 100);
        Assert.NotNull(session.Selection);
        Assert.Equal(255, session.Selection!.GetPixel(100, 100).Alpha);
        Assert.Equal(0, session.Selection.GetPixel(400, 100).Alpha);
        Assert.NotNull(preview.Source);
        Assert.Equal(new SKColor(200, 30, 30), session.Composite().GetPixel(100, 100)); // Nothing was painted.
        Screenshots.Save(panel, "33-color-range-panel");
        Screenshots.Save(window, "33-color-range");
        // Shift adds the green, Alt takes it away again, and the Remove eyedropper does the same without a modifier.
        Click(400, 100, RawInputModifiers.Shift);
        Assert.Equal(255, session.Selection!.GetPixel(400, 100).Alpha);
        Click(400, 100, RawInputModifiers.Alt);
        Assert.Equal(0, session.Selection!.GetPixel(400, 100).Alpha);
        Click(100, 100); // A plain click with the Sample eyedropper starts over, forgetting the removed color.
        var buttons = panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(b => b is not CheckBox).ToList(); // A CheckBox is a ToggleButton too.
        Assert.Equal(3, buttons.Count);
        Assert.True(buttons[0].IsChecked);
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ColorRangeSample.Add, session.ColorRange!.SampleMode);
        Assert.True(buttons[1].IsChecked);
        Click(400, 100);
        Assert.Equal(255, session.Selection!.GetPixel(400, 100).Alpha);
        // Fuzziness and Invert come from the panel.
        var fuzziness = panel.GetVisualDescendants().OfType<SliderField>().Single();
        Assert.Equal(40, fuzziness.Value);
        panel.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.ColorRange.Invert);
        Assert.Null(session.Selection); // Everything was in the two colors, so the inverse is empty.
        panel.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        // OK keeps the selection as one undo step, and the panel goes away.
        panel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(session.ColorRange);
        Assert.Empty(window.OwnedWindows.OfType<ColorRangeWindow>());
        Assert.Equal("色彩范围", session.History.UndoName);
        Assert.Equal(255, session.Selection!.GetPixel(400, 100).Alpha);
        // The Brush paints again.
        Click(100, 100);
        Assert.Equal("画笔", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Escape_cancels_and_another_edit_keeps_what_the_panel_shows()
    {
        session.SelectRect(new SKRect(0, 0, 50, 50));
        var before = session.Selection;
        var panel = Open();
        Click(400, 100);
        Assert.Equal(255, session.Selection!.GetPixel(400, 100).Alpha);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(session.ColorRange);
        Assert.Empty(window.OwnedWindows.OfType<ColorRangeWindow>());
        Assert.Same(before, session.Selection);
        // A menu command while the panel is open commits the color range first, so nothing shown is lost.
        panel = Open();
        Click(400, 100);
        session.SelectAll();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(session.ColorRange);
        Assert.Empty(window.OwnedWindows.OfType<ColorRangeWindow>());
        session.Undo();
        Assert.Equal("色彩范围", session.History.UndoName);
        Assert.Equal(0, session.Selection!.GetPixel(100, 100).Alpha);
        _ = panel;
    }
}
