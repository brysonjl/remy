using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Remy.App.ViewModels;
using Remy.Core.History;
using Remy.Core.Models;
using Remy.Core.Session;
using Remy.Core.Settings;

namespace Remy.App.Wiring;

/// <summary>Overlay + navigation sink for the coordinator. Marshals onto the Dispatcher in order; any Show supersedes a pending hide.</summary>
public sealed class OverlayPresenter(CompositionRoot root, Dispatcher dispatcher) : IDictationPresenter
{
    int _hideToken;
    OverlayViewModel Vm => root.OverlayVm;

    void Run(Action a) => dispatcher.BeginInvoke(a);
    void Show(Action a) { Interlocked.Increment(ref _hideToken); Run(a); }

    public void BeginSession() { Interlocked.Increment(ref _hideToken); Run(() => root.Overlay.BeginSession()); }
    public void ShowListening(double level, string transcript) => Show(() => Vm.ShowListening(level, transcript));
    public void ShowProcessing() => Show(() => Vm.State = OverlayState.Processing);
    public void ShowCleaning() => Show(() => Vm.State = OverlayState.Cleaning);
    public void ShowPreparing(string name) => Show(() => { Vm.PreparingText = $"Loading {name}…"; Vm.State = OverlayState.Preparing; });
    public void ShowSuccess() => Show(() => Vm.State = OverlayState.Success);
    public void ShowCopyOnly(string hint) => Show(() => { Vm.CopyHint = hint; Vm.State = OverlayState.CopyOnly; });
    public void ShowBusy(string text) => Show(() => Vm.ShowBusy(text));
    public void ShowError(string title, string detail) => Show(() => Vm.ShowError(title, detail));
    public void ShowHoldToCancel(double progress) => Show(() => Vm.ShowHoldToCancel(progress));

    public void Hide(TimeSpan delay)
    {
        int token = Interlocked.Increment(ref _hideToken);
        if (delay <= TimeSpan.Zero) { Run(Vm.Hide); return; }
        _ = Task.Delay(delay).ContinueWith(_ => { if (token == Volatile.Read(ref _hideToken)) Run(Vm.Hide); });
    }

    public void ShowMainWindow() => Run(() => ((App)Application.Current).ShowMainWindow());
    public void NavigateToHistory() => Run(() => root.Shell.Navigate(AppPage.History));
}

/// <summary>ModelStore seen by the coordinator.</summary>
public sealed class ModelsAdapter(ModelStore store) : ISessionModels
{
    public string SpeechDisplayName => ModelCatalog.ParakeetV2.DisplayName;
    public bool IsSpeechInstalled => store.IsInstalled(ModelCatalog.ParakeetV2);
    public bool IsSpeechDownloading => store.Inspect(ModelCatalog.ParakeetV2).State is ModelReadinessState.Downloading or ModelReadinessState.Verifying;
    public bool IsS1MiniInstalled => store.IsInstalled(ModelCatalog.S1Mini);

    public async Task<ModelLeaseHandle> AcquireSpeechAsync(CancellationToken ct)
    {
        var l = await store.AcquireLeaseAsync(ModelCatalog.ParakeetV2, false, ct).ConfigureAwait(false);
        return new ModelLeaseHandle(l.Directory, l);
    }

    public async Task<ModelLeaseHandle> AcquireS1MiniAsync(CancellationToken ct)
    {
        var l = await store.AcquireLeaseAsync(ModelCatalog.S1Mini, false, ct).ConfigureAwait(false);
        return new ModelLeaseHandle(l.Directory, l);
    }
}

/// <summary>Switches between the SQLite repository and the ephemeral one when "Save new dictations" is toggled.
/// A database that could not be opened is never silently history-off: it stays <see cref="UnavailableHistoryRepository"/>.</summary>
public sealed class HistoryHost(RemySettings settings, IHistoryRepository persistent)
{
    readonly IHistoryRepository _ephemeral = new EphemeralHistoryRepository();
    public IHistoryRepository Persistent => persistent;
    public IHistoryRepository Current => settings.RemyHistoryEnabled ? persistent : _ephemeral;

    public static async Task<IHistoryRepository> OpenAsync(string path)
    {
        try { return await SqliteHistoryRepository.OpenAsync(path).ConfigureAwait(false); }
        catch (HistoryStorageException ex) { return new UnavailableHistoryRepository(ex.Message); }
    }
}

public static class MicrophonePrivacy
{
    /// <summary>True when the OS microphone privacy setting denies desktop apps (ConsentStore).</summary>
    public static bool IsDenied()
    {
        try
        {
            const string path = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var k = hive.OpenSubKey(path);
                if (k?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    public static void OpenSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch { }
    }
}
