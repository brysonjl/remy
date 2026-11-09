namespace Remy.Core.Cleanup;

/// <summary>Provider URL building (cleanup.md §1.2/§1.3). Throws CleanupFailureException(invalidConfiguration).</summary>
public static class CleanupEndpoints
{
    public const string DefaultOllamaBaseUrl = "http://127.0.0.1:11434";
    public const string DefaultOpenAICompatibleBaseUrl = "http://127.0.0.1:1234/v1";
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    private static UriBuilder ParseBase(string? baseUrl)
    {
        var s = (baseUrl ?? "").Trim();
        if (s.Length == 0) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(u.UserInfo))
            throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        return new UriBuilder(u) { Query = "", Fragment = "" };
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Build(UriBuilder b, IEnumerable<string> segs)
    {
        b.Path = "/" + string.Join("/", segs);
        return b.Uri.AbsoluteUri;
    }

    /// <summary>kind: "generate", "tags" or "show".</summary>
    public static string OllamaUrl(string? baseUrl, string kind)
    {
        var b = ParseBase(baseUrl);
        var segs = Segments(b.Path).ToList();
        var n = segs.Count;
        if (n >= 2 && segs[n - 2].Equals("api", StringComparison.OrdinalIgnoreCase) && segs[n - 1].Equals(kind, StringComparison.OrdinalIgnoreCase)) { }
        else if (n >= 1 && segs[n - 1].Equals("api", StringComparison.OrdinalIgnoreCase)) segs.Add(kind);
        else { segs.Add("api"); segs.Add(kind); }
        return Build(b, segs);
    }

    /// <summary>path: "chat/completions" or "models".</summary>
    public static string OpenAIUrl(string? baseUrl, string path)
    {
        var b = ParseBase(baseUrl);
        var segs = Segments(b.Path).ToList();
        var req = path.Split('/');
        var endsWithReq = segs.Count >= req.Length &&
            Enumerable.Range(0, req.Length).All(i => segs[segs.Count - req.Length + i].Equals(req[i], StringComparison.OrdinalIgnoreCase));
        if (!endsWithReq)
        {
            if (segs.Count == 0 || !segs[^1].Equals("v1", StringComparison.OrdinalIgnoreCase)) segs.Add("v1");
            segs.AddRange(req);
        }
        return Build(b, segs);
    }
}
