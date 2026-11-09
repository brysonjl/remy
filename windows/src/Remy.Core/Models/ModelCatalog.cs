namespace Remy.Core.Models;

/// <summary>The two downloadable models. Pinned revisions and hashes; nothing is ever fetched at startup.</summary>
public static class ModelCatalog
{
    private const string ParakeetRepo = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8";
    private const string ParakeetRev = "84b478d541cdbf0c296a7ab5f1e20a6f8f998114";
    private const string S1Repo = "superwhisper/s1-mini-GGUF";
    private const string S1Rev = "ee2c0f56e56345f475749a44ff2893e21c3cb292";

    public const string ParakeetFolder = "parakeet-tdt-0.6b-v2-int8";
    public const string S1MiniFolder = "S1-mini";
    public const string S1MiniGgufFile = "s1-mini-q4_k_m.gguf";

    /// <summary>sherpa-onnx Parakeet TDT 0.6B v2 int8 (CC-BY-4.0, base model nvidia/parakeet-tdt-0.6b-v2).</summary>
    public static ModelManifest ParakeetV2 { get; } = new(
        "parakeet-v2", "Parakeet v2", ParakeetFolder, ParakeetRepo, ParakeetRev, "CC-BY-4.0",
        new[]
        {
            F(ParakeetRepo, ParakeetRev, "encoder.int8.onnx", 652282309, "92a10bd9c23936ea0566c29f963bc57c2a5df4261b6a8921c39abea66557ca8b"),
            F(ParakeetRepo, ParakeetRev, "decoder.int8.onnx", 7257753, "4d097bae3bac5c96018d5293ba39f1b657296102b142affda40378f38b020f41"),
            F(ParakeetRepo, ParakeetRev, "joiner.int8.onnx", 1739080, "77a29f3e5ac7d37074e0af465391b80f314015d5b97f0f90c6c7767f1d56f428"),
            F(ParakeetRepo, ParakeetRev, "tokens.txt", 9384, "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d"),
        });

    /// <summary>S1-mini by Superwhisper (Apache-2.0 plus naming term). Installed as Models\S1-mini\.</summary>
    public static ModelManifest S1Mini { get; } = new(
        "s1-mini", "S1-mini by Superwhisper", S1MiniFolder, S1Repo, S1Rev, "Apache-2.0",
        new[]
        {
            F(S1Repo, S1Rev, S1MiniGgufFile, 484219808, "3b41ebe2502cbd03e811d5d16b022f5ab551eda58d62597d152f89535003c634"),
            F(S1Repo, S1Rev, "LICENSE", 11878, "d956d2d305a0639211c9cbde71501accb0e1474cc9ddf79a47820a522aff6f98"),
        });

    public static IReadOnlyList<ModelManifest> All { get; } = new[] { ParakeetV2, S1Mini };

    private static ModelFile F(string repo, string rev, string path, long size, string sha) =>
        new(path, size, sha, ModelManifest.UrlFor(repo, rev, path));
}
