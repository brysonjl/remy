using Remy.Core.Contracts;

namespace Remy.Core.Speech;

/// <summary>
/// Stable displayed preview (pipeline.md 4.2 step 5). Words are stored in absolute recording time.
/// A new window decode replaces everything at or after its first word; earlier words stay frozen.
/// </summary>
public sealed class PreviewTimeline
{
    private readonly List<RecognizedWord> _words = new();
    private string _fallback = "";

    public string Text => _words.Count > 0 ? string.Join(' ', _words.Select(w => w.Text)) : _fallback;

    public bool HasText => Text.Length > 0;

    /// <summary>Apply a window result. <paramref name="windowStartSeconds"/> is the window's start within the recording.</summary>
    public void Apply(double windowStartSeconds, RecognitionResult result)
    {
        string text = result.Text.Trim();
        if (result.Words.Count == 0)
        {
            // No timings: raw text is the fallback. Empty results never clear an existing preview.
            if (text.Length > 0) _fallback = text;
            return;
        }
        var shifted = result.Words
            .Select(w => new RecognizedWord(w.Text, w.StartSeconds + windowStartSeconds, w.EndSeconds + windowStartSeconds))
            .ToList();
        double firstStart = shifted[0].StartSeconds;
        _words.RemoveAll(w => w.EndSeconds > firstStart);
        _words.AddRange(shifted);
    }

    public void Reset() { _words.Clear(); _fallback = ""; }
}
