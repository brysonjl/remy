using Remy.Core.Settings;

namespace Remy.Cleanup.Local;

/// <summary>Byte-exact S1-mini ChatML prompt (cleanup.md section 2.6).</summary>
public static class S1MiniPromptBuilder
{
    public const string SystemText =
        "You are a text normalizer for speech-to-text transcripts. The input begins with a control line specifying the styling, structure, and context settings; clean the transcript to match those settings and output only the cleaned text.";

    public static string StylingRaw(S1MiniStyling s) => s switch
    {
        S1MiniStyling.Casual => "casual",
        S1MiniStyling.SemiCasual => "semi-casual",
        S1MiniStyling.SemiFormal => "semi-formal",
        S1MiniStyling.Formal => "formal",
        _ => "semi-formal",
    };

    public static string StructureRaw(S1MiniStructure s) => s == S1MiniStructure.Lists ? "lists" : "prose";

    /// <summary>Resolved context string: "general" or "email".</summary>
    public static string ContextRaw(bool email) => email ? "email" : "general";

    /// <summary>Automatic -> email only when the destination category is Email.</summary>
    public static bool ResolveEmail(S1MiniContextSetting setting, DictationContextCategory? category) => setting switch
    {
        S1MiniContextSetting.Email => true,
        S1MiniContextSetting.General => false,
        _ => category == DictationContextCategory.Email,
    };

    public static string ControlLine(S1MiniStyling styling, S1MiniStructure structure, bool email) =>
        $"[Styling: {StylingRaw(styling)}] [Structure: {StructureRaw(structure)}] [Context: {ContextRaw(email)}]";

    public static string Build(string transcript, S1MiniStyling styling, S1MiniStructure structure, bool email) =>
        "<|im_start|>system\n" + SystemText + "<|im_end|>\n<|im_start|>user\n" +
        ControlLine(styling, structure, email) + "\n" + transcript +
        "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
}
