using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>Which captured text may reach the active provider (cleanup.md §1.0, §5.6, settings-models.md §1.3).</summary>
public static class ContextSharing
{
    public static bool UsesDictationContext(TranscriptPostProcessingMode mode) => mode is
        TranscriptPostProcessingMode.S1Mini or TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenRouter or
        TranscriptPostProcessingMode.OpenAICompatible or TranscriptPostProcessingMode.Profile;

    /// <summary>Loopback endpoint and a model tag that does not end in -cloud / :cloud.</summary>
    public static bool IsLocalEndpoint(string? baseUrl, string? model)
    {
        var tag = (model ?? "").Trim().ToLowerInvariant();
        if (tag.EndsWith("-cloud", StringComparison.Ordinal) || tag.EndsWith(":cloud", StringComparison.Ordinal)) return false;
        var host = DictationContextClassifier.HostOf(baseUrl);
        if (host is null) return false;
        return host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    /// <summary>True when title, URL, field role and before/selected/after text may be included.</summary>
    public static bool IncludesCapturedText(CleanupRuntime runtime) => runtime.Mode switch
    {
        TranscriptPostProcessingMode.S1Mini => false,                        // category only
        TranscriptPostProcessingMode.OpenRouter => runtime.ContextPolicy.ShareCapturedTextRemotely,
        TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenAICompatible or TranscriptPostProcessingMode.Profile =>
            IsLocalEndpoint(runtime.BaseUrl, runtime.Model) || runtime.ContextPolicy.ShareCapturedTextRemotely,
        _ => false,
    };
}

/// <summary>Builds the per-dictation context snapshot with the privacy policy and size limits applied (cleanup.md §5.3).</summary>
public static partial class DictationContextSnapshotBuilder
{
    public const int CursorTextLimit = 600;
    public const int MetadataLimit = 300;
    public const int MaxLexicalHints = 24;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "because", "before", "being", "could", "email", "from", "have", "into", "just",
        "message", "other", "should", "that", "their", "there", "these", "they", "this", "through", "very", "want", "were",
        "what", "when", "where", "which", "while", "with", "would", "your",
    };

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}._'’-]{2,}")]
    private static partial Regex TokenRegex();

    private static readonly char[] SentenceEnders = ['.', '!', '?', '。', '！', '？'];

    /// <summary>Returns null when context awareness is off or the mode does not use context.</summary>
    /// <param name="target">Destination captured at recording start (may be null).</param>
    /// <param name="captured">UIA capture (null on timeout / secure / sensitive app).</param>
    /// <param name="appName">Friendly app name; defaults to the executable name without ".exe".</param>
    public static DictationContextSnapshot? Build(DestinationTarget? target, CapturedContext? captured, CleanupRuntime runtime, string? appName = null)
    {
        var policy = runtime.ContextPolicy;
        if (!policy.IsEnabled || !ContextSharing.UsesDictationContext(runtime.Mode)) return null;

        var process = target?.ProcessName ?? captured?.ProcessName;
        var title = Clip(captured?.WindowTitle ?? target?.WindowTitle, MetadataLimit);
        var url = Clip(captured?.BrowserUrl, MetadataLimit);
        var host = captured?.BrowserHost ?? DictationContextClassifier.HostOf(url);
        var name = appName ?? DefaultAppName(process);

        var (category, contextEnabled) = DictationContextClassifier.Classify(process, title, url ?? host, name, policy.AppRules);
        var secure = target?.IsSecure ?? false;
        var excluded = !contextEnabled || DictationContextClassifier.IsSensitiveApplication(process) || (target?.IsRemy ?? false);

        var style = policy.WritingStyle(category);

        if (secure || excluded)
            return new DictationContextSnapshot(category, style, null, null, null, null, [],
                IsSecureField: secure, IsExcluded: excluded);

        var before = TailClip(captured?.TextBeforeCursor, CursorTextLimit);
        var after = HeadClip(captured?.TextAfterCursor, CursorTextLimit);
        var selected = HeadClip(captured?.SelectedText, CursorTextLimit);
        var label = Clip(captured?.FieldLabel, MetadataLimit);
        var placeholder = Clip(captured?.FieldPlaceholder, MetadataLimit);

        var (placement, continues) = DerivePlacement(before, selected, after);
        var purpose = ClassifyFieldPurpose(label, placeholder);
        var hints = LexicalHints(name, url, title, before, selected, after);

        var includeText = ContextSharing.IncludesCapturedText(runtime);
        CapturedContext? kept = includeText && captured is not null
            ? new CapturedContext(captured.ProcessName, title ?? "", url, host, label, placeholder, before, after, selected)
            : null;

        return new DictationContextSnapshot(category, style, name, process, includeText ? host : null, kept, hints,
            placement, continues, purpose);
    }

    /// <summary>Converts a snapshot to the prompt context, re-applying the sharing policy (defence in depth).</summary>
    public static DictationPostProcessingContext? ToPostProcessingContext(DictationContextSnapshot? snapshot, CleanupRuntime runtime)
    {
        if (snapshot is null) return null;
        var text = ContextSharing.IncludesCapturedText(runtime) && !snapshot.IsSecureField && !snapshot.IsExcluded;
        var c = text ? snapshot.Captured : null;
        return new DictationPostProcessingContext(
            snapshot.Category,
            snapshot.Style.Sanitized(snapshot.Category),
            snapshot.CursorPlacement,
            snapshot.ContinuesExistingSentence,
            snapshot.IsSecureField || snapshot.IsExcluded ? null : snapshot.AppName,
            c?.BrowserUrl,
            c is null ? null : c.WindowTitle,
            c?.FieldLabel,
            snapshot.FieldPurpose,
            c?.TextBeforeCursor,
            c?.SelectedText,
            c?.TextAfterCursor);
    }

    public static (DictationCursorPlacement Placement, bool ContinuesExistingSentence) DerivePlacement(string? before, string? selected, string? after)
    {
        var hasSel = !string.IsNullOrEmpty(selected);
        var hasBefore = !string.IsNullOrWhiteSpace(before);
        var hasAfter = !string.IsNullOrWhiteSpace(after);

        var continues = false;
        if ((hasAfter || hasSel) && hasBefore)
        {
            var b = before!;
            var i = b.Length - 1;
            while (i >= 0 && char.IsWhiteSpace(b[i])) i--;
            if (i >= 0 && Array.IndexOf(SentenceEnders, b[i]) < 0 && !b.AsSpan(i + 1).ContainsAny('\n', '\r')) continues = true;
        }

        DictationCursorPlacement placement;
        if (hasSel) placement = DictationCursorPlacement.ReplacingSelection;
        else if (!hasBefore && !hasAfter) placement = DictationCursorPlacement.EmptyField;
        else if (!hasBefore) placement = DictationCursorPlacement.StartOfText;
        else if (!hasAfter) placement = DictationCursorPlacement.EndOfText;
        else placement = continues ? DictationCursorPlacement.MidSentence : DictationCursorPlacement.BetweenSentences;
        return (placement, continues);
    }

    private static readonly string[] SearchTokens = ["search", "find", "filter", "query"];
    private static readonly string[] SearchCompounds = ["searchbox", "searchfield", "searchinput", "searchquery", "searchtextbox"];

    public static DictationFieldPurpose ClassifyFieldPurpose(params string?[] metadata)
    {
        foreach (var m in metadata)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            var folded = Fold(m);
            var tokens = Regex.Split(folded, @"[^a-z0-9]+").Where(t => t.Length > 0);
            if (tokens.Any(t => SearchTokens.Contains(t))) return DictationFieldPurpose.SearchQuery;
            var alnum = Regex.Replace(folded, @"[^a-z0-9]", "");
            if (SearchCompounds.Any(c => alnum.Contains(c, StringComparison.Ordinal))) return DictationFieldPurpose.SearchQuery;
        }
        return DictationFieldPurpose.Unknown;
    }

    private static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public static IReadOnlyList<string> LexicalHints(params string?[] sources)
    {
        var joined = string.Join(" ", sources.Where(s => !string.IsNullOrEmpty(s)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in TokenRegex().Matches(joined))
        {
            var t = m.Value.Trim('.', '\'', '’', '-');
            if (t.Length < 3 || StopWords.Contains(t) || !IsDistinctive(t) || !seen.Add(t)) continue;
            result.Add(t);
            if (result.Count >= MaxLexicalHints) break;
        }
        return result;
    }

    private static bool IsDistinctive(string t)
    {
        if (char.IsUpper(t[0])) return true;
        if (t.Length >= 7) return true;
        for (var i = 1; i < t.Length; i++)
            if (char.IsUpper(t[i]) || char.IsDigit(t[i]) || t[i] is '.' or '_' or '-') return true;
        return char.IsDigit(t[0]);
    }

    private static string? DefaultAppName(string? process)
    {
        var p = (process ?? "").Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        return p.Length == 0 ? null : p;
    }

    private static bool IsLow(string s, int i) => i > 0 && i < s.Length && char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1]);

    private static string? Clip(string? s, int max) => HeadClip(s, max);

    public static string? HeadClip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length <= max) return s;
        var end = IsLow(s, max) ? max - 1 : max;
        return s[..end];
    }

    public static string? TailClip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length <= max) return s;
        var start = s.Length - max;
        if (IsLow(s, start)) start++;
        return s[start..];
    }
}
