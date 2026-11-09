using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remy.Core.Cleanup;

/// <summary>Budget inputs a provider discovered before chunking.</summary>
internal sealed record RemoteLimits(int Budget, int? MaxCompletionTokens);

/// <summary>Shared per-process caches (model details 300 s / 8, OpenRouter catalog 900 s / 1).</summary>
public sealed class CleanupProviderCaches
{
    internal TimedRequestCache<(string Url, string Model), OllamaModelDetails> OllamaDetails { get; } = new(TimeSpan.FromSeconds(300), 8);
    internal TimedRequestCache<string, IReadOnlyList<OpenRouterModelInfo>> OpenRouterCatalog { get; } = new(TimeSpan.FromSeconds(900), 1);

    public void Clear() { OllamaDetails.Clear(); OpenRouterCatalog.Clear(); }
}

internal abstract class RemoteProvider
{
    public abstract string Name { get; }
    public virtual int MaxConcurrentRequests => 1;
    public abstract Task<RemoteLimits> PrepareAsync(CancellationToken ct);
    public abstract Task<string> CompleteAsync(string system, string user, string chunkText, CancellationToken ct);

    protected static JsonNode SchemaNode() => JsonNode.Parse(CleanupAnswer.SchemaJson)!;

    protected static JsonNode ResponseFormat() => new JsonObject
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject { ["name"] = "dictate_anywhere_cleanup", ["strict"] = true, ["schema"] = SchemaNode() },
    };

    protected static JsonArray Messages(string system, string user) => new()
    {
        new JsonObject { ["role"] = "system", ["content"] = system },
        new JsonObject { ["role"] = "user", ["content"] = user },
    };

    protected static string Serialize(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

internal sealed class OllamaCleanupProvider(
    CleanupHttpTransport http, CleanupProviderCaches caches, string? baseUrl, string model, string? apiKey,
    TimeSpan timeout, bool structured, bool discovery, bool thinkingControl, bool reasoningEnabled) : RemoteProvider
{
    private OllamaModelDetails _details = OllamaModelDetails.Empty;
    public override string Name => "Ollama";

    public static string TrimLatest(string model) => model.EndsWith(":latest", StringComparison.Ordinal) ? model[..^7] : model;

    public static async Task<OllamaModelDetails> FetchDetailsAsync(CleanupHttpTransport http, CleanupProviderCaches caches,
        string? baseUrl, string model, string? apiKey, CancellationToken ct)
    {
        var url = CleanupEndpoints.OllamaUrl(baseUrl, "show");
        return await caches.OllamaDetails.GetAsync((url, TrimLatest(model.Trim())), async () =>
        {
            var body = new JsonObject { ["model"] = model.Trim() }.ToJsonString();
            var bytes = await http.SendAsync(HttpMethod.Post, url, body, apiKey, TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
            return OllamaModelDetails.Parse(bytes);
        }).ConfigureAwait(false);
    }

    public override async Task<RemoteLimits> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        CleanupEndpoints.OllamaUrl(baseUrl, "generate"); // validate early
        if (discovery)
        {
            try { _details = await FetchDetailsAsync(http, caches, baseUrl, model, apiKey, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { _details = OllamaModelDetails.Empty; } // discovery failure fails open to defaults
        }
        return new RemoteLimits(CleanupChunker.Budget(_details.CleanupContextLength), null);
    }

    public override async Task<string> CompleteAsync(string system, string user, string chunkText, CancellationToken ct)
    {
        var url = CleanupEndpoints.OllamaUrl(baseUrl, "generate");
        var options = new JsonObject
        {
            ["num_ctx"] = _details.CleanupContextLength,
            ["num_predict"] = _details.KnownNonThinking ? CleanupChunker.OutputReserve(CleanupChunker.Utf8(chunkText)) : -1,
        };
        var body = new JsonObject
        {
            ["model"] = model.Trim(),
            ["system"] = system,
            ["prompt"] = user,
            ["stream"] = false,
            ["keep_alive"] = "10m",
            ["options"] = options,
        };
        if (structured) body["format"] = SchemaNode();
        if (thinkingControl && _details.ReasoningOffOverride(reasoningEnabled) is { } think) body["think"] = think;
        var bytes = await http.SendAsync(HttpMethod.Post, url, Serialize(body), apiKey, timeout, ct).ConfigureAwait(false);
        return CleanupAnswer.ParseOllama(bytes);
    }
}

internal sealed class OpenAICompatibleCleanupProvider(
    CleanupHttpTransport http, string? baseUrl, string model, string? apiKey, TimeSpan timeout, bool structured) : RemoteProvider
{
    public override string Name => "OpenAI-compatible";

    public override Task<RemoteLimits> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        CleanupEndpoints.OpenAIUrl(baseUrl, "chat/completions");
        return Task.FromResult(new RemoteLimits(CleanupChunker.MaxBudget, null));
    }

    public override async Task<string> CompleteAsync(string system, string user, string chunkText, CancellationToken ct)
    {
        var url = CleanupEndpoints.OpenAIUrl(baseUrl, "chat/completions");
        var body = new JsonObject { ["model"] = model.Trim(), ["stream"] = false, ["messages"] = Messages(system, user) };
        if (structured) body["response_format"] = ResponseFormat();
        var bytes = await http.SendAsync(HttpMethod.Post, url, Serialize(body), apiKey, timeout, ct).ConfigureAwait(false);
        return CleanupAnswer.ParseCompatible(bytes);
    }
}

internal sealed class OpenRouterCleanupProvider(
    CleanupHttpTransport http, CleanupProviderCaches caches, string model, string? apiKey, TimeSpan timeout, bool reasoningEnabled) : RemoteProvider
{
    private OpenRouterModelInfo? _info;
    public override string Name => "OpenRouter";
    public override int MaxConcurrentRequests => 2;

    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["HTTP-Referer"] = "https://github.com/brysonjl/remy",
        ["X-OpenRouter-Title"] = "Remy",
        ["X-Title"] = "Remy",
    };

    public static Task<IReadOnlyList<OpenRouterModelInfo>> FetchCatalogAsync(CleanupHttpTransport http, CleanupProviderCaches caches, CancellationToken ct, bool refresh = false) =>
        caches.OpenRouterCatalog.GetAsync(CleanupEndpoints.OpenRouterBaseUrl, async () =>
        {
            var bytes = await http.SendAsync(HttpMethod.Get, CleanupEndpoints.OpenRouterBaseUrl + "/models", null, null,
                TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
            return OpenRouterModelInfo.ParseCatalog(bytes);
        }, refresh);

    public override async Task<RemoteLimits> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new CleanupFailureException(RemyCleanupFailure.Authentication);
        try
        {
            var catalog = await FetchCatalogAsync(http, caches, ct).ConfigureAwait(false);
            var lookup = OpenRouterModelInfo.CatalogLookupId(model);
            _info = catalog.FirstOrDefault(m => m.Id.Equals(lookup, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { _info = null; }
        return new RemoteLimits(CleanupChunker.Budget(_info?.ContextLength), _info?.MaxCompletionTokens);
    }

    public override async Task<string> CompleteAsync(string system, string user, string chunkText, CancellationToken ct)
    {
        var structured = _info?.SupportsStructuredOutputs ?? true;
        var body = new JsonObject { ["model"] = model.Trim(), ["messages"] = Messages(system, user) };
        if (structured)
        {
            body["provider"] = new JsonObject { ["require_parameters"] = true };
            body["response_format"] = ResponseFormat();
        }
        if (_info is not null && !OpenRouterModelInfo.HasVariantSuffix(model) &&
            _info.ReasoningOffControl(reasoningEnabled) is { } ctl)
            body["reasoning"] = new JsonObject { [ctl.Key] = ctl.Value is bool b ? JsonValue.Create(b) : JsonValue.Create((string)ctl.Value) };
        var bytes = await http.SendAsync(HttpMethod.Post, CleanupEndpoints.OpenRouterBaseUrl + "/chat/completions",
            Serialize(body), apiKey, timeout, ct, Headers).ConfigureAwait(false);
        return CleanupAnswer.ParseOpenRouter(bytes);
    }
}
