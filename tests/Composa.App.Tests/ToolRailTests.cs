using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The tool rail's groups: a tool with variants opens them beside its button when held or right-clicked.</summary>
public class ToolRailTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public ToolRailTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>Where a control in another top level (the flyout's popup) lies in the window's coordinates.</summary>
    private Point InWindow(Control control) => window.PointToClient(control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)));

    private async Task Hold(Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        var until = DateTime.UtcNow + ToolButton.HoldDelay + TimeSpan.FromMilliseconds(250);
        while (DateTime.UtcNow < until) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
    }

    private List<string> BarTexts() => window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("options"))
        .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void Right_clicking_the_lasso_opens_its_group_beside_it_and_a_click_picks_polygonal()
    {
        var lasso = window.RailButton(Tool.Lasso);
        window.MouseDown(Center(lasso), MouseButton.Right);
        window.MouseUp(Center(lasso), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(lasso.IsGroupOpen);
        var items = lasso.GroupItems;
        Assert.Equal(["套索", "多边形套索"], items.Select(i => (string)i.Header!));
        Assert.All(items, i => Assert.Equal(new KeyGesture(Key.L), i.InputGesture));
        Assert.All(items, i => Assert.NotNull(i.Icon));
        Assert.Equal([true, false], items.Select(i => i.IsChecked));
        // Nothing is highlighted until the pointer is over a tool.
        Assert.All(items, i => Assert.False(i.IsSelected));
        // The group opens on the button's right, level with it, as Photoshop's does.
        var button = lasso.PointToScreen(new Point(lasso.Bounds.Width, 0));
        var first = items[0].PointToScreen(new Point(0, 0));
        Assert.True(first.X >= button.X, $"the group starts at {first.X}, left of the button's edge at {button.X}");
        Assert.True(Math.Abs(first.Y - button.Y) < 20, $"the group starts at {first.Y}, the button at {button.Y}");
        Assert.Equal(Tool.Move, session.Tool);
        Screenshots.Save(window, "tool-group-rail");
        Screenshots.Save(TopLevel.GetTopLevel(items[0])!, "tool-group-flyout");

        var popup = TopLevel.GetTopLevel(items[1])!;
        var polygonal = items[1].TranslatePoint(new Point(items[1].Bounds.Width / 2, items[1].Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(polygonal, MouseButton.Left);
        popup.MouseUp(polygonal, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(lasso.IsGroupOpen);
        Assert.Equal(Tool.Lasso, session.Tool);
        Assert.Equal(LassoKind.Polygonal, session.LassoKind);
        Assert.True(lasso.IsChecked);
        Assert.Equal("多边形套索", lasso.Current!.Name);
        Assert.StartsWith("多边形套索 (L)", ToolTip.GetTip(lasso) as string);
        // The bar names the lasso in use and has no box to switch it.
        Assert.Contains("多边形套索", BarTexts());
        Assert.Empty(window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("options")).GetVisualDescendants().OfType<ComboBox>());
    }

    [AvaloniaFact]
    public async Task Holding_the_marquee_opens_its_group_and_letting_go_on_ellipse_picks_it()
    {
        var marquee = window.RailButton(Tool.Marquee);
        await Hold(Center(marquee));
        Assert.True(marquee.IsGroupOpen);
        Assert.Equal(Tool.Move, session.Tool);

        var ellipse = marquee.GroupItems[1];
        Assert.Equal("椭圆选框", ellipse.Header);
        window.MouseMove(InWindow(ellipse), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ellipse.IsSelected);
        Assert.False(marquee.GroupItems[0].IsSelected);
        window.MouseUp(InWindow(ellipse), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(marquee.IsGroupOpen);
        Assert.Equal(Tool.Marquee, session.Tool);
        Assert.Equal(MarqueeKind.Ellipse, session.MarqueeKind);
        Assert.Contains("椭圆选框", BarTexts());
    }

    [AvaloniaFact]
    public async Task Letting_go_of_a_hold_on_the_button_leaves_the_group_open_without_picking()
    {
        var shape = window.RailButton(Tool.Shape);
        await Hold(Center(shape));
        window.MouseUp(Center(shape), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shape.IsGroupOpen);
        Assert.Equal(Tool.Move, session.Tool);
        Assert.Equal(["矩形", "圆角矩形", "椭圆", "直线"], shape.GroupItems.Select(i => (string)i.Header!));
        Assert.All(shape.GroupItems, i => Assert.Equal(new KeyGesture(Key.U), i.InputGesture));
    }

    [AvaloniaFact]
    public async Task A_click_picks_the_tool_the_button_shows_without_opening_its_group()
    {
        session.LassoKind = LassoKind.Polygonal;
        var lasso = window.RailButton(Tool.Lasso);
        window.MouseDown(Center(lasso), MouseButton.Left);
        window.MouseUp(Center(lasso), MouseButton.Left);
        for (var i = 0; i < 50; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }

        Assert.Equal(Tool.Lasso, session.Tool);
        Assert.Equal(LassoKind.Polygonal, session.LassoKind);
        Assert.False(lasso.IsGroupOpen);
        // A tool on its own has no group to open.
        var crop = window.RailButton(Tool.Crop);
        Assert.False(crop.HasGroup);
        window.MouseDown(Center(crop), MouseButton.Right);
        window.MouseUp(Center(crop), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.False(crop.IsGroupOpen);
    }

    [AvaloniaFact]
    public void Brush_and_eraser_share_a_group_with_their_own_keys_and_the_buttons_follow_the_keys()
    {
        // The smear modes are a group too, and its key steps through them.
        var smear = window.RailButton(Tool.Smear);
        session.SmearMode = SmearMode.Liquify;
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SmearMode.Blur, session.SmearMode);
        Assert.Equal("模糊", smear.Current!.Name);
        Assert.Contains("模糊", BarTexts());

        var brush = window.RailButton(Tool.Brush);
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("橡皮擦", brush.Current!.Name);
        Assert.Contains("橡皮擦", BarTexts());

        smear.OpenGroup(fromKeyboard: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["液化", "模糊", "抹擦", "减淡", "加深"], smear.GroupItems.Select(i => (string)i.Header!));
        // Opened from the keyboard, the arrow keys start from the current mode.
        Assert.Equal([false, true, false, false, false], smear.GroupItems.Select(i => i.IsSelected));
        Screenshots.Save(TopLevel.GetTopLevel(smear.GroupItems[0])!, "tool-group-smear");

        brush.OpenGroup();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["画笔", "橡皮擦"], brush.GroupItems.Select(i => (string)i.Header!));
        Assert.Equal([new KeyGesture(Key.B), new KeyGesture(Key.E)], brush.GroupItems.Select(i => i.InputGesture));
        Assert.Equal([false, true], brush.GroupItems.Select(i => i.IsChecked));
    }
}
