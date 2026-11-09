using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remy.App.Services;
using Remy.Core.Contracts;
using Remy.Core.Models;

namespace Remy.App.Pages;

/// <summary>Speech Model page (ui.md 3.2, Windows: one engine, Parakeet v2 int8 via sherpa-onnx).</summary>
public sealed partial class SpeechModelPageViewModel : ObservableObject, IDisposable
{
    public const string ModelDetail = "Accuracy-focused English dictation. Uses Parakeet's English-only v2 checkpoint.";
    public const string SizeText = "~661 MB";
    static readonly ModelManifest Manifest = ModelCatalog.ParakeetV2;

    readonly AppServices _s;
    readonly System.Windows.Threading.Dispatcher _ui = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
    CancellationTokenSource? _cts;
    bool _preparing;
    ModelProgress? _progress;

    public SpeechModelPageViewModel(AppServices s)
    {
        _s = s;
        s.Models.Changed += OnModelsChanged;
        s.Dictation.StateChanged += OnDictationChanged;
        Refresh();
    }

    public void Dispose()
    {
        _s.Models.Changed -= OnModelsChanged;
        _s.Dictation.StateChanged -= OnDictationChanged;
    }

    void OnModelsChanged(string id) { if (id == Manifest.Id) _ui.BeginInvoke(Refresh); }
    void OnDictationChanged(object? s, EventArgs e) => _ui.BeginInvoke(Refresh);

    // ------------------------------------------------------------------ state

    [ObservableProperty] private ModelReadiness _readiness = new(ModelReadinessState.NotDownloaded);
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _deleting;

    public string StatusTitle => Readiness.Title;
    public bool IsWorking => Readiness.IsWorking;
    public bool IsReady => Readiness.IsReady;
    public bool IsDownloading => Readiness.State == ModelReadinessState.Downloading;
    public bool IsInstalled => _s.Models.IsInstalled(Manifest);
    public double ProgressValue => Readiness.Progress ?? 0;
    public string ProgressCaption => _progress?.Caption ?? "";
    public string ProgressName => "Parakeet v2 download";

    public bool CanDownload => Readiness.State is ModelReadinessState.NotDownloaded;
    public bool CanPrepare => Readiness.State is ModelReadinessState.Downloaded or ModelReadinessState.Failed && IsInstalled;
    public bool CanDelete => IsInstalled && !IsWorking && !Deleting;
    public bool CanCancel => IsDownloading || Readiness.State == ModelReadinessState.Verifying;
    public bool ShowRemove => IsInstalled || IsReady;
    public bool Busy => IsWorking || Deleting;
    public bool ShowErrorLine => !string.IsNullOrEmpty(Error) || !string.IsNullOrEmpty(Readiness.Detail);
    public string ErrorText => !string.IsNullOrEmpty(Error) ? Error : Readiness.Detail ?? "";

    void Refresh()
    {
        var store = _s.Models.Inspect(Manifest);
        ModelReadiness r;
        if (store.IsWorking) r = store;
        else if (Deleting) r = new(ModelReadinessState.Deleting);
        else if (!_s.Models.IsInstalled(Manifest)) r = new(ModelReadinessState.NotDownloaded);
        else if (_preparing) r = new(ModelReadinessState.Preparing);
        else r = _s.Dictation.SpeechReadiness switch
        {
            EngineReadiness.Ready => new(ModelReadinessState.Ready),
            EngineReadiness.Loading => new(ModelReadinessState.Preparing),
            EngineReadiness.Failed => new(ModelReadinessState.Failed, "The speech model could not be loaded. Try Prepare Model again."),
            _ => new(ModelReadinessState.Downloaded),
        };
        Readiness = r;
        if (!r.IsWorking) _progress = null;
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand]
    async Task Download()
    {
        Error = "";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<ModelProgress>(p => { _progress = p; _ui.BeginInvoke(Refresh); });
        try
        {
            await _s.Models.InstallAsync(Manifest, progress, ct);
        }
        catch (OperationCanceledException) { _progress = null; Refresh(); return; }
        catch (Exception e) { Error = e.Message; _progress = null; Refresh(); return; }
        await PrepareCore();
    }

    [RelayCommand]
    void CancelDownload() => _cts?.Cancel();

    [RelayCommand]
    Task Prepare()
    {
        Error = "";
        return PrepareCore();
    }

    async Task PrepareCore()
    {
        _preparing = true; Refresh();
        try { await _s.Dictation.PrepareSpeechModelAsync(); }
        catch (Exception e) { Error = e.Message; }
        finally { _preparing = false; Refresh(); }
    }

    [RelayCommand]
    async Task Delete()
    {
        if (!ConfirmDialog.Ask("Delete Parakeet v2?",
                $"This will remove the Parakeet v2 speech model ({SizeText}). You can download it again later.",
                "Delete", destructive: true)) return;
        Error = "";
        Deleting = true; Refresh();
        try
        {
            await _s.Dictation.UnloadSpeechModelAsync();
            await _s.Models.DeleteAsync(Manifest);
        }
        catch (ModelStoreException e) when (e.Kind is ModelStoreError.Busy or ModelStoreError.Leased)
        {
            Error = "The selected model is busy or has changed. Review it and try deleting again.";
        }
        catch (Exception e) { Error = e.Message; }
        finally { Deleting = false; Refresh(); }
    }
}
