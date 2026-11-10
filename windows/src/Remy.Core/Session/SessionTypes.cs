using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Session;

public enum DictationMode { Hold, Toggle }

public enum CancellationReason { User, Superseded, Shutdown, InputUnavailable }

/// <summary>What the coordinator tells the UI (overlay, main window). Implemented in Remy.App on the Dispatcher; tests use a recorder.
/// Any Show* call supersedes a pending delayed Hide.</summary>
public interface IDictationPresenter
{
    void BeginSession();
    void ShowListening(double level, string transcript);
    void ShowProcessing();
    void ShowCleaning();
    void ShowPreparing(string name);
    void ShowSuccess();
    void ShowCopyOnly(string hint);
    void ShowBusy(string text);
    void ShowError(string title, string detail);
    void ShowHoldToCancel(double progress);
    void Hide(TimeSpan delay);
    /// <summary>Blocking issue: bring the main window forward.</summary>
    void ShowMainWindow();
    void NavigateToHistory();
}

/// <summary>A held model (files must not be deleted while it is held).</summary>
public sealed class ModelLeaseHandle(string directory, IDisposable lease) : IDisposable
{
    public string Directory { get; } = directory;
    public void Dispose() => lease.Dispose();
}

/// <summary>Model files as the coordinator needs them (adapter over ModelStore in the app).</summary>
public interface ISessionModels
{
    string SpeechDisplayName { get; }
    bool IsSpeechInstalled { get; }
    bool IsSpeechDownloading { get; }
    bool IsS1MiniInstalled { get; }
    Task<ModelLeaseHandle> AcquireSpeechAsync(CancellationToken ct);
    Task<ModelLeaseHandle> AcquireS1MiniAsync(CancellationToken ct);
}

public sealed class DictationOptions
{
    public TimeSpan CandidateGap { get; init; } = TimeSpan.FromMilliseconds(220);
    public TimeSpan LevelPollInterval { get; init; } = TimeSpan.FromMilliseconds(33);
    public TimeSpan CleanupDeadline { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RouteSettle { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan SuccessHide { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan FallbackHide { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan NoTextHide { get; init; } = TimeSpan.FromSeconds(0.5);
    public TimeSpan StartFailureHide { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ShutdownDeliveryWait { get; init; } = TimeSpan.FromSeconds(3);
    public string CopyHint { get; init; } = "Press Ctrl+V";
}

/// <summary>Frozen per-session configuration (pipeline.md 2.3). Never re-read after the session begins.</summary>
public sealed record SessionConfig(
    string? MicrophoneId,
    IReadOnlyList<string> Vocabulary,
    bool FillerEnabled,
    IReadOnlyList<string> FillerWords,
    bool MuteSystemAudio,
    bool BoostMicrophone,
    bool PreserveCancelledSessions,
    bool HistoryEnabled,
    RemyDictationDestination Destination,
    bool ShowTextPreview,
    string Language,
    string StartSound,
    string CompletionSound,
    bool SoundsEnabled,
    float SoundVolume,
    CleanupRuntime Cleanup,
    RemyCleanupFailure? CleanupBuildFailure);

/// <summary>Recovery "Continue": previously recovered words become the transcript prefix of a fresh toggle session.</summary>
public sealed record SessionContinuation(string Prefix, Guid SourceRecoveryId, double PreviousDurationSeconds, string? TargetExecutable);

public static class TerminalApps
{
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal.exe", "wt.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "wezterm-gui.exe", "alacritty.exe", "mintty.exe", "ghostty.exe", "kitty.exe",
    };

    public static bool IsTerminal(string? processName) => processName is not null && Names.Contains(processName);
}

/// <summary>Why a delivery is copy-only instead of pasted.</summary>
public enum CopyOnlyReason { None, NoTarget, Secure, Elevated, NotEditable, TerminalMultiline, FocusNotRestored, FocusChanged, ClipboardChanged, Revoked }

public static class PasteGate
{
    /// <summary>Static policy on the destination proof (pipeline.md 5.3 with the Windows decisions). IsEditable null = unknown = allow.</summary>
    public static CopyOnlyReason Evaluate(DestinationTarget? target, string text)
    {
        if (target is null || target.IsRemy) return CopyOnlyReason.NoTarget;
        if (target.IsSecure) return CopyOnlyReason.Secure;
        if (target.IsElevated) return CopyOnlyReason.Elevated;
        if (target.IsEditable == false) return CopyOnlyReason.NotEditable;
        if (TerminalApps.IsTerminal(target.ProcessName) && (text.Contains('\n') || text.Contains('\r'))) return CopyOnlyReason.TerminalMultiline;
        return CopyOnlyReason.None;
    }
}

/// <summary>Built-in credential names (same as the Mac Keychain items).</summary>
public static class BuiltInCredentials
{
    public const string OpenRouterService = "io.github.brysonjl.remy.openrouter";
    public const string OpenRouterAccount = "openrouter-api-key";
    public const string OpenAICompatibleService = "io.github.brysonjl.remy.openai-compatible";
    public const string OpenAICompatibleAccount = "openai-compatible-api-key";
}

/// <summary>User-facing coordinator strings (verbatim from the Mac spec where one exists).</summary>
public static class SessionMessages
{
    public static string StillDownloading(string model) => $"{model} is still downloading. Try again when it finishes.";
    public static string NotReady(string model) => $"{model} is not ready. Download it from Speech Model settings.";
    public const string Busy = "Already working on your dictation";
    public const string TransformBusy = "Already transforming";
    public const string UnknownAudioError = "Unknown audio startup error";
    public static string RecordingStartError(string message) => $"Could not start the microphone. {message}";
    public static string StartFailed(string message) => $"Failed to start recording: {message}";
    public const string RecordingAvailableInHistory = " The recording is available in History.";
    public static string RecoveryCopyNotSaved(string message, string reason) => $"{message} The recovery copy could not be saved: {reason}";
    public const string DecodeFailed = "Speech recognition did not finish. Your available preview is incomplete; use any available recovery recording to try again.";
    public const string QuitRecoveryNotSaved = "Recovery details could not be saved before quitting. Existing audio was kept.";
    public const string CouldNotCopy = "Could not copy";
    public const string CouldNotCopyDetail = "Open Remy to copy the transcript.";
    public const string HistoryNotSavedTitle = "History not saved";
    public const string HistoryNotSavedDetail = "The transcript was not pasted. Open History to copy it.";
    public const string RawUsed = "Raw transcript used";
    public const string RawCopied = "Raw transcript · Press Ctrl+V";
    public const string RawInRemy = "Raw text in Remy";
}

/// <summary>Cleanup problems that need attention (pipeline.md 8.3; the names match Remy.App's CleanupProblem enum).</summary>
public static class CleanupProblems
{
    public const string S1MiniLanguageUnsupported = nameof(S1MiniLanguageUnsupported);
    public const string S1MiniNotDownloaded = nameof(S1MiniNotDownloaded);
    public const string OllamaModelMissing = nameof(OllamaModelMissing);
    public const string OpenRouterKeyMissing = nameof(OpenRouterKeyMissing);
    public const string OpenRouterModelMissing = nameof(OpenRouterModelMissing);
    public const string OpenAICompatibleModelMissing = nameof(OpenAICompatibleModelMissing);
    public const string SavedProfileMissing = nameof(SavedProfileMissing);

    public static string Message(string problem) => problem switch
    {
        S1MiniLanguageUnsupported => "S1-mini cleans up English only. Other languages are pasted without S1-mini cleanup.",
        S1MiniNotDownloaded => "Download S1-mini to use local transcript cleanup.",
        OllamaModelMissing => "Choose an Ollama model to use transcript cleanup.",
        OpenRouterKeyMissing => "Add an OpenRouter API key to use transcript cleanup.",
        OpenRouterModelMissing => "Choose an OpenRouter model to use transcript cleanup.",
        OpenAICompatibleModelMissing => "Choose a model on your OpenAI-compatible server to use transcript cleanup.",
        SavedProfileMissing => "Create or select a saved cleanup profile. Your speech transcript is kept if cleanup is unavailable.",
        _ => "",
    };
}
