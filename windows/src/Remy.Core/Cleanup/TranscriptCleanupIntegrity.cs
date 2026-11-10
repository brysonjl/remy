using System.Globalization;
using System.Text;

namespace Remy.Core.Cleanup;

/// <summary>Restores spoken words where cleanup inserted a known term with no similarly spelled source (v0.2-features.md §4).</summary>
public static class TranscriptCleanupIntegrity
{
    const int MaximumAlignmentCells = 4_000_000;

    readonly record struct Word(int Start, int End, string Key);

    readonly record struct Region(int OutStart, int OutEnd, int SrcStart, int SrcEnd);

    /// <param name="source">The corrected transcript sent to cleanup.</param>
    /// <param name="output">The normalized cleanup output.</param>
    /// <param name="terms">Vocabulary plus the screen names used for that cleanup.</param>
    public static (string Text, int Reverted) RevertUnsupportedTerms(string source, string output, IEnumerable<string> terms)
    {
        var termKeys = new List<string[]>();
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            var keys = Words(term).Select(w => w.Key).ToArray();
            if (keys.Length == 0) continue;
            if (seenTerms.Add(string.Join('\u001f', keys))) termKeys.Add(keys);
        }
        if (termKeys.Count == 0) return (output, 0);

        var sourceWords = Words(source);
        var outputWords = Words(output);
        var sourceKeys = sourceWords.Select(w => w.Key).ToArray();
        var outputKeys = outputWords.Select(w => w.Key).ToArray();

        var added = termKeys.Where(t => Occurrences(outputKeys, t).Count > Occurrences(sourceKeys, t).Count).ToList();
        if (added.Count == 0) return (output, 0);

        var matches = Alignment(sourceKeys, outputKeys);
        if (matches is null) return (output, 0);

        var regions = new List<Region>();
        foreach (var term in added)
        {
            var joined = string.Concat(term);
            foreach (var start in Occurrences(outputKeys, term))
            {
                var end = start + term.Length;
                var allAligned = true;
                for (var k = start; k < end; k++) if (matches[k] is null) { allAligned = false; break; }
                if (allAligned) continue;

                var before = -1;
                for (var k = start - 1; k >= 0; k--) if (matches[k] is not null) { before = k; break; }
                var after = -1;
                for (var k = end; k < outputKeys.Length; k++) if (matches[k] is not null) { after = k; break; }

                var outStart = before >= 0 ? before + 1 : 0;
                var outEnd = after >= 0 ? after : outputKeys.Length;
                var srcStart = before >= 0 ? matches[before]!.Value + 1 : 0;
                var srcEnd = after >= 0 ? matches[after]!.Value : sourceKeys.Length;

                if (regions.Any(r => r.OutStart == outStart && r.OutEnd == outEnd)) continue;
                var span = srcEnd > srcStart ? sourceKeys[srcStart..srcEnd] : [];
                if (HasSimilarSpan(joined, span, term.Length + 2)) continue;
                regions.Add(new Region(outStart, outEnd, srcStart, srcEnd));
            }
        }
        if (regions.Count == 0) return (output, 0);

        var text = new StringBuilder(output);
        var applied = 0;
        var lowestApplied = int.MaxValue;
        foreach (var region in regions.OrderByDescending(r => r.OutStart))
        {
            if (region.OutEnd > outputWords.Count || region.OutStart >= region.OutEnd) continue;
            if (region.OutEnd > lowestApplied && lowestApplied != int.MaxValue) continue;   // overlaps a region already applied
            var start = outputWords[region.OutStart].Start;
            var end = outputWords[region.OutEnd - 1].End;
            var replacement = region.SrcEnd <= region.SrcStart
                ? ""
                : source[sourceWords[region.SrcStart].Start..sourceWords[region.SrcEnd - 1].End];

            if (replacement.Length == 0)
            {
                var left = start;
                while (left > 0 && text[left - 1] == ' ') left--;
                if (left < start) start = left;
                else while (end < text.Length && text[end] == ' ') end++;
            }
            else if (char.IsUpper(text[start]) && IsSentenceStart(text, start))
            {
                replacement = UpperFirstElement(replacement);
            }

            text.Remove(start, end - start);
            text.Insert(start, replacement);
            lowestApplied = region.OutStart;
            applied++;
        }
        return applied == 0 ? (output, 0) : (text.ToString(), applied);
    }

    // ------------------------------------------------------------------ words

    static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var e = StringInfo.GetTextElementEnumerator(text);
        var start = -1;
        var end = 0;
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            var index = e.ElementIndex;
            var r = Rune.GetRuneAt(element, 0);
            if (Rune.IsLetter(r) || Rune.IsNumber(r))
            {
                if (start < 0) start = index;
                end = index + element.Length;
            }
            else if (start >= 0)
            {
                words.Add(new Word(start, end, Fold(text[start..end])));
                start = -1;
            }
        }
        if (start >= 0) words.Add(new Word(start, end, Fold(text[start..end])));
        return words;
    }

    /// <summary>Case-, accent- and width-insensitive key.</summary>
    static string Fold(string word)
    {
        var d = word.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }

    static List<int> Occurrences(string[] keys, string[] term)
    {
        var result = new List<int>();
        for (var i = 0; i + term.Length <= keys.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < term.Length; j++) if (keys[i + j] != term[j]) { ok = false; break; }
            if (ok) result.Add(i);
        }
        return result;
    }

    // ------------------------------------------------------------------ alignment (LCS of word keys)

    static int?[]? Alignment(string[] source, string[] output)
    {
        var matches = new int?[output.Length];
        var prefix = 0;
        while (prefix < source.Length && prefix < output.Length && source[prefix] == output[prefix]) { matches[prefix] = prefix; prefix++; }
        var maxSuffix = Math.Min(source.Length, output.Length) - prefix;
        var suffix = 0;
        while (suffix < maxSuffix && source[source.Length - 1 - suffix] == output[output.Length - 1 - suffix])
        {
            matches[output.Length - 1 - suffix] = source.Length - 1 - suffix;
            suffix++;
        }
        var src = source[prefix..(source.Length - suffix)];
        var outp = output[prefix..(output.Length - suffix)];
        if (src.Length == 0 || outp.Length == 0) return matches;
        if ((long)src.Length * outp.Length > MaximumAlignmentCells) return null;

        var table = new int[src.Length + 1, outp.Length + 1];
        for (var i = src.Length - 1; i >= 0; i--)
            for (var j = outp.Length - 1; j >= 0; j--)
                table[i, j] = src[i] == outp[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        int a = 0, b = 0;
        while (a < src.Length && b < outp.Length)
        {
            if (src[a] == outp[b]) { matches[prefix + b] = prefix + a; a++; b++; }
            else if (table[a + 1, b] >= table[a, b + 1]) a++;
            else b++;
        }
        return matches;
    }

    // ------------------------------------------------------------------ similarity

    static bool HasSimilarSpan(string term, string[] source, int maxWords)
    {
        var termElements = Elements(term);
        for (var start = 0; start < source.Length; start++)
        {
            var joined = "";
            for (var end = start; end < Math.Min(source.Length, start + maxWords); end++)
            {
                joined += source[end];
                var joinedElements = Elements(joined);
                var longest = Math.Max(joinedElements.Length, termElements.Length);
                if (EditDistance(joinedElements, termElements) * 3 <= longest) return true;
                if (joinedElements.Length > termElements.Length * 2) break;
            }
        }
        return false;
    }

    static string[] Elements(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list.ToArray();
    }

    static int EditDistance(string[] a, string[] b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ------------------------------------------------------------------ text helpers

    static bool IsSentenceStart(StringBuilder text, int index)
    {
        var i = index;
        while (i > 0 && text[i - 1] is ' ' or '\t') i--;
        if (i == 0) return true;
        return text[i - 1] is '.' or '!' or '?' or '\n';
    }

    static string UpperFirstElement(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        if (!e.MoveNext()) return s;
        var first = (string)e.Current;
        return first.ToUpperInvariant() + s[first.Length..];
    }
}
