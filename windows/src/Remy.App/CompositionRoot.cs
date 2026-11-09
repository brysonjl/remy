using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Remy.App.Overlay;
using Remy.App.Pages;
using Remy.App.Services;
using Remy.App.Tray;
using Remy.App.ViewModels;
using Remy.App.Wiring;
using Remy.Cleanup.Local;
using Remy.Core.Cleanup;
using Remy.Core.Diagnostics;
using Remy.Core.Models;
using Remy.Core.Recovery;
using Remy.Core.Session;
using Remy.Core.Settings;
using Remy.Core.Storage;
using Remy.Platform.Audio;
using Remy.Platform.Delivery;
using Remy.Platform.Destination;
using Remy.Platform.Hotkeys;
using Remy.Platform.Services;
using Remy.Speech;
using AppThemeMode = Remy.App.ViewModels.ThemeMode;
using AppOverlaySize = Remy.App.ViewModels.OverlaySize;
using CoreThemeMode = Remy.Core.Settings.ThemeMode;
using CoreOverlaySize = Remy.Core.Settings.OverlaySize;

namespace Remy.App;

/// <summary>
/// Builds the UI-side objects and is the single place where Core / Platform services get connected.
/// Startup order: services (history, recovery sweep, profiles, models) -> coordinator -> hotkeys -> pages -> bindings.
/// </summary>
public sealed class CompositionRoot
{
    public ShellViewModel Shell { get; } = new();
    public OverlayViewModel OverlayVm { get; } = new();
    public TrayViewModel TrayVm { get; } = new();
    public OverlayWindow Overlay { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;

    public AppServices? Services { get; private set; }
    public DictationCoordinator? Coordinator { get; private set; }
    public SettingsStore? SettingsStore { get; private set; }
    public RemySettings? Settings { get; private set; }

    readonly List<IDisposable> _owned = [];
    DispatcherTimer? _poll;
    bool _syncQueued;
    string _attentionSignature = "";
    QuickVocabularyWindow? _vocabulary;

    /// <summary>UI only (demo and snapshot modes): no services, no hooks.</summary>
    public static CompositionRoot CreateUiOnly(App app)
    {
        var root = new CompositionRoot();
        root.Main = new MainWindow(root.Shell);
        root.Overlay = new OverlayWindow(root.OverlayVm);
        root.TrayVm.Commands = new UiOnlyTrayCommands(app);
        root.Shell.VersionText = VersionText();
        return root;
    }

    static string VersionText() => $"Version {typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    public static CompositionRoot Create(App app, SettingsStore store, RemySettings settings)
    {
        var root = new CompositionRoot { SettingsStore = store, Settings = settings };
        var dispatcher = app.Dispatcher;
        root.Main = new MainWindow(root.Shell);
        root.Overlay = new OverlayWindow(root.OverlayVm);
        root.Shell.VersionText = VersionText();

        // ---- platform and core services
        SystemAudioControl.RecoverStaleMute();
        RemyPaths.EnsureModelsDirectory();
        var credentials = new CredentialManagerStore();
        var models = new ModelStore(RemyPaths.ModelsDirectory, new HttpClient());
        var history = new HistoryHost(settings, HistoryHost.OpenAsync(RemyPaths.HistoryDatabaseFile).GetAwaiter().GetResult());
        var recovery = new RecoveryStore(RemyPaths.RecoveryDirectory);
        try { recovery.List(); } catch (Exception ex) { RemyLog.Error("Recovery", "startup sweep failed", ex); }   // expiry sweep + orphan discovery

        CleanupProfileController? profiles = null;
        try
        {
            profiles = new CleanupProfileController(new CleanupProfileFileRepository(RemyPaths.EnsureCleanupProfilesDirectory()), credentials);
            profiles.Load();
        }
        catch (Exception ex) { RemyLog.Error("Cleanup", "profiles could not be loaded", ex); profiles = null; }

        var s1 = new S1MiniCleanupEngine(m => RemyLog.Warn("S1Mini", "llama: " + m),
            () => models.IsInstalled(ModelCatalog.S1Mini) ? Path.Combine(models.DirectoryFor(ModelCatalog.S1Mini), ModelCatalog.S1MiniGgufFile) : null);
        var router = new CleanupRouter(s1);
        var discovery = new CleanupDiscovery(router.Transport, router.Caches);

        Remy.Core.Contracts.IAudioCapture audio = DevHooks.CreateFakeAudio() ?? new WasapiAudioCapture();
        var hotkeys = new LowLevelKeyboardHotkeyListener();
        var sounds = new WindowsSoundPlayer(RemyPaths.EnsureSoundsDirectory());
        var autostart = new RegistryAutostart();
        var power = new PowerEvents();
        var presenter = new OverlayPresenter(root, dispatcher);

        var coordinator = new DictationCoordinator(new DictationDependencies
        {
            Settings = settings, Hotkeys = hotkeys, Audio = audio, SystemAudio = new SystemAudioControl(),
            Destinations = new DestinationService(), Clipboard = new ClipboardDelivery(), Sounds = sounds,
            Recognizer = new SherpaParakeetRecognizer(), Cleanup = router, S1Mini = s1, History = () => history.Current,
            Recovery = recovery, Models = new ModelsAdapter(models), Credentials = credentials, Profiles = profiles,
            Presenter = presenter, Power = power, MicrophoneDenied = MicrophonePrivacy.IsDenied,
        });
        root.Coordinator = coordinator;
        root._owned.AddRange([hotkeys, power, router, s1, sounds, coordinator]);

        var services = new AppServices
        {
            Settings = settings, SettingsStore = store, Dictation = coordinator, Models = models, CleanupRouter = router,
            CleanupDiscovery = discovery, Profiles = profiles, Credentials = credentials, History = () => history.Current,
            Recovery = recovery, Audio = audio, Hotkeys = hotkeys, Sounds = sounds, Autostart = autostart,
        };
        root.Services = services;

        // ---- pages
        foreach (var p in Enum.GetValues<AppPage>())
            if (root.Main.GetPage(p) is IServicePage sp) sp.Attach(services);

        // ---- bindings and UI sync
        root.ApplyInitialSettings(app, settings);
        settings.PropertyChanged += (_, e) => dispatcher.BeginInvoke(() => root.OnSettingChanged(app, e.PropertyName));
        coordinator.StateChanged += (_, _) => root.QueueSync(dispatcher);
        root.TrayVm.Commands = new TrayCommands(app, root);
        root.Shell.PageRequested += _ => root.Sync();

        root._poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2.5) };
        root._poll.Tick += (_, _) => root.Sync();   // microphone privacy toggle can change while the app runs
        root._poll.Start();
        app.Activated += (_, _) => root.Sync();

        coordinator.Initialize();
        if (settings.LaunchAtLogin && !autostart.IsEnabled) { try { autostart.SetEnabled(true); } catch (Exception ex) { RemyLog.Error("Autostart", "enable failed", ex); } }
        root.Sync();
        return root;
    }

    // ------------------------------------------------------------------ settings bindings

    static AppThemeMode Map(CoreThemeMode m) => m switch { CoreThemeMode.Dark => AppThemeMode.Dark, CoreThemeMode.Light => AppThemeMode.Light, _ => AppThemeMode.System };
    static AppOverlaySize Map(CoreOverlaySize s) => s switch { CoreOverlaySize.Compact => AppOverlaySize.Compact, CoreOverlaySize.Large => AppOverlaySize.Large, _ => AppOverlaySize.Comfortable };
    static AppAppearance Map(AppAppearanceMode m) => m == AppAppearanceMode.DockAndMenuBar ? AppAppearance.TaskbarAndTray : AppAppearance.TrayOnly;

    void ApplyInitialSettings(App app, RemySettings s)
    {
        OverlayVm.ThemeMode = Map(s.OverlayThemeMode);
        OverlayVm.Size = Map(s.OverlaySize);
        OverlayVm.ShowTextPreview = s.ShowTextPreview;
        app.ApplyAppearance(Map(s.AppAppearanceMode));
    }

    void OnSettingChanged(App app, string? name)
    {
        var s = Settings!;
        switch (name)
        {
            case nameof(RemySettings.ThemeMode): ThemeManager.Apply(Map(s.ThemeMode)); break;
            case nameof(RemySettings.OverlayThemeMode): OverlayVm.ThemeMode = Map(s.OverlayThemeMode); break;
            case nameof(RemySettings.OverlaySize): OverlayVm.Size = Map(s.OverlaySize); break;
            case nameof(RemySettings.ShowTextPreview): OverlayVm.ShowTextPreview = s.ShowTextPreview; break;
            case nameof(RemySettings.AppAppearanceMode): app.ApplyAppearance(Map(s.AppAppearanceMode)); break;
        }
        if (name != null) Coordinator?.ApplySettingsChange(name);   // idempotent for the hook; pages may also call it
        Sync();
    }

    // ------------------------------------------------------------------ coordinator -> UI

    void QueueSync(Dispatcher d)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        d.BeginInvoke(DispatcherPriority.Normal, () => { Interlocked.Exchange(ref _queued, 0); Sync(); });
    }
    int _queued;

    /// <summary>Recompute everything the shell and tray show from coordinator state. UI thread only.</summary>
    public void Sync()
    {
        var c = Coordinator; var s = Settings;
        if (c == null || s == null) return;
        var items = c.AttentionItems;

        // attention banner: rebuild only when the set changed (avoids flicker and pager resets)
        var sig = string.Join("\n", items.Select(i => i.Kind + "|" + i.Message));
        if (sig != _attentionSignature)
        {
            _attentionSignature = sig;
            Shell.AttentionIssues.Clear();
            foreach (var i in items) Shell.AttentionIssues.Add(ToIssue(i));
        }

        Shell.ServiceStatus = c.Status switch
        {
            Remy.Core.Contracts.DictationStatus.Recording => ServiceStatus.Listening,
            Remy.Core.Contracts.DictationStatus.Processing => ServiceStatus.Transcribing,
            Remy.Core.Contracts.DictationStatus.Error => ServiceStatus.Error,
            _ when items.Any(i => i.IsBlocking) => ServiceStatus.SetupNeeded,
            _ => c.SpeechReadiness switch
            {
                Remy.Core.Contracts.EngineReadiness.Loading => ServiceStatus.PreparingSpeechModel,
                Remy.Core.Contracts.EngineReadiness.Ready => ServiceStatus.ReadyToDictate,
                _ => ServiceStatus.LoadsOnFirstDictation,
            },
        };

        TrayVm.CanCopyLastTranscript = !string.IsNullOrEmpty(c.LastTranscript);
        TrayVm.IsStopVisible = c.CanStop;
        TrayVm.IsUseRawVisible = c.CanUseRawNow;
        TrayVm.IsCancelVisible = c.CanCancel;
        TrayVm.IsAddVocabularyVisible = s.TranscriptPostProcessingMode != TranscriptPostProcessingMode.None;
    }

    AttentionIssue ToIssue(Remy.Core.Contracts.AttentionItem i)
    {
        var c = Coordinator!;
        if (i.Kind == "microphone") return AttentionIssue.Microphone(MicrophonePrivacy.OpenSettings);
        if (i.Kind == "speechSetup")
            return AttentionIssue.SpeechSetup(c.SpeechReadiness == Remy.Core.Contracts.EngineReadiness.Failed, () => Shell.Navigate(AppPage.SpeechModel));
        if (i.Kind == "recovery") return AttentionIssue.Recovery(i.Message, () => c.DismissAttention("recovery"));
        if (i.Kind == "recordingFailed") return AttentionIssue.RecordingFailed(i.Message, () => c.DismissAttention("recordingFailed"));
        var problem = Enum.TryParse<CleanupProblem>(i.Kind["cleanup:".Length..], out var p) ? p : CleanupProblem.SavedProfileMissing;
        return AttentionIssue.Cleanup(problem, () => Shell.Navigate(AppPage.TranscriptCleanup));
    }

    /// <summary>Menu refresh: flags and the microphone list (pipeline.md 3.4).</summary>
    public void RefreshTrayMenu()
    {
        Sync();
        var s = Settings!;
        var devices = Services!.Audio.GetInputDevices();
        var selected = s.SelectedMicrophoneUID;
        bool selectedPresent = selected != null && devices.Any(d => d.Id == selected);
        var def = devices.FirstOrDefault(d => d.IsDefault);
        TrayVm.AutomaticTitle = def != null ? $"Automatic ({def.Name})" : "Automatic";
        TrayVm.IsAutomaticSelected = !selectedPresent;
        TrayVm.Microphones.Clear();
        foreach (var d in devices) TrayVm.Microphones.Add(new MicrophoneItem(d.Id, d.Name, selectedPresent && d.Id == selected));
    }

    public void ToggleQuickVocabulary()
    {
        if (_vocabulary is { IsVisible: true }) { _vocabulary.Activate(); return; }
        _vocabulary = new QuickVocabularyWindow(Services!);
        _vocabulary.Closed += (_, _) => _vocabulary = null;
        _vocabulary.Show();
        _vocabulary.Activate();
    }

    /// <summary>Graceful shutdown: coordinator first (preserves recovery audio, restores audio state), then flush settings.</summary>
    public async Task ShutdownAsync()
    {
        _poll?.Stop();
        try { if (Coordinator != null) await Coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { RemyLog.Error("App", "coordinator shutdown failed", ex); }
        try { SettingsStore?.Flush(); } catch { }
        foreach (var o in _owned) { try { o.Dispose(); } catch { } }
    }
}

sealed class TrayCommands(App app, CompositionRoot root) : ITrayCommands
{
    public void OpenRemy() => app.ShowMainWindow();
    public void CopyLastTranscript() => _ = root.Coordinator!.CopyLastTranscriptAsync();
    public void StopDictation() => _ = root.Coordinator!.StopAsync();
    public void UseRawNow() => root.Coordinator!.UseRawNow();
    public void CancelDictation() => _ = root.Coordinator!.CancelAsync();
    public void AddCustomVocabulary() => root.ToggleQuickVocabulary();
    public void SelectMicrophone(string? deviceId) => root.Settings!.SelectedMicrophoneUID = deviceId;
    public void MenuOpening() => root.RefreshTrayMenu();
    public void Quit() => app.QuitApp();
}

/// <summary>Demo/snapshot modes: Open and Quit only.</summary>
sealed class UiOnlyTrayCommands(App app) : ITrayCommands
{
    public void OpenRemy() => app.ShowMainWindow();
    public void CopyLastTranscript() { }
    public void StopDictation() { }
    public void UseRawNow() { }
    public void CancelDictation() { }
    public void AddCustomVocabulary() { }
    public void SelectMicrophone(string? deviceId) { }
    public void MenuOpening() { }
    public void Quit() => app.QuitApp();
}
