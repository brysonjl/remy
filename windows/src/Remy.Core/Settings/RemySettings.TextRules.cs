using System.Text.Json.Nodes;
using Remy.Core.Text;

namespace Remy.Core.Settings;

/// <summary>Spelling corrections ("heard as") and snippets (v0.2-features.md §1-§3). Keys: vocabularyMisspellings, dictationSnippets.</summary>
public sealed partial class RemySettings
{
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoMisspellings =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _vocabularyMisspellings = NoMisspellings;

    /// <summary>Heard-as spellings keyed by the exact <see cref="CustomVocabulary"/> term. Keys not in the vocabulary are inert.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> VocabularyMisspellings
    {
        get => _vocabularyMisspellings;
        set
        {
            var copy = (value ?? NoMisspellings).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToArray(), StringComparer.Ordinal);
            if (SameMisspellings(_vocabularyMisspellings, copy)) return;
            _vocabularyMisspellings = copy;
            Raise(nameof(VocabularyMisspellings));
            Raise(nameof(VocabularyCorrections));
        }
    }

    private IReadOnlyList<DictationSnippet> _dictationSnippets = Array.Empty<DictationSnippet>();

    public IReadOnlyList<DictationSnippet> DictationSnippets
    {
        get => _dictationSnippets;
        set
        {
            var v = (value ?? []).ToArray();
            if (_dictationSnippets.SequenceEqual(v)) return;
            _dictationSnippets = v;
            Raise(nameof(DictationSnippets));
        }
    }

    /// <summary>Derived: for each vocabulary term in order, its non-empty heard-as spellings.</summary>
    public IReadOnlyList<VocabularyCorrection> VocabularyCorrections
    {
        get
        {
            var list = new List<VocabularyCorrection>();
            foreach (var term in CustomVocabulary)
                if (_vocabularyMisspellings.TryGetValue(term, out var m) && m.Count > 0) list.Add(new VocabularyCorrection(term, m.ToArray()));
            return list;
        }
    }

    /// <summary>Sets (or with an empty list, clears) the heard-as spellings of a term, adding the term to the vocabulary.
    /// Returns false when the term is empty or longer than 60 characters.</summary>
    public bool SetVocabularyCorrection(string term, IEnumerable<string> misspellings)
    {
        term = (term ?? "").Trim();
        if (term.Length == 0 || DictationTextRules.Length(term) > DictationTextRules.MaximumPhraseLength) return false;

        var unique = new List<string>();
        foreach (var raw in misspellings ?? [])
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0 || DictationTextRules.Length(s) > DictationTextRules.MaximumPhraseLength) continue;
            if (string.Equals(s, term, StringComparison.Ordinal)) continue;
            if (unique.Any(u => string.Equals(u, s, StringComparison.OrdinalIgnoreCase))) continue;
            unique.Add(s);
        }

        var stored = CustomVocabulary.FirstOrDefault(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase)) ?? term;
        if (!CustomVocabulary.Contains(stored, StringComparer.Ordinal)) CustomVocabulary = CustomVocabulary.Append(stored).ToList();

        var map = _vocabularyMisspellings.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (unique.Count == 0) map.Remove(stored); else map[stored] = unique;
        VocabularyMisspellings = map;
        return true;
    }

    /// <summary>Merges a parsed CSV into the vocabulary and spellings. Returns the imported / skipped counts.</summary>
    public (int Imported, int Skipped) ImportVocabulary(VocabularyCsvImport.ParseResult parsed)
    {
        var merged = VocabularyCsvImport.Merge(parsed, CustomVocabulary, _vocabularyMisspellings);
        CustomVocabulary = merged.Terms;
        VocabularyMisspellings = merged.Misspellings;
        return (merged.Imported, merged.Skipped);
    }

    /// <summary>Drops spellings of terms that left the vocabulary (exact, ordinal match).</summary>
    partial void OnCustomVocabularyChanged()
    {
        var keep = new HashSet<string>(_customVocabulary, StringComparer.Ordinal);
        if (_vocabularyMisspellings.Keys.All(keep.Contains)) { Raise(nameof(VocabularyCorrections)); return; }
        _vocabularyMisspellings = _vocabularyMisspellings.Where(kv => keep.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Raise(nameof(VocabularyMisspellings));
        Raise(nameof(VocabularyCorrections));
    }

    static bool SameMisspellings(IReadOnlyDictionary<string, IReadOnlyList<string>> a, IReadOnlyDictionary<string, IReadOnlyList<string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var o) || !v.SequenceEqual(o)) return false;
        return true;
    }

    partial void AddTextRuleEntries(EntryBuilder b)
    {
        b.Raw("vocabularyMisspellings", ReadMisspellings, WriteMisspellings);
        b.Raw("dictationSnippets", ReadSnippets, WriteSnippets);
    }

    private void ReadMisspellings(JsonNode? n)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (n is JsonObject o)
        {
            foreach (var (term, value) in o)
            {
                if (value is not JsonArray a) continue;
                var list = new List<string>();
                foreach (var item in a)
                    if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)
                        && DictationTextRules.Length(s) <= DictationTextRules.MaximumPhraseLength) list.Add(s);
                if (list.Count > 0) map[term] = list;
            }
        }
        VocabularyMisspellings = map;
    }

    private JsonNode? WriteMisspellings()
    {
        var o = new JsonObject();
        foreach (var (term, list) in _vocabularyMisspellings)
        {
            var a = new JsonArray();
            foreach (var s in list) a.Add(s);
            o[term] = a;
        }
        return o;
    }

    private void ReadSnippets(JsonNode? n)
    {
        var list = new List<DictationSnippet>();
        if (n is JsonArray a)
        {
            foreach (var item in a)
            {
                if (item is not JsonObject o) continue;
                if (!(o["id"] is JsonValue iv && iv.TryGetValue<string>(out var ids) && Guid.TryParse(ids, out var id))) continue;
                if (!(o["trigger"] is JsonValue tv && tv.TryGetValue<string>(out var trigger))) continue;
                if (!(o["expansion"] is JsonValue ev && ev.TryGetValue<string>(out var expansion))) continue;
                if (new DictationSnippet(id, trigger, expansion).Validated() is { } valid) list.Add(valid);
            }
        }
        DictationSnippets = list;
    }

    private JsonNode? WriteSnippets()
    {
        var a = new JsonArray();
        foreach (var s in _dictationSnippets)
            a.Add(new JsonObject { ["id"] = s.Id.ToString("D").ToUpperInvariant(), ["trigger"] = s.Trigger, ["expansion"] = s.Expansion });
        return a;
    }
}
