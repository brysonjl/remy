using System.IO;
using System.Windows;
using Remy.App.Services;
using Remy.App.Tray;
using Remy.App.ViewModels;
using Remy.App.Wiring;
using Remy.Core.Diagnostics;
using Remy.Core.Settings;
using Remy.Core.Storage;
using AppThemeMode = Remy.App.ViewModels.ThemeMode;

namespace Remy.App;

public partial class App : Application
{
    const string MutexName = @"Local\io.github.brysonjl.remy";
    const string ActivateEventName = @"Local\io.github.brysonjl.remy.activate";

    Mutex? _mutex;
    EventWaitHandle? _activate;
    TrayHost? _tray;
    CompositionRoot? _root;
    int _quitting;
    readonly CancellationTokenSource _cts = new();

    public CompositionRoot Root => _root!;
    public static string[] Args { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = e.Args;
        bool demo = e.Args.Contains("--demo"), snap = e.Args.Contains("--snap");

        // Single instance: a second launch signals the first to show its main window, then exits.
        if (!demo && !snap)
        {
            _mutex = new Mutex(true, MutexName, out bool first);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            if (!first) { _activate.Set(); Shutdown(); return; }
            var t = new Thread(() =>
            {
                while (WaitHandle.WaitAny([_activate, _cts.Token.WaitHandle]) == 0)
                    Dispatcher.BeginInvoke(ShowMainWindow);
            }) { IsBackground = true, Name = "RemyActivation" };
            t.Start();
        }

        if (demo || snap) { StartUiOnly(e, demo, snap); return; }

        // ---- 1. settings and the configuration-version gate (before any window)
        bool firstRun = !File.Exists(RemyPaths.SettingsFile);
        RemyPaths.EnsureConfigDirectory();
        RemyPaths.EnsureLogsDirectory();
        var store = new SettingsStore();
        var loaded = store.Load();
        if (loaded.Gate.IsBlocking)
        {
            MessageBox.Show(loaded.Gate.BlockingMessage, ConfigurationVersion.BlockingDialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        RemyLog.Info("App", "startup");

        // ---- 2. theme, then the composition root (services, coordinator, hotkeys, pages, tray)
        ThemeManager.Initialize(loaded.Settings.ThemeMode switch
        {
            Remy.Core.Settings.ThemeMode.Dark => AppThemeMode.Dark,
            Remy.Core.Settings.ThemeMode.Light => AppThemeMode.Light,
            _ => AppThemeMode.System,
        });

        _root = CompositionRoot.Create(this, store, loaded.Settings);
        _tray = new TrayHost(_root.TrayVm);

        // Main window on first run or when setup is needed; otherwise the app lives in the tray (a second launch opens it).
        bool setupNeeded = _root.Shell.AttentionIssues.Any(i => i.IsBlocking);
        if (firstRun || setupNeeded || e.Args.Contains("--show")) _root.Main.Show();

        DevHooks.StartDictationTrigger(_root.Coordinator!, e.Args);
    }

    void StartUiOnly(StartupEventArgs e, bool demo, bool snap)
    {
        var mode = AppThemeMode.System;
        var themeArg = e.Args.FirstOrDefault(a => a.StartsWith("--theme="))?[8..];
        if (themeArg == "dark") mode = AppThemeMode.Dark; else if (themeArg == "light") mode = AppThemeMode.Light;
        ThemeManager.Initialize(mode);
        _root = CompositionRoot.CreateUiOnly(this);
        _tray = new TrayHost(_root.TrayVm);
        _root.Main.Show();
        if (demo) new DemoRunner(_root).Start();
        if (snap) _ = new SnapshotRunner(_root, e.Args[Array.IndexOf(e.Args, "--snap") + 1]).RunAsync();
    }

    public void ShowMainWindow() => _root?.Main.ShowAndActivate();

    /// <summary>Applies the "App appears in" policy (Tray only vs Taskbar and tray).</summary>
    public void ApplyAppearance(AppAppearance a) { if (_root != null) _root.Main.Appearance = a; }

    /// <summary>Graceful quit: stop the coordinator (recovery audio preserved, mute restored), flush settings, then exit.</summary>
    public void QuitApp()
    {
        if (Interlocked.Exchange(ref _quitting, 1) == 1) return;
        _ = QuitAsync();
    }

    async Task QuitAsync()
    {
        try { if (_root != null) await _root.ShutdownAsync(); }
        catch (Exception ex) { RemyLog.Error("App", "quit failed", ex); }
        _cts.Cancel();
        if (_root != null) { _root.Main.AllowClose = true; _root.Overlay.Close(); _root.Main.Close(); }
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
