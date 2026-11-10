using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

/// <summary>Per-category S1-mini styles (cleanup.md §1.5; settings key s1MiniAppStyling).</summary>
public sealed record S1MiniAppStyling(
    S1MiniStyling Email,
    S1MiniStyling WorkMessaging,
    S1MiniStyling PersonalMessaging,
    S1MiniStyling Other,
    IReadOnlyDictionary<DictationContextCategory, S1MiniStyling>? ExtendedStyles = null)
{
    public static S1MiniAppStyling Recommended { get; } =
        new(S1MiniStyling.Formal, S1MiniStyling.SemiFormal, S1MiniStyling.SemiCasual, S1MiniStyling.SemiFormal);
}

/// <summary>Context-awareness policy frozen at recording start (pipeline.md §2.3).</summary>
public sealed record ContextPolicy(
    bool IsEnabled,
    bool ShareCapturedTextRemotely,
    IReadOnlyDictionary<DictationContextCategory, Context.DictationResolvedWritingStyle> WritingStyles,
    S1MiniAppStyling S1Styles,
    IReadOnlyList<Context.DictationAppRule> AppRules)
{
    /// <summary>The frozen resolved style for a category (Original when none was stored), sanitized for the category.</summary>
    public Context.DictationResolvedWritingStyle WritingStyle(DictationContextCategory category) =>
        (WritingStyles.TryGetValue(category, out var s) ? s : Context.DictationResolvedWritingStyle.BuiltIn(DictationWritingStyle.Original))
            .Sanitized(category);
}

/// <summary>Frozen cleanup configuration for one dictation. Built once at recording start and never re-read.
/// Holds the credential value captured at that moment; never log or persist this record.</summary>
public sealed record CleanupRuntime(
    TranscriptPostProcessingMode Mode,
    string Prompt,
    string? BaseUrl,
    string? Model,
    bool ReasoningEnabled,
    string? ApiKey,
    TimeSpan RequestTimeout,
    CleanupProfileAdapter? ProfileAdapter,     // set when Mode == Profile
    Guid? ProfileId,
    string? ProfileName,
    bool StructuredOutput,                     // profiles: capabilities.structuredOutput; built-in Ollama/OpenAI-compatible: true
    bool Discovery,                            // profiles: capabilities.discovery; built-in Ollama: true
    bool ThinkingControl,                      // profiles: capabilities.thinkingControl; built-in: true
    S1MiniStructure S1Structure,
    S1MiniContextSetting S1Context,
    ContextPolicy ContextPolicy)
{
    public static CleanupRuntime Off(ContextPolicy policy) => new(
        TranscriptPostProcessingMode.None, "", null, null, false, null, TimeSpan.FromSeconds(30), null, null, null,
        false, false, false, S1MiniStructure.Prose, S1MiniContextSetting.Automatic, policy);
}
