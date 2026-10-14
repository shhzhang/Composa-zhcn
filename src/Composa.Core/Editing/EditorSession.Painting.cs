using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private BrushStroke? stroke;
    private Layer? strokeLayer;
    private SKBitmap? strokeOriginal;
    private SKMatrix strokeToLayer;
    private BrushMode strokeMode;
    private SKPoint? cloneSource;
    private SKPoint? cloneOffset;
    private SKPoint? lastStrokeEnd;
    private SKPoint? smoothingAnchor;
    private SKPoint? smoothingPointer;

    public bool IsStroking => stroke != null;
    /// <summary>Screen pixels per document pixel, told by the canvas, so Smoothing feels the same at any zoom.</summary>
    public double ViewZoom { get; set; } = 1;
    /// <summary>Lets a pen's pressure vary the brush size.</summary>
    public bool PressureSensitive { get; set; } = true;
    /// <summary>Where the Clone Stamp samples from, in document space.</summary>
    public SKPoint? CloneSource => cloneSource;
    /// <summary>Where the clone source currently is while painting, for the crosshair overlay.</summary>
    public SKPoint? CloneSamplePoint(SKPoint cursor) =>
        cloneSource == null ? null : cloneOffset is { } o && CloneAligned ? new SKPoint(cursor.X + o.X, cursor.Y + o.Y) : cloneSource;
    /// <summary>Where the previous stroke ended, so Shift-click can draw a straight line from it.</summary>
    public SKPoint? LastStrokeEnd => lastStrokeEnd;

    public void SetCloneSource(SKPoint documentPoint)
    {
        cloneSource = documentPoint;
        cloneOffset = null;
    }

    /// <summary>The brush mode the current tool paints with.</summary>
    public BrushMode CurrentBrushMode => Tool switch
    {
        Tool.SpotHealing => BrushMode.Heal,
        Tool.CloneStamp => BrushMode.Clone,
        Tool.Smear => SmearMode switch
        {
            SmearMode.Liquify => BrushMode.Liquify, SmearMode.Smudge => BrushMode.Smudge, SmearMode.Dodge => BrushMode.Dodge, SmearMode.Burn => BrushMode.Burn, _ => BrushMode.Blur
        },
        _ => EraserMode ? BrushMode.Erase : BrushMode.Paint
    };

    /// <summary>
    /// Paints one whole stroke through the given points with its own brush and color, as an agent or a script does,
    /// leaving the tool, brush and colors as they were. Returns the reason when the active layer can't be painted.
    /// </summary>
    public string? PaintStroke(IReadOnlyList<SKPoint> points, BrushSettings brush, SKColor color, BrushMode mode = BrushMode.Paint)
    {
        if (points.Count == 0) return "一笔笔画至少需要一个点。";
        var (savedBrush, savedForeground) = (Brush, Foreground);
        Brush = brush with { Smoothing = 0 };                             // The points are the stroke; nothing to smooth.
        Foreground = color;
        try
        {
            if (!BeginStroke(points[0], out var problem, mode: mode)) return problem;
            foreach (var point in points.Skip(1)) ContinueStroke(point);
            EndStroke();
            return null;
        }
        finally { (Brush, Foreground) = (savedBrush, savedForeground); }
    }

    /// <summary>
    /// Paints many strokes as one undoable step, "Brush Strokes", for an agent blocking in a picture or drawing every
    /// line it traced. Stops at the first stroke that cannot be painted and says why, with how many landed before it.
    /// </summary>
    public (int Painted, string? Problem) PaintStrokes(IReadOnlyList<PlannedStroke> strokes)
    {
        var painted = 0;
        foreach (var stroke in strokes)
        {
            var entries = History.Count;
            var problem = PaintStroke(stroke.Points, stroke.Brush, stroke.Color, stroke.Mode);
            if (problem != null) return (painted, problem);
            painted++;
            // A stroke that touched nothing left no entry; every other one folds into the first.
            if (painted > 1 && History.Count == entries + 1) FoldLastStep("画笔笔触");
        }
        return (painted, null);
    }

    /// <summary>Starts painting at a document point. Returns false (with a reason) when the active layer can't be painted.</summary>
    /// <param name="mode">The brush to use; the current tool's when left out.</param>
    public bool BeginStroke(SKPoint point, out string? problem, bool lineFromLast = false, BrushMode? mode = null)
    {
        problem = null;
        if (ActiveLayer is not { } layer) { problem = "请选择要绘画的图层。"; return false; }
        if (!IsEditingMask && layer.Pixels == null) { problem = layer.IsGroup ? "无法在文件夹上绘画。请选择其中的图层。" : "调整图层没有像素。请添加蒙版后再绘画。"; return false; }
        if (!IsEditingMask && layer.IsLive) { problem = $"这是实时的{(layer.Text != null ? "文字" : "形状")}。请先栅格化（图层菜单）才能在上面绘画。"; return false; }
        if (!document.IsEffectivelyVisible(layer)) { problem = "该图层已隐藏。"; return false; }
        mode ??= CurrentBrushMode;
        if (mode == BrushMode.Clone && cloneSource == null) { problem = "请先按住 Alt 单击设置仿制源。"; return false; }
        if (mode == BrushMode.Heal && IsEditingMask) { problem = "污点修复画笔只作用于像素，不适用于蒙版。"; return false; }

        Begin(mode.Value switch
        {
            BrushMode.Erase => "橡皮擦", BrushMode.Clone => "仿制图章", BrushMode.Heal => "污点修复画笔",
            BrushMode.Liquify => "液化", BrushMode.Blur => "模糊", BrushMode.Smudge => "抹擦", BrushMode.Dodge => "减淡", BrushMode.Burn => "加深", _ => "画笔"
        });
        // A brush on a mask can paint anywhere on the canvas, as Photoshop's does, growing the mask past its layer. The
        // smearing brushes work the mask's own pixels and stay within it.
        if (!IsEditingMask || mode is BrushMode.Paint or BrushMode.Erase) GrowToCanvas(layer);
        var target = Target(layer);
        var matrix = TargetMatrix(layer);
        if (!matrix.TryInvert(out strokeToLayer)) { Cancel(); problem = "图层太小，无法绘画。"; return false; }
        var scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));

        SKBitmap? source = null;
        var offset = SKPointI.Empty;
        if (mode == BrushMode.Clone)
        {
            if (!CloneAligned || cloneOffset == null) cloneOffset = new SKPoint(cloneSource!.Value.X - point.X, cloneSource.Value.Y - point.Y);
            var o = cloneOffset.Value;
            var translationOnly = layer.Pixels != null && layer.Transform.IsPureTranslation(layer.Pixels.Width, layer.Pixels.Height) || layer.Pixels == null;
            if (SampleAllLayers && !IsEditingMask && translationOnly)
            {
                source = Pixels.Clone(Composite());
                offset = new SKPointI((int)Math.Round(o.X + layer.Transform.X), (int)Math.Round(o.Y + layer.Transform.Y));
            }
            else
            {
                source = target;
                var v = strokeToLayer.MapVector(o.X, o.Y);
                offset = new SKPointI((int)Math.Round(v.X), (int)Math.Round(v.Y));
            }
        }
        var color = mode == BrushMode.Paint ? Foreground : SKColors.Black;
        stroke = new BrushStroke(target, Brush, mode.Value, color, scale)
        {
            Selection = document.Selection, ToDocument = matrix, CloneSource = source, CloneOffset = offset
        };
        strokeLayer = layer;
        strokeOriginal = target;
        strokeMode = mode.Value;
        SetTarget(layer, stroke.Working);
        Pixels.SetLive(stroke.Working, true);
        if (lineFromLast && lastStrokeEnd is { } from) stroke.AddPoint(strokeToLayer.MapPoint(from));
        // The first dab always lands; from here on Smoothing decides how the brush follows.
        smoothingAnchor = smoothingPointer = point;
        Paint(point, 1);
        if (lineFromLast) Invalidate(AffectedArea(layer));
        return true;
    }

    public void ContinueStroke(SKPoint point, float pressure = 1)
    {
        if (stroke == null || strokeLayer == null) return;
        smoothingPointer = point;
        if (Smoothed(point) is not { } painted) return;
        Paint(painted, pressure);
    }

    /// <summary>
    /// Where the brush actually is with Smoothing on: it trails the pointer on a string and only moves once the
    /// pointer pulls that string taut, the model Photoshop uses. The string's length is in screen points, so it feels
    /// the same however far the canvas is zoomed in. Null while the string is still slack, which is the whole point:
    /// those jitters never reach the stroke.
    /// </summary>
    private SKPoint? Smoothed(SKPoint point)
    {
        if (!IsSmoothing || smoothingAnchor is not { } anchor) return point;
        var radius = Brush.Smoothing / Math.Max(0.01, ViewZoom);
        float dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= radius) return null;
        var step = (float)((distance - radius) / distance);
        var moved = new SKPoint(anchor.X + dx * step, anchor.Y + dy * step);
        smoothingAnchor = moved;
        return moved;
    }

    /// <summary>Smoothing is offered for Paint and Erase; healing, cloning and smearing keep their own feel.</summary>
    private bool IsSmoothing => strokeMode is BrushMode.Paint or BrushMode.Erase && Brush.Smoothing > 0;

    private void Paint(SKPoint point, float pressure)
    {
        var changed = stroke!.AddPoint(strokeToLayer.MapPoint(point), PressureSensitive ? pressure : 1);
        lastStrokeEnd = point;
        if (changed.IsEmpty) return;
        var area = Geometry.RoundOut(stroke.ToDocument.MapRect(SKRect.Create(changed.Left, changed.Top, changed.Width, changed.Height)));
        area.Inflate(1, 1);
        // A clipping base or a folder/adjustment mask changes more than its own rectangle's worth of layers, but never
        // outside the rectangle itself, so the dab's area is all that needs re-rendering.
        Invalidate(area);
    }

    public void EndStroke()
    {
        if (stroke == null || strokeLayer == null) return;
        var layer = strokeLayer;
        // Smoothing leaves the brush short of the pointer; the stroke ends where the hand did.
        if (IsSmoothing && smoothingPointer is { } pointer && smoothingAnchor is { } anchor && pointer != anchor) Paint(pointer, 1);
        if (stroke.Touched.IsEmpty)
        {
            SetTarget(layer, strokeOriginal!);
            CloseStroke();
            Cancel();
            return;
        }
        if (strokeMode == BrushMode.Heal)
        {
            using var mask = stroke.CoverageMask();
            if ((long)stroke.Touched.Width * stroke.Touched.Height > Inpaint.MaxArea)
            {
                CloseStroke();
                Cancel();
                Problem?.Invoke("该区域太大，无法一笔修复。请用更小的笔画分次修复。");
                return;
            }
            var selection = SelectionInTargetSpace(layer);
            var healed = MixBySelection(strokeOriginal!, Inpaint.Fill(strokeOriginal!, mask), selection);
            if (selection != document.Selection) selection?.Dispose();
            SetTarget(layer, healed);
            Invalidate(AffectedArea(layer));
        }
        CloseStroke();
        Commit();
        LayersChanged?.Invoke();
    }

    public void CancelStroke()
    {
        if (stroke == null) return;
        CloseStroke();
        Cancel();
    }

    private void CloseStroke()
    {
        if (stroke != null) { Pixels.SetLive(stroke.Working, false); Pixels.Invalidate(stroke.Working); }
        if (stroke?.CloneSource != null && !ReferenceEquals(stroke.CloneSource, strokeOriginal)) stroke.CloneSource.Dispose();
        stroke?.Dispose();
        stroke = null;
        strokeLayer = null;
        strokeOriginal = null;
        smoothingAnchor = smoothingPointer = null;
    }
}
