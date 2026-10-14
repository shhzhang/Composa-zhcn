using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class EnhanceResolutionUiTests
{
    [AvaloniaFact]
    public void The_layer_menu_command_enhances_a_scaled_up_layer_behind_the_progress_window()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(400, 300, SKColors.White);
        var small = Pixels.NewColor(40, 30);
        small.Erase(new SKColor(200, 60, 40));
        var layer = session.AddImageLayer("logo", small, new SKPoint(200, 150));
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        var item = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "增强分辨率");
        var layerMenu = (MenuItem)item.Parent!;
        Assert.Equal("图层(_L)", layerMenu.Header as string);

        // Shown at its own size, there is nothing to gain and the item is disabled.
        layerMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        Assert.False(item.IsEnabled);
        session.Apply("place", () => layer.Transform = layer.Transform with { X = 100, Y = 75, Width = 120, Height = 90 });
        layerMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        Assert.True(item.IsEnabled);

        var delay = ProgressWindow.Delay;
        ProgressWindow.Delay = TimeSpan.FromMilliseconds(1);
        try
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (session.History.UndoName != "增强分辨率" && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("增强分辨率", session.History.UndoName);
            var after = session.ActiveLayer!;
            Assert.Equal((120, 90), (after.Pixels!.Width, after.Pixels.Height));
            Assert.Equal(new SKRect(100, 75, 220, 165), after.Bounds);
            Assert.Empty(window.OwnedWindows.OfType<ProgressWindow>());
        }
        finally { ProgressWindow.Delay = delay; }
    }
}
