using System.Text;
using System.Text.RegularExpressions;

namespace Remy.Cleanup.Local;

/// <summary>Output checks for S1-mini (cleanup.md 1.1, 1.5, 3.3). Self-contained copy so this project does not depend on Core internals.</summary>
public static class S1MiniOutputSafety
{
    private static readonly Regex NumericSign = new(
        @"(?<![\p{L}\p{N}])(?:minus\s+|negative\s+|[-−]\s*)[$€£]?\s*([0-9]+(?:[.,][0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] RefusalPhrases =
    {
        "i cannot", "i can't", "i'm sorry", "i am sorry", "i'm unable", "i am unable", "sorry, i", "i apologize",
        "not able to assist", "cannot assist", "can't assist", "cannot help", "can't help", "not appropriate",
        "i'm not able", "i am not able", "as an ai", "as a language model",
    };

    private static readonly Regex HanDash = new(@"(?<=\p{IsCJKUnifiedIdeographs})\s*—+\s*", RegexOptions.CultureInvariant);
    private static readonly Regex Dash = new(@"\s*—\s*", RegexOptions.CultureInvariant);

    public static bool ContainsControlLeak(string output) =>
        output.Contains("<think>", StringComparison.Ordinal) || output.Contains("<|im_", StringComparison.Ordinal);

    /// <summary>True when the model dropped a minus/negative sign present in the input.</summary>
    public static bool RemovesNumericSign(string input, string output)
    {
        var expected = Collect(input);
        if (expected.Count == 0) return false;
        var actual = Collect(output);
        var leftover = new List<string>();
        foreach (var e in expected)
        {
            var i = actual.IndexOf(e);
            if (i >= 0) actual.RemoveAt(i); else leftover.Add(e);
        }
        foreach (var e in leftover)
        {
            var i = actual.FindIndex(a => a.StartsWith(e + ".", StringComparison.Ordinal));
            if (i < 0) return true;
            actual.RemoveAt(i);
        }
        return false;
    }

    private static List<string> Collect(string text) =>
        NumericSign.Matches(text).Select(m => m.Groups[1].Value.Replace(",", "")).ToList();

    public static bool LooksLikeNewRefusalMessage(string input, string output)
    {
        var i = input.ToLowerInvariant();
        var o = output.ToLowerInvariant();
        foreach (var p in RefusalPhrases)
            if (o.Contains(p, StringComparison.Ordinal) && !i.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool LooksLikeGeneratedContent(string input, string output)
    {
        int inLen = ScalarCount(input), outLen = ScalarCount(output);
        return inLen < 30 ? outLen > Math.Max(inLen * 3, 60) : outLen > inLen * 2;
    }

    private static int ScalarCount(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    /// <summary>normalizePostProcessedTranscript (cleanup.md 1.1).</summary>
    public static string NormalizeTranscript(string text)
    {
        var t = HanDash.Replace(text, "，");
        t = Dash.Replace(t, ", ");
        t = t.Replace(" ,", ",").Replace(".,", ".").Replace("!,", "!").Replace("?,", "?")
             .Replace(" ，", "，").Replace(" 。", "。").Replace(" ！", "！")
             .Replace(" ？", "？").Replace("。，", "。");
        return t.Trim();
    }

    /// <summary>Per-chunk decision after generation (leak is checked by the caller first).
    /// Returns the text to use: the cleaned output, or the input when any safety check fails.</summary>
    public static string Accept(string input, string rawOutput)
    {
        var trimmed = rawOutput.Trim();
        if (trimmed.Length == 0) return input;
        var cleaned = NormalizeTranscript(trimmed);
        if (cleaned.Length == 0) return input;
        if (RemovesNumericSign(input, cleaned)) return input;
        if (LooksLikeNewRefusalMessage(input, cleaned) || LooksLikeGeneratedContent(input, cleaned)) return input;
        return cleaned;
    }
}
