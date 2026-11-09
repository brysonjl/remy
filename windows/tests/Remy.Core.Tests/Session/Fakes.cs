using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Hotkeys;
using Remy.Core.Recovery;
using Remy.Core.Session;
using Remy.Core.Settings;

namespace Remy.Core.Tests.Session;

sealed class FakeHotkeys : IHotkeyListener
{
    public event EventHandler<HotkeyBinding>? BindingPressed;
    public event EventHandler<HotkeyBinding>? BindingReleased;
    public event EventHandler<double?>? CancelProgressChanged;
    public event EventHandler? CancelRequested;
    public bool CancellationEnabled { get; set; }
    public int Starts, Stops, NeutralRequests;
    public void Configure(IReadOnlyList<HotkeyBinding> recordingBindings, HotkeyBinding? cancelShortcut, bool holdToCancel) { }
    public void Start() => Starts++;
    public void Stop() => Stops++;
    public void RequireNeutralModifiers() => NeutralRequests++;
    public void ResetCancellationGesture() { }
    public Task<HotkeyBinding?> RecordShortcutAsync(bool allowEscape, CancellationToken ct) => Task.FromResult<HotkeyBinding?>(null);
    public void Dispose() { }

    public static readonly HotkeyBinding Hold = new(Guid.NewGuid(), null, HotkeyModifiers.Ctrl | HotkeyModifiers.Win, "Ctrl+Win", HotkeyMode.HoldToRecord);
    public static readonly HotkeyBinding Toggle = new(Guid.NewGuid(), 0x20, HotkeyModifiers.Ctrl, "Ctrl+Space", HotkeyMode.HandsFreeToggle);
    public void Press(HotkeyBinding b) => BindingPressed?.Invoke(this, b);
    public void Release(HotkeyBinding b) => BindingReleased?.Invoke(this, b);
    public void Progress(double? p) => CancelProgressChanged?.Invoke(this, p);
    public void RaiseCancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}

sealed class FakeAudio : IAudioCapture
{
    public List<AudioInputDevice> Devices { get; } = [new("mic1", "Mic 1", true)];
    public HashSet<string?> FailingDevices { get; } = [];
    public List<string?> StartAttempts { get; } = [];
    public Func<Task>? StartGate;
    public Action<ReadOnlyMemory<float>>? Callback;
    public int Stops;
    public bool Started;
    public event EventHandler? DevicesChanged { add { } remove { } }
    public float CurrentLevel => 0.3f;
    public IReadOnlyList<AudioInputDevice> GetInputDevices() => Devices;

    public async Task StartAsync(string? deviceId, Action<ReadOnlyMemory<float>> onSamples, CancellationToken ct)
    {
        StartAttempts.Add(deviceId);
        if (StartGate != null) await StartGate();
        ct.ThrowIfCancellationRequested();
        if (FailingDevices.Contains(deviceId)) throw new AudioCaptureException("Failed to select the specified microphone.");
        Callback = onSamples; Started = true;
    }

    public Task StopAsync() { Stops++; Started = false; return Task.CompletedTask; }

    /// <summary>Speech-like test tone, 16 kHz.</summary>
    public void FeedSpeech(double seconds)
    {
        int n = (int)(seconds * 16000), block = 1600;
        for (int off = 0; off < n; off += block)
        {
            var b = new float[Math.Min(block, n - off)];
            for (int i = 0; i < b.Length; i++) b[i] = 0.2f * MathF.Sin(2 * MathF.PI * 440 * (off + i) / 16000f);
            Callback?.Invoke(b);
        }
    }

    public void FeedSilence(double seconds)
    {
        int n = (int)(seconds * 16000);
        Callback?.Invoke(new float[n]);
    }
}

sealed class FakeSystemAudio : ISystemAudioControl
{
    public int Mutes, Boosts, Restores;
    public object? MuteOutputForRecording() { Mutes++; return "mute"; }
    public object? BoostInput(string? deviceId, float target) { Boosts++; return "boost"; }
    public Task RestoreAsync(object? m, object? b, TimeSpan settle) { Restores++; return Task.CompletedTask; }
}

sealed class FakeDestinations : IDestinationService
{
    public DestinationTarget? Target { get; set; } = Notepad();
    public bool RestoreResult = true, StillFocused = true;
    public Action? OnCapture;
    public int Captures, Restores;
    public static DestinationTarget Notepad(bool secure = false, bool elevated = false, bool? editable = true, string process = "notepad.exe") =>
        new(1234, 42, process, @"C:\Windows\notepad.exe", "Untitled", "1.2.3", editable, secure, elevated, false);
    public DestinationTarget? CaptureForeground() { Captures++; OnCapture?.Invoke(); return Target; }
    public bool IsStillFocused(DestinationTarget target) => StillFocused;
    public Task<bool> RestoreFocusAsync(DestinationTarget target, CancellationToken ct) { Restores++; return Task.FromResult(RestoreResult); }
    public Task<CapturedContext?> CaptureContextAsync(DestinationTarget target, ContextCaptureLimits limits, CancellationToken ct) => Task.FromResult<CapturedContext?>(null);
}

sealed class FakeClipboard : IClipboardDelivery
{
    public List<string> Copies { get; } = [];
    public int Pastes;
    public uint Sequence = 100;
    public bool CopyFails;
    public Func<Task>? PasteGate;
    public Action? OnPaste;
    public uint ClipboardSequenceNumber => Sequence;
    public Task<uint?> CopyAsync(string text)
    {
        if (CopyFails) return Task.FromResult<uint?>(null);
        Copies.Add(text); Sequence++;
        return Task.FromResult<uint?>(Sequence);
    }
    public async Task<PasteDispatchResult> SendPasteAsync(CancellationToken ct)
    {
        Pastes++;
        OnPaste?.Invoke();
        if (PasteGate != null) await PasteGate();
        return PasteDispatchResult.Dispatched;
    }
    public Task<string?> ReadTextAsync() => Task.FromResult<string?>(Copies.LastOrDefault());
}

sealed class FakeSounds : ISoundPlayer
{
    public List<string> Played { get; } = [];
    public IReadOnlyList<SoundOption> BuiltInSounds => [];
    public void Play(string soundId, float volume) => Played.Add(soundId);
    public string ImportCustomSound(string path) => "none";
    public string DisplayName(string soundId) => soundId;
}

sealed class FakeRecognizer : ISpeechRecognizer
{
    public bool IsLoaded { get; set; } = true;
    public Func<string> FinalText = () => "hello world";
    public Func<CancellationToken, Task>? FinalGate;
    public int FinalCalls, WindowCalls;
    public SemaphoreSlim FinalEntered = new(0);
    public Task LoadAsync(string d, CancellationToken ct) { IsLoaded = true; return Task.CompletedTask; }
    public void Unload() => IsLoaded = false;
    public Task<RecognitionResult> DecodeWindowAsync(ReadOnlyMemory<float> s, CancellationToken ct)
    {
        WindowCalls++;
        return Task.FromResult(new RecognitionResult("hello", [new RecognizedWord("hello", 0, 0.4)], TimeSpan.Zero));
    }
    public async Task<RecognitionResult> DecodeFinalAsync(ReadOnlyMemory<float> s, CancellationToken ct)
    {
        FinalCalls++;
        FinalEntered.Release();
        if (FinalGate != null) await FinalGate(ct);
        ct.ThrowIfCancellationRequested();
        var t = FinalText();
        return new RecognitionResult(t, [], TimeSpan.Zero);
    }
    public void Dispose() { }
}

sealed class FakeCleanup : ICleanupEngine
{
    public Func<CleanupRequest, CancellationToken, Task<CleanupResult>>? Handler;
    public int Calls;
    public CleanupRequest? Last;
    public async Task<CleanupResult> CleanAsync(CleanupRequest request, CancellationToken ct)
    {
        Calls++; Last = request;
        if (Handler != null) return await Handler(request, ct);
        return new CleanupResult(CleanupOutcome.Cleaned, request.Input + "!", null, "Fake", "m", TimeSpan.FromMilliseconds(5));
    }
    public Task PrewarmAsync(CleanupRuntime runtime, CancellationToken ct) => Task.CompletedTask;
}

sealed class FakeModels : ISessionModels
{
    public bool Installed = true, Downloading;
    public string SpeechDisplayName => "Parakeet v2";
    public bool IsSpeechInstalled => Installed;
    public bool IsSpeechDownloading => Downloading;
    public bool IsS1MiniInstalled => false;
    public Task<ModelLeaseHandle> AcquireSpeechAsync(CancellationToken ct) => Task.FromResult(new ModelLeaseHandle("x", new Nop()));
    public Task<ModelLeaseHandle> AcquireS1MiniAsync(CancellationToken ct) => Task.FromResult(new ModelLeaseHandle("x", new Nop()));
    sealed class Nop : IDisposable { public void Dispose() { } }
}

sealed class FakeCredentials : ICredentialStore
{
    public string? Read(string service, string account) => null;
    public void Write(string service, string account, string? secret) { }
    public void Delete(string service, string account) { }
}

sealed class RecordingPresenter : IDictationPresenter
{
    readonly List<string> _events = [];
    readonly object _l = new();
    void Add(string e) { lock (_l) _events.Add(e); }
    public string[] Snapshot() { lock (_l) return _events.ToArray(); }
    public bool Has(string prefix) => Snapshot().Any(e => e.StartsWith(prefix, StringComparison.Ordinal));
    public void BeginSession() => Add("begin");
    public void ShowListening(double level, string transcript) => Add("listening");
    public void ShowProcessing() => Add("processing");
    public void ShowCleaning() => Add("cleaning");
    public void ShowPreparing(string name) => Add("preparing:" + name);
    public void ShowSuccess() => Add("success");
    public void ShowCopyOnly(string hint) => Add("copyonly:" + hint);
    public void ShowBusy(string text) => Add("busy:" + text);
    public void ShowError(string title, string detail) => Add("error:" + title + "|" + detail);
    public void ShowHoldToCancel(double progress) => Add("hold:" + progress);
    public void Hide(TimeSpan delay) => Add("hide");
    public void ShowMainWindow() => Add("mainwindow");
    public void NavigateToHistory() => Add("history");
}

/// <summary>Logs the order of history writes and lets a test make any call return NotSaved.</summary>
sealed class FakeHistory : IHistoryRepository
{
    public List<string> Log { get; } = [];
    public List<(Guid Id, DeliveryOutcome Outcome, string? Reason)> Outcomes { get; } = [];
    public List<CleanupAttemptRecord> Attempts { get; } = [];
    public List<(SelectionKind Kind, string Text)> Selections { get; } = [];
    public HashSet<string> NotSaved { get; } = [];
    public bool IsEphemeral => false;

    HistoryAck Ack(string name)
    {
        lock (Log) Log.Add(name);
        return NotSaved.Contains(name) ? HistoryAck.NotSaved(HistoryFailureCategory.Persistence, "disk") : HistoryAck.Saved;
    }
    Task<HistoryAck> A(string name) => Task.FromResult(Ack(name));

    public Task<HistoryAck> BeginSessionAsync(HistorySessionMetadata metadata, CancellationToken ct = default) => A("begin");
    public Task<HistoryAck> SaveRawAsync(Guid sessionId, string rawChunk, CancellationToken ct = default) => A("raw");
    public Task<HistoryAck> AppendCleanupAttemptAsync(CleanupAttemptRecord attempt, CancellationToken ct = default) { Attempts.Add(attempt); return A("attempt"); }
    public Task<HistoryAck> SelectOutputAsync(Guid sessionId, SelectionKind kind, string text, Guid? attemptId = null, CancellationToken ct = default) { Selections.Add((kind, text)); return A("select"); }
    public Task<HistoryAck> RecordDeliveryIntentAsync(Guid deliveryId, Guid sessionId, string payload, CancellationToken ct = default) => A("intent");
    public Task<HistoryAck> RecordDeliveryOutcomeAsync(Guid deliveryId, DeliveryOutcome outcome, string? reason = null, CancellationToken ct = default) { Outcomes.Add((deliveryId, outcome, reason)); return A("outcome"); }
    public Task<HistoryAck> MarkInterruptedAsync(Guid sessionId, CancellationToken ct = default) => A("interrupted");
    public Task<HistoryAck> CompleteAsync(Guid sessionId, CancellationToken ct = default) => A("complete");
    public Task<HistoryAck> FailAsync(Guid sessionId, string errorCategory, CancellationToken ct = default) => A("fail");
    public Task<HistoryAck> RecordRecoveredAsync(HistorySessionMetadata metadata, string text, string? raw, CancellationToken ct = default) => A("recovered");
    public Task<HistoryResult<HistoryPage>> QueryAsync(HistoryQuery query, CancellationToken ct = default) => Task.FromResult(new HistoryResult<HistoryPage>(HistoryAck.Saved, new HistoryPage([], null)));
    public Task<HistoryResult<HistorySession?>> GetAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(new HistoryResult<HistorySession?>(HistoryAck.Saved, null));
    public Task<HistoryAck> DeleteAsync(Guid sessionId, CancellationToken ct = default) => A("delete");
    public Task<HistoryAck> ClearAllAsync(CancellationToken ct = default) => A("clear");
}

sealed class Harness : IDisposable
{
    public readonly FakeHotkeys Hotkeys = new();
    public readonly FakeAudio Audio = new();
    public readonly FakeSystemAudio SystemAudio = new();
    public readonly FakeDestinations Destinations = new();
    public readonly FakeClipboard Clipboard = new();
    public readonly FakeSounds Sounds = new();
    public readonly FakeRecognizer Recognizer = new();
    public readonly FakeCleanup Cleanup = new();
    public readonly FakeModels Models = new();
    public readonly FakeHistory History = new();
    public readonly RecordingPresenter Ui = new();
    public readonly RemySettings Settings = new();
    public readonly RecoveryStore Recovery;
    public readonly DictationCoordinator Coordinator;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "remy-session-" + Guid.NewGuid().ToString("N"));

    public Harness(Action<Harness>? configure = null)
    {
        Recovery = new RecoveryStore(_dir);
        configure?.Invoke(this);
        Coordinator = new DictationCoordinator(new DictationDependencies
        {
            Settings = Settings, Hotkeys = Hotkeys, Audio = Audio, SystemAudio = SystemAudio, Destinations = Destinations,
            Clipboard = Clipboard, Sounds = Sounds, Recognizer = Recognizer, Cleanup = Cleanup, History = () => History,
            Recovery = Recovery, Models = Models, Credentials = new FakeCredentials(), Presenter = Ui,
            Options = new DictationOptions
            {
                CandidateGap = TimeSpan.FromMilliseconds(2), LevelPollInterval = TimeSpan.FromMilliseconds(10), RouteSettle = TimeSpan.Zero,
                SuccessHide = TimeSpan.Zero, FallbackHide = TimeSpan.Zero, NoTextHide = TimeSpan.Zero, StartFailureHide = TimeSpan.Zero,
                ShutdownDeliveryWait = TimeSpan.FromMilliseconds(200),
            },
        });
    }

    public static async Task WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    /// <summary>Hold, speak, release, and wait for the session to end.</summary>
    public async Task DictateHoldAsync(double seconds = 1.2)
    {
        Hotkeys.Press(FakeHotkeys.Hold);
        await WaitFor(() => Coordinator.Status == DictationStatus.Recording);
        Audio.FeedSpeech(seconds);
        Hotkeys.Release(FakeHotkeys.Hold);
        await WaitFor(() => !Coordinator.IsBusy);
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
