using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Core.Tests;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>A GIMP file through the window: the report, declining it, opening it, and placing it into a document.</summary>
public class XcfImportUiTests
{
    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Opening_a_gimp_file_reports_its_conversions_and_cancel_applies_nothing()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var writer = new XcfWriter { Version = 20, Width = 300, Height = 200, Compression = 2 };
        writer.Guides.Add((100, false));
        writer.Layers.Add(new XcfWriterLayer { Name = "Badge", Width = 60, Height = 60, X = 200, Y = 100, Mode = 1 }.Filled(new SKColor(0xD0, 0x40, 0x30)));
        writer.Layers.Add(new XcfWriterLayer { Name = "Headline", Width = 180, Height = 40, X = 30, Y = 30, Text = "(text \"Hello\")", Effects = 1 }.Filled(new SKColor(0x20, 0x30, 0x50)));
        writer.Layers.Add(new XcfWriterLayer { Name = "Background", Width = 300, Height = 200 }.Filled(new SKColor(0xF2, 0xE8, 0xD5)));
        var path = Path.Combine(Path.GetTempPath(), "composa-xcf-" + Guid.NewGuid().ToString("N") + ".xcf");
        File.WriteAllBytes(path, writer.Build());
        try
        {
            var opening = window.OpenPaths([path]);
            await Pump(() => window.OwnedWindows.Count > 0);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("打开 " + Path.GetFileName(path) + "？", dialog.Title);
            var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("GIMP 特性", text);
            Assert.Contains("Headline", text);
            Assert.Contains("重新键入", text);
            Assert.Contains("效果（GIMP 滤镜）", text);
            Assert.Contains("溶解", text);
            Screenshots.Save(dialog, "42-xcf-conversions");

            // Cancel: nothing opens.
            dialog.Close(false);
            await Pump(() => opening.IsCompleted);
            Assert.Null(window.Session);

            // Import: the file becomes a document with its layers bottom to top and its guide.
            opening = window.OpenPaths([path]);
            await Pump(() => window.OwnedWindows.Count > 0);
            Assert.Single(window.OwnedWindows).Close(true);
            await Pump(() => opening.IsCompleted);
            var session = window.Session!;
            Assert.Equal(Path.GetFileNameWithoutExtension(path), session.Title);
            Assert.Equal(["Background", "Headline", "Badge"], session.Document.Layers.Select(l => l.Name));
            Assert.Equal((300, 200), (session.Document.Width, session.Document.Height));
            Assert.Single(session.Document.Guides);
            Screenshots.Save(window, "43-xcf-opened");

            // Placed into that document, a second copy arrives inside a folder named after the file.
            opening = window.PlacePaths([path], new SKPoint(150, 100));
            await Pump(() => window.OwnedWindows.Count > 0);
            Assert.Single(window.OwnedWindows).Close(true);
            await Pump(() => opening.IsCompleted);
            var folder = session.ActiveLayer!;
            Assert.True(folder.IsGroup);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), folder.Name);
            Assert.Equal(3, folder.Children.Count);
            Assert.Equal("导入 GIMP 文件", session.History.UndoName);
            Assert.Single(session.Document.Guides);   // Placing does not bring the file's guides.
        }
        finally { TempFiles.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_gimp_file_with_nothing_to_convert_opens_without_asking()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var writer = new XcfWriter { Version = 11, Width = 50, Height = 40 };
        writer.Layers.Add(new XcfWriterLayer { Name = "Only", Width = 50, Height = 40 }.Filled(SKColors.Teal));
        var path = Path.Combine(Path.GetTempPath(), "composa-xcf-" + Guid.NewGuid().ToString("N") + ".xcf");
        File.WriteAllBytes(path, writer.Build());
        try
        {
            var opening = window.OpenPaths([path]);
            await Pump(() => opening.IsCompleted);
            Assert.Empty(window.OwnedWindows);
            Assert.Equal("Only", Assert.Single(window.Session!.Document.Layers).Name);
        }
        finally { TempFiles.Delete(path); }
    }
}
