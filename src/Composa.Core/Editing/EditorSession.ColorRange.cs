using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

/// <summary>
/// Select > Color Range while its panel is open: the colors picked so far, the fuzziness and the preview. The
/// selection on the canvas follows every change without an undo step; OK keeps it as one, Cancel puts back the one
/// there was.
/// </summary>
public sealed class ColorRangeEdit
{
    public int Fuzziness { get; internal set; } = ColorRange.DefaultFuzziness;
    public bool Invert { get; internal set; }
    /// <summary>What the next click on the canvas does, unless Shift (add) or Alt (remove) says otherwise.</summary>
    public ColorRangeSample SampleMode { get; internal set; } = ColorRangeSample.Sample;
    public List<SKColor> Include { get; } = [];
    public List<SKColor> Exclude { get; } = [];
    /// <summary>The selection in black and white, small enough for the panel. Null until a color is picked.</summary>
    public SKBitmap? Preview { get; internal set; }
    /// <summary>How many pixels the selection holds; zero until a color is picked.</summary>
    public long Count { get; internal set; }
    /// <summary>The image as shown, at document size: what the colors are matched against.</summary>
    internal SKBitmap Image { get; }
    /// <summary>The selection when the panel opened, which Cancel restores.</summary>
    internal SKBitmap? Original { get; }
    public bool HasColors => Include.Count > 0;

    internal ColorRangeEdit(SKBitmap image, SKBitmap? original) { Image = image; Original = original; }
}

public sealed partial class EditorSession
{
    public ColorRangeEdit? ColorRange { get; private set; }
    /// <summary>Raised when the panel opens or closes, and whenever what it shows changes.</summary>
    public event Action? ColorRangeChanged;

    public bool CanSelectColorRange => ColorRange == null && !IsInteracting;

    /// <summary>Opens Color Range on the picture as shown, with nothing picked yet; the selection stays until a color is.</summary>
    public bool BeginColorRange()
    {
        if (!CanSelectColorRange) return false;
        ColorRange = new ColorRangeEdit(Pixels.Clone(Composite()), document.Selection);
        ColorRangeChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// A click on the canvas while the panel is open. Shift adds the color and Alt takes it away, whichever eyedropper
    /// is chosen; a click on transparent pixels picks nothing.
    /// </summary>
    public void SampleColorRange(int x, int y, ColorRangeSample? held = null)
    {
        if (ColorRange is not { } edit || Selections.ColorRange.ColorAt(edit.Image, x, y) is not { } color) return;
        switch (held ?? edit.SampleMode)
        {
            case ColorRangeSample.Sample: edit.Include.Clear(); edit.Include.Add(color); edit.Exclude.Clear(); break;
            case ColorRangeSample.Add: edit.Include.Add(color); break;
            default: edit.Exclude.Add(color); break;
        }
        UpdateColorRange();
    }

    public void SetColorRangeFuzziness(int fuzziness)
    {
        if (ColorRange is not { } edit) return;
        var clamped = Math.Clamp(fuzziness, Selections.ColorRange.MinFuzziness, Selections.ColorRange.MaxFuzziness);
        if (edit.Fuzziness == clamped) return;
        edit.Fuzziness = clamped;
        UpdateColorRange();
    }

    public void SetColorRangeInvert(bool invert)
    {
        if (ColorRange is not { } edit || edit.Invert == invert) return;
        edit.Invert = invert;
        UpdateColorRange();
    }

    public void SetColorRangeSampleMode(ColorRangeSample mode)
    {
        if (ColorRange is not { } edit || edit.SampleMode == mode) return;
        edit.SampleMode = mode;
        ColorRangeChanged?.Invoke();
    }

    /// <summary>Matches the image against the picked colors and shows the result as the selection, without an undo step.</summary>
    private void UpdateColorRange()
    {
        if (ColorRange is not { } edit) return;
        edit.Preview?.Dispose();
        edit.Preview = null;
        if (!edit.HasColors)
        {
            edit.Count = 0;
            PreviewSelection(edit.Original);
        }
        else
        {
            var (mask, count) = Selections.ColorRange.Match(edit.Image, edit.Include, edit.Exclude, edit.Fuzziness, edit.Invert);
            edit.Count = count;
            edit.Preview = Selections.ColorRange.Preview(mask);
            if (count == 0) { mask.Dispose(); PreviewSelection(null); }
            else PreviewSelection(mask);
        }
        ColorRangeChanged?.Invoke();
    }

    /// <summary>OK: the selection shown becomes the selection, as one undo step named Color Range. With nothing picked, nothing changes.</summary>
    public void CommitColorRange()
    {
        if (ColorRange is not { } edit) return;
        var result = document.Selection;
        document.Selection = edit.Original;
        EndColorRange(edit);
        if (!edit.HasColors) { SelectionChanged?.Invoke(); return; }
        SetSelection("色彩范围", result);
        if (result == null) SelectionChanged?.Invoke();
    }

    /// <summary>Cancel: the selection there was comes back.</summary>
    public void CancelColorRange()
    {
        if (ColorRange is not { } edit) return;
        document.Selection = edit.Original;
        EndColorRange(edit);
        SelectionChanged?.Invoke();
    }

    private void EndColorRange(ColorRangeEdit edit)
    {
        ColorRange = null;
        edit.Preview?.Dispose();
        edit.Image.Dispose();
        ColorRangeChanged?.Invoke();
    }
}
