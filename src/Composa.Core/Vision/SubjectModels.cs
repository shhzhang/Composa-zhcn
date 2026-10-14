// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/matte-models.ts and scripts/fetch-matte-models.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
namespace Composa.Vision;

/// <summary>How a model's single-channel output becomes a 0 to 1 matte.</summary>
public enum MaskActivation
{
    /// <summary>The head is already bounded; stretch what it gave to the full range, as rembg does.</summary>
    MinMax,
    /// <summary>The head is a logit; squash it.</summary>
    Sigmoid
}

/// <summary>
/// One on-device segmentation model: where its file is, what the file must hash to, and the tensor contract the
/// runner needs. The normalization and the activation differ per model and a wrong value does not crash, it quietly
/// ruins the matte, so every value here was confirmed against the real graph (see <c>SubjectModelTests</c>).
/// </summary>
public sealed record SubjectModel(
    string Id,
    string Name,
    string File,
    string Sha256,
    long Bytes,
    string Licence,
    string Attribution,
    string Source,
    string Url,
    int InputSize,
    float[] Mean,
    float[] Std,
    MaskActivation Activation,
    string Note) : OnnxModel(Id, Name, File, Sha256, Bytes, Licence, Attribution, Source, Url);

/// <summary>
/// The models Composa knows. Only weights under a permissive licence (Apache-2.0, MIT or BSD) are listed: BRIA's RMBG
/// models are non-commercial and never ship. Every entry's licence, source and hash is also recorded in
/// <c>packaging/THIRD-PARTY-NOTICES.txt</c>, which travels with the files.
/// </summary>
public static class SubjectModels
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>U²-Net lite: a saliency net for any subject, 4.6 MB at 320², and the default. Edges are soft, and busy or low-contrast backgrounds can confuse it.</summary>
    public static readonly SubjectModel U2NetP = new(
        Id: "u2netp",
        Name: "任何主体",
        File: "u2netp.onnx",
        Sha256: "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
        Bytes: 4_574_861,
        Licence: "Apache-2.0",
        Attribution: "U²-Net, Copyright (c) 2020 Xuebin Qin et al.",
        Source: "https://github.com/xuebinqin/U-2-Net",
        Url: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        InputSize: 320,
        Mean: ImageNetMean,
        Std: ImageNetStd,
        Activation: MaskActivation.MinMax,
        Note: "适用于任何主体。边缘柔和，繁杂或低对比度的背景可能使其混淆。");

    /// <summary>MODNet: a portrait matting net, 26 MB, run at 512². Follows hair and soft edges; weaker on anything that is not a person.</summary>
    public static readonly SubjectModel ModNet = new(
        Id: "modnet",
        Name: "人物",
        File: "modnet.onnx",
        Sha256: "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9",
        Bytes: 25_888_640,
        Licence: "Apache-2.0",
        Attribution: "MODNet, Copyright (c) 2020 Zhanghan Ke et al.",
        Source: "https://github.com/ZHKKKe/MODNet",
        Url: "https://huggingface.co/Xenova/modnet/resolve/main/onnx/model.onnx",
        InputSize: 512,
        Mean: [0.5f, 0.5f, 0.5f],
        Std: [0.5f, 0.5f, 0.5f],
        Activation: MaskActivation.MinMax,
        Note: "针对人物调校：头发与边缘柔和。对非人物对象效果较弱。");

    public static readonly IReadOnlyList<SubjectModel> All = [U2NetP, ModNet];

    /// <summary>Where the model files are; see <see cref="OnnxModels.Directory"/>.</summary>
    public static string Directory => OnnxModels.Directory;

    public static SubjectModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
