using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>File > Export Look as .cube: what goes into the table, what is left out, and how fine it is.</summary>
public class LookExportTests
{
    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_dialog_lists_what_is_baked_and_what_is_left_out_and_returns_the_size()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var asked = LookDialogs.ExportLook(window, ["Curves 1", "Vivid Slide"], [("Grain 1", "changes from place to place"), ("Gaussian Blur 1", "reads the pixels around each pixel")], 33);
        Pump();
        var dialog = window.OwnedWindows.Last();
        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Curves 1", texts);
        Assert.Contains("Vivid Slide", texts);
        Assert.Contains("Grain 1 changes from place to place", texts);
        Assert.Contains("Gaussian Blur 1 reads the pixels around each pixel", texts);
        var sizes = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(3, sizes.ItemCount);
        Assert.Equal("33 个点", sizes.SelectedItem);
        Screenshots.Save(dialog, "32-export-look");
        sizes.SelectedIndex = 2;
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "导出…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(65, await asked);

        // Cancel answers nothing, and a document without a bakeable layer never gets as far as the dialog.
        asked = LookDialogs.ExportLook(window, ["Curves 1"], [], 17);
        Pump();
        dialog = window.OwnedWindows.Last();
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("未包含的图层") == true);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Null(await asked);

        var session = EditorSession.NewCanvas(40, 30, SKColors.White);
        window.AddSession(session);
        session.AddAdjustmentLayer(new GrainAdjustment { Amount = 20 });
        Pump();
        window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "导出 Look 为 .cube…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Assert.Empty(window.OwnedWindows);
        session.AddAdjustmentLayer(new InvertAdjustment());
        Pump();
        window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "导出 Look 为 .cube…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        dialog = Assert.Single(window.OwnedWindows);
        Assert.Contains("颗粒 1 随位置变化", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        dialog.Close();
        Pump();
    }
}
