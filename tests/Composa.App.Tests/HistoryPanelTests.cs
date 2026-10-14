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
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class HistoryPanelTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public HistoryPanelTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(300, 200, SKColors.White);
        window.AddSession(session);
        session.Fill(SKColors.Red, "Red");
        session.AddBlankLayer();
        session.SelectRect(new SKRect(20, 20, 120, 90));
        session.Fill(SKColors.Blue, "Blue");
        session.Deselect();
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    private HistoryPanel Panel => window.GetVisualDescendants().OfType<HistoryPanel>().Single();

    /// <summary>The rows top to bottom, oldest state first.</summary>
    private List<Border> Rows() => Panel.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is History.Step).ToList();

    private List<string> Names() => Rows().Select(r => ((History.Step)r.Tag!).Name).ToList();

    private Point PointOf(int row) => Rows()[row].TranslatePoint(new Point(60, HistoryPanel.RowHeight / 2), window)!.Value;

    private void Click(int row)
    {
        var point = PointOf(row);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private void Menu(string header)
    {
        window.GetLogicalDescendants().OfType<Menu>().First().GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == header)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void The_panel_lists_every_step_under_the_layers_and_goes_back_several_at_once()
    {
        Assert.Equal(["新建画布", "Red", "新建图层", "矩形选框", "Blue", "取消选择"], Names());
        Screenshots.Save(window, "40-history-panel");

        var restores = 0;
        session.LayersChanged += () => restores++;
        Click(1);
        Assert.Equal(1, session.History.CurrentIndex);
        Assert.Equal(1, restores); // Four steps back in one move.
        Assert.Single(session.Document.Layers);
        // The steps Redo brings back stay listed, dimmed, until an edit drops them.
        Assert.Equal(6, Rows().Count);
        Assert.Equal(Avalonia.Media.FontStyle.Italic, Rows()[4].GetVisualDescendants().OfType<TextBlock>().Single().FontStyle);
        Screenshots.Save(window, "41-history-gone-back");

        Click(4);
        Assert.Equal(4, session.History.CurrentIndex);
        Assert.Equal(2, session.Document.Layers.Count);
        session.Fill(SKColors.Green, "Green");
        Pump();
        Assert.Equal(["新建画布", "Red", "新建图层", "矩形选框", "Blue", "Green"], Names());
    }

    [AvaloniaFact]
    public void A_scrub_follows_the_pointer_and_stops_at_the_ends()
    {
        var restores = 0;
        session.LayersChanged += () => restores++;
        window.MouseDown(PointOf(5), MouseButton.Left);
        window.MouseMove(PointOf(4));
        window.MouseMove(PointOf(3));
        window.MouseMove(PointOf(2));
        Pump();
        Assert.Equal(2, session.History.CurrentIndex);
        // One move per row crossed at most; the headless input runs the dispatcher after every move, so the folding of
        // moves that arrive within one frame cannot show here.
        Assert.Equal(3, restores);
        // Past the top of the list the scrub stops at the first state.
        var above = PointOf(0) - new Point(0, 200);
        window.MouseMove(above);
        Pump();
        Assert.Equal(0, session.History.CurrentIndex);
        window.MouseMove(PointOf(3));
        window.MouseUp(PointOf(3), MouseButton.Left);
        Pump();
        Assert.Equal(3, session.History.CurrentIndex);
        // Once released, moving over the list does nothing.
        window.MouseMove(PointOf(1));
        Pump();
        Assert.Equal(3, session.History.CurrentIndex);
    }

    [AvaloniaFact]
    public void A_click_while_typing_commits_the_text_first()
    {
        window.SelectTool(Tool.Text);
        var editor = session.BeginText(new SKPoint(40, 150));
        editor.Insert("Hi");
        Pump();
        Click(2);
        Assert.False(session.IsEditingText);
        Assert.Equal("文字", session.History.Steps[^1].Name);
        Assert.Equal(2, session.History.CurrentIndex);
    }

    [AvaloniaFact]
    public void The_saved_state_carries_a_disk_and_going_back_to_it_clears_the_modified_mark()
    {
        session.MarkSaved(Path.Combine(Path.GetTempPath(), "history-panel.cmps"));
        Pump();
        bool Marked(int row) => Rows()[row].GetVisualDescendants().OfType<Border>().Any(b => ToolTip.GetTip(b) is "这是文件中保存的状态");
        Assert.True(Marked(5));
        Assert.False(session.IsModified);
        Click(2);
        Assert.True(session.IsModified);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == session.Title + " •");
        Click(5);
        Assert.False(session.IsModified);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == session.Title + " •");
    }

    [AvaloniaFact]
    public void Each_tab_shows_its_own_history()
    {
        var other = EditorSession.NewCanvas(100, 100, SKColors.White);
        window.AddSession(other);
        Pump();
        Assert.Equal(["新建画布"], Names());
        other.Fill(SKColors.Red);
        Pump();
        Assert.Equal(["新建画布", "填充"], Names());
    }

    [AvaloniaFact]
    public void The_window_menu_shows_and_hides_the_panel_and_the_header_collapses_it_and_both_are_remembered()
    {
        Menu("历史记录");
        Assert.Empty(window.GetVisualDescendants().OfType<HistoryPanel>());
        Assert.False(window.Settings.Dock["History"].Visible);
        Menu("历史记录");
        Assert.Single(window.GetVisualDescendants().OfType<HistoryPanel>());

        var header = window.GetVisualDescendants().OfType<Border>().Single(b => ToolTip.GetTip(b) is "折叠 历史记录");
        var point = header.TranslatePoint(new Point(20, 10), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
        Assert.True(window.Settings.Dock["History"].Collapsed);
        Assert.Empty(window.GetVisualDescendants().OfType<HistoryPanel>());
        Screenshots.Save(window, "42-history-collapsed");
    }

    [AvaloniaFact]
    public void Every_step_gets_the_icon_of_what_it_did()
    {
        Assert.Equal(Icons.Marquee, HistoryPanel.IconFor("矩形选框"));
        Assert.Equal(Icons.Marquee, HistoryPanel.IconFor("羽化选区"));
        Assert.Equal(Icons.Marquee, HistoryPanel.IconFor("色彩范围"));
        Assert.Equal(Icons.Drop, HistoryPanel.IconFor("模糊"));
        Assert.Equal(Icons.Effects, HistoryPanel.IconFor("高斯模糊"));
        Assert.Equal(Icons.Effects, HistoryPanel.IconFor("投影"));
        Assert.Equal(Icons.Adjust, HistoryPanel.IconFor("曲线"));
        Assert.Equal(Icons.Adjust, HistoryPanel.IconFor("新建调整图层"));
        Assert.Equal(Icons.Text, HistoryPanel.IconFor("更改文字样式"));
        Assert.Equal(Icons.Mask, HistoryPanel.IconFor("添加图层蒙版"));
        Assert.Equal(Icons.Crop, HistoryPanel.IconFor("顺时针旋转画布 90°"));
        Assert.Equal(Icons.Layers, HistoryPanel.IconFor("重命名图层"));
    }
}
