using System.Text.Json;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

public sealed record ModelListResult(bool Ok, IReadOnlyList<string> Models, RemyCleanupFailure? Failure);

public sealed record OpenRouterModelEntry(string Id, int? ContextLength, bool SupportsStructuredOutputs);

public sealed record OllamaReasoningInfo(OllamaReasoningCapability Capability, int ContextLength, bool KnownNonThinking);

public sealed record ConnectionTestResult(bool Passed, RemyCleanupFailure? Failure);

/// <summary>Model discovery and "Test connection" helpers for the settings UI (cleanup.md §1.2-§1.4, §4.6).</summary>
public sealed class CleanupDiscovery(CleanupHttpTransport http, CleanupProviderCaches? caches = null)
{
    public const string ConnectionCheckInput = "Remy connection check. Keep the final blue word.";

    private readonly CleanupProviderCaches _caches = caches ?? new CleanupProviderCaches();
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    /// <summary>GET /api/tags: installed model names (trimmed, de-duplicated, server order).</summary>
    public async Task<ModelListResult> ListOllamaModelsAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        try
        {
            var url = CleanupEndpoints.OllamaUrl(baseUrl, "tags");
            var bytes = await http.SendAsync(HttpMethod.Get, url, null, apiKey, Timeout5, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var names = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        var s = n.GetString()!.Trim();
                        if (s.Length > 0 && !names.Contains(s)) names.Add(s);
                    }
            return new ModelListResult(true, names, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ModelListResult(false, [], RemyCleanupFailure.Classify(ex)); }
    }

    /// <summary>Selected model matches an installed one (exact when it has a tag, else exact or "name:" prefix).</summary>
    public static string? MatchingInstalledModel(string selected, IEnumerable<string> installed)
    {
        var sel = selected.Trim();
        if (sel.Length == 0) return null;
        foreach (var n in installed)
        {
            if (sel.Contains(':')) { if (n == sel) return n; }
            else if (n == sel || n.StartsWith(sel + ":", StringComparison.Ordinal)) return n;
        }
        return null;
    }

    /// <summary>POST /api/show: reasoning capability, context length and thinking facts for one model.</summary>
    public async Task<(OllamaReasoningInfo? Info, RemyCleanupFailure? Failure)> GetOllamaReasoningAsync(
        string? baseUrl, string model, string? apiKey, CancellationToken ct, bool refresh = false)
    {
        try
        {
            if (refresh) _caches.OllamaDetails.Invalidate((CleanupEndpoints.OllamaUrl(baseUrl, "show"), OllamaCleanupProvider.TrimLatest(model.Trim())));
            var d = await OllamaCleanupProvider.FetchDetailsAsync(http, _caches, baseUrl, model, apiKey, ct).ConfigureAwait(false);
            return (new OllamaReasoningInfo(d.Reasoning, d.CleanupContextLength, d.KnownNonThinking), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, RemyCleanupFailure.Classify(ex)); }
    }

    /// <summary>GET {base}/models for OpenAI-compatible servers: ids trimmed, de-duplicated, sorted case-insensitively.</summary>
    public async Task<ModelListResult> ListOpenAICompatibleModelsAsync(string? baseUrl, string? apiKey, CancellationToken ct)
    {
        try
        {
            var url = CleanupEndpoints.OpenAIUrl(baseUrl, "models");
            var bytes = await http.SendAsync(HttpMethod.Get, url, null, apiKey, Timeout5, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var ids = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var m in data.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var s = id.GetString()!.Trim();
                        if (s.Length > 0 && !ids.Contains(s)) ids.Add(s);
                    }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return new ModelListResult(true, ids, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ModelListResult(false, [], RemyCleanupFailure.Classify(ex)); }
    }

    public static bool SelectedModelIsAvailable(string selected, IEnumerable<string> ids) =>
        ids.Any(i => i.Equals(selected.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>OpenRouter catalog (no auth): structured-output capable first, then case-insensitive id.</summary>
    public async Task<(IReadOnlyList<OpenRouterModelEntry> Models, RemyCleanupFailure? Failure)> ListOpenRouterModelsAsync(CancellationToken ct, bool refresh = false)
    {
        try
        {
            var cat = await OpenRouterCleanupProvider.FetchCatalogAsync(http, _caches, ct, refresh).ConfigureAwait(false);
            var list = cat.OrderByDescending(m => m.SupportsStructuredOutputs).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(m => new OpenRouterModelEntry(m.Id, m.ContextLength, m.SupportsStructuredOutputs)).ToList();
            return (list, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ([], RemyCleanupFailure.Classify(ex)); }
    }

    /// <summary>Capability lookup for one OpenRouter model (reasoning toggle, structured outputs).</summary>
    public async Task<OpenRouterModelInfo?> GetOpenRouterModelAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var cat = await OpenRouterCleanupProvider.FetchCatalogAsync(http, _caches, ct).ConfigureAwait(false);
            var id = OpenRouterModelInfo.CatalogLookupId(modelId);
            return cat.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Runs one cleanup through <paramref name="engine"/> with the synthetic connection-check input.
    /// Passes iff the call returns non-blank cleaned text without a failure; details are never shown to the user.
    /// </summary>
    public static async Task<ConnectionTestResult> TestConnectionAsync(ICleanupEngine engine, CleanupRuntime runtime, CancellationToken ct, TimeSpan? limit = null)
    {
        var l = limit ?? TimeSpan.FromSeconds(Math.Clamp(runtime.RequestTimeout.TotalSeconds, 8, 120));
        var req = new CleanupRequest(Guid.NewGuid(), Guid.NewGuid(), ConnectionCheckInput, runtime, null, [], DateTimeOffset.UtcNow + l);
        try
        {
            var r = await engine.CleanAsync(req, ct).ConfigureAwait(false);
            var ok = r.Failure is null && r.Outcome is CleanupOutcome.Cleaned or CleanupOutcome.Unchanged && !string.IsNullOrWhiteSpace(r.Output);
            return new ConnectionTestResult(ok, ok ? null : r.Failure ?? RemyCleanupFailure.Of(RemyCleanupFailure.ProviderFailure));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ConnectionTestResult(false, RemyCleanupFailure.Classify(ex)); }
    }
}
