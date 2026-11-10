using System.Globalization;
using System.Text;

namespace Remy.Core.Context;

/// <summary>Extracts likely names and identifiers from visible window text (v0.2-features.md §7.4). Pure; nothing is stored.</summary>
public static class ScreenNameTerms
{
    public const int MaximumTerms = 25;
    const int MinimumLength = 3, MaximumLength = 60, MaximumNameWords = 4;

    static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "al", "bin", "da", "das", "de", "del", "der", "di", "do", "dos", "du", "la", "le", "van", "von",
    };

    // Same words as the focused-field hint stop list in DictationContextSnapshotBuilder (that list is private to its owner).
    const string HintStopWords =
        "about after again also because before being could email from have into just message other should that their there these they this through very want were what when where which while with would your";

    const string ExtraStopWords =
        "monday tuesday wednesday thursday friday saturday sunday mon tue tues wed thu thur thurs fri sat sun " +
        "january february march april may june july august september october november december " +
        "jan feb mar apr jun jul aug sep sept oct nov dec today yesterday tomorrow now week month year " +
        "inbox sent drafts draft archive archived junk trash spam outbox reply replies forward send compose new " +
        "subject re fwd fw to cc bcc from attachment attachments unread read mark flag flagged starred important " +
        "snoozed label labels folder folders mailbox mailboxes favorites notifications mentions activity threads " +
        "thread channels channel direct messages chat chats members people contacts calendar notes online offline " +
        "away active home menu search settings preferences file files edit view window help open close save cancel " +
        "delete done ok okay yes no back next previous more less show hide all none add remove copy paste undo redo " +
        "format insert tools account accounts profile sign log untitled share invite join leave the a an and or but " +
        "if so then it its i we you he she my our his her hi hello hey dear thanks thank regards best cheers " +
        "sincerely please sorry sure great good who why how is are was be will can do does did has had not let lets " +
        "string int bool double float array error void true false self optional nil null none";

    static readonly HashSet<string> Stoplist = new(
        (HintStopWords + " " + ExtraStopWords).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    enum Kind { Name, Identifier, Word }

    sealed class Candidate(string term, Kind kind, int order, bool midSentence, bool displayName)
    {
        public string Term { get; } = term;
        public Kind Kind { get; } = kind;
        public int Order { get; } = order;
        public int Count { get; set; } = 1;
        public bool MidSentence { get; set; } = midSentence;
        public bool DisplayName { get; set; } = displayName;
    }

    static readonly char[] TermTrim = [' ', '\t'];
    static readonly HashSet<char> Separators = new(['(', ')', '[', ']', '{', '}', '<', '>', '"', '“', '”', '=', '|', '•', '·']);
    static readonly HashSet<char> WordTrim = new([' ', '.', ',', ':', ';', '!', '?', '…', '\'', '’', '"', '“', '”', '(', ')', '[', ']', '{', '}', '<', '>', '*', '#', '-', '–', '—', '/', '\\']);

    public static IReadOnlyList<string> Extract(IEnumerable<string> texts, int limit = MaximumTerms)
    {
        var store = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var next = 0;

        void Add(string raw, Kind kind, bool mid, bool displayName = false)
        {
            var term = raw.Trim(TermTrim);
            var length = new StringInfo(term).LengthInTextElements;
            if (length < MinimumLength || length > MaximumLength || Stoplist.Contains(term.ToLowerInvariant())) return;
            var key = term.ToLowerInvariant();
            if (store.TryGetValue(key, out var c))
            {
                c.Count++;
                c.MidSentence |= mid;
                c.DisplayName |= displayName;
            }
            else store[key] = new Candidate(term, kind, next++, mid, displayName);
        }

        foreach (var text in texts)
            foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                Scan(line, Add);

        var ranked = store.Values
            .Where(c => c.Kind != Kind.Word || c.MidSentence || c.DisplayName || c.Count > 1 || HasDistinctiveForm(c.Term))
            .Select(c => (c, score: Score(c)))
            .OrderByDescending(x => x.score).ThenBy(x => x.c.Order)
            .Take(limit)
            .Select(x => x.c.Term)
            .ToList();
        return ranked;
    }

    /// <summary>Focused-field hints first, then the secondary terms, case-insensitively de-duplicated.</summary>
    public static IReadOnlyList<string> Merged(IEnumerable<string> primary, IEnumerable<string> secondary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var t in primary.Concat(secondary)) if (seen.Add(t)) result.Add(t);
        return result;
    }

    static double Score(Candidate c)
    {
        var s = c.Kind switch { Kind.Name => 3.0, Kind.Identifier => 2.5, _ => c.MidSentence ? 1.0 : 0.5 };
        if (c.DisplayName) s += 1.5;
        if (c.Term.EnumerateRunes().Any(r => r.Value > 0x7F)) s += 1;
        if (c.Kind == Kind.Word && IsIdentifier(c.Term)) s += 1.5;
        if (c.Term.IndexOfAny(['-', '\'', '’']) >= 0) s += 0.5;
        s += 0.5 * Math.Min(c.Count - 1, 3);
        return s;
    }

    // ------------------------------------------------------------------ scan

    static void Scan(string line, Action<string, Kind, bool, bool> add)
    {
        var run = new List<string>();
        var runIsSentenceInitial = false;
        var atSentenceStart = true;

        void Flush(bool displayName = false)
        {
            while (run.Count > 0 && Particles.Contains(run[^1])) run.RemoveAt(run.Count - 1);
            if (run.Count > MaximumNameWords)
            {
                foreach (var w in run) if (HasDistinctiveForm(w)) add(w, Kind.Word, true, false);
            }
            else if (run.Count >= 2) add(string.Join(' ', run), Kind.Name, true, displayName);
            else if (run.Count == 1) add(run[0], Kind.Word, !runIsSentenceInitial, displayName);
            run.Clear();
        }

        foreach (var token in Tokens(line))
        {
            if (token.Contains('@')) { Flush(true); atSentenceStart = false; continue; }
            var lower = token.ToLowerInvariant();
            if (token.Contains("://", StringComparison.Ordinal) || lower.StartsWith("www.", StringComparison.Ordinal))
            {
                Flush(); atSentenceStart = false; continue;
            }

            var last = token[^1];
            var endsSentence = last is '.' or '!' or '?' or '…';
            var endsClause = endsSentence || last is ',' or ';' or ':' or ')';
            var word = token.Trim(WordTrim.ToArray());
            if (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal)) word = word[..^2];

            Word(word, endsClause);
            atSentenceStart = endsSentence;
        }
        Flush();

        void Word(string word, bool endsClause)
        {
            if (word.Length == 0) { Flush(); return; }
            if (word.Contains('.') && word.Any(char.IsLower))
            {
                Flush();
                foreach (var part in word.Split('.')) if (IsIdentifier(part)) add(part, Kind.Identifier, true, false);
                return;
            }
            if (IsIdentifier(word) && (char.IsLower(word[0]) || word[0] == '_' || word.Contains('_')))
            {
                Flush();
                add(word, Kind.Identifier, true, false);
                return;
            }
            if (IsNameWord(word))
            {
                if (Stoplist.Contains(word.ToLowerInvariant())) { Flush(); return; }
                if (run.Count == 0) runIsSentenceInitial = atSentenceStart;
                run.Add(word);
                if (endsClause) Flush();
                return;
            }
            if (run.Count > 0 && Particles.Contains(word) && !endsClause) { run.Add(word); return; }
            Flush();
        }
    }

    static IEnumerable<string> Tokens(string line)
    {
        var sb = new StringBuilder();
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch) || Separators.Contains(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    static bool IsNameWord(string w)
    {
        if (w.Length == 0 || !char.IsUpper(w[0])) return false;
        var hasLower = false;
        foreach (var r in w.EnumerateRunes())
        {
            if (Rune.IsLower(r)) hasLower = true;
            if (!(Rune.IsLetter(r) || r.Value is '-' or '\'' or '’')) return false;
        }
        return hasLower;
    }

    /// <summary>camelCase, PascalCase with a second hump, or snake_case.</summary>
    internal static bool IsIdentifier(string w)
    {
        if (w.Length < 3) return false;
        var hasLower = false; var innerHump = false; var innerUnderscore = false;
        char prev = '\0';
        for (var i = 0; i < w.Length; i++)
        {
            var ch = w[i];
            if (ch == '_') { if (i > 0 && i < w.Length - 1) innerUnderscore = true; }
            else if (char.IsLower(ch)) hasLower = true;
            else if (char.IsUpper(ch)) { if (char.IsLower(prev) || char.IsDigit(prev)) innerHump = true; }
            else if (!char.IsDigit(ch)) return false;
            prev = ch;
        }
        return hasLower && (innerHump || innerUnderscore);
    }

    internal static bool HasDistinctiveForm(string t) => t.EnumerateRunes().Any(r => r.Value > 0x7F) || IsIdentifier(t);
}
