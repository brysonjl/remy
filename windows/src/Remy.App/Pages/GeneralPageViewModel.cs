using ThemeMode = Remy.Core.Settings.ThemeMode;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Remy.App.Services;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.App.Pages;

/// <summary>One row of the Sound effects block (ui.md 3.8): picker, Import audio…, Preview.</summary>
public sealed partial class SoundPickerViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly Func<string> _get;
    readonly Action<string> _set;
    readonly string _title;

    public SoundPickerViewModel(AppServices s, string title, Func<string> get, Action<string> set)
    {
        _s = s; _title = title; _get = get; _set = set;
        Title = title;
        Rebuild();
    }

    public string Title { get; }
    public string AutomationName => $"{_title} sound";
    public string ImportName => $"Import {_title.ToLowerInvariant()} sound";
    public string PreviewName => $"Preview {_title.ToLowerInvariant()} sound";

    public ObservableCollection<Choice<string>> Options { get; } = [];
    [ObservableProperty] private string _message = "";
    [NotifyPropertyChangedFor(nameof(CanPreview), nameof(CanImport))] [ObservableProperty] private bool _importing;

    public bool CanPreview => !Importing && SelectedId != "none";
    public bool CanImport => !Importing;

    public string SelectedId
    {
        get => _get();
        set
        {
            if (value is null || value == _get()) return;
            _set(value);
            Message = "";
            Rebuild();
            OnPropertyChanged(nameof(CanPreview));
        }
    }

    public void Rebuild()
    {
        string cur = _get();
        Options.Clear();
        Options.Add(new("none", "None"));
        foreach (var o in _s.Sounds.BuiltInSounds) Options.Add(new(o.Id, o.DisplayName));
        if (cur != "none" && !Options.Any(o => o.Value == cur))
            Options.Add(new(cur, cur.StartsWith("custom:", StringComparison.Ordinal) ? "Custom audio" : "Unavailable sound"));
        OnPropertyChanged(nameof(SelectedId));
        OnPropertyChanged(nameof(CanPreview));
    }

    [RelayCommand]
    void Preview() => _s.Sounds.Play(SelectedId, _s.Settings.SoundEffectsVolume);

    [RelayCommand]
    void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a short sound effect",
            Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;
        Importing = true;
        try
        {
            string id = _s.Sounds.ImportCustomSound(dlg.FileName);
            SelectedId = id;
            Message = "Sound imported. Use Preview to listen.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Message = string.IsNullOrWhiteSpace(e.Message)
                ? "Audio could not be imported. Choose a supported local clip up to 10 seconds and 10 MiB."
                : e.Message;
        }
        finally { Importing = false; }
    }
}

public sealed partial class GeneralPageViewModel : ObservableObject, IDisposable
{
    public const string AutomaticId = "";
    readonly AppServices _s;
    readonly System.Windows.Threading.Dispatcher _ui = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public GeneralPageViewModel(AppServices s)
    {
        _s = s;
        StartSound = new SoundPickerViewModel(s, "Recording started", () => s.Settings.RemyDictationStartSound, v => s.Settings.RemyDictationStartSound = v);
        CompletionSound = new SoundPickerViewModel(s, "Dictation completed", () => s.Settings.RemyDictationCompletionSound, v => s.Settings.RemyDictationCompletionSound = v);
        s.Settings.PropertyChanged += OnSettings;
        s.Audio.DevicesChanged += OnDevices;
        RebuildMicrophones();
    }

    public void Dispose()
    {
        _s.Settings.PropertyChanged -= OnSettings;
        _s.Audio.DevicesChanged -= OnDevices;
    }

    void OnSettings(object? sender, PropertyChangedEventArgs e) => _ui.BeginInvoke(() =>
    {
        if (e.PropertyName == nameof(RemySettings.SelectedMicrophoneUID)) RebuildMicrophones();
        if (e.PropertyName is nameof(RemySettings.RemyDictationStartSound)) StartSound.Rebuild();
        if (e.PropertyName is nameof(RemySettings.RemyDictationCompletionSound)) CompletionSound.Rebuild();
        OnPropertyChanged(string.Empty);
    });

    void OnDevices(object? sender, EventArgs e) => _ui.BeginInvoke(RebuildMicrophones);

    // ---- Startup
    [ObservableProperty] private string _launchAtLoginError = "";

    public bool LaunchAtLogin
    {
        get => _s.Settings.LaunchAtLogin;
        set
        {
            if (value == _s.Settings.LaunchAtLogin) return;
            try
            {
                _s.Autostart.SetEnabled(value);
                _s.Settings.LaunchAtLogin = value;
                LaunchAtLoginError = value && !_s.Autostart.IsEnabled ? "Launch at Login is not active. Turn it off and on to retry." : "";
            }
            catch (InvalidOperationException e)
            {
                LaunchAtLoginError = $"Could not update Launch at Login. {e.Message}";
            }
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<AppAppearanceMode>> AppearanceOptions { get; } =
        [.. new[] { AppAppearanceMode.MenuBarOnly, AppAppearanceMode.DockAndMenuBar }.Select(m => new Choice<AppAppearanceMode>(m, m.DisplayName()))];
    public AppAppearanceMode AppAppearance { get => _s.Settings.AppAppearanceMode; set => _s.Settings.AppAppearanceMode = value; }

    public bool PrewarmEnginesAtStartup
    {
        get => _s.Settings.PrewarmEnginesAtStartup;
        set
        {
            _s.Settings.PrewarmEnginesAtStartup = value;
            if (value) _ = _s.Dictation.PrepareSpeechModelAsync();
        }
    }

    // ---- Display
    public IReadOnlyList<Choice<ThemeMode>> ThemeOptions { get; } =
        [.. new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark }.Select(m => new Choice<ThemeMode>(m, m.DisplayName()))];
    public ThemeMode Theme { get => _s.Settings.ThemeMode; set => _s.Settings.ThemeMode = value; }

    // ---- Text destination
    public IReadOnlyList<Choice<RemyDictationDestination>> DestinationOptions { get; } =
        [.. new[] { RemyDictationDestination.OriginalField, RemyDictationDestination.CurrentField }.Select(m => new Choice<RemyDictationDestination>(m, m.DisplayName()))];
    public RemyDictationDestination Destination { get => _s.Settings.RemyDictationDestination; set => _s.Settings.RemyDictationDestination = value; }

    public string DestinationHint => (Destination == RemyDictationDestination.OriginalField
        ? "Returns to the app and field where dictation started. If that field cannot be restored, copy the result and paste it manually."
        : "Chooses your focused app and field after cleanup finishes. If that field is unavailable, copy the result and paste it manually.")
        + " Changes apply to your next dictation.";

    // ---- Audio
    public ObservableCollection<Choice<string>> Microphones { get; } = [];

    void RebuildMicrophones()
    {
        var devices = _s.Audio.GetInputDevices();
        string saved = _s.Settings.SelectedMicrophoneUID ?? "";
        bool specific = saved.Length > 0 && devices.Any(d => d.Id == saved);
        var def = devices.FirstOrDefault(d => d.IsDefault);
        string auto = !specific && def is not null ? $"Automatic ({def.Name})" : "Automatic";
        Microphones.Clear();
        Microphones.Add(new(AutomaticId, auto));
        foreach (var d in devices) Microphones.Add(new(d.Id, d.Name));
        OnPropertyChanged(nameof(MicrophoneId));
    }

    public string MicrophoneId
    {
        get
        {
            string saved = _s.Settings.SelectedMicrophoneUID ?? "";
            return saved.Length > 0 && Microphones.Any(m => m.Value == saved) ? saved : AutomaticId;
        }
        set
        {
            if (value is null) return;
            _s.Settings.SelectedMicrophoneUID = value.Length == 0 ? null : value;
            _s.Dictation.ApplySettingsChange(nameof(RemySettings.SelectedMicrophoneUID));
        }
    }

    public bool BoostMicrophone { get => _s.Settings.BoostMicrophoneVolumeEnabled; set => _s.Settings.BoostMicrophoneVolumeEnabled = value; }
    public bool MuteSystemAudio { get => _s.Settings.MuteSystemAudioDuringRecordingEnabled; set => _s.Settings.MuteSystemAudioDuringRecordingEnabled = value; }
    public bool SoundEffects { get => _s.Settings.SoundEffectsEnabled; set => _s.Settings.SoundEffectsEnabled = value; }
    public double SoundVolume { get => _s.Settings.SoundEffectsVolume; set => _s.Settings.SoundEffectsVolume = (float)Math.Clamp(value, 0, 1); }

    public SoundPickerViewModel StartSound { get; }
    public SoundPickerViewModel CompletionSound { get; }
}
