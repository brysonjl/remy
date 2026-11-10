using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remy.Core.Cleanup;

/// <summary>Single freeform requests for Transforms (v0.2-features.md §8.5). Reuses the cleanup transport, caches and answer parsing.
/// Exactly one request per call: no cleanup JSON schema, no response_format, no chunking, no retry. HTTP error bodies are never surfaced.</summary>
public sealed class TransformCompletion(CleanupHttpTransport http, CleanupProviderCaches caches)
{
    public static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(60);

    static readonly IReadOnlyDictionary<string, string> OpenRouterHeaders = new Dictionary<string, string>
    {
        ["HTTP-Referer"] = "https://github.com/brysonjl/remy",
        ["X-OpenRouter-Title"] = "Remy",
        ["X-Title"] = "Remy",
    };

    /// <summary>The Ollama context size for a request (never lets the server silently truncate). Throws inputTooLong.</summary>
    public static int ContextLength(string system, string user, int? modelContextLength)
    {
        int userBytes = CleanupChunker.Utf8(user);
        int input = (CleanupChunker.Utf8(system) + userBytes) / 3 + 64;
        int needed = input + Math.Max(512, userBytes * 2 / 3);
        int limit = Math.Min(modelContextLength ?? 8192, 32768);
        int ctx = Math.Min(limit, Math.Max(8192, (needed + 1023) / 1024 * 1024));
        if (needed > ctx) throw new CleanupFailureException(RemyCleanupFailure.InputTooLong);
        return ctx;
    }

    /// <param name="discovery">Built-in mode: true; profile: capabilities.discovery.</param>
    /// <param name="thinkingControl">Built-in mode: true; profile: capabilities.thinkingControl.</param>
    public async Task<string> OllamaAsync(string? baseUrl, string model, string system, string user, string? apiKey, TimeSpan timeout,
        bool discovery, bool thinkingControl, bool reasoningEnabled, CancellationToken ct)
    {
        var name = (model ?? "").Trim();
        if (name.Length == 0) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        var url = CleanupEndpoints.OllamaUrl(baseUrl, "generate");

        var details = OllamaModelDetails.Empty;
        if (discovery)
        {
            try { details = await OllamaCleanupProvider.FetchDetailsAsync(http, caches, baseUrl, name, apiKey, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { details = OllamaModelDetails.Empty; }   // failures are ignored: unknown context length
        }

        int ctx = ContextLength(system, user, details.ContextLength);
        var body = new JsonObject
        {
            ["model"] = name,
            ["system"] = system,
            ["prompt"] = user,
            ["stream"] = false,
            ["keep_alive"] = "10m",
            ["options"] = new JsonObject { ["num_ctx"] = ctx },
        };
        if (thinkingControl && details.ReasoningOffOverride(reasoningEnabled) is { } think) body["think"] = think;
        var bytes = await http.SendAsync(HttpMethod.Post, url, Serialize(body), apiKey, timeout, ct).ConfigureAwait(false);
        return CleanupAnswer.ParseOllama(bytes);
    }

    public async Task<string> OpenRouterAsync(string model, string system, string user, string? apiKey, CancellationToken ct,
        bool reasoningEnabled, TimeSpan? timeout = null)
    {
        var name = (model ?? "").Trim();
        if (name.Length == 0) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new CleanupFailureException(RemyCleanupFailure.Authentication);

        OpenRouterModelInfo? info = null;
        try
        {
            var catalog = await OpenRouterCleanupProvider.FetchCatalogAsync(http, caches, ct).ConfigureAwait(false);
            var lookup = OpenRouterModelInfo.CatalogLookupId(name);
            info = catalog.FirstOrDefault(m => m.Id.Equals(lookup, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { info = null; }

        var body = new JsonObject { ["model"] = name, ["messages"] = Messages(system, user) };
        if (info is not null && !OpenRouterModelInfo.HasVariantSuffix(name) && info.ReasoningOffControl(reasoningEnabled) is { } ctl)
            body["reasoning"] = new JsonObject { [ctl.Key] = ctl.Value is bool b ? JsonValue.Create(b) : JsonValue.Create((string)ctl.Value) };
        var bytes = await http.SendAsync(HttpMethod.Post, CleanupEndpoints.OpenRouterBaseUrl + "/chat/completions",
            Serialize(body), apiKey, timeout ?? RemoteTimeout, ct, OpenRouterHeaders).ConfigureAwait(false);
        return CleanupAnswer.ParseOpenRouter(bytes);
    }

    public async Task<string> OpenAICompatibleAsync(string? baseUrl, string model, string system, string user, string? apiKey,
        TimeSpan timeout, CancellationToken ct)
    {
        var name = (model ?? "").Trim();
        if (name.Length == 0) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        var url = CleanupEndpoints.OpenAIUrl(baseUrl, "chat/completions");
        var body = new JsonObject { ["model"] = name, ["stream"] = false, ["messages"] = Messages(system, user) };
        var bytes = await http.SendAsync(HttpMethod.Post, url, Serialize(body), apiKey, timeout, ct).ConfigureAwait(false);
        return CleanupAnswer.ParseCompatible(bytes);
    }

    static JsonArray Messages(string system, string user) => new()
    {
        new JsonObject { ["role"] = "system", ["content"] = system },
        new JsonObject { ["role"] = "user", ["content"] = user },
    };

    static string Serialize(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
