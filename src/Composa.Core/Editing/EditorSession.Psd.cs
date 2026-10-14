using Composa.IO.Psd;
using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public const int MaxLayers = 10_000;

    /// <summary>A Photoshop file opened on its own: the file's canvas becomes the document.</summary>
    public static EditorSession OpenPhotoshop(PsdImport import, string name) => new(import.ToDocument()) { SuggestedName = name };

    /// <summary>
    /// A Photoshop file dropped into an existing document: its layers arrive inside one folder named after the file,
    /// centered on <paramref name="center"/> when given, as one undoable step.
    /// </summary>
    public Layer PlacePhotoshop(PsdImport import, string name, SKPoint? center = null) =>
        PlaceImported(import.Layers, name, "导入 Photoshop 文件", center, PsdException.TooLarge);

    /// <summary>Another editor's layers into this document, inside one folder, as one step named <paramref name="step"/>.</summary>
    private Layer PlaceImported(List<Layer> incoming, string name, string step, SKPoint? center, Func<Exception> tooLarge)
    {
        if (document.AllLayers().Count() + Document.Flatten(incoming).Count() + 1 > MaxLayers) throw tooLarge();
        var group = Layer.Group(name);
        Apply(step, () =>
        {
            if (center is { } at)
            {
                var box = SKRect.Empty;
                foreach (var leaf in Document.Flatten(incoming).Where(l => l.Pixels != null))
                    box = box.IsEmpty ? leaf.Bounds : SKRect.Union(box, leaf.Bounds);
                if (!box.IsEmpty)
                {
                    double dx = Math.Round(at.X - box.MidX), dy = Math.Round(at.Y - box.MidY);
                    foreach (var leaf in Document.Flatten(incoming).Where(l => l.Pixels != null))
                        leaf.Transform = leaf.Transform with { X = leaf.Transform.X + dx, Y = leaf.Transform.Y + dy };
                }
            }
            group.Children.AddRange(incoming);
            document.InsertAboveActive(group);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        return group;
    }
}
