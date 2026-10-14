using Composa.IO.Xcf;
using Composa.Model;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    /// <summary>A GIMP file opened on its own: the file's canvas, layers and guides become the document.</summary>
    public static EditorSession OpenGimp(XcfImport import, string name) => new(import.ToDocument()) { SuggestedName = name };

    /// <summary>
    /// A GIMP file dropped into an existing document: its layers arrive inside one folder named after the file,
    /// centered on <paramref name="center"/> when given, as one undoable step. Its guides stay behind.
    /// </summary>
    public Layer PlaceGimp(XcfImport import, string name, SKPoint? center = null) =>
        PlaceImported(import.Layers, name, "导入 GIMP 文件", center, XcfException.TooLarge);
}
