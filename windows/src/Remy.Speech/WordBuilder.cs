using System.Text;
using Remy.Core.Contracts;

namespace Remy.Speech;

/// <summary>Builds words from SentencePiece tokens: a piece starting with the U+2581 marker (sherpa returns it as a space) begins a new word.</summary>
public static class WordBuilder
{
    private const char Marker = '▁';
    private const double LastTokenSeconds = 0.08;

    public static List<RecognizedWord> Build(IReadOnlyList<string>? tokens, IReadOnlyList<float>? timestamps,
        double offsetSeconds, double totalSeconds)
    {
        var words = new List<RecognizedWord>();
        if (tokens is null || tokens.Count == 0) return words;
        bool haveTimes = timestamps is not null && timestamps.Count == tokens.Count;
        var text = new StringBuilder();
        double wordStart = 0, lastStart = 0;

        void Flush(double end)
        {
            if (text.Length == 0) return;
            words.Add(new RecognizedWord(text.ToString(), offsetSeconds + wordStart, offsetSeconds + Math.Max(end, wordStart)));
            text.Clear();
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            double t = haveTimes ? timestamps![i] : totalSeconds * i / tokens.Count;
            if (tok.StartsWith(Marker) || tok.StartsWith(' ')) Flush(t);
            string piece = tok.TrimStart(Marker, ' ');
            lastStart = t;
            if (piece.Length == 0) continue;
            if (text.Length == 0) wordStart = t;
            text.Append(piece);
        }
        Flush(haveTimes ? lastStart + LastTokenSeconds : totalSeconds);
        return words;
    }
}
