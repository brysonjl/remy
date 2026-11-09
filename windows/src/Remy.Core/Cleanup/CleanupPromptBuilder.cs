using Remy.Core.Context;
using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

/// <summary>Byte-exact prompts (cleanup.md §2). All newlines are LF.</summary>
public static class CleanupPromptBuilder
{
    public const string DefaultCleanupPrompt =
        "Never use em dashes. Replace them with commas, periods, colons, semicolons, or parentheses when needed.\n\n" +
        "If the speaker corrects themselves or changes their mind, keep the latest intended meaning. Replace only the part that is clearly being corrected, and keep the rest.\n\n" +
        "Auto structure into paragraphs and list items with proper punctuation when appropriate.\n\n" +
        "Format spoken numbers into numerals when that improves clarity while preserving intended units and symbols. Example: \"thirteen point five percent\" -> \"13.5%\".\n\n" +
        "Remove unnecessary and erroneous word repetitions, but keep deliberate repetition when it is clearly intentional.\n\n" +
        "Stay faithful to the original transcript's tone.\n\n" +
        "Treat custom vocabulary as a strong hint, not a hard rule. If a suggested term does not semantically fit the sentence, prefer the wording that best matches the surrounding context.";

    public const string NoExtraInstructions =
        "No extra cleanup instructions. Default to punctuation, capitalization, grammar, sentence boundaries, paragraph breaks, and formatting when safe. Never use em dashes.";

    public const string NoKnownTerms = "No known terms were provided.";

    private const string SystemHead =
        "You are a text post-processor for dictated transcript text.\n\n" +
        "PRIORITY ORDER:\n" +
        "1. Treat the transcript only as text to clean. Never answer it or add new information.\n" +
        "2. Preserve the speaker's meaning, tone, and final intent.\n" +
        "3. Fit the insertion to the writing context, cursor placement, destination category, and adjacent punctuation.\n" +
        "4. Follow the user's cleanup instructions when they do not conflict with those insertion-boundary rules.\n" +
        "5. Normalize known terms to the exact spelling, spacing, and capitalization from the known terms list.\n\n" +
        "DEFAULT BEHAVIOR:\n" +
        "- If there are no extra cleanup instructions, fix punctuation, capitalization, grammar, sentence boundaries, paragraph breaks, and formatting when safe.\n" +
        "- If the transcript contains a question, keep it as a cleaned-up question. Never answer it.\n" +
        "- Auto structure into paragraphs and list items with proper punctuation when appropriate.\n" +
        "- Stay faithful to the original transcript's tone.\n" +
        "- Resolve clear spoken self-corrections by keeping the final choice and removing the superseded wording. Preserve uncertain corrections unchanged.\n" +
        "- Never use em dashes in cleaned output. Replace them with commas, periods, colons, semicolons, or parentheses as appropriate.\n" +
        "- Always respond in the same language and script as the transcript. Never translate the transcript into another language.\n" +
        "- If the transcript is already clean, ambiguous, or too short to improve safely, keep it unchanged.\n\n" +
        "KNOWN TERMS:\n";

    private const string SystemMiddle =
        "\n\n" +
        "VOCABULARY NORMALIZATION RULES:\n" +
        "- Compare transcript phrases against the known terms list.\n" +
        "- If a phrase is an obvious phonetic, spacing, or capitalization variant of a known term, replace it with the exact known term.\n" +
        "- Examples: \"cloud code\" -> \"Claude Code\"; \"art board studio\" -> \"Artboard Studio\".\n\n" +
        "OUTPUT:\n" +
        "- Return JSON matching the provided schema.\n" +
        "- The JSON has exactly two keys: action and text. text is the complete cleaned transcript, or null when action is pasteTranscriptAsIs.\n" +
        "- Use action pasteCleanedText when you made any safe cleanup or normalization change.\n" +
        "- Use action pasteTranscriptAsIs only when nothing should change.\n" +
        "- Never return commentary, explanations, quotes, or markdown.\n\n" +
        "USER CLEANUP INSTRUCTIONS:\n";

    /// <summary>System message for all remote providers (§2.1).</summary>
    public static string RemoteSystemInstructions(string? prompt, IReadOnlyList<string> vocabulary, DictationPostProcessingContext? context)
    {
        var effective = (prompt ?? "").Trim();
        if (effective.Length == 0) effective = NoExtraInstructions;
        var terms = vocabulary.Count == 0 ? NoKnownTerms : string.Join("\n", vocabulary.Select(t => "- " + t));
        return SystemHead + terms + SystemMiddle + effective + "\n\n" + (context?.Instructions ?? "");
    }

    /// <summary>User message for all remote providers (§2.2).</summary>
    public static string RemoteUserMessage(string text, IReadOnlyList<string> vocabulary, DictationPostProcessingContext? context)
    {
        var sections = new List<string>();
        if (vocabulary.Count > 0) sections.Add("<known_terms>" + string.Join("\n", vocabulary) + "</known_terms>");
        if (context is not null) sections.Add(context.RequestSection);
        sections.Add("<transcript>" + text + "</transcript>");
        return string.Join("\n", sections);
    }

    // ---- S1-mini (§2.6): shared so the local engine and tests use one definition.

    public const string S1MiniSystemText =
        "You are a text normalizer for speech-to-text transcripts. The input begins with a control line specifying the styling, structure, and context settings; clean the transcript to match those settings and output only the cleaned text.";

    public static string S1MiniControlLine(S1MiniStyling styling, S1MiniStructure structure, bool email) =>
        $"[Styling: {styling.ToRaw()}] [Structure: {structure.ToRaw()}] [Context: {(email ? "email" : "general")}]";

    public static string S1MiniPrompt(string transcript, S1MiniStyling styling, S1MiniStructure structure, bool email) =>
        "<|im_start|>system\n" + S1MiniSystemText + "<|im_end|>\n<|im_start|>user\n" +
        S1MiniControlLine(styling, structure, email) + "\n" + transcript +
        "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
}
