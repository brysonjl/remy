using Remy.Core.Settings;

namespace Remy.Core.Context;

/// <summary>Raw values, display names and style instructions for context enums (cleanup.md §2.4/§2.5/§5.1).</summary>
public static class DictationText
{
    public static string ToRaw(this DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => "email",
        DictationContextCategory.WorkMessaging => "workMessaging",
        DictationContextCategory.PersonalMessaging => "personalMessaging",
        DictationContextCategory.Terminal => "terminal",
        DictationContextCategory.TerminalAgent => "terminalAgent",
        DictationContextCategory.Browser => "browser",
        DictationContextCategory.CodingAgent => "codingAgent",
        DictationContextCategory.AiChat => "aiChat",
        _ => "other",
    };

    public static bool TryParseCategory(string? raw, out DictationContextCategory c)
    {
        foreach (var v in Enum.GetValues<DictationContextCategory>())
            if (string.Equals(v.ToRaw(), raw, StringComparison.Ordinal)) { c = v; return true; }
        c = DictationContextCategory.Other;
        return false;
    }

    public static string DisplayName(this DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => "Email",
        DictationContextCategory.WorkMessaging => "Work messaging",
        DictationContextCategory.PersonalMessaging => "Personal messaging",
        DictationContextCategory.Terminal => "Terminal",
        DictationContextCategory.TerminalAgent => "Terminal coding agent",
        DictationContextCategory.Browser => "Browser",
        DictationContextCategory.CodingAgent => "Coding agent",
        DictationContextCategory.AiChat => "AI chat",
        _ => "Other",
    };

    public static string ToRaw(this DictationWritingStyle s) => s switch
    {
        DictationWritingStyle.Formal => "formal",
        DictationWritingStyle.Neutral => "neutral",
        DictationWritingStyle.Casual => "casual",
        DictationWritingStyle.VeryCasual => "veryCasual",
        DictationWritingStyle.Excited => "excited",
        _ => "original",
    };

    public static bool TryParseStyle(string? raw, out DictationWritingStyle s)
    {
        foreach (var v in Enum.GetValues<DictationWritingStyle>())
            if (string.Equals(v.ToRaw(), raw, StringComparison.Ordinal)) { s = v; return true; }
        s = DictationWritingStyle.Casual;
        return false;
    }

    public static string DisplayName(this DictationWritingStyle s) => s switch
    {
        DictationWritingStyle.Formal => "Formal",
        DictationWritingStyle.Neutral => "Neutral",
        DictationWritingStyle.Casual => "Casual",
        DictationWritingStyle.VeryCasual => "Very Casual",
        DictationWritingStyle.Excited => "Excited",
        _ => "Original tone",
    };

    public static string CleanupInstruction(this DictationWritingStyle s) => s switch
    {
        DictationWritingStyle.Formal => "Use polished, complete sentences with conventional capitalization and punctuation.",
        DictationWritingStyle.Neutral => "Use a clear, balanced tone without making the text notably formal, casual, or enthusiastic. Preserve the speaker's meaning.",
        DictationWritingStyle.Casual => "Use natural conversational wording. Keep punctuation light and do not add a final period to a short message.",
        DictationWritingStyle.VeryCasual => "Use concise chat-style wording. Keep punctuation minimal and do not add a final period to a short message.",
        DictationWritingStyle.Excited => "Use an upbeat, energetic tone while preserving the speaker's meaning. Do not invent claims or emojis.",
        _ => "Preserve the transcript's original tone, register, emotional intensity, and level of formality. Only clean grammar, punctuation, structure, and obvious speech artifacts without restyling the wording.",
    };

    /// <summary>Styles offered for a category (cleanup.md §2.5).</summary>
    public static IReadOnlyList<DictationWritingStyle> StyleOptions(DictationContextCategory c) =>
        c == DictationContextCategory.PersonalMessaging
            ? [DictationWritingStyle.Formal, DictationWritingStyle.Neutral, DictationWritingStyle.Casual, DictationWritingStyle.VeryCasual, DictationWritingStyle.Original]
            : [DictationWritingStyle.Formal, DictationWritingStyle.Neutral, DictationWritingStyle.Casual, DictationWritingStyle.Excited, DictationWritingStyle.Original];

    /// <summary>A style not offered for the category becomes Casual.</summary>
    public static DictationWritingStyle Sanitized(this DictationWritingStyle s, DictationContextCategory c) =>
        StyleOptions(c).Contains(s) ? s : DictationWritingStyle.Casual;

    /// <summary>Default style per category (cleanup.md §5.1).</summary>
    public static DictationWritingStyle DefaultStyle(DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => DictationWritingStyle.Formal,
        DictationContextCategory.WorkMessaging => DictationWritingStyle.Formal,
        DictationContextCategory.PersonalMessaging => DictationWritingStyle.Casual,
        DictationContextCategory.Other => DictationWritingStyle.Formal,
        DictationContextCategory.Browser => DictationWritingStyle.Neutral,
        _ => DictationWritingStyle.Original,
    };

    public static string ToRaw(this S1MiniStyling s) => s switch
    {
        S1MiniStyling.Casual => "casual",
        S1MiniStyling.SemiCasual => "semi-casual",
        S1MiniStyling.SemiFormal => "semi-formal",
        _ => "formal",
    };

    public static string ToRaw(this S1MiniStructure s) => s == S1MiniStructure.Lists ? "lists" : "prose";
}
