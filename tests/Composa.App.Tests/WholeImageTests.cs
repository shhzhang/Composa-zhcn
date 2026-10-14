using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Commands that take the whole picture: copying it, and the tab menu that offers it.</summary>
public class WholeImageTests
{
    private static MainWindow Open()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
    }

    private static void Menu(MainWindow window, string header) =>
        Click(window.GetLogicalDescendants().OfType<Menu>().First().GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == header));

    private static string Status(MainWindow window) => string.Join(" | ", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static List<MenuItem> TabMenu(MainWindow window, EditorSession session)
    {
        var tab = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.ContextMenu?.Items.OfType<MenuItem>().Any(i => i.Header as string == "复制图像") == true)
            .ElementAt(window.Sessions.ToList().IndexOf(session));
        return tab.ContextMenu!.Items.OfType<MenuItem>().ToList();
    }

    [AvaloniaFact]
    public void A_copy_says_what_went_to_the_clipboard()
    {
        var window = Open();
        var session = EditorSession.NewCanvas(320, 200, SKColors.White);
        window.AddSession(session);

        Menu(window, "合并拷贝");
        Assert.Contains("已拷贝 320 × 200 像素", Status(window));
        session.SelectRect(new SKRect(10, 10, 110, 60));
        Menu(window, "合并拷贝");
        Assert.Contains("已拷贝 100 × 50 像素", Status(window));
        session.Deselect();
        Menu(window, "拷贝");
        Assert.Contains("已拷贝 1 个图层", Status(window));
        // The next command puts the tool hint back.
        Menu(window, "全部");
        Assert.DoesNotContain("已拷贝", Status(window));
    }

    [AvaloniaFact]
    public void The_tab_menu_copies_the_whole_picture_whatever_is_selected()
    {
        var window = Open();
        var first = EditorSession.NewCanvas(320, 200, SKColors.White);
        var second = EditorSession.NewCanvas(64, 48, SKColors.White);
        window.AddSession(first);
        window.AddSession(second);
        first.SelectRect(new SKRect(10, 10, 110, 60));

        // The menu acts on its own tab, not on the current one.
        Click(TabMenu(window, first).Single(i => i.Header as string == "复制图像"));
        Assert.Equal((320, 200), (EditorSession.Clipboard!.Pixels.Width, EditorSession.Clipboard.Pixels.Height));
        Assert.Null(EditorSession.CopiedLayers);
        Assert.Same(second, window.Sessions[^1]);
        Assert.Contains("已拷贝 320 × 200 像素", Status(window));
        Assert.NotNull(first.Selection);
    }

    [AvaloniaFact]
    public void The_tab_menu_closes_the_other_documents_and_opens_only_a_saved_file_s_folder()
    {
        var window = Open();
        var first = EditorSession.NewCanvas(320, 200, SKColors.White);
        var second = EditorSession.NewCanvas(64, 48, SKColors.White);
        window.AddSession(first);
        window.AddSession(second);

        var menu = TabMenu(window, first);
        Assert.False(menu.Single(i => i.Header as string == "在文件夹中显示").IsEnabled);
        Click(menu.Single(i => i.Header as string == "关闭其他"));
        Assert.Equal([first], window.Sessions);
        Assert.False(TabMenu(window, first).Single(i => i.Header as string == "关闭其他").IsEnabled);
    }

    [AvaloniaFact]
    public void Duplicate_opens_a_copy_in_a_new_tab_from_the_menu_and_from_the_tab()
    {
        var window = Open();
        var original = EditorSession.NewCanvas(320, 200, SKColors.White);
        original.SuggestedName = "Poster";
        window.AddSession(original);

        Menu(window, "复制");
        Assert.Equal(["Poster", "Poster 拷贝"], window.Sessions.Select(s => s.Title));
        Assert.Same(window.Sessions[1], window.Session);
        Click(TabMenu(window, original).Single(i => i.Header as string == "复制"));
        Assert.Equal(["Poster", "Poster 拷贝", "Poster 拷贝"], window.Sessions.Select(s => s.Title));
        Assert.Equal((320, 200), (window.Session!.Document.Width, window.Session.Document.Height));
    }
}
