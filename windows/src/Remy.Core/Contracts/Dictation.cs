// The dictation coordinator's surface for the UI (tray, overlay, pages). Owned by the lead; additive changes only.

namespace Remy.Core.Contracts;

/// <summary>UI-facing status (pipeline.md §2.1).</summary>
public enum DictationStatus { Idle, Recording, Processing, Error }

/// <summary>Speech engine readiness shown in the sidebar footer and the Speech Model page.</summary>
public enum EngineReadiness { NotDownloaded, Downloading, Loading, Ready, Failed, Downloaded /* installed, not loaded: loads on first dictation */ }

public sealed record AttentionItem(
    string Kind,          // "microphone", "speechSetup", "recovery", "recordingFailed", "cleanup", ... (ui.md §2.4)
    string Title,
    string Message,
    bool IsBlocking);

public interface IDictationController
{
    DictationStatus Status { get; }
    EngineReadiness SpeechReadiness { get; }
    /// <summary>Last delivered (or copy-only) text, for "Copy Last Transcript".</summary>
    string? LastTranscript { get; }
    bool CanStop { get; }
    bool CanCancel { get; }
    /// <summary>True while a cleanup attempt is running and undecided ("Use Raw Now").</summary>
    bool CanUseRawNow { get; }
    IReadOnlyList<AttentionItem> AttentionItems { get; }

    /// <summary>Raised (on any thread) when any property above changes.</summary>
    event EventHandler? StateChanged;

    Task StartFromMenuAsync();
    Task StopAsync();
    Task CancelAsync();
    void UseRawNow();
    Task CopyLastTranscriptAsync();

    /// <summary>Load (or reload) the speech model now, e.g. after a download. Never throws.</summary>
    Task PrepareSpeechModelAsync();
    /// <summary>Unload engines before deleting a model.</summary>
    Task UnloadSpeechModelAsync();
    Task PrepareS1MiniAsync();
    Task UnloadS1MiniAsync();

    /// <summary>Recovery actions from the History page (pipeline.md §6.4).</summary>
    Task ContinueRecoverySessionAsync(Guid recoverySessionId);
    /// <summary>Transcribe the saved audio, copy the text to the clipboard and record it in history.
    /// Returns null on success, or a user-facing failure message.</summary>
    Task<string?> RecoverTextAsync(Guid recoverySessionId);

    /// <summary>Re-read settings that affect running services (bindings, microphone list, prewarm).</summary>
    void ApplySettingsChange(string propertyName);

    /// <summary>Graceful shutdown (pipeline.md §2.7).</summary>
    Task ShutdownAsync();
}
