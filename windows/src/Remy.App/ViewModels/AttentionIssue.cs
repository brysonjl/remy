namespace Remy.App.ViewModels;

/// <summary>One attention-banner entry (parity/ui.md 2.4). Mac-only issues (accessibility, Apple Speech, automation) are not ported.</summary>
public enum AttentionKind { Microphone, SpeechSetup, Recovery, RecordingFailed, Cleanup }

public sealed class AttentionIssue
{
    public AttentionKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string ActionTitle { get; init; } = "";
    /// <summary>Blocking issues show a warning triangle, optional ones an info circle.</summary>
    public bool IsBlocking { get; init; } = true;
    /// <summary>Invoked by the banner action button. The wiring step supplies the real behavior.</summary>
    public Action? Action { get; init; }
    public string Glyph => IsBlocking ? "" : "";

    // ---- Factories with the verbatim strings from the spec ----
    public static AttentionIssue Microphone(Action openSettings) => new()
    {
        Kind = AttentionKind.Microphone, Title = "Microphone access needed", Message = "Allow microphone access to start dictating.",
        ActionTitle = "Open Settings", Action = openSettings,
    };

    public static AttentionIssue SpeechSetup(bool preparationFailed, Action openSpeechModelPage) => new()
    {
        Kind = AttentionKind.SpeechSetup, Title = "Dictation setup needed",
        Message = preparationFailed ? "The speech model could not be prepared. Review Speech Model settings." : "Download a speech model to start dictating.",
        ActionTitle = "Set Up", Action = openSpeechModelPage,
    };

    public static AttentionIssue Recovery(string errorText, Action dismiss) => new()
    {
        Kind = AttentionKind.Recovery, Title = "Dictation recovery needs attention", Message = errorText, ActionTitle = "Dismiss", Action = dismiss,
    };

    public static AttentionIssue RecordingFailed(string errorText, Action dismiss) => new()
    {
        Kind = AttentionKind.RecordingFailed, Title = "Recording could not start", Message = errorText, ActionTitle = "Dismiss", Action = dismiss,
    };

    public static AttentionIssue Cleanup(CleanupProblem problem, Action viewSetup) => new()
    {
        Kind = AttentionKind.Cleanup, Title = "Transcript cleanup needs attention", IsBlocking = false,
        Message = problem switch
        {
            CleanupProblem.S1MiniLanguageUnsupported => "S1-mini cleans up English only. Other languages are pasted without S1-mini cleanup.",
            CleanupProblem.S1MiniNotDownloaded => "Download S1-mini to use local transcript cleanup.",
            CleanupProblem.OllamaModelMissing => "Choose an Ollama model to use transcript cleanup.",
            CleanupProblem.OpenRouterKeyMissing => "Add an OpenRouter API key to use transcript cleanup.",
            CleanupProblem.OpenRouterModelMissing => "Choose an OpenRouter model to use transcript cleanup.",
            CleanupProblem.OpenAICompatibleModelMissing => "Choose a model on your OpenAI-compatible server to use transcript cleanup.",
            CleanupProblem.SavedProfileMissing => "Create or select a saved cleanup profile. Your speech transcript is kept if cleanup is unavailable.",
            _ => "",
        },
        ActionTitle = "View Setup", Action = viewSetup,
    };
}

public enum CleanupProblem
{
    S1MiniLanguageUnsupported, S1MiniNotDownloaded, OllamaModelMissing, OpenRouterKeyMissing,
    OpenRouterModelMissing, OpenAICompatibleModelMissing, SavedProfileMissing,
}
