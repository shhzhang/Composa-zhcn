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

public class LayersPanelInputTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;
    private readonly LayersPanel panel;

    public LayersPanelInputTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(300, 200, SKColors.White);
        window.AddSession(session);
        for (var i = 0; i < 3; i++) session.AddBlankLayer();
        Dispatcher.UIThread.RunJobs();
        panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
    }

    /// <summary>Rows top to bottom, as shown.</summary>
    private List<Border> Rows() => panel.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is Layer).ToList();

    private Point Center(Border row, double x) => row.TranslatePoint(new Point(x, row.Bounds.Height / 2), window)!.Value;

    /// <summary>Rows are rebuilt when the selection changes, so the point is taken before the press.</summary>
    private void Click(Border row, double x, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = Center(row, x);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Clicking_selects_and_ctrl_click_extends()
    {
        var rows = Rows();
        Assert.Equal(4, rows.Count);
        Click(rows[2], 150);
        Assert.Equal(((Layer)rows[2].Tag!).Id, session.Document.ActiveLayerId);
        Click(Rows()[0], 150, RawInputModifiers.Control);
        Assert.Equal(2, session.Document.SelectedLayerIds.Count);
    }

    [AvaloniaFact]
    public void Swiping_down_the_eye_column_hides_each_layer_passed()
    {
        var rows = Rows();
        window.MouseDown(Center(rows[0], 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i < 3; i++)
        {
            window.MouseMove(Center(Rows()[i], 16), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(Center(Rows()[2], 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var layers = Rows().Select(r => (Layer)r.Tag!).ToList();
        Assert.Equal([false, false, false, true], layers.Select(l => l.Visible));
        // After the swipe ended, merely hovering changes nothing.
        window.MouseMove(Center(Rows()[3], 16));
        Assert.True(((Layer)Rows()[3].Tag!).Visible);
    }

    [AvaloniaFact]
    public void Dragging_a_row_reorders_layers()
    {
        var rows = Rows();
        var top = (Layer)rows[0].Tag!;
        window.MouseDown(Center(rows[0], 150), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var target = Rows()[2];
        var below = target.TranslatePoint(new Point(150, target.Bounds.Height - 4), window)!.Value;
        window.MouseMove(Center(Rows()[1], 150), RawInputModifiers.LeftMouseButton);
        window.MouseMove(below, RawInputModifiers.LeftMouseButton);
        window.MouseUp(below, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.Document.Layers.IndexOf(top)); // Was index 3 (top); now just above the background.
        Assert.Equal("调整图层顺序", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Double_click_renames_even_an_unselected_layer()
    {
        var row = Rows()[2];
        var layer = (Layer)row.Tag!;
        Click(row, 150);
        Click(Rows()[2], 150);
        var box = Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement());
        box.Text = "Sky";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sky", session.Document.Find(layer.Id)!.Name);
    }
}

public class LayerContextMenuTests
{
    [AvaloniaFact]
    public void The_row_menu_lists_the_layer_mask_and_visibility_actions_and_follows_the_layer()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(300, 200, SKColors.White);
        window.AddSession(session);
        var layer = session.AddBlankLayer();
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        List<string> Headers() => panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag == layer).ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
        MenuItem Item(string header) => panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag == layer).ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header! == header);

        var headers = Headers();
        Assert.Equal(["复制图层", "重命名…", "删除图层"], headers.Take(3));
        Assert.Contains("创建剪贴蒙版", headers);
        Assert.Contains("编组所选图层", headers);
        Assert.Contains("移出文件夹", headers);
        Assert.Contains("向下合并", headers);
        Assert.Contains("添加蒙版", headers);
        Assert.Contains("停用蒙版", headers);
        Assert.Contains("删除蒙版", headers);
        Assert.Equal("隐藏图层", headers[^1]);
        Assert.DoesNotContain("图层效果", headers);
        Assert.False(Item("移出文件夹").IsEnabled);
        Assert.False(Item("删除蒙版").IsEnabled);
        Assert.Equal(["显示全部 (白色)", "隐藏全部 (黑色)"], Item("添加蒙版").Items.OfType<MenuItem>().Select(i => (string)i.Header!));

        session.AddMask(layer);
        session.SetVisible(layer, false);
        Dispatcher.UIThread.RunJobs();
        headers = Headers();
        // Adding a mask targets it, so Delete at the top now deletes the mask, as the mask section's own item does.
        Assert.Equal("删除蒙版", headers[2]);
        session.EditingMask = false;
        session.NotifyLayersChanged();
        Dispatcher.UIThread.RunJobs();
        headers = Headers();
        Assert.Equal("删除图层", headers[2]);
        Assert.False(Item("添加蒙版").IsEnabled);
        Assert.True(Item("删除蒙版").IsEnabled);
        Assert.Equal("显示图层", headers[^1]);

        // Several selected: the menu says so, and Rename needs one layer.
        session.SelectLayer(session.Document.Layers[0].Id, extend: true);
        Dispatcher.UIThread.RunJobs();
        headers = Headers();
        Assert.Equal("复制所选图层", headers[0]);
        Assert.Equal("删除所选图层", headers[2]);
        Assert.False(Item("重命名…").IsEnabled);
    }
}
