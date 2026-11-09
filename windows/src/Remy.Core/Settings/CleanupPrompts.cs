namespace Remy.Core.Settings;

/// <summary>Verbatim recommended cleanup prompt (cleanup.md 2.3) and writing-style instructions (settings-models.md 1.3).</summary>
public static class CleanupPrompts
{
    /// <summary>cleanup.md 2.3, byte-exact, LF line endings, no trailing newline.</summary>
    public static readonly string RecommendedTranscriptCleanupPrompt = string.Join("\n", new[]
    {
        "Never use em dashes. Replace them with commas, periods, colons, semicolons, or parentheses when needed.",
        "",
        "If the speaker corrects themselves or changes their mind, keep the latest intended meaning. Replace only the part that is clearly being corrected, and keep the rest.",
        "",
        "Auto structure into paragraphs and list items with proper punctuation when appropriate.",
        "",
        "Format spoken numbers into numerals when that improves clarity while preserving intended units and symbols. Example: \"thirteen point five percent\" -> \"13.5%\".",
        "",
        "Remove unnecessary and erroneous word repetitions, but keep deliberate repetition when it is clearly intentional.",
        "",
        "Stay faithful to the original transcript's tone.",
        "",
        "Treat custom vocabulary as a strong hint, not a hard rule. If a suggested term does not semantically fit the sentence, prefer the wording that best matches the surrounding context."
    });

    public static string WritingStyleInstruction(DictationWritingStyle style) => style switch
    {
        DictationWritingStyle.Formal => "Use polished, complete sentences with conventional capitalization and punctuation.",
        DictationWritingStyle.Neutral => "Use a clear, balanced tone without making the text notably formal, casual, or enthusiastic. Preserve the speaker's meaning.",
        DictationWritingStyle.Casual => "Use natural conversational wording. Keep punctuation light and do not add a final period to a short message.",
        DictationWritingStyle.VeryCasual => "Use concise chat-style wording. Keep punctuation minimal and do not add a final period to a short message.",
        DictationWritingStyle.Excited => "Use an upbeat, energetic tone while preserving the speaker's meaning. Do not invent claims or emojis.",
        _ => "Preserve the transcript's original tone, register, emotional intensity, and level of formality. Only clean grammar, punctuation, structure, and obvious speech artifacts without restyling the wording.",
    };
}
