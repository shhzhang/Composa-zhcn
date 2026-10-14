using Composa.Editing;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>Select > Color Range: every pixel near the picked colors, previewed live and kept as one undo step.</summary>
public class ColorRangeTests
{
    /// <summary>A red left half, a green right half, a blue square in the middle and a transparent strip along the bottom.</summary>
    private static SKBitmap Picture()
    {
        var bitmap = Pixels.NewColor(100, 60);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var red = new SKPaint { Color = new SKColor(200, 30, 30) };
        using var green = new SKPaint { Color = new SKColor(30, 200, 30) };
        using var blue = new SKPaint { Color = new SKColor(30, 30, 200) };
        canvas.DrawRect(new SKRect(0, 0, 50, 50), red);
        canvas.DrawRect(new SKRect(50, 0, 100, 50), green);
        canvas.DrawRect(new SKRect(40, 20, 60, 30), blue);
        return bitmap;
    }

    [Fact]
    public void Matching_takes_the_colors_within_the_fuzziness_and_never_transparent_pixels()
    {
        using var picture = Picture();
        var (mask, count) = ColorRange.Match(picture, [new SKColor(200, 30, 30)], [], 40, invert: false);
        Assert.Equal(50 * 50 - 10 * 10, count);                             // The red half without the blue square's part.
        Assert.Equal(255, mask.GetPixel(10, 10).Alpha);
        Assert.Equal(0, mask.GetPixel(70, 10).Alpha);
        Assert.Equal(0, mask.GetPixel(45, 25).Alpha);
        Assert.Equal(0, mask.GetPixel(10, 55).Alpha);                        // Transparent.
        // A fuzziness of 170 per channel reaches the green and the blue too; excluding a color takes it back out.
        Assert.Equal(100 * 50, ColorRange.Match(picture, [new SKColor(200, 30, 30)], [], 170, false).Count);
        Assert.Equal(50 * 50 - 10 * 10, ColorRange.Match(picture, [new SKColor(200, 30, 30), new SKColor(30, 200, 30)], [new SKColor(30, 200, 30)], 40, false).Count);
        // Invert selects everything else, the transparent strip included, as a green screen's subject would be.
        var (inverted, invertedCount) = ColorRange.Match(picture, [new SKColor(30, 200, 30)], [], 40, invert: true);
        Assert.Equal(100 * 60 - (50 * 50 - 10 * 10), invertedCount);
        Assert.Equal(255, inverted.GetPixel(10, 55).Alpha);
        // The color under a point is averaged over its neighbors; a transparent spot picks nothing.
        Assert.Equal(new SKColor(200, 30, 30), ColorRange.ColorAt(picture, 10, 10));
        Assert.Null(ColorRange.ColorAt(picture, 10, 55));
        Assert.Null(ColorRange.ColorAt(picture, 200, 10));
        var edge = ColorRange.ColorAt(picture, 50, 10)!.Value;               // Three red columns, six green: a blend.
        Assert.True(edge.Green > edge.Red && edge.Red > 60);
        using var preview = ColorRange.Preview(mask);
        Assert.True(preview.Width <= ColorRange.PreviewWidth * 2 && preview.Height <= ColorRange.PreviewHeight * 2);
        Assert.True(preview.GetPixel(preview.Width / 8, preview.Height / 4).Red > 200);
        Assert.True(preview.GetPixel(preview.Width * 7 / 8, preview.Height / 4).Red < 50);
    }

    [Fact]
    public void The_session_previews_the_selection_and_keeps_it_as_one_undo_step()
    {
        var session = EditorSession.NewCanvas(100, 60);
        session.AddImageLayer("picture", Picture());
        session.SelectRect(new SKRect(0, 0, 10, 10));
        var before = session.Selection;
        var changes = 0;
        session.ColorRangeChanged += () => changes++;
        Assert.True(session.BeginColorRange());
        Assert.False(session.CanSelectColorRange);
        Assert.Same(before, session.Selection);                             // Nothing picked: the old selection stays.
        session.SampleColorRange(10, 10);
        Assert.Equal(50 * 50 - 10 * 10, session.ColorRange!.Count);
        Assert.NotNull(session.ColorRange.Preview);
        Assert.Equal(255, session.Selection!.GetPixel(10, 10).Alpha);
        Assert.Equal("矩形选框", session.History.UndoName);      // Previews do not undo.
        session.SampleColorRange(70, 10, ColorRangeSample.Add);             // Shift-click adds green: both halves but the blue square.
        Assert.Equal(100 * 50 - 20 * 10, session.ColorRange.Count);
        session.SampleColorRange(45, 25, ColorRangeSample.Add);
        Assert.Equal(100 * 50, session.ColorRange.Count);
        session.SampleColorRange(45, 25, ColorRangeSample.Remove);          // Alt-click takes the blue back out.
        Assert.Equal(100 * 50 - 20 * 10, session.ColorRange.Count);
        session.SetColorRangeSampleMode(ColorRangeSample.Remove);
        session.SampleColorRange(70, 10);                                    // The chosen eyedropper applies without a modifier.
        Assert.Equal(50 * 50 - 10 * 10, session.ColorRange.Count);
        session.SetColorRangeInvert(true);
        Assert.Equal(100 * 60 - (50 * 50 - 10 * 10), session.ColorRange.Count);
        session.SetColorRangeInvert(false);
        session.SetColorRangeFuzziness(500);
        Assert.Equal(ColorRange.MaxFuzziness, session.ColorRange.Fuzziness);
        session.SampleColorRange(10, 55);                                    // Transparent: picks nothing, changes nothing.
        Assert.Equal(ColorRange.MaxFuzziness, session.ColorRange.Fuzziness);
        session.SetColorRangeFuzziness(40);
        Assert.True(changes > 5);
        session.CommitColorRange();
        Assert.Null(session.ColorRange);
        Assert.Equal("色彩范围", session.History.UndoName);
        Assert.Equal(255, session.Selection!.GetPixel(10, 10).Alpha);
        Assert.Equal(0, session.Selection.GetPixel(70, 10).Alpha);
        session.Undo();
        Assert.Same(before, session.Selection);
    }

    [Fact]
    public void Cancel_and_an_empty_pick_put_the_old_selection_back_and_another_edit_commits()
    {
        var session = EditorSession.NewCanvas(100, 60);
        session.AddImageLayer("picture", Picture());
        session.SelectRect(new SKRect(0, 0, 10, 10));
        var before = session.Selection;
        var steps = session.History.UndoName;
        session.BeginColorRange();
        session.SampleColorRange(70, 10);
        session.CancelColorRange();
        Assert.Same(before, session.Selection);
        Assert.Equal(steps, session.History.UndoName);
        // OK with nothing picked is a Cancel.
        session.BeginColorRange();
        session.CommitColorRange();
        Assert.Same(before, session.Selection);
        Assert.Equal(steps, session.History.UndoName);
        // A pick that matches nothing at all, kept, deselects.
        session.BeginColorRange();
        session.SampleColorRange(70, 10);
        session.SetColorRangeFuzziness(0);
        session.SampleColorRange(70, 10, ColorRangeSample.Remove);
        Assert.Null(session.Selection);
        session.CommitColorRange();
        Assert.Null(session.Selection);
        Assert.Equal("色彩范围", session.History.UndoName);
        // Another edit beginning while the panel is open keeps the selection shown.
        session.BeginColorRange();
        session.SampleColorRange(10, 10);
        session.SelectAll();
        Assert.Null(session.ColorRange);
        session.Undo();
        Assert.Equal(255, session.Selection!.GetPixel(10, 10).Alpha);
        Assert.Equal(0, session.Selection.GetPixel(70, 10).Alpha);
        Assert.Equal("色彩范围", session.History.UndoName);
    }
}
