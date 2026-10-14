// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/session.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Composa.Vision;

/// <summary>
/// Runs the models through ONNX Runtime on the CPU: one session per model file, kept for the application's lifetime,
/// with a fixed thread count so the same picture gives the same answer. The runtime is a native library that may be
/// missing or refuse to load (a package built without it, an unsupported CPU); <see cref="IsAvailable"/> says so once
/// and everything then falls back to what worked without models, the way <c>ImageMagick.IsAvailable</c> gates formats.
/// </summary>
public static class ModelRunner
{
    /// <summary>The thread count every session uses. Fixed, rather than every core, so a result is the same from one run to the next.</summary>
    public static readonly int Threads = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static readonly Lazy<bool> available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<string, InferenceSession> sessions = [];
    private static readonly HashSet<string> verified = [];
    private static readonly Lock gate = new();

    /// <summary>Whether the runtime loads on this machine. Decided once; a failure is remembered so nothing probes again.</summary>
    public static bool IsAvailable => available.Value;

    /// <summary>Whether this model can run here: the runtime loads and the file is on this machine.</summary>
    public static bool CanRun(OnnxModel model) => IsAvailable && model.IsInstalled;

    private static bool Probe()
    {
        try
        {
            using var options = new SessionOptions();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or OnnxRuntimeException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the model on a normalized [1,3,edge,edge] tensor and returns its first output, edge by edge floats.
    /// The file is hashed the first time it is loaded; a file that is not the one the catalog names is refused.
    /// A cancellation stops the run itself, not only the stages around it.
    /// </summary>
    /// <exception cref="InvalidDataException">The model file is missing or is not the file the catalog names.</exception>
    public static float[] Run(SubjectModel model, float[] input, int edge, CancellationToken cancellation = default)
    {
        var (output, _, _) = RunImage(model, input, edge, edge, cancellation);
        if (output.Length != edge * edge) throw new InvalidDataException($"{model.Name} 模型对 {edge}×{edge} 像素返回了 {output.Length} 个值。");
        return output;
    }

    /// <summary>
    /// Runs a model that takes a [1,C,height,width] tensor and answers with planes of its own size, as the upscalers
    /// do: the first output's values with its width and height. The same loading, hashing and cancellation rules as
    /// <see cref="Run"/>.
    /// </summary>
    public static (float[] Planes, int Width, int Height) RunImage(OnnxModel model, float[] input, int width, int height, CancellationToken cancellation = default)
    {
        var session = Session(model);
        cancellation.ThrowIfCancellationRequested();
        var channels = input.Length / (width * height);
        var tensor = new DenseTensor<float>(input, [1, channels, height, width]);
        using var runOptions = new RunOptions();
        using var stop = cancellation.Register(() => runOptions.Terminate = true);
        try
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)], [session.OutputNames[0]], runOptions);
            var output = results[0].AsTensor<float>();
            var dims = output.Dimensions;
            return (output.ToArray(), dims[^1], dims[^2]);
        }
        catch (OnnxRuntimeException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation);
        }
    }

    private static InferenceSession Session(OnnxModel model)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(model.Id, out var existing)) return existing;
            if (!model.IsInstalled) throw new InvalidDataException($"未安装 {model.Name} 模型：缺少 {model.Path}。");
            if (!verified.Contains(model.Id))
            {
                if (!model.Verify()) throw new InvalidDataException($"{model.Path} 不是 Composa 构建时所用的 {model.Name} 模型，因此不会被使用。");
                verified.Add(model.Id);
            }
            var options = new SessionOptions
            {
                IntraOpNumThreads = Threads,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            try
            {
                var session = new InferenceSession(model.Path, options);
                sessions[model.Id] = session;
                return session;
            }
            finally { options.Dispose(); }
        }
    }
}
