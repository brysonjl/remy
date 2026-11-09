using System.Text.RegularExpressions;

namespace Remy.Core.Models;

public sealed record ModelFile(string Path, long Size, string Sha256, string Url);

/// <summary>Pinned model bundle (settings-models.md 2.3): immutable revision, per-file size + SHA-256 + exact URL.</summary>
public sealed record ModelManifest(
    string Id,                 // stable id, e.g. "parakeet-v2"
    string DisplayName,        // "Parakeet v2"
    string DirectoryName,      // folder under Models\, e.g. "parakeet-tdt-0.6b-v2-int8"
    string Repo,               // owner/name on huggingface.co
    string Revision,           // 40 lowercase hex
    string License,
    IReadOnlyList<ModelFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);

    public static string UrlFor(string repo, string revision, string path) =>
        $"https://huggingface.co/{repo}/resolve/{revision}/{path}";

    private static readonly Regex Hex40 = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex Component = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>Returns null when valid, else a reason.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(License)) return "license missing";
        if (Files.Count == 0) return "no files";
        if (!Hex40.IsMatch(Revision)) return "revision is not a 40-character commit sha";
        var repoParts = Repo.Split('/');
        if (repoParts.Length != 2 || repoParts.Any(p => !SafeComponent(p))) return "repo is not owner/name";
        if (!SafeComponent(DirectoryName)) return "unsafe directory name";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
        {
            if (f.Path.Split('/').Any(p => !SafeComponent(p))) return $"unsafe path {f.Path}";
            if (!seen.Add(f.Path)) return $"duplicate path {f.Path}";
            if (f.Size < 0) return $"negative size {f.Path}";
            if (!Hex64.IsMatch(f.Sha256)) return $"bad sha256 {f.Path}";
            if (f.Url != UrlFor(Repo, Revision, f.Path)) return $"url does not match pinned revision {f.Path}";
        }
        foreach (var f in Files)
            if (Files.Any(o => o.Path.StartsWith(f.Path + "/", StringComparison.OrdinalIgnoreCase)))
                return $"{f.Path} is also a directory";
        return null;
    }

    private static bool SafeComponent(string s) => s != "." && s != ".." && Component.IsMatch(s);
}
