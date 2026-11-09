// Contracts implemented by Remy.Platform (Win32) and consumed by the session coordinator and the UI.
// Owned by the lead. Additive changes only; list any change in your final report.

namespace Remy.Core.Contracts;

// ------------------------------------------------------------------ hotkeys

/// <summary>Global keyboard hook. Implemented with WH_KEYBOARD_LL on a dedicated thread.</summary>
public interface IHotkeyListener : IDisposable
{
    /// <summary>Replace the recording bindings and the cancel shortcut. Bumps the gesture generation.</summary>
    void Configure(IReadOnlyList<Hotkeys.HotkeyBinding> recordingBindings, Hotkeys.HotkeyBinding? cancelShortcut, bool holdToCancel);

    void Start();
    void Stop();

    /// <summary>Cancel shortcut only matches while true (pipeline.md §1.5).</summary>
    bool CancellationEnabled { get; set; }

    /// <summary>After resume: ignore shortcuts until all modifiers are released once.</summary>
    void RequireNeutralModifiers();

    void ResetCancellationGesture();

    /// <summary>Raised on the hook thread's queue consumer, never inside the hook callback. Marshal to UI as needed.</summary>
    event EventHandler<Hotkeys.HotkeyBinding>? BindingPressed;
    event EventHandler<Hotkeys.HotkeyBinding>? BindingReleased;
    /// <summary>Hold-to-cancel progress 0..1, or null when the gesture resets.</summary>
    event EventHandler<double?>? CancelProgressChanged;
    event EventHandler? CancelRequested;

    /// <summary>Shortcut recorder (pipeline.md §1.6). Monitoring is suspended while recording.
    /// Completes with the captured binding (Mode unset), or null if aborted with Esc (when !allowEscape) or <paramref name="ct"/>.</summary>
    Task<Hotkeys.HotkeyBinding?> RecordShortcutAsync(bool allowEscape, CancellationToken ct);
}

// ------------------------------------------------------------------ audio

public sealed record AudioInputDevice(string Id, string Name, bool IsDefault);

/// <summary>Microphone capture. Delivers 16 kHz mono float PCM.</summary>
public interface IAudioCapture
{
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    /// <summary>Raised when devices are added/removed or the default changes.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Start capture on <paramref name="deviceId"/> (null = system default). Completes once the first buffer
    /// arrives (1.5 s readiness timeout, 5 s overall), throwing <see cref="AudioCaptureException"/> with a user-facing message.
    /// <paramref name="onSamples"/> is called on the capture thread with 16 kHz mono samples in order.</summary>
    Task StartAsync(string? deviceId, Action<ReadOnlyMemory<float>> onSamples, CancellationToken ct);

    /// <summary>Stop and flush the resampler tail through onSamples before returning. Idempotent.</summary>
    Task StopAsync();

    /// <summary>Recent input level 0..1 (RMS-based, for the waveform).</summary>
    float CurrentLevel { get; }
}

public sealed class AudioCaptureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Output mute and input boost during recording (pipeline.md §3.2).</summary>
public interface ISystemAudioControl
{
    /// <summary>Mute the default output unless it is already muted or headphone-like. Returns a token for restore.</summary>
    object? MuteOutputForRecording();
    /// <summary>Raise the input device volume to <paramref name="target"/> (0..1). Returns a token for restore.</summary>
    object? BoostInput(string? deviceId, float target);
    /// <summary>Restore whatever the tokens changed. Safe with null tokens.</summary>
    Task RestoreAsync(object? muteToken, object? boostToken, TimeSpan settleAfterCaptureStop);
}

// ------------------------------------------------------------------ destination, context, delivery

/// <summary>Identity of the window and field where dictation started (pipeline.md §5.3).</summary>
public sealed record DestinationTarget(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,          // e.g. "slack.exe", lowercase
    string? ExecutablePath,
    string WindowTitle,
    string? FocusedElementId,    // UIA RuntimeId joined with '.', null if unavailable
    bool? IsEditable,            // null = unknown
    bool IsSecure,               // UIA IsPassword
    bool IsElevated,             // target runs at a higher integrity level than Remy
    bool IsRemy);

/// <summary>Context captured for cleanup (cleanup.md §5.3). All text is capped by the caller's limits.</summary>
public sealed record CapturedContext(
    string ProcessName,
    string WindowTitle,
    string? BrowserUrl,
    string? BrowserHost,
    string? FieldLabel,
    string? FieldPlaceholder,
    string? TextBeforeCursor,
    string? TextAfterCursor,
    string? SelectedText);

public interface IDestinationService
{
    /// <summary>Capture the foreground window and focused element. Must be fast (target &lt; 50 ms) and never throw.</summary>
    DestinationTarget? CaptureForeground();

    /// <summary>True if <paramref name="target"/> is still the foreground window and (when known) the same focused element.</summary>
    bool IsStillFocused(DestinationTarget target);

    /// <summary>Bring the window back and refocus the element if possible. Returns true when the target is verified focused.</summary>
    Task<bool> RestoreFocusAsync(DestinationTarget target, CancellationToken ct);

    /// <summary>Bounded context capture via UIA (timeout ~250 ms). Returns null on timeout, secure fields or sensitive apps.</summary>
    Task<CapturedContext?> CaptureContextAsync(DestinationTarget target, ContextCaptureLimits limits, CancellationToken ct);
}

public sealed record ContextCaptureLimits(int CursorTextChars = 600, int MetadataChars = 300);

public enum PasteDispatchResult { Dispatched, BlockedModifiersHeld, BlockedNotForeground, BlockedElevated, Failed }

/// <summary>Clipboard and synthetic paste. All calls run on Platform's STA worker.</summary>
public interface IClipboardDelivery
{
    /// <summary>Set the clipboard to <paramref name="text"/> (Unicode text only). Returns the clipboard sequence number after the write, or null on failure.</summary>
    Task<uint?> CopyAsync(string text);

    /// <summary>Current GetClipboardSequenceNumber(), used to detect another app changing the clipboard.</summary>
    uint ClipboardSequenceNumber { get; }

    /// <summary>Wait (up to 1 s) for physical modifiers to be released, then send Ctrl+V as one indivisible SendInput batch.</summary>
    Task<PasteDispatchResult> SendPasteAsync(CancellationToken ct);

    Task<string?> ReadTextAsync();
}

// ------------------------------------------------------------------ misc platform services

public interface ICredentialStore
{
    string? Read(string service, string account);
    /// <summary>Empty or whitespace deletes. Throws <see cref="InvalidOperationException"/> with a user-facing message on failure.</summary>
    void Write(string service, string account, string? secret);
    void Delete(string service, string account);
}

public sealed record SoundOption(string Id, string DisplayName);

public interface ISoundPlayer
{
    IReadOnlyList<SoundOption> BuiltInSounds { get; }
    /// <summary>Plays asynchronously; invalid or missing ids play nothing.</summary>
    void Play(string soundId, float volume);
    /// <summary>Copy a user file into the Sounds folder. Returns the new "custom:..." id or throws with a user-facing message.</summary>
    string ImportCustomSound(string path);
    string DisplayName(string soundId);
}

public interface IAutostart
{
    bool IsEnabled { get; }
    /// <summary>Throws InvalidOperationException with a user-facing message on failure.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>Sleep/resume and session lock notifications.</summary>
public interface IPowerEvents
{
    event EventHandler? Suspending;
    event EventHandler? Resumed;
}
