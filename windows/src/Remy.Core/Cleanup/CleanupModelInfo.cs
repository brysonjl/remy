using System.Text.Json;

namespace Remy.Core.Cleanup;

/// <summary>Successful-load-only async cache with capacity and lifetime (cleanup.md TimedRequestCache).</summary>
public sealed class TimedRequestCache<TKey, TValue> where TKey : notnull
{
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly Dictionary<TKey, (Task<TValue> Task, DateTime At)> _items = new();
    private readonly object _gate = new();

    public TimedRequestCache(TimeSpan lifetime, int capacity) { _lifetime = lifetime; _capacity = capacity; }

    public async Task<TValue> GetAsync(TKey key, Func<Task<TValue>> load, bool refresh = false)
    {
        Task<TValue> task;
        lock (_gate)
        {
            if (refresh) _items.Remove(key);
            if (_items.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < _lifetime) task = e.Task;
            else
            {
                task = load();
                _items[key] = (task, DateTime.UtcNow);
                while (_items.Count > _capacity)
                    _items.Remove(_items.OrderBy(kv => kv.Value.At).First().Key);
            }
        }
        try { return await task.ConfigureAwait(false); }
        catch
        {
            lock (_gate) { if (_items.TryGetValue(key, out var e) && e.Task == task) _items.Remove(key); }
            throw;
        }
    }

    public void Invalidate(TKey key) { lock (_gate) _items.Remove(key); }
    public void Clear() { lock (_gate) _items.Clear(); }
}

public enum OllamaReasoningCapability { Unsupported, Toggle, Level, Required }

/// <summary>Decoded /api/show (cleanup.md §1.2).</summary>
public sealed record OllamaModelDetails(
    bool HasThinkingBlock,
    IReadOnlyList<string> ThinkingValues,   // "true", "false" or level strings
    IReadOnlyList<string> Capabilities,
    int? ContextLength,                     // smallest positive *.context_length
    IReadOnlyList<string> Families)
{
    public static OllamaModelDetails Empty { get; } = new(false, [], [], null, []);

    private bool IsGptOss => Families.Any(f => f.Replace("-", "").Equals("gptoss", StringComparison.OrdinalIgnoreCase));

    public int CleanupContextLength => Math.Min(CleanupChunker.MaxBudget, ContextLength is > 0 ? ContextLength.Value : CleanupChunker.MaxBudget);

    public bool KnownNonThinking =>
        HasThinkingBlock
            ? ThinkingValues.Count == 1 && ThinkingValues[0] == "false"
            : Capabilities.Contains("completion") && !Capabilities.Contains("thinking");

    public OllamaReasoningCapability Reasoning
    {
        get
        {
            if (HasThinkingBlock)
            {
                var canOff = ThinkingValues.Contains("false");
                var other = ThinkingValues.Any(v => v != "false");
                if (canOff) return other ? OllamaReasoningCapability.Toggle : OllamaReasoningCapability.Unsupported;
                if (ThinkingValues.Any(v => v != "true")) return OllamaReasoningCapability.Level;
                return other ? OllamaReasoningCapability.Required : OllamaReasoningCapability.Unsupported;
            }
            if (IsGptOss) return OllamaReasoningCapability.Level;
            return Capabilities.Contains("thinking") ? OllamaReasoningCapability.Toggle : OllamaReasoningCapability.Unsupported;
        }
    }

    /// <summary>Value for "think" when the user turned reasoning off, else null (nothing sent).</summary>
    public bool? ReasoningOffOverride(bool reasoningEnabled)
    {
        if (reasoningEnabled) return null;
        if (HasThinkingBlock) return ThinkingValues.Contains("false") ? false : null;
        return Reasoning == OllamaReasoningCapability.Toggle ? false : null;
    }

    public static OllamaModelDetails Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Empty;
        var hasThinking = false;
        var values = new List<string>();
        if (root.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.Object)
        {
            hasThinking = true;
            if (th.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                    values.Add(v.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String => v.GetString() ?? "",
                        _ => "",
                    });
            values.RemoveAll(string.IsNullOrEmpty);
        }
        var caps = new List<string>();
        if (root.TryGetProperty("capabilities", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray()) if (c.ValueKind == JsonValueKind.String) caps.Add(c.GetString()!);
        int? ctx = null;
        if (root.TryGetProperty("model_info", out var mi) && mi.ValueKind == JsonValueKind.Object)
            foreach (var p in mi.EnumerateObject())
                if (p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Number &&
                    p.Value.TryGetInt64(out var n) && n > 0)
                    ctx = ctx is null ? (int)Math.Min(n, int.MaxValue) : Math.Min(ctx.Value, (int)Math.Min(n, int.MaxValue));
        var fams = new List<string>();
        if (root.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("family", out var f) && f.ValueKind == JsonValueKind.String) fams.Add(f.GetString()!);
            if (d.TryGetProperty("families", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var x in fs.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) fams.Add(x.GetString()!);
        }
        return new OllamaModelDetails(hasThinking, values, caps, ctx, fams);
    }
}

/// <summary>One OpenRouter catalog row (cleanup.md §1.4).</summary>
public sealed record OpenRouterModelInfo(
    string Id,
    int? ContextLength,
    int? MaxCompletionTokens,
    IReadOnlyList<string>? SupportedParameters,
    bool ReasoningPresent,
    bool? ReasoningMandatory,
    bool EffortsFieldPresent,
    IReadOnlyList<string>? SupportedEfforts)
{
    public bool SupportsStructuredOutputs => SupportedParameters is null || SupportedParameters.Contains("structured_outputs");

    private bool SupportsNone => EffortsFieldPresent && (SupportedEfforts is null || SupportedEfforts.Contains("none"));

    public bool CanDisableReasoning => ReasoningPresent && ReasoningMandatory != true && (ReasoningMandatory == false || SupportsNone);

    /// <summary>The "reasoning" request object when the user turned reasoning off, else null.</summary>
    public (string Key, object Value)? ReasoningOffControl(bool reasoningEnabled)
    {
        if (reasoningEnabled || !CanDisableReasoning) return null;
        if (SupportsNone) return ("effort", "none");
        if (ReasoningMandatory == false) return ("enabled", false);
        return null;
    }

    public static string CatalogLookupId(string modelId)
    {
        var id = modelId.Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var v in new[] { ":exacto", ":floor", ":nitro", ":online" })
                if (id.EndsWith(v, StringComparison.OrdinalIgnoreCase)) { id = id[..^v.Length]; changed = true; }
        }
        return id;
    }

    public static bool HasVariantSuffix(string modelId) => CatalogLookupId(modelId) != modelId.Trim();

    public static IReadOnlyList<OpenRouterModelInfo> ParseCatalog(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<OpenRouterModelInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in data.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString()!.Trim();
            if (id.Length == 0) continue;

            int? PositiveInt(JsonElement parent, string name) =>
                parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number &&
                e.TryGetInt64(out var n) && n > 0 ? (int)Math.Min(n, int.MaxValue) : null;

            var c1 = PositiveInt(m, "context_length");
            int? c2 = null, maxCompletion = null;
            if (m.TryGetProperty("top_provider", out var tp)) { c2 = PositiveInt(tp, "context_length"); maxCompletion = PositiveInt(tp, "max_completion_tokens"); }
            int? ctx = c1 is null ? c2 : c2 is null ? c1 : Math.Min(c1.Value, c2.Value);

            List<string>? sp = null;
            if (m.TryGetProperty("supported_parameters", out var spe) && spe.ValueKind == JsonValueKind.Array)
            {
                sp = [];
                foreach (var p in spe.EnumerateArray()) if (p.ValueKind == JsonValueKind.String) sp.Add(p.GetString()!);
            }

            var rPresent = false; bool? mandatory = null; var effortsPresent = false; List<string>? efforts = null;
            if (m.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                rPresent = true;
                if (r.TryGetProperty("mandatory", out var man) && man.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    mandatory = man.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("supported_efforts", out var se))
                {
                    effortsPresent = true;
                    if (se.ValueKind == JsonValueKind.Array)
                    {
                        efforts = [];
                        foreach (var x in se.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) efforts.Add(x.GetString()!);
                    }
                }
            }
            list.Add(new OpenRouterModelInfo(id, ctx, maxCompletion, sp, rPresent, mandatory, effortsPresent, efforts));
        }
        return list;
    }
}
