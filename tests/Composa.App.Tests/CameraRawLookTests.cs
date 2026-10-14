using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Save Look in the Camera Raw panel: the grade's color stages as a .cube, with the spatial groups named as left out.</summary>
public class CameraRawLookTests
{
    private static async Task Settle(Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(until(), "The panel did not get there.");
    }

    [AvaloniaFact]
    public async Task Save_look_writes_the_color_stages_and_names_what_it_left_out()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var photo = Composa.Rendering.Pixels.NewColor(160, 100);   // Kept alive: the panel's timer reads it until the panel is closed.
        photo.Erase(new SKColor(128, 110, 100));
        var folder = Path.Combine(Path.GetTempPath(), "composa-raw-look-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Sunset.cube");
        try
        {
            // A grade of nothing a table can hold leaves the button disabled.
            _ = CameraRawDialog.Show(window, new CameraRawSettings { VignetteAmount = -40 }, photo, _ => { }, () => photo, () => Task.FromResult<string?>(path), "Sunset");
            Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Last();
            Button Save() => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "存储外观…");
            Assert.False(Save().IsEnabled);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();

            var grade = new CameraRawSettings { Exposure = 0.5, Saturation = 20, VignetteAmount = -40, GrainAmount = 15 };
            _ = CameraRawDialog.Show(window, grade, photo, _ => { }, () => photo, () => Task.FromResult<string?>(path), "Sunset");
            Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.Last();
            Assert.True(Save().IsEnabled);
            Save().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(() => File.Exists(path) && dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("已存储 Sunset.cube") == true));
            Screenshots.Save(dialog, "33-camera-raw-save-look");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "已存储 Sunset.cube，不含 效果");
            var lattice = ColorLattice.Load(path);
            Assert.Equal(("Sunset", 33), (lattice.Title, lattice.Size));
            Assert.Contains("不含 效果（查找表无法容纳此项）", File.ReadAllText(path));
            // The table is the grade's color stages: brighter and more saturated than the identity, vignette and grain aside.
            var (r, g, b) = lattice.Sample(0.5f, 0.4f, 0.4f);
            Assert.True(r > 0.5f && r - b > 0.1f, $"{(r, g, b)}");
            Assert.Single(window.OwnedWindows);                                          // Saving keeps the panel open.

            // Without a place to save to, the row is not there at all.
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            _ = CameraRawDialog.Show(window, grade, photo, _ => { }, () => photo);
            Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.Last();
            Assert.False(Save().IsEffectivelyVisible);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
