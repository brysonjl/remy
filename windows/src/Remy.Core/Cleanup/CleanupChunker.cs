using System.Globalization;
using System.Text;

namespace Remy.Core.Cleanup;

/// <summary>One piece of the transcript: whitespace prefix/suffix are re-attached, only Text is sent.</summary>
public sealed record CleanupChunk(string Prefix, string Text, string Suffix)
{
    public static CleanupChunk From(string raw)
    {
        var t = raw.TrimStart();
        var prefix = raw[..(raw.Length - t.Length)];
        var core = t.TrimEnd();
        var suffix = t[core.Length..];
        return new CleanupChunk(prefix, core, suffix);
    }

    public bool PassesThrough => Text.Length == 0;
}

/// <summary>Bounded request budget math and grapheme-safe chunking (cleanup.md §1.1). Units are UTF-8 bytes.</summary>
public static class CleanupChunker
{
    public const int MaxBudget = 8192;
    public const int OverheadBytes = 512;
    public const int FirstProbe = 512;
    public const int WholeTextCharLimit = 4096;

    public static int Utf8(string s) => Encoding.UTF8.GetByteCount(s);

    public static int Budget(int? contextLength) => Math.Min(MaxBudget, contextLength is > 0 ? contextLength.Value : MaxBudget);

    public static int OutputReserve(int utf8Bytes) => Math.Max(128, (int)Math.Ceiling(utf8Bytes * 1.3) + 64);

    /// <summary>assessSingleRequest: true when one request carrying <paramref name="text"/> fits.</summary>
    public static bool FitsSingleRequest(string text, string instructions, string userMessage, int budget, int? maxCompletionTokens)
    {
        var t = Utf8(text);
        var ins = Utf8(instructions);
        if (t > budget || ins > budget) return false;
        var reserve = OutputReserve(t);
        if (ins + OverheadBytes + Utf8(userMessage) + reserve > budget) return false;
        if (maxCompletionTokens is { } m && reserve > m) return false;
        return true;
    }

    private static bool IsWhitespace(string g) => g.Length > 0 && g.All(char.IsWhiteSpace);
    private static bool IsNewline(string g) => g.Contains('\n') || g.Contains('\r');
    private static bool IsCjkStop(string g) => g is "。" or "！" or "？";

    /// <summary>
    /// Splits <paramref name="text"/> into chunks for which <paramref name="fits"/> (given the exact text that would be sent)
    /// returns true. Throws CleanupFailureException(inputTooLong) when an unbreakable segment cannot fit.
    /// </summary>
    public static IReadOnlyList<CleanupChunk> Chunks(string text, Func<string, bool> fits)
    {
        var g = GraphemeList(text);
        if (g.Count <= WholeTextCharLimit && fits(text.Trim()))
            return [CleanupChunk.From(text)];

        var chunks = new List<CleanupChunk>();
        var start = 0;
        var probeHint = FirstProbe;
        string Slice(int from, int len) => string.Concat(g.Skip(from).Take(len));
        bool Fits(int from, int len) => fits(Slice(from, len).Trim());

        while (start < g.Count)
        {
            var remaining = g.Count - start;
            int low;
            if (Fits(start, remaining)) low = remaining;
            else
            {
                var lo = 0;
                var p = Math.Min(probeHint, remaining);
                var hi = remaining;
                while (true)
                {
                    if (Fits(start, p))
                    {
                        lo = p;
                        if (p >= remaining) break;
                        p = Math.Min(p * 2, remaining);
                    }
                    else { hi = p; break; }
                }
                while (hi - lo > 1)
                {
                    var mid = lo + (hi - lo) / 2;
                    if (Fits(start, mid)) lo = mid; else hi = mid;
                }
                low = lo;
            }

            if (low == 0) throw new CleanupFailureException(RemyCleanupFailure.InputTooLong);
            probeHint = Math.Max(1, low);

            int end;
            if (low == remaining) end = remaining;
            else
            {
                var boundaries = new List<int>();
                var sentence = new List<int>();
                for (var b = 1; b <= low; b++)
                {
                    var prev = g[start + b - 1];
                    if (!(IsWhitespace(prev) || IsCjkStop(prev))) continue;
                    boundaries.Add(b);
                    var twoBack = b >= 2 ? g[start + b - 2] : "";
                    if (IsNewline(prev) || IsCjkStop(prev) || twoBack is "." or "!" or "?") sentence.Add(b);
                }
                var half = low / 2;
                var chosen = sentence.LastOrDefault(b => b >= half);
                end = chosen != 0 ? chosen : (boundaries.Count > 0 ? boundaries[^1] : 0);
                // Re-check, stepping back through earlier boundaries.
                var idx = boundaries.IndexOf(end);
                while (end > 0 && !Fits(start, end))
                {
                    idx--;
                    end = idx >= 0 ? boundaries[idx] : 0;
                }
                if (end == 0) throw new CleanupFailureException(RemyCleanupFailure.InputTooLong);
            }

            chunks.Add(CleanupChunk.From(Slice(start, end)));
            start += end;
        }
        return chunks;
    }

    private static List<string> GraphemeList(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }
}
