using System.Globalization;
using System.Text;

namespace Remy.Core.Text;

/// <summary>A saved term with its "heard as" spellings (v0.2-features.md §1). Derived from settings, never stored.</summary>
public sealed record VocabularyCorrection(string Term, IReadOnlyList<string> Misspellings);

/// <summary>A spoken trigger phrase and the text that replaces it (v0.2-features.md §2).</summary>
public sealed record DictationSnippet(Guid Id, string Trigger, string Expansion)
{
    public const int MaximumTriggerLength = 60;
    public const int MaximumExpansionLength = 4000;

    /// <summary>A copy with trimmed values, or null when invalid. An empty expansion is valid ("removes the phrase").</summary>
    public DictationSnippet? Validated()
    {
        var trigger = (Trigger ?? "").Trim();
        var expansion = (Expansion ?? "").Trim();
        if (trigger.Length == 0 || DictationTextRules.Length(trigger) > MaximumTriggerLength
            || DictationTextRules.Length(expansion) > MaximumExpansionLength) return null;
        return this with { Trigger = trigger, Expansion = expansion };
    }
}

/// <summary>Immutable per-session spelling corrections and snippets (v0.2-features.md §1-§2). All matching works on text
/// elements (grapheme clusters), case-insensitively, whole words/phrases only.</summary>
public sealed class DictationTextRules
{
    public const int MaximumPhraseLength = 60;

    public static DictationTextRules Empty { get; } = new([], []);

    sealed record Rule(string[] Units, string First, string Last, string Replacement, bool IsSnippet);

    sealed class RuleSet
    {
        public required Rule[] Rules { get; init; }
        public required Dictionary<string, int[]> ByFirstUnit { get; init; }
    }

    readonly RuleSet _all;        // snippets first, then corrections (lower index = snippet)
    readonly RuleSet _snippets;   // snippets only
    readonly bool _hasCorrections;

    public IReadOnlyList<VocabularyCorrection> Corrections { get; }
    public IReadOnlyList<DictationSnippet> Snippets { get; }

    public DictationTextRules(IEnumerable<VocabularyCorrection> corrections, IEnumerable<DictationSnippet> snippets)
    {
        Corrections = corrections.Select(c => new VocabularyCorrection(c.Term, c.Misspellings.ToArray())).ToArray();
        Snippets = snippets.ToArray();

        var snippetRules = new List<Rule>();
        foreach (var s in Snippets)
            if (MakeRule(s.Trigger, s.Expansion, true) is { } r) snippetRules.Add(r);
        var correctionRules = new List<Rule>();
        foreach (var c in Corrections)
            foreach (var m in c.Misspellings)
                if (MakeRule(m, c.Term, false) is { } r) correctionRules.Add(r);

        _hasCorrections = correctionRules.Count > 0;
        _snippets = Build(snippetRules);
        _all = Build(snippetRules.Concat(correctionRules).ToList());
    }

    /// <summary>True when every correction has no spellings and there are no snippets.</summary>
    public bool IsEmpty => !_hasCorrections && Snippets.Count == 0;

    // ------------------------------------------------------------------ public passes

    /// <summary>Replaces heard-as spellings by their saved terms. Snippet triggers win overlaps and are copied through.</summary>
    public string ApplyCorrections(string text)
    {
        if (!_hasCorrections || text.Length == 0) return text;
        var chars = Elements(text);
        var matches = Select(Candidates(chars, _all));
        return Replace(chars, matches, _all.Rules, expandSnippets: false);
    }

    /// <summary>Expands snippet triggers. A whole utterance that is just a trigger becomes exactly the expansion.</summary>
    public string ExpandSnippets(string text)
    {
        if (_snippets.Rules.Length == 0 || text.Length == 0) return text;
        var chars = Elements(text);

        var from = 0;
        var to = chars.Length;
        while (from < to && IsTrimmable(chars[from])) from++;
        while (to > from && IsTrimmable(chars[to - 1])) to--;
        if (to > from)
        {
            var core = chars[from..to];
            Candidate? best = null;
            foreach (var c in Candidates(core, _snippets))
                if (c.Start == 0 && c.End == core.Length && (best is null || c.RuleIndex < best.Value.RuleIndex)) best = c;
            if (best is { } b) return _snippets.Rules[b.RuleIndex].Replacement;
        }

        return Replace(chars, Select(Candidates(chars, _snippets)), _snippets.Rules, expandSnippets: true);
    }

    // ------------------------------------------------------------------ rules

    static Rule? MakeRule(string phrase, string replacement, bool isSnippet)
    {
        phrase = (phrase ?? "").Trim();
        if (phrase.Length == 0) return null;
        var elements = Elements(phrase);
        var units = new List<string>();
        foreach (var e in elements)
        {
            if (IsWhite(e)) { if (units.Count == 0 || units[^1] != " ") units.Add(" "); }
            else units.Add(e.ToLowerInvariant());
        }
        return new Rule(units.ToArray(), elements[0], elements[^1], replacement, isSnippet);
    }

    static RuleSet Build(IList<Rule> rules)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
        {
            if (!map.TryGetValue(rules[i].Units[0], out var l)) map[rules[i].Units[0]] = l = [];
            l.Add(i);
        }
        return new RuleSet { Rules = rules.ToArray(), ByFirstUnit = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal) };
    }

    // ------------------------------------------------------------------ matching

    readonly record struct Candidate(int Start, int End, int RuleIndex, bool IsSnippet);

    static List<Candidate> Candidates(string[] chars, RuleSet set)
    {
        var result = new List<Candidate>();
        if (set.Rules.Length == 0) return result;
        var folded = new string[chars.Length];
        for (var i = 0; i < chars.Length; i++) folded[i] = IsWhite(chars[i]) ? " " : chars[i].ToLowerInvariant();

        for (var s = 0; s < chars.Length; s++)
        {
            if (!set.ByFirstUnit.TryGetValue(folded[s], out var indices)) continue;
            foreach (var ri in indices)
            {
                var rule = set.Rules[ri];
                if (!IsBoundary(s > 0 ? chars[s - 1] : null, rule.First)) continue;
                var p = s;
                var ok = true;
                foreach (var unit in rule.Units)
                {
                    if (p >= chars.Length) { ok = false; break; }
                    if (unit == " ")
                    {
                        if (!IsWhite(chars[p])) { ok = false; break; }
                        while (p < chars.Length && IsWhite(chars[p])) p++;
                    }
                    else
                    {
                        if (folded[p] != unit) { ok = false; break; }
                        p++;
                    }
                }
                if (!ok) continue;
                if (!IsBoundary(p < chars.Length ? chars[p] : null, rule.Last)) continue;
                result.Add(new Candidate(s, p, ri, rule.IsSnippet));
            }
        }
        return result;
    }

    static List<Candidate> Select(List<Candidate> candidates)
    {
        candidates.Sort((a, b) =>
        {
            if (a.IsSnippet != b.IsSnippet) return a.IsSnippet ? -1 : 1;
            var la = a.End - a.Start; var lb = b.End - b.Start;
            if (la != lb) return lb.CompareTo(la);
            if (a.Start != b.Start) return a.Start.CompareTo(b.Start);
            return a.RuleIndex.CompareTo(b.RuleIndex);
        });
        var taken = new List<Candidate>();
        if (candidates.Count == 0) return taken;
        var occupied = new bool[candidates.Max(c => c.End)];
        foreach (var c in candidates)
        {
            var clash = false;
            for (var i = c.Start; i < c.End; i++) if (occupied[i]) { clash = true; break; }
            if (clash) continue;
            for (var i = c.Start; i < c.End; i++) occupied[i] = true;
            taken.Add(c);
        }
        taken.Sort((a, b) => a.Start.CompareTo(b.Start));
        return taken;
    }

    static string Replace(string[] chars, List<Candidate> matches, Rule[] rules, bool expandSnippets)
    {
        if (matches.Count == 0) return string.Concat(chars);
        var output = new StringBuilder();
        var cursor = 0;
        foreach (var m in matches)
        {
            for (var i = cursor; i < m.Start; i++) output.Append(chars[i]);
            cursor = m.End;
            var rule = rules[m.RuleIndex];
            if (rule.IsSnippet && !expandSnippets)
            {
                for (var i = m.Start; i < m.End; i++) output.Append(chars[i]);
                continue;
            }
            output.Append(rule.Replacement);
            if (rule.Replacement.Length != 0) continue;

            var next = cursor < chars.Length ? chars[cursor] : null;
            if (output.Length > 0 && output[^1] == ' ' && (next is null || next == " " || IsPunctuation(next)))
            {
                while (output.Length > 0 && output[^1] == ' ') output.Length--;
            }
            else if (output.Length == 0 || char.IsWhiteSpace(output[^1]))
            {
                while (cursor < chars.Length && chars[cursor] == " ") cursor++;
            }
        }
        for (var i = cursor; i < chars.Length; i++) output.Append(chars[i]);
        return output.ToString();
    }

    // ------------------------------------------------------------------ character classes

    internal static string[] Elements(string text)
    {
        var list = new List<string>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list.ToArray();
    }

    /// <summary>Length in text elements (grapheme clusters), the spec's "characters".</summary>
    public static int Length(string text) => new StringInfo(text).LengthInTextElements;

    static Rune First(string element) => Rune.GetRuneAt(element, 0);

    static bool IsWhite(string element) => Rune.IsWhiteSpace(First(element));

    static bool IsPunctuation(string element) => IsPunctuationRune(First(element));

    static bool IsPunctuationRune(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    static bool IsTrimmable(string element) => IsWhite(element) || IsPunctuation(element);

    static bool IsWordRune(Rune r) => Rune.IsLetter(r) || Rune.IsNumber(r) || r.Value == '_';

    static bool IsUnsegmented(string element)
    {
        var v = First(element).Value;
        return v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2EBEF or >= 0x3040 and <= 0x30FF;
    }

    /// <summary>True when a rule edge may touch <paramref name="neighbor"/>: no neighbor, a non-word neighbor, or CJK on either side.</summary>
    static bool IsBoundary(string? neighbor, string edge)
    {
        if (neighbor is null) return true;
        if (!IsWordRune(First(neighbor))) return true;
        return IsUnsegmented(neighbor) || IsUnsegmented(edge);
    }
}
