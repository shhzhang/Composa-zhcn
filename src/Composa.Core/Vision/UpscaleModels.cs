// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/upscale-models.ts and scripts/fetch-upscale-models.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
namespace Composa.Vision;

/// <summary>
/// One enlarging model: a fixed multiple, RGB in the 0 to 1 range in and out, any width and height. The file rules
/// are <see cref="OnnxModel"/>'s.
/// </summary>
public sealed record UpscaleModel(
    string Id,
    string Name,
    string File,
    string Sha256,
    long Bytes,
    string Licence,
    string Attribution,
    string Source,
    string Url,
    int Scale,
    string Note) : OnnxModel(Id, Name, File, Sha256, Bytes, Licence, Attribution, Source, Url);

/// <summary>
/// The enlarging models Composa knows. Real-ESRGAN publishes PyTorch weights only, so the file is a community ONNX
/// conversion pinned by its hash, as Lolly pins it. Lolly's roster also carries the 67 MB x4plus model, slow on a
/// CPU, the illustration model, which exists only as a conversion made with torch, and GFPGAN face restoration,
/// which invents faces; none of those ship here.
/// </summary>
public static class UpscaleModels
{
    /// <summary>Real-ESRGAN general v3 (SRVGGNetCompact): four times, 4.9 MB, fast enough on a CPU. In git.</summary>
    public static readonly UpscaleModel General = new(
        Id: "realesr-general-x4v3",
        Name: "增强",
        File: "realesr-general-x4v3.onnx",
        Sha256: "09b757accd747d7e423c1d352b3e8f23e77cc5742d04bae958d4eb8082b76fa4",
        Bytes: 4_871_181,
        Licence: "BSD-3-Clause",
        Attribution: "Real-ESRGAN, Copyright (c) 2021 Xintao Wang and contributors",
        Source: "https://github.com/xinntao/Real-ESRGAN",
        Url: "https://huggingface.co/OwlMaster/AllFilesRope/resolve/main/realesr-general-x4v3.onnx",
        Scale: 4,
        Note: "放大照片时生成合理的细节。精细边缘与纹理清晰锐利；人脸和文字可能出现偏差。");

    public static readonly IReadOnlyList<UpscaleModel> All = [General];

    /// <summary>Whether enlarging with a model works here: the runtime loads and the file is installed.</summary>
    public static bool IsAvailable => ModelRunner.CanRun(General);

    /// <summary>Why <see cref="IsAvailable"/> is false, for a dialog's note, or null when it is true.</summary>
    public static string? UnavailableReason =>
        ModelRunner.CanRun(General) ? null
        : !ModelRunner.IsAvailable ? "模型运行时未能在此计算机上加载。"
        : $"未安装放大模型（{General.Path}）。";
}
