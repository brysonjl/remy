using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remy.App.Tray;

/// <summary>Implemented by the wiring step; the tray menu only calls these.</summary>
public interface ITrayCommands
{
    void OpenRemy();
    void CopyLastTranscript();
    void StopDictation();
    void UseRawNow();
    void CancelDictation();
    void AddCustomVocabulary();
    /// <summary>null = Automatic.</summary>
    void SelectMicrophone(string? deviceId);
    void Quit();
    /// <summary>Called each time the Microphone submenu / menu opens so the host can refresh <see cref="TrayViewModel.Microphones"/>.</summary>
    void MenuOpening();
}

public sealed record MicrophoneItem(string? Id, string Title, bool IsSelected);

/// <summary>
/// Tray menu state (parity/ui.md section 5). The wiring step updates the flags when the menu opens (<see cref="ITrayCommands.MenuOpening"/>)
/// and whenever session state changes.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject
{
    public ITrayCommands? Commands { get; set; }

    /// <summary>Disabled (not hidden) when there is no last transcript.</summary>
    [ObservableProperty] private bool _canCopyLastTranscript;
    /// <summary>Visible only while recording and not transitioning.</summary>
    [ObservableProperty] private bool _isStopVisible;
    /// <summary>Visible only while processing with a cleanup attempt running and no outcome yet.</summary>
    [ObservableProperty] private bool _isUseRawVisible;
    /// <summary>Visible when cancellation is available.</summary>
    [ObservableProperty] private bool _isCancelVisible;
    /// <summary>Visible only when the cleanup mode supports custom vocabulary.</summary>
    [ObservableProperty] private bool _isAddVocabularyVisible;
    [ObservableProperty] private string _tooltip = "Remy";
    /// <summary>"Automatic" or "Automatic (device name)".</summary>
    [ObservableProperty] private string _automaticTitle = "Automatic";
    /// <summary>True when no specific microphone is selected (or it is unavailable): Automatic gets the check.</summary>
    [ObservableProperty] private bool _isAutomaticSelected = true;

    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];

    public ICommand OpenCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand UseRawCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand VocabularyCommand { get; }
    public ICommand QuitCommand { get; }
    public ICommand SelectMicrophoneCommand { get; }

    public TrayViewModel()
    {
        OpenCommand = new RelayCommand(() => Commands?.OpenRemy());
        CopyCommand = new RelayCommand(() => Commands?.CopyLastTranscript(), () => CanCopyLastTranscript);
        StopCommand = new RelayCommand(() => Commands?.StopDictation());
        UseRawCommand = new RelayCommand(() => Commands?.UseRawNow());
        CancelCommand = new RelayCommand(() => Commands?.CancelDictation());
        VocabularyCommand = new RelayCommand(() => Commands?.AddCustomVocabulary());
        QuitCommand = new RelayCommand(() => Commands?.Quit());
        SelectMicrophoneCommand = new RelayCommand<string?>(id => Commands?.SelectMicrophone(string.IsNullOrEmpty(id) ? null : id));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CanCopyLastTranscript)) ((RelayCommand)CopyCommand).NotifyCanExecuteChanged(); };
    }

    /// <summary>Raised when the menu opens; the host calls <see cref="ITrayCommands.MenuOpening"/> here.</summary>
    public void NotifyMenuOpening() => Commands?.MenuOpening();
}
