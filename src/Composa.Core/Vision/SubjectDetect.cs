using Composa.Filters;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Vision;

/// <summary>How Select Subject, the Object Selection tool and Remove Background tell the subject from the backdrop.</summary>
public enum SubjectDetect
{
    /// <summary>U²-Net lite: any subject in an ordinary photo.</summary>
    Any,
    /// <summary>MODNet: a person, with hair and soft edges.</summary>
    Person,
    /// <summary>No model: the backdrop is what touches the picture's edges in a near-uniform color.</summary>
    Backdrop
}

/// <summary>
/// The one place that turns a Detect choice into a matte, falling back to the plain backdrop when the model it
/// names cannot run here (the runtime did not load, or the file is not installed).
/// </summary>
public static class SubjectFinder
{
    public static SubjectModel? ModelFor(SubjectDetect detect) => detect switch
    {
        SubjectDetect.Any => SubjectModels.U2NetP,
        SubjectDetect.Person => SubjectModels.ModNet,
        _ => null
    };

    public static string DisplayName(SubjectDetect detect) => detect switch
    {
        SubjectDetect.Any => "任何主体",
        SubjectDetect.Person => "人物",
        _ => "纯色背景"
    };

    /// <summary>Whether the choice can be honoured here. The backdrop method always can.</summary>
    public static bool IsAvailable(SubjectDetect detect) => ModelFor(detect) is not { } model || ModelRunner.CanRun(model);

    /// <summary>The choice that will actually run: the one asked for, or the backdrop when its model is not available.</summary>
    public static SubjectDetect Resolve(SubjectDetect detect) => IsAvailable(detect) ? detect : SubjectDetect.Backdrop;

    /// <summary>Why <see cref="Resolve"/> fell back, for the status line, or null when it did not.</summary>
    public static string? FallbackReason(SubjectDetect detect)
    {
        if (ModelFor(detect) is not { } model || ModelRunner.CanRun(model)) return null;
        return !ModelRunner.IsAvailable
            ? "主体检测运行时未能在此计算机上加载，因此改用了纯色背景。"
            : $"未安装 {model.Name} 模型（{model.Path}），因此改用了纯色背景。";
    }

    /// <summary>
    /// The subject of <paramref name="source"/> as a soft <c>Alpha8</c> matte at the source's size, or null when
    /// nothing stands out. Transparent pixels are backdrop whatever their color. Runs the model when the choice
    /// names one that is available; safe off the UI thread on a committed bitmap.
    /// </summary>
    public static SKBitmap? Matte(SKBitmap source, SubjectDetect detect, CancellationToken cancellation = default)
    {
        if (ModelFor(Resolve(detect)) is { } model)
        {
            var matte = SubjectMatting.Matte(source, model, cancellation);
            if (SelectionMask.Bounds(matte, 128).IsEmpty) { matte.Dispose(); return null; }
            return matte;
        }
        return ObjectSelection.Subject(source);
    }
}
