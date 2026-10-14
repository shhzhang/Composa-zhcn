using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class GuidesUiTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public GuidesUiTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        session.View = session.View with { ShowRulers = true, ShowGrid = true };
        window.Canvas.ViewOptionsChanged(rulersWereShown: false);
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
    private Point OnCanvas(double x, double y) => window.Canvas.TranslatePoint(new Point(x, y), window)!.Value;

    private void Drag(Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Guides_come_out_of_the_rulers_move_with_the_move_tool_snap_moves_and_go_back_into_the_ruler()
    {
        // Pull a horizontal guide from the top ruler down to y = 100, and a vertical one to x = 250.
        Drag(OnCanvas(400, CanvasView.RulerThickness / 2), At(300, 100));
        Drag(OnCanvas(CanvasView.RulerThickness / 2, 300), At(250, 200));
        Assert.Equal(2, session.Guides.Count);
        var horizontal = session.Guides.Single(g => g.Axis == GuideAxis.Horizontal);
        Assert.Equal(100, horizontal.Position);
        Assert.Equal(250, session.Guides.Single(g => g.Axis == GuideAxis.Vertical).Position);
        Assert.Equal("新建参考线", session.History.UndoName);

        // With the Move tool the guide itself drags.
        Drag(At(150, 100), At(150, 160));
        Assert.Equal(160, session.Guides.Single(g => g.Id == horizontal.Id).Position);
        Assert.Equal("移动参考线", session.History.UndoName);

        // A layer moved near the vertical guide snaps its edge onto it.
        var box = Rendering.Pixels.NewColor(60, 40);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(100, 300));
        Dispatcher.UIThread.RunJobs();
        Drag(At(100, 300), At(100 + 117, 300)); // Right edge would land at 247; the guide at 250 pulls it over.
        Assert.Equal(190, layer.Transform.X);
        Screenshots.Save(window, "25-rulers-guides-grid");

        // Dropping a guide back onto a ruler deletes it.
        Drag(At(150, 160), OnCanvas(400, 5));
        Assert.Single(session.Guides);
        Assert.Equal("删除参考线", session.History.UndoName);

        // Locked guides stay put.
        session.View = session.View with { LockGuides = true };
        Drag(At(250, 300), At(300, 300));
        Assert.Equal(250, session.Guides.Single().Position);
        Assert.Equal(GuideAxis.Vertical, session.Guides.Single().Axis);
    }

    [AvaloniaFact]
    public void View_menu_shortcuts_toggle_rulers_grid_guides_and_snap()
    {
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
        Assert.False(session.View.ShowRulers);
        window.KeyPressQwerty(PhysicalKey.Quote, RawInputModifiers.Control);
        Assert.False(session.View.ShowGrid);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control);
        Assert.False(session.View.ShowGuides);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(session.View.Snap);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(session.View.LockGuides);
    }

    [AvaloniaFact]
    public void Grid_settings_show_on_the_canvas_as_they_change_and_cancel_puts_them_back()
    {
        session.View = session.View with { ShowGrid = false };
        var original = session.View;
        var item = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "网格设置…");
        Window Open()
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            return window.OwnedWindows.Last();
        }

        var dialog = Open();
        Assert.True(session.View.ShowGrid); // The grid shows while the dialog is open, whatever View > Show says.
        var boxes = dialog.GetVisualDescendants().OfType<NumericUpDown>().ToList();
        var combos = dialog.GetVisualDescendants().OfType<ComboBox>().ToList();
        var ok = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "确定");
        boxes[0].Value = 100;
        combos[0].SelectedIndex = (int)GridColorPreset.Cyan;
        combos[1].SelectedIndex = (int)GridStyle.Dots;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new LayoutGrid { Spacing = 100, Subdivisions = 8 }, session.View.Grid); // Every change previews at once.
        Assert.Equal((GridColorPreset.Cyan, GridStyle.Dots), (session.View.GridAppearance.Preset, session.View.GridAppearance.Style));
        Screenshots.Save(dialog, "31-grid-settings");
        Screenshots.Save(window, "25b-grid-cyan-dots");
        // More subdivisions than pixels between gridlines cannot be accepted, and the canvas keeps the last good grid.
        boxes[0].Value = 4;
        Dispatcher.UIThread.RunJobs();
        Assert.False(ok.IsEnabled);
        Assert.Equal(100, session.View.Grid.Spacing);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "恢复默认").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(ok.IsEnabled);
        Assert.Equal(new LayoutGrid(), session.View.Grid);
        Assert.Equal(GridColorPreset.LightGray, session.View.GridAppearance.Preset);
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(original, session.View); // Cancel: the grid is hidden again and drawn as before.

        // OK keeps the settings, still without showing the grid, and remembers them for the next launch.
        dialog = Open();
        dialog.GetVisualDescendants().OfType<NumericUpDown>().First().Value = 32;
        dialog.Close(true);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.View.ShowGrid);
        Assert.Equal(32, session.View.Grid.Spacing);
        Assert.Equal(32, window.Settings.View.Grid.Spacing);
    }
}
