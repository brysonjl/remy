using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Diagnostics;
using Remy.Core.History;
using Remy.Core.Hotkeys;
using Remy.Core.Models;
using Remy.Core.Recovery;
using Remy.Core.Settings;

namespace Remy.Core.Session;

/// <summary>Everything the coordinator composes. Platform-neutral so tests can use fakes.</summary>
public sealed class DictationDependencies
{
    public required RemySettings Settings { get; init; }
    public required IHotkeyListener Hotkeys { get; init; }
    public required IAudioCapture Audio { get; init; }
    public required ISystemAudioControl SystemAudio { get; init; }
    public required IDestinationService Destinations { get; init; }
    public required IClipboardDelivery Clipboard { get; init; }
    public required ISoundPlayer Sounds { get; init; }
    public required ISpeechRecognizer Recognizer { get; init; }
    public required ICleanupEngine Cleanup { get; init; }
    public IS1MiniEngine? S1Mini { get; init; }
    /// <summary>Read once per session (the app switches between SQLite and ephemeral when history is toggled).</summary>
    public required Func<IHistoryRepository> History { get; init; }
    public required RecoveryStore Recovery { get; init; }
    public required ISessionModels Models { get; init; }
    public required ICredentialStore Credentials { get; init; }
    public CleanupProfileController? Profiles { get; init; }
    public required IDictationPresenter Presenter { get; init; }
    public IPowerEvents? Power { get; init; }
    /// <summary>True when the OS microphone privacy setting blocks access (registry consent store). Polled for the banner.</summary>
    public Func<bool>? MicrophoneDenied { get; init; }
    public DictationOptions Options { get; init; } = new();
}

/// <summary>
/// The dictation session coordinator (pipeline.md sections 1-8). One foreground session at a time. The session object is
/// its own token: every continuation after an await checks <see cref="Current"/> and drops stale work silently.
/// Public members are thread-safe; shared state changes under <c>_gate</c>, side effects run outside it.
/// </summary>
public sealed partial class DictationCoordinator : IDictationController, IDisposable
{
    readonly DictationDependencies _d;
    readonly RemySettings _settings;
    readonly DictationOptions _opt;
    readonly IDictationPresenter _ui;
    readonly CleanupRuntimeFactory _runtimeFactory;
    readonly object _gate = new();
    readonly CancellationTokenSource _life = new();

    DictationSession? _session;
    long _generation;
    volatile bool _sleeping, _shutdown;
    volatile bool _holdKeyDown;
    double? _cancelProgress;

    string? _lastTranscript;
    string? _recoveryError, _recordingStartError;
    bool _speechPreparing, _speechFailed, _presentedSpeechSetup;
    volatile bool _recoveryBusy;
    readonly SemaphoreSlim _modelGate = new(1, 1);
    ModelLeaseHandle? _speechLease, _s1Lease;

    public DictationCoordinator(DictationDependencies d)
    {
        _d = d;
        _settings = d.Settings;
        _opt = d.Options;
        _ui = d.Presenter;
        _runtimeFactory = new CleanupRuntimeFactory(d.Settings, d.Credentials, d.Profiles);

        d.Hotkeys.BindingPressed += OnBindingPressed;
        d.Hotkeys.BindingReleased += OnBindingReleased;
        d.Hotkeys.CancelProgressChanged += OnCancelProgress;
        d.Hotkeys.CancelRequested += OnCancelRequested;
        if (d.Power != null) { d.Power.Suspending += OnSuspending; d.Power.Resumed += OnResumed; }
    }

    // ------------------------------------------------------------------ IDictationController state

    public event EventHandler? StateChanged;

    void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { RemyLog.Error("Session", "state listener failed", ex); }
        try { _d.Hotkeys.CancellationEnabled = CanCancel; } catch { }
    }

    public DictationStatus Status
    {
        get
        {
            var s = _session;
            return s?.Stage switch
            {
                null => DictationStatus.Idle,
                SessionStage.Preparing => DictationStatus.Idle,
                SessionStage.Recording => DictationStatus.Recording,
                _ => DictationStatus.Processing,
            };
        }
    }

    /// <summary>True while any session exists (preparing, recording, processing).</summary>
    public bool IsBusy => _session != null;

    public EngineReadiness SpeechReadiness
    {
        get
        {
            if (_d.Recognizer.IsLoaded) return EngineReadiness.Ready;
            if (_speechPreparing) return EngineReadiness.Loading;
            if (_speechFailed) return EngineReadiness.Failed;
            if (_d.Models.IsSpeechDownloading) return EngineReadiness.Downloading;
            return _d.Models.IsSpeechInstalled ? EngineReadiness.Downloaded : EngineReadiness.NotDownloaded;
        }
    }

    public string? LastTranscript => _lastTranscript;
    public bool CanStop => _session?.Stage == SessionStage.Recording;

    public bool CanCancel
    {
        get
        {
            var s = _session;
            return s != null && !s.Cancelling && !s.Lease.PasteClaimed;
        }
    }

    public bool CanUseRawNow
    {
        get { var s = _session; return s is { Stage: SessionStage.Cleaning, CleanupDecided: false, Cancelling: false }; }
    }

    public string? RecoveryError => _recoveryError;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Apply hotkey bindings, start the hook and optionally prewarm the engines. Call once after construction.</summary>
    public void Initialize()
    {
        ConfigureHotkeys();
        try { _d.Hotkeys.Start(); } catch (Exception ex) { RemyLog.Error("Session", "hotkey start failed", ex); }
        if (_settings.PrewarmEnginesAtStartup)
        {
            if (_d.Models.IsSpeechInstalled) _ = Guard(PrepareSpeechModelAsync, "prewarm");
            if (_settings.TranscriptPostProcessingMode == TranscriptPostProcessingMode.S1Mini && _d.Models.IsS1MiniInstalled)
                _ = Guard(PrepareS1MiniAsync, "prewarm-s1");
        }
        RaiseStateChanged();
    }

    string? _hotkeySignature;

    /// <summary>Idempotent: pages and the app's settings listener may both ask; the hook is reconfigured only when something changed.</summary>
    void ConfigureHotkeys()
    {
        var cancel = _settings.CancelShortcut;
        var bindings = _settings.HotkeyBindings.Where(b => b.HasBinding).ToList();
        var sig = string.Join(";", bindings.Select(b => $"{b.Id}/{b.VirtualKey}/{(int)b.Modifiers}/{b.Mode}"))
            + "|" + (cancel.HasBinding ? $"{cancel.VirtualKey}/{(int)cancel.Modifiers}" : "-") + "|" + _settings.HoldToCancel;
        lock (_gate) { if (sig == _hotkeySignature) return; _hotkeySignature = sig; }
        _d.Hotkeys.Configure(bindings, cancel.HasBinding ? cancel : null, _settings.HoldToCancel);
    }

    public void ApplySettingsChange(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(RemySettings.HotkeyBindings):
            case nameof(RemySettings.CancelShortcut):
            case nameof(RemySettings.HoldToCancel):
                if (!_shutdown) ConfigureHotkeys();
                break;
            case nameof(RemySettings.TranscriptPostProcessingMode):
                if (_settings.TranscriptPostProcessingMode == TranscriptPostProcessingMode.S1Mini && _d.Models.IsS1MiniInstalled)
                    _ = Guard(PrepareS1MiniAsync, "s1-load");
                break;
        }
        RaiseStateChanged();
    }

    static async Task Guard(Func<Task> work, string area)
    {
        try { await work().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemyLog.Error("Session", area + " failed", ex); }
    }

    // ------------------------------------------------------------------ hotkeys (pipeline.md 1.3)

    bool Current(DictationSession s) => ReferenceEquals(_session, s) && !s.Cts.IsCancellationRequested && !_shutdown;

    void OnBindingPressed(object? sender, HotkeyBinding b)
    {
        if (_sleeping || _shutdown) return;
        if (b.Mode == HotkeyMode.HoldToRecord)
        {
            _holdKeyDown = true;
            _ = Guard(() => StartDictationAsync(DictationMode.Hold), "start");
        }
        else
        {
            if (_session?.Stage == SessionStage.Recording) _ = Guard(StopAsync, "stop");
            else _ = Guard(() => StartDictationAsync(DictationMode.Toggle), "start");
        }
    }

    void OnBindingReleased(object? sender, HotkeyBinding b)
    {
        if (_sleeping || _shutdown || b.Mode != HotkeyMode.HoldToRecord) return;
        _holdKeyDown = false;
        // Only a hold-started session is stopped by a hold key-up (a toggle session is not hijacked by a stray hold binding).
        var s = _session;
        if (s is { Mode: DictationMode.Hold, Stage: SessionStage.Recording }) _ = Guard(StopAsync, "stop");
        // A release while still preparing is picked up when the microphone goes live (see BeginRecordingAsync).
    }

    void OnCancelProgress(object? sender, double? p)
    {
        _cancelProgress = p;
        if (p is double v) _ui.ShowHoldToCancel(v);
        else RefreshOverlay();
    }

    void OnCancelRequested(object? sender, EventArgs e)
    {
        _cancelProgress = null;
        _ = Guard(() => CancelAsync(), "cancel");
    }

    void RefreshOverlay()
    {
        var s = _session;
        switch (s?.Stage)
        {
            case null: _ui.Hide(TimeSpan.Zero); break;
            case SessionStage.Recording: _ui.ShowListening(_d.Audio.CurrentLevel, s.Transcript); break;
            case SessionStage.Cleaning: _ui.ShowCleaning(); break;
            case SessionStage.Preparing: break;
            default: _ui.ShowProcessing(); break;
        }
    }

    void OnSuspending(object? sender, EventArgs e)
    {
        _sleeping = true;
        _holdKeyDown = false;
        try { _d.Hotkeys.Stop(); } catch { }
        _ = Guard(() => CancelAsync(CancellationReason.InputUnavailable), "sleep-cancel");
    }

    void OnResumed(object? sender, EventArgs e)
    {
        _sleeping = false;
        if (_shutdown) return;
        try { _d.Hotkeys.Start(); _d.Hotkeys.RequireNeutralModifiers(); } catch (Exception ex) { RemyLog.Error("Session", "hotkey resume failed", ex); }
    }

    // ------------------------------------------------------------------ menu commands

    public Task StartFromMenuAsync() => StartDictationAsync(DictationMode.Toggle);

    public Task StopAsync()
    {
        DictationSession? s;
        lock (_gate)
        {
            s = _session;
            if (s is not { Stage: SessionStage.Recording } || s.Cancelling) return Task.CompletedTask;
            s.Stage = SessionStage.Stopping;
            s.FinishDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _d.Hotkeys.ResetCancellationGesture();
        RaiseStateChanged();
        _ = FinishGuardAsync(s);
        return s.FinishDone.Task;
    }

    public void UseRawNow()
    {
        var s = _session;
        if (s is not { Stage: SessionStage.Cleaning, CleanupDecided: false }) return;
        s.UseRawRequested = true;
        try { s.CleanupCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task CopyLastTranscriptAsync()
    {
        var t = _lastTranscript;
        if (string.IsNullOrEmpty(t)) return;
        await _d.Clipboard.CopyAsync(t).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ speech and S1-mini models

    public async Task PrepareSpeechModelAsync()
    {
        await _modelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_d.Recognizer.IsLoaded) return;
            if (!_d.Models.IsSpeechInstalled) { _speechFailed = false; return; }
            _speechPreparing = true; _speechFailed = false;
            RaiseStateChanged();
            using var perf = PerfTrace.Begin("stt.modelLoad");
            try
            {
                var lease = await _d.Models.AcquireSpeechAsync(_life.Token).ConfigureAwait(false);
                try { await _d.Recognizer.LoadAsync(lease.Directory, _life.Token).ConfigureAwait(false); }
                catch { lease.Dispose(); throw; }
                _speechLease?.Dispose();
                _speechLease = lease;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _speechFailed = true;
                RemyLog.Error("Speech", "model load failed", ex);
            }
        }
        finally
        {
            _speechPreparing = false;
            _modelGate.Release();
            RaiseStateChanged();
        }
    }

    public async Task UnloadSpeechModelAsync()
    {
        await _modelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _session;
            if (s != null) await CancelCoreAsync(s, CancellationReason.Superseded).ConfigureAwait(false);
            _d.Recognizer.Unload();
            _speechLease?.Dispose(); _speechLease = null;
            _speechFailed = false;
        }
        finally { _modelGate.Release(); RaiseStateChanged(); }
    }

    public async Task PrepareS1MiniAsync()
    {
        if (_d.S1Mini == null) return;
        await _modelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_d.S1Mini.IsLoaded || !_d.Models.IsS1MiniInstalled) return;
            var lease = await _d.Models.AcquireS1MiniAsync(_life.Token).ConfigureAwait(false);
            try { await _d.S1Mini.LoadAsync(Path.Combine(lease.Directory, ModelCatalog.S1MiniGgufFile), _life.Token).ConfigureAwait(false); }
            catch { lease.Dispose(); throw; }
            _s1Lease?.Dispose();
            _s1Lease = lease;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemyLog.Error("S1Mini", "load failed", ex); }
        finally { _modelGate.Release(); RaiseStateChanged(); }
    }

    public async Task UnloadS1MiniAsync()
    {
        await _modelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _d.S1Mini?.Unload();
            _s1Lease?.Dispose(); _s1Lease = null;
        }
        finally { _modelGate.Release(); RaiseStateChanged(); }
    }

    // ------------------------------------------------------------------ attention (pipeline.md 8.3)

    public IReadOnlyList<AttentionItem> AttentionItems
    {
        get
        {
            var items = new List<AttentionItem>();
            bool micDenied = false;
            try { micDenied = _d.MicrophoneDenied?.Invoke() ?? false; } catch { }
            if (micDenied)
                items.Add(new("microphone", "Microphone access needed", "Allow microphone access to start dictating.", true));

            var readiness = SpeechReadiness;
            if (readiness is EngineReadiness.Failed or EngineReadiness.NotDownloaded)
                items.Add(new("speechSetup", "Dictation setup needed",
                    readiness == EngineReadiness.Failed ? "The speech model could not be prepared. Review Speech Model settings." : "Download a speech model to start dictating.", true));

            if (_recoveryError is { } r)
                items.Add(new("recovery", "Dictation recovery needs attention", r, true));
            if (_recordingStartError is { } rs && !micDenied)
                items.Add(new("recordingFailed", "Recording could not start", rs, true));

            foreach (var p in _runtimeFactory.Problems(_d.Models.IsS1MiniInstalled))
                items.Add(new("cleanup:" + p, "Transcript cleanup needs attention", CleanupProblems.Message(p), false));
            return items;
        }
    }

    /// <summary>Banner "Dismiss" for the recovery and recordingFailed issues.</summary>
    public void DismissAttention(string kind)
    {
        if (kind == "recovery") _recoveryError = null;
        else if (kind == "recordingFailed") _recordingStartError = null;
        RaiseStateChanged();
    }

    void SetRecoveryError(string message)
    {
        _recoveryError = message;
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ shutdown (pipeline.md 2.7)

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        try { _d.Hotkeys.Stop(); } catch { }
        var s = _session;
        if (s != null)
        {
            if (s.Lease.PasteClaimed && s.FinishDone != null)
            {
                await Task.WhenAny(s.FinishDone.Task, Task.Delay(_opt.ShutdownDeliveryWait)).ConfigureAwait(false);
            }
            else
            {
                await CancelCoreAsync(s, CancellationReason.Shutdown).ConfigureAwait(false);
            }
        }
        _life.Cancel();
        try { await _d.Audio.StopAsync().ConfigureAwait(false); } catch { }
        try { _d.Recognizer.Unload(); } catch { }
        try { _d.S1Mini?.Unload(); } catch { }
        _speechLease?.Dispose(); _speechLease = null;
        _s1Lease?.Dispose(); _s1Lease = null;
        _ui.Hide(TimeSpan.Zero);
    }

    public void Dispose()
    {
        _d.Hotkeys.BindingPressed -= OnBindingPressed;
        _d.Hotkeys.BindingReleased -= OnBindingReleased;
        _d.Hotkeys.CancelProgressChanged -= OnCancelProgress;
        _d.Hotkeys.CancelRequested -= OnCancelRequested;
        if (_d.Power != null) { _d.Power.Suspending -= OnSuspending; _d.Power.Resumed -= OnResumed; }
        _life.Cancel();
    }

    /// <summary>Test/dev hook: join two texts with one space (pipeline.md 5.7).</summary>
    internal static string Join(string? a, string? b)
    {
        a = a?.Trim() ?? ""; b = b?.Trim() ?? "";
        return a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;
    }
}
