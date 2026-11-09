using System.Text;
using System.Text.RegularExpressions;

namespace Remy.Core.Text;

/// <summary>Local filler-word removal, run before cleanup for every mode (cleanup.md §6.5).</summary>
public static class FillerWordRemover
{
    public static readonly IReadOnlyList<string> DefaultWords = ["um", "uh", "erm", "er", "hmm", "嗯", "呃"];

    public const int MaxWords = 1000;
    public const int MaxWordBytes = 256;

    private static readonly HashSet<string> BareMatch = new(StringComparer.Ordinal) { "嗯", "呃" };

    private static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x9FFF) || (cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0xF900 && cp <= 0xFAFF) ||
        (cp >= 0x20000 && cp <= 0x2FA1F) || (cp >= 0xFF66 && cp <= 0xFF9F);

    private static bool ContainsCjk(string w)
    {
        foreach (var r in w.EnumerateRunes()) if (IsCjk(r.Value)) return true;
        return false;
    }

    /// <summary>Builds the combined case-insensitive alternation, or null when there is nothing to remove.</summary>
    public static Regex? BuildPattern(IEnumerable<string> words)
    {
        var parts = new List<string>();
        foreach (var raw in words.Take(MaxWords))
        {
            var w = raw.Trim(' ');
            if (w.Length == 0 || Encoding.UTF8.GetByteCount(w) > MaxWordBytes) continue;
            var esc = Regex.Escape(w);
            if (ContainsCjk(w))
            {
                var p = $"(?:{esc})+";
                if (!BareMatch.Contains(w)) p = $@"(?<![^\s\p{{P}}]){p}(?![^\s\p{{P}}])";
                parts.Add(p);
            }
            else parts.Add($@"\b{esc}\b");
        }
        if (parts.Count == 0) return null;
        return new Regex("(?:" + string.Join("|", parts) + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string Remove(string text, IEnumerable<string>? words = null)
    {
        var re = BuildPattern(words ?? DefaultWords);
        if (re is null) return text;
        var s = re.Replace(text, "");
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        foreach (var p in new[] { ",", ".", "!", "?", "，", "。", "！", "？" }) s = s.Replace(" " + p, p);
        s = s.Replace("，，", "，");
        return s.Trim(' ');
    }
}
