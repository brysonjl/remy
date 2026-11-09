using CommunityToolkit.Mvvm.ComponentModel;

namespace Remy.App.ViewModels;

/// <summary>What the floating recorder shows (parity/ui.md 4.3).</summary>
public enum OverlayState
{
    Hidden,
    /// <summary>Live transcript (if <see cref="OverlayViewModel.ShowTextPreview"/>) + waveform driven by <see cref="OverlayViewModel.Level"/>.</summary>
    Listening,
    /// <summary>Spinner badge (transcribing).</summary>
    Processing,
    /// <summary>Spinner + <see cref="OverlayViewModel.CleaningText"/> (transcript cleanup running).</summary>
    Cleaning,
    /// <summary>Spinner + "Loading &lt;name&gt;..." (model warm-up).</summary>
    Preparing,
    Success,
    /// <summary>Spinner + <see cref="OverlayViewModel.NoticeText"/> ("Already working on your dictation"); also used for "no text" notices.</summary>
    Busy,
    /// <summary>Clipboard icon + "Press Ctrl+V".</summary>
    CopyOnly,
    /// <summary>Warning + <see cref="OverlayViewModel.ErrorMessage"/> (title) and <see cref="OverlayViewModel.ErrorDetail"/>.</summary>
    Error,
    /// <summary>"Hold to cancel" + determinate bar <see cref="OverlayViewModel.HoldProgress"/> 0..1.</summary>
    HoldToCancel,
}

public enum OverlaySize { Compact, Comfortable, Large }

/// <summary>
/// Drives the overlay. The wiring step sets these from the session coordinator (UI thread only).
/// Typical: BeginSession on the window, then ShowListening every ~33 ms while recording, Show(Processing)..., Hide().
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    public const string DefaultBusyText = "Already working on your dictation";
    public const string DefaultCopyHint = "Press Ctrl+V";

    [ObservableProperty] private OverlayState _state = OverlayState.Hidden;
    [ObservableProperty] private string _transcriptText = "";
    /// <summary>Smoothed microphone level 0..1.</summary>
    [ObservableProperty] private double _level;
    [ObservableProperty] private bool _showTextPreview = true;
    [ObservableProperty] private OverlaySize _size = OverlaySize.Comfortable;
    /// <summary>Overlay theme, independent of the app theme (System follows the OS).</summary>
    [ObservableProperty] private ThemeMode _themeMode = ThemeMode.System;
    [ObservableProperty] private string _noticeText = DefaultBusyText;
    [ObservableProperty] private string _cleaningText = "Cleaning up…";
    [ObservableProperty] private string _preparingText = "Loading model…";
    [ObservableProperty] private string _copyHint = DefaultCopyHint;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _errorDetail = "";
    [ObservableProperty] private double _holdProgress;

    public void ShowListening(double level, string transcript) { TranscriptText = transcript; Level = level; State = OverlayState.Listening; }
    public void Hide() => State = OverlayState.Hidden;
    public void ShowBusy(string? text = null) { NoticeText = text ?? DefaultBusyText; State = OverlayState.Busy; }
    public void ShowError(string message, string detail = "") { ErrorMessage = message; ErrorDetail = detail; State = OverlayState.Error; }
    public void ShowHoldToCancel(double progress) { HoldProgress = Math.Clamp(progress, 0, 1); State = OverlayState.HoldToCancel; }
}
