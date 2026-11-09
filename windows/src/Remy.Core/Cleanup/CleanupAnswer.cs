using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Remy.Core.Cleanup;

/// <summary>Answer handling shared by every remote provider and the S1 path (cleanup.md §1.1).</summary>
public static partial class CleanupAnswer
{
    public const string ActionPasteCleaned = "pasteCleanedText";
    public const string ActionPasteAsIs = "pasteTranscriptAsIs";

    /// <summary>JSON Schema for structured output (§1.1), exact bytes.</summary>
    public const string SchemaJson =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"pasteCleanedText\",\"pasteTranscriptAsIs\"]},\"text\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"action\",\"text\"],\"additionalProperties\":false}";

    private static readonly string[] RefusalPhrases =
    [
        "i cannot", "i can't", "i'm sorry", "i am sorry", "i'm unable", "i am unable", "sorry, i", "i apologize",
        "not able to assist", "cannot assist", "can't assist", "cannot help", "can't help", "not appropriate",
        "i'm not able", "i am not able", "as an ai", "as a language model",
    ];

    private static int ScalarCount(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    public static bool LooksLikeGeneratedContent(string input, string output)
    {
        var i = ScalarCount(input);
        var o = ScalarCount(output);
        return i < 30 ? o > Math.Max(i * 3, 60) : o > i * 2;
    }

    public static bool LooksLikeNewRefusalMessage(string input, string output)
    {
        var li = input.ToLowerInvariant();
        var lo = output.ToLowerInvariant();
        foreach (var p in RefusalPhrases)
            if (lo.Contains(p, StringComparison.Ordinal) && !li.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs}|\p{IsCJKUnifiedIdeographsExtensionA})\s*—+\s*")]
    private static partial Regex HanDash();

    [GeneratedRegex(@"\s*—\s*")]
    private static partial Regex AnyDash();

    /// <summary>normalizePostProcessedTranscript (§1.1). Only U+2014 is touched.</summary>
    public static string Normalize(string text)
    {
        var s = HanDash().Replace(text, "，");
        s = AnyDash().Replace(s, ", ");
        s = s.Replace(" ,", ",").Replace(".,", ".").Replace("!,", "!").Replace("?,", "?")
             .Replace(" ，", "，").Replace(" 。", "。").Replace(" ！", "！").Replace(" ？", "？").Replace("。，", "。");
        return s.Trim();
    }

    /// <summary>Strip one outer Markdown fence (§1.1 step 1).</summary>
    public static string StripFence(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal) && t.EndsWith("```", StringComparison.Ordinal))
        {
            var lines = t.Split('\n');
            if (lines.Length < 2) return "";
            return string.Join("\n", lines.Skip(1).Take(lines.Length - 2)).Trim();
        }
        return t;
    }

    private static bool TryDecodeEnvelope(string json, out string? action, out string? text)
    {
        action = null; text = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String) return false;
            var act = a.GetString()!.Trim();
            if (act != ActionPasteCleaned && act != ActionPasteAsIs) return false;
            if (doc.RootElement.TryGetProperty("text", out var t))
            {
                if (t.ValueKind == JsonValueKind.String) text = t.GetString();
                else if (t.ValueKind != JsonValueKind.Null) return false;
            }
            action = act;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryFindEnvelope(string s, out string? action, out string? text)
    {
        action = null; text = null;
        for (var start = 0; start < s.Length; start++)
        {
            if (s[start] != '{') continue;
            var depth = 0; var inString = false; var escaped = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (TryDecodeEnvelope(s.Substring(start, i - start + 1), out action, out text)) return true;
                        break;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>cleanedRemotePostProcessingResponse (§1.1). Never throws; falls back to <paramref name="original"/>.</summary>
    public static string CleanedResponse(string answer, string original)
    {
        var normalized = StripFence(answer);
        if (normalized.Length == 0) return original;

        string? action; string? text;
        var structured = TryDecodeEnvelope(normalized, out action, out text) || TryFindEnvelope(normalized, out action, out text);
        if (structured)
        {
            if (action == ActionPasteAsIs) return original;
            var cleaned = Normalize((text ?? "").Trim());
            if (cleaned.Length == 0) return original;
            if (LooksLikeNewRefusalMessage(original, cleaned) || LooksLikeGeneratedContent(original, cleaned)) return original;
            return cleaned;
        }

        if (normalized.StartsWith('{') || normalized.StartsWith('[') ||
            normalized.Contains("\"action\"", StringComparison.Ordinal) ||
            normalized.Contains("<think>", StringComparison.Ordinal))
            return original;

        var plain = Normalize(normalized);
        if (plain.Length == 0) return original;
        if (LooksLikeNewRefusalMessage(original, plain) || LooksLikeGeneratedContent(original, plain)) return original;
        return plain;
    }

    // ------------------------------------------------------------------ provider body parsers

    public const int ResponseBytesLimit = 2_097_152;
    public const int AnswerBytesLimit = 262_144;

    private static CleanupFailureException Fail(string category) => new(category);

    private static JsonDocument ParseBody(byte[] body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw Fail(RemyCleanupFailure.InvalidResponse); }
    }

    private static string CheckAnswer(string answer)
    {
        if (Encoding.UTF8.GetByteCount(answer) > AnswerBytesLimit) throw Fail(RemyCleanupFailure.ResponseTooLarge);
        if (string.IsNullOrWhiteSpace(answer)) throw Fail(RemyCleanupFailure.InvalidResponse);
        return answer.Trim();
    }

    /// <summary>Ollama /api/generate complete JSON body -> answer text.</summary>
    public static string ParseOllama(byte[] body)
    {
        using var doc = ParseBody(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Fail(RemyCleanupFailure.InvalidResponse);
        if (root.TryGetProperty("error", out var err) &&
            !(err.ValueKind == JsonValueKind.Null || (err.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(err.GetString()))))
            throw Fail(RemyCleanupFailure.ProviderFailure);
        if (root.TryGetProperty("message", out _) || root.TryGetProperty("tool_calls", out _))
            throw Fail(RemyCleanupFailure.InvalidResponse);
        if (!root.TryGetProperty("done", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Fail(RemyCleanupFailure.IncompleteResponse);
        if (done.ValueKind != JsonValueKind.True) throw Fail(RemyCleanupFailure.IncompleteResponse);
        if (root.TryGetProperty("done_reason", out var dr) && dr.ValueKind != JsonValueKind.Null &&
            !(dr.ValueKind == JsonValueKind.String && dr.GetString() == "stop"))
            throw Fail(RemyCleanupFailure.IncompleteResponse);
        if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.String)
            throw Fail(RemyCleanupFailure.InvalidResponse);
        return CheckAnswer(resp.GetString()!);
    }

    /// <summary>OpenAI-compatible chat completion (strict: finish_reason must be "stop").</summary>
    public static string ParseCompatible(byte[] body) => ParseChat(body, strictFinish: true);

    /// <summary>OpenRouter chat completion (finish_reason may be absent).</summary>
    public static string ParseOpenRouter(byte[] body) => ParseChat(body, strictFinish: false);

    private static string ParseChat(byte[] body, bool strictFinish)
    {
        using var doc = ParseBody(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Fail(RemyCleanupFailure.InvalidResponse);
        if (root.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null) throw Fail(RemyCleanupFailure.InvalidResponse);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw Fail(RemyCleanupFailure.InvalidResponse);
        if (choices.GetArrayLength() != 1) throw Fail(RemyCleanupFailure.InvalidResponse);
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) throw Fail(RemyCleanupFailure.InvalidResponse);
        if (!choice.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            throw Fail(RemyCleanupFailure.InvalidResponse);

        if (msg.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() != "assistant")
            throw Fail(RemyCleanupFailure.InvalidResponse);
        if (msg.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(refusal.GetString()))
            throw Fail(strictFinish ? RemyCleanupFailure.RequestRejected : RemyCleanupFailure.IncompleteResponse);
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0)
            throw Fail(RemyCleanupFailure.InvalidResponse);
        if (msg.TryGetProperty("function_call", out var fc) && fc.ValueKind != JsonValueKind.Null)
            throw Fail(RemyCleanupFailure.InvalidResponse);

        string? finish = null;
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString();
        if (strictFinish ? finish != "stop" : (finish is not null && finish != "stop"))
            throw Fail(RemyCleanupFailure.IncompleteResponse);

        if (!msg.TryGetProperty("content", out var content)) throw Fail(RemyCleanupFailure.InvalidResponse);
        string answer;
        if (content.ValueKind == JsonValueKind.String) answer = content.GetString()!;
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var p in content.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) throw Fail(RemyCleanupFailure.InvalidResponse);
                var type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                switch (type)
                {
                    case "text":
                        if (p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) parts.Add(tx.GetString()!);
                        else throw Fail(RemyCleanupFailure.InvalidResponse);
                        break;
                    case "reasoning": case "reasoning_text": case "thinking": break;
                    default: throw Fail(RemyCleanupFailure.InvalidResponse);
                }
            }
            answer = string.Join("\n", parts);
        }
        else if (content.ValueKind == JsonValueKind.Null && !strictFinish) throw Fail(RemyCleanupFailure.IncompleteResponse);
        else throw Fail(RemyCleanupFailure.InvalidResponse);

        if (!strictFinish && string.IsNullOrWhiteSpace(answer)) throw Fail(RemyCleanupFailure.IncompleteResponse);
        return CheckAnswer(answer);
    }
}
