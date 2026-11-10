using System.Text;

namespace Remy.Core.Text;

public enum VocabularyCsvFailure { TooLarge, Unreadable }

public sealed class VocabularyCsvException(VocabularyCsvFailure failure) : Exception(failure.ToString())
{
    public VocabularyCsvFailure Failure { get; } = failure;
}

/// <summary>Vocabulary CSV import (v0.2-features.md §3): one column = a term, two columns = misspelling,correction.</summary>
public static class VocabularyCsvImport
{
    public const int MaximumBytes = 3 * 1024 * 1024;
    public const int MaximumEntries = 1000;

    public const string TooLargeMessage = "Choose a CSV file up to 3 MB.";
    public const string UnreadableMessage = "This file could not be read. Save it as a UTF-8 CSV and try again.";
    public static string SuccessMessage(int imported, int skipped) => $"Imported {imported}, skipped {skipped}.";
    public static string FailureMessage(VocabularyCsvFailure f) => f == VocabularyCsvFailure.TooLarge ? TooLargeMessage : UnreadableMessage;

    public sealed record Entry(string Term, string? Misspelling);

    public sealed record ParseResult(IReadOnlyList<Entry> Entries, int Skipped);

    public sealed record MergeResult(
        IReadOnlyList<string> Terms, IReadOnlyDictionary<string, IReadOnlyList<string>> Misspellings, int Imported, int Skipped);

    static readonly string[][] Headers =
    [
        ["word"], ["words"], ["term"], ["terms"], ["vocabulary"],
        ["misspelling", "correction"], ["misspelling", "correct spelling"],
    ];

    /// <summary>Checks the file length before reading it.</summary>
    public static ParseResult Read(string path)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) throw new VocabularyCsvException(VocabularyCsvFailure.TooLarge);
            bytes = File.ReadAllBytes(path);
        }
        catch (VocabularyCsvException) { throw; }
        catch (Exception) { throw new VocabularyCsvException(VocabularyCsvFailure.Unreadable); }
        return Parse(bytes);
    }

    public static ParseResult Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new VocabularyCsvException(VocabularyCsvFailure.TooLarge);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (Exception) { throw new VocabularyCsvException(VocabularyCsvFailure.Unreadable); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var entries = new List<Entry>();
        var skipped = 0;
        var seenFirst = false;
        foreach (var raw in Rows(text))
        {
            var fields = raw.Select(f => f.Trim()).ToList();
            while (fields.Count > 0 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
            if (fields.Count == 0) continue;

            var first = !seenFirst;
            seenFirst = true;
            if (first && IsHeader(fields)) continue;

            if (entries.Count >= MaximumEntries || fields.Count > 2
                || fields.Any(f => f.Length == 0 || DictationTextRules.Length(f) > DictationTextRules.MaximumPhraseLength))
            {
                skipped++;
                continue;
            }
            entries.Add(fields.Count == 1 ? new Entry(fields[0], null) : new Entry(fields[1], fields[0]));
        }
        return new ParseResult(entries, skipped);
    }

    static bool IsHeader(List<string> fields) =>
        Headers.Any(h => h.Length == fields.Count && h.Zip(fields).All(p => string.Equals(p.First, p.Second.ToLowerInvariant(), StringComparison.Ordinal)));

    static bool IsNewline(char c) => c is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029' or '\v' or '\f';

    /// <summary>RFC 4180 row splitter ("\r\n" counts as one newline).</summary>
    public static List<string[]> Rows(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"' && field.Length == 0 && !wasQuoted) { inQuotes = true; wasQuoted = true; }
            else if (c == ',')
            {
                row.Add(field.ToString()); field.Clear(); wasQuoted = false;
            }
            else if (IsNewline(c))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); wasQuoted = false;
                rows.Add(row.ToArray()); row.Clear();
            }
            else field.Append(c);
        }

        if (field.Length > 0 || wasQuoted || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    public static MergeResult Merge(ParseResult parsed, IReadOnlyList<string> terms, IReadOnlyDictionary<string, IReadOnlyList<string>> misspellings)
    {
        var termList = terms.ToList();
        var map = misspellings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        int imported = 0, skipped = parsed.Skipped;

        foreach (var entry in parsed.Entries)
        {
            var existing = termList.FirstOrDefault(t => string.Equals(t, entry.Term, StringComparison.OrdinalIgnoreCase));
            var term = existing ?? entry.Term;
            var added = existing is null;
            if (added) termList.Add(term);

            if (entry.Misspelling is { } m && !string.Equals(m, term, StringComparison.Ordinal))
            {
                if (!map.TryGetValue(term, out var list)) map[term] = list = [];
                if (!list.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(m);
                    added = true;
                }
            }
            if (added) imported++; else skipped++;
        }

        var result = map.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
        return new MergeResult(termList, result, imported, skipped);
    }
}
