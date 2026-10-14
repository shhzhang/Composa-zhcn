using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The Color Lookup dialog: every look drawn on the picture, a file of your own, and the layer it makes.</summary>
public class ColorLookupUiTests
{
    private static readonly string[] Identity2 = ["TITLE \"Warm test\"", "LUT_3D_SIZE 2", "0 0 0", "1 0 0", "0 1 0", "1 1 0", "0 0 1", "1 0 1", "0 1 1", "1 1 1"];

    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The dialog reports a change through a short timer, which needs the loop to run: waits until <paramref name="until"/> holds.</summary>
    private static async Task Settle(Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(until(), "The dialog did not report the change.");
    }

    private static SKBitmap Picture()
    {
        var gradient = new SKBitmap(new SKImageInfo(256, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var x = 0; x < 256; x++) for (var y = 0; y < 64; y++) gradient.SetPixel(x, y, new SKColor((byte)x, (byte)(x / 2 + y), (byte)(255 - x)));
        return gradient;
    }

    /// <summary>The look tiles, in the order shown.</summary>
    private static List<Border> Tiles(Window dialog) => dialog.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is string).ToList();

    private static void Click(Window dialog, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(point, MouseButton.Left);
        dialog.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Press(Window dialog, string text) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task Every_look_is_drawn_on_the_picture_and_a_click_picks_it()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        using var picture = Picture();
        var reported = new List<Adjustment>();
        _ = AdjustmentDialogs.Edit(window, new ColorLookupAdjustment(), reported.Add, null, SKColors.Black, SKColors.White, () => picture, () => Task.FromResult<string?>(null));
        Pump();
        var dialog = window.OwnedWindows.Last();
        var tiles = Tiles(dialog);
        Assert.Equal(Looks.Names, tiles.Select(t => (string)t.Tag!));
        Assert.All(tiles, t => Assert.Equal(Brushes.Transparent, t.BorderBrush));          // nothing chosen yet
        var images = tiles.Select(t => (Avalonia.Media.Imaging.Bitmap)t.GetVisualDescendants().OfType<Image>().Single().Source!).ToList();
        Assert.All(images, i => Assert.Equal(72, i.PixelSize.Width));                       // the picture, reduced, not a blank
        Assert.Equal(72 * 64 / 256, images[0].PixelSize.Height);
        Screenshots.Save(dialog, "29-color-lookup");

        var count = reported.Count;
        Click(dialog, tiles[3]);
        await Settle(() => reported.Count > count);
        var picked = Assert.IsType<ColorLookupAdjustment>(reported.Last());
        Assert.Equal(("鲜艳反转片", Looks.Find("鲜艳反转片")!.Id, 100d), (picked.Source, picked.LatticeId, picked.Amount));
        Assert.Equal(Palette.Accent, tiles[3].BorderBrush);
        Assert.Equal(Brushes.Transparent, tiles[0].BorderBrush);
        var amount = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "数量");
        Assert.Equal(100, amount.Reset);
        Screenshots.Save(dialog, "29-color-lookup-picked");
        dialog.Close();
        Pump();

        // Opened on a look already chosen, the dialog shows it chosen; without a picture the tiles show a ramp of colors.
        _ = AdjustmentDialogs.Edit(window, picked with { Amount = 40 }, reported.Add, null, SKColors.Black, SKColors.White);
        Pump();
        dialog = window.OwnedWindows.Last();
        Assert.Equal(Palette.Accent, Tiles(dialog)[3].BorderBrush);
        Assert.Equal(40, dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "数量").Value);
        Assert.Equal(48, ((Avalonia.Media.Imaging.Bitmap)Tiles(dialog)[0].GetVisualDescendants().OfType<Image>().Single().Source!).PixelSize.Height);
        dialog.Close();
        Pump();
    }

    [AvaloniaFact]
    public async Task A_file_of_your_own_becomes_a_tile_and_a_broken_one_is_refused()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        using var picture = Picture();
        var folder = Path.Combine(Path.GetTempPath(), "composa-lookup-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var good = Path.Combine(folder, "warm.cube");
        var broken = Path.Combine(folder, "broken.cube");
        await File.WriteAllTextAsync(good, string.Join('\n', Identity2));
        await File.WriteAllTextAsync(broken, "LUT_3D_SIZE 2\n0 0 0\nnope\n");
        try
        {
            var next = good;
            var reported = new List<Adjustment>();
            _ = AdjustmentDialogs.Edit(window, new ColorLookupAdjustment(), reported.Add, null, SKColors.Black, SKColors.White, () => picture, () => Task.FromResult<string?>(next));
            Pump();
            var dialog = window.OwnedWindows.Last();
            Press(dialog, "载入文件…");
            await Settle(() => reported.Count > 1);
            var tiles = Tiles(dialog);
            Assert.Equal(5, tiles.Count);
            Assert.Equal("warm.cube", tiles[4].Tag);
            Assert.Equal(Palette.Accent, tiles[4].BorderBrush);
            var loaded = Assert.IsType<ColorLookupAdjustment>(reported.Last());
            Assert.Equal("warm.cube", loaded.Source);
            Assert.Equal("Warm test", loaded.Lattice!.Title);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "warm.cube");
            Screenshots.Save(dialog, "30-color-lookup-file");

            // A file that cannot be read says which line, and the look stays what it was.
            next = broken;
            var before = reported.Count;
            Press(dialog, "载入文件…");
            await Settle(() => window.OwnedWindows.Count > 1);
            var alert = window.OwnedWindows.Last();
            Assert.NotSame(dialog, alert);
            Assert.Contains(alert.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("broken.cube 无法读取。第 3 行：") == true);
            alert.Close();
            Pump();
            Assert.Equal(before, reported.Count);
            Assert.Equal(5, Tiles(dialog).Count);

            // Loading another file replaces the file's tile rather than adding one.
            next = good;
            Press(dialog, "载入文件…");
            await Settle(() => reported.Count > before);
            Assert.Equal(5, Tiles(dialog).Count);

            // Choosing nothing in the picker changes nothing.
            next = null!;
            var again = reported.Count;
            Press(dialog, "载入文件…");
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(again, reported.Count);
            Assert.Equal(loaded.LatticeId, Assert.IsType<ColorLookupAdjustment>(reported.Last()).LatticeId);
            dialog.Close();
            Pump();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_layer_is_named_after_its_look_and_cancel_leaves_no_trace()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(120, 80, SKColors.White);
        window.AddSession(session);
        session.Fill(new SKColor(200, 90, 60), "Fill");
        Pump();
        var steps = session.History.Count;

        void NewLookupLayer() => window.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => m.Header as string == "颜色查找…" && (m.Parent as MenuItem)?.Header as string == "新建调整图层")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        NewLookupLayer();
        Pump();
        var dialog = window.OwnedWindows.Last();
        Assert.Equal(2, session.Document.Layers.Count);                                       // the layer exists while the dialog is open
        Press(dialog, "取消");
        Pump();
        Assert.Single(session.Document.Layers);
        Assert.Equal(steps, session.History.Count);

        NewLookupLayer();
        Pump();
        dialog = window.OwnedWindows.Last();
        Click(dialog, Tiles(dialog)[0]);
        await Settle(() => (session.Document.Layers[1].Adjustment as ColorLookupAdjustment)?.Lattice != null);   // the preview reached the layer
        Press(dialog, "确定");
        Pump();
        var layer = session.Document.Layers[1];
        Assert.Equal("精致黑白", layer.Name);
        Assert.Equal(Looks.Find("精致黑白")!.Id, Assert.IsType<ColorLookupAdjustment>(layer.Adjustment).LatticeId);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("新建调整图层", session.History.UndoName);
        using var flat = Composa.Rendering.DocumentRenderer.Flatten(session.Document);
        var pixel = flat.GetPixel(10, 10);
        Assert.True(Math.Abs(pixel.Red - pixel.Green) <= 3 && Math.Abs(pixel.Green - pixel.Blue) <= 3, $"{pixel} should be neutral");
        Screenshots.Save(window, "31-color-lookup-layer");

        // The Image menu's command works on the pixels themselves and is one step named for the adjustment.
        session.SelectLayer(session.Document.Layers[0].Id);
        window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == "颜色查找…" && (m.Parent as MenuItem)?.Header as string != "新建调整图层")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        dialog = window.OwnedWindows.Last();
        Assert.Equal(72 * 80 / 120, ((Avalonia.Media.Imaging.Bitmap)Tiles(dialog)[0].GetVisualDescendants().OfType<Image>().Single().Source!).PixelSize.Height);   // drawn on the layer
        Click(dialog, Tiles(dialog)[3]);
        Press(dialog, "确定");
        Pump();
        Assert.Equal("颜色查找", session.History.UndoName);
        Assert.Equal(2, session.Document.Layers.Count);
    }
}
