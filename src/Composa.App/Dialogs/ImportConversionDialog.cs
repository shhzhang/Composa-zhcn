using Avalonia.Controls;
using Avalonia.Media;
using Composa.IO;

namespace Composa.App.Dialogs;

/// <summary>What a Photoshop or GIMP file loses on the way in, listed per layer, with the choice to go ahead or not.</summary>
public static class ImportConversionDialog
{
    /// <param name="format">What the file is, as the intro names it: "Photoshop" or "GIMP".</param>
    public static Task<bool> Confirm(Window owner, string fileName, string format, IReadOnlyList<ImportConversion> conversions)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var item in conversions.Take(500))
        {
            var message = Ui.Label(item.Message, Palette.Foreground);
            message.TextWrapping = TextWrapping.Wrap;
            message.MaxWidth = 470;
            rows.Children.Add(Ui.Column(2, Ui.Label(item.LayerName, Palette.Foreground, weight: FontWeight.SemiBold), message));
        }
        if (conversions.Count > 500) rows.Children.Add(Ui.Label($"…另有 {conversions.Count - 500} 项。", Palette.Secondary));
        var intro = Ui.Label($"Composa 将转换这些 {format} 特性。在你继续之前不会应用任何更改。", Palette.Secondary);
        intro.TextWrapping = TextWrapping.Wrap;
        intro.MaxWidth = 500;
        var list = new ScrollViewer { Content = rows, MaxHeight = 320, Width = 500, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = Ui.Column(12, intro, list);
        return new DialogWindow($"打开 {fileName}？", body, "导入").Ask(owner);
    }
}
