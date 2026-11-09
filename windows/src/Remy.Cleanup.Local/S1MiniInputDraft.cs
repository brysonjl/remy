namespace Remy.Cleanup.Local;

/// <summary>Prompt-lookup drafting: proposes the tokens that followed the last emitted n-gram in the transcript.
/// Output-identical to plain greedy because every proposal is verified by greedy sampling.</summary>
public sealed class S1MiniInputDraft
{
    public const int MaxProposals = 16;
    public const int MaxSuffix = 4;
    private readonly int[] _source;
    private int _cursor;

    public S1MiniInputDraft(IReadOnlyList<int> sourceTokens) => _source = sourceTokens.ToArray();

    /// <summary>Sentence-case the draft source (first letter after start/newline/". ! ?"+space uppercased).</summary>
    public static string SentenceCase(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool up = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (up && char.IsLetter(c)) { sb.Append(char.ToUpperInvariant(c)); up = false; continue; }
            sb.Append(c);
            if (c == '\n') up = true;
            else if (char.IsWhiteSpace(c)) { if (i > 0 && ".!?".Contains(text[i - 1])) up = true; }
            else up = false;
        }
        return sb.ToString();
    }

    public IReadOnlyList<int> Propose(IReadOnlyList<int> emitted)
    {
        if (_source.Length == 0 || emitted.Count == 0) return Array.Empty<int>();
        for (int len = Math.Min(MaxSuffix, emitted.Count); len >= 1; len--)
        {
            int at = Find(emitted, len, _cursor);
            if (at < 0) at = Find(emitted, len, 0);
            if (at < 0) continue;
            int start = at + len;
            int count = Math.Min(len == 1 ? 4 : MaxProposals, _source.Length - start);
            if (count <= 0) continue;
            _cursor = start;
            var r = new int[count];
            Array.Copy(_source, start, r, 0, count);
            return r;
        }
        return Array.Empty<int>();
    }

    private int Find(IReadOnlyList<int> emitted, int len, int from)
    {
        int e0 = emitted.Count - len;
        for (int i = from; i + len <= _source.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < len; k++)
                if (_source[i + k] != emitted[e0 + k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
