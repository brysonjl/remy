using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remy.App.Services;
using Remy.Core.Hotkeys;
using Remy.Core.Settings;
using Remy.Core.Transforms;

namespace Remy.App.Pages;

/// <summary>ShortcutRecorderView state: shows Not set / keycaps / "Press any key combo…" and the matching buttons.</summary>
public sealed partial class RecorderViewModel(ShortcutsPageViewModel owner, string name, bool allowEscape) : ObservableObject
{
    public string Name { get; } = name;
    public bool AllowEscape { get; } = allowEscape;

    HotkeyBinding _value = HotkeyBinding.Empty();
    public HotkeyBinding Value
    {
        get => _value;
        set
        {
            _value = value;
            Keycaps = Tokenize(value.DisplayName);
            OnPropertyChanged(nameof(Value)); OnPropertyChanged(nameof(IsSet)); OnPropertyChanged(nameof(Keycaps));
            OnPropertyChanged(nameof(ShowSet)); OnPropertyChanged(nameof(ShowNotSet));
        }
    }

    public IReadOnlyList<string> Keycaps { get; private set; } = [];
    public bool IsSet => Value.HasBinding;

    bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        set { if (SetProperty(ref _isRecording, value)) { OnPropertyChanged(nameof(ShowSet)); OnPropertyChanged(nameof(ShowNotSet)); } }
    }
    public bool ShowSet => IsSet && !IsRecording;
    public bool ShowNotSet => !IsSet && !IsRecording;
    /// <summary>False while another recorder on the page is active.</summary>
    public bool Idle => !owner.AnyRecording;

    public string RecordName => $"Record {Name}";
    public string ChangeName => $"Change {Name}";
    public string ClearName => $"Clear {Name}";
    public string CancelName => $"Cancel recording {Name}";

    public void NotifyIdle() => OnPropertyChanged(nameof(Idle));

    [RelayCommand] Task Begin() => owner.BeginRecordingAsync(this);
    [RelayCommand] void Stop() => owner.StopRecording();
    [RelayCommand] void Clear() => owner.ClearRecorder(this);

    /// <summary>"Ctrl+Alt+Space" -> ["Ctrl","Alt","Space"]; side-aware "LCtrl" -> "L Ctrl" (one keycap).</summary>
    public static IReadOnlyList<string> Tokenize(string display)
    {
        if (string.IsNullOrEmpty(display)) return [];
        var parts = display.EndsWith("++", StringComparison.Ordinal)
            ? display[..^2].Split('+').Append("+").ToArray()
            : display.Split('+', StringSplitOptions.RemoveEmptyEntries);
        return [.. parts.Select(p => Regex.Replace(p, "^([LR])(Ctrl|Alt|Shift|Win)$", "$1 $2"))];
    }
}

public sealed partial class BindingRowViewModel : ObservableObject
{
    readonly ShortcutsPageViewModel _owner;
    public BindingRowViewModel(ShortcutsPageViewModel owner, HotkeyBinding binding, int index)
    {
        _owner = owner;
        Index = index;
        Recorder = new RecorderViewModel(owner, $"shortcut {index}", allowEscape: false) { Value = binding };
        Id = binding.Id;
        _mode = binding.Mode;
    }

    public Guid Id { get; }
    public int Index { get; }
    public RecorderViewModel Recorder { get; }
    public HotkeyBinding Binding => Recorder.Value with { Mode = _mode };
    public string RemoveName => $"Remove shortcut {Index}";
    public string ModeName => $"Activation mode for shortcut {Index}";

    HotkeyMode _mode;
    public HotkeyMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Caption));
            _owner.Commit();
        }
    }

    public string Title => Mode == HotkeyMode.HandsFreeToggle ? "Toggle dictation" : "Hold to record";
    public string Caption => Mode == HotkeyMode.HandsFreeToggle ? "Tap once to start, tap again to stop" : "Hold the keys down while you speak";

    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _warningText = "";
    public bool Idle => !_owner.AnyRecording;
    public bool CanRemove => _owner.Rows.Count > 1 && Idle;
    public bool CanRemoveVisible => _owner.Rows.Count > 1;
    public void NotifyIdle() { OnPropertyChanged(nameof(Idle)); OnPropertyChanged(nameof(CanRemove)); OnPropertyChanged(nameof(CanRemoveVisible)); Recorder.NotifyIdle(); }

    [RelayCommand] void Remove() => _owner.Remove(this);
}

public sealed partial class ShortcutsPageViewModel : ObservableObject, IDisposable
{
    readonly AppServices _s;
    readonly System.Windows.Threading.Dispatcher _ui = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
    CancellationTokenSource? _cts;
    bool _writing;

    public static IReadOnlyList<Choice<HotkeyMode>> ModeOptions { get; } =
        [new(HotkeyMode.HoldToRecord, HotkeyMode.HoldToRecord.DisplayName()), new(HotkeyMode.HandsFreeToggle, HotkeyMode.HandsFreeToggle.DisplayName())];

    public ObservableCollection<BindingRowViewModel> Rows { get; } = [];
    public RecorderViewModel CancelRecorder { get; }

    public ShortcutsPageViewModel(AppServices s)
    {
        _s = s;
        CancelRecorder = new RecorderViewModel(this, "Cancel shortcut", allowEscape: true);
        s.Settings.PropertyChanged += OnSettings;
        Load();
    }

    public void Dispose() => _s.Settings.PropertyChanged -= OnSettings;

    void OnSettings(object? sender, PropertyChangedEventArgs e) => _ui.BeginInvoke(() =>
    {
        if (_writing) return;
        if (e.PropertyName is nameof(RemySettings.HotkeyBindings) or nameof(RemySettings.CancelShortcut) && !AnyRecording) Load();
        OnPropertyChanged(nameof(HoldToCancel)); OnPropertyChanged(nameof(PreserveCancelledSessions));
    });

    // ------------------------------------------------------------------ state

    [ObservableProperty] private bool _anyRecording;
    [ObservableProperty] private string _cancelErrorText = "";
    [ObservableProperty] private string _cancelWarningText = "";
    public bool Idle => !AnyRecording;
    public bool CanAdd => Rows.Count < HotkeyDefaults.MaxRecordingBindings && !AnyRecording;
    public bool ShowAdd => Rows.Count < HotkeyDefaults.MaxRecordingBindings;

    partial void OnAnyRecordingChanged(bool value)
    {
        foreach (var r in Rows) r.NotifyIdle();
        CancelRecorder.NotifyIdle();
        OnPropertyChanged(nameof(Idle)); OnPropertyChanged(nameof(CanAdd));
    }

    void Load()
    {
        Rows.Clear();
        int i = 1;
        foreach (var b in _s.Settings.HotkeyBindings) Rows.Add(new BindingRowViewModel(this, b, i++));
        CancelRecorder.Value = _s.Settings.CancelShortcut;
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(ShowAdd));
        foreach (var r in Rows) r.NotifyIdle();
        ValidateAll();
    }

    /// <summary>Writes rows to Settings and tells the coordinator to rebuild the hook configuration.</summary>
    public void Commit()
    {
        _writing = true;
        try { _s.Settings.HotkeyBindings = Rows.Select(r => r.Binding).ToList(); }
        finally { _writing = false; }
        _s.Dictation.ApplySettingsChange(nameof(RemySettings.HotkeyBindings));
        ValidateAll();
    }

    void CommitCancel()
    {
        _writing = true;
        try { _s.Settings.CancelShortcut = CancelRecorder.Value; }
        finally { _writing = false; }
        _s.Dictation.ApplySettingsChange(nameof(RemySettings.CancelShortcut));
        ValidateAll();
    }

    /// <summary>Recomputes the persistent conflict lines (errors for blocking, warnings otherwise).</summary>
    void ValidateAll()
    {
        var cancel = CancelRecorder.Value.HasBinding ? CancelRecorder.Value : null;
        foreach (var row in Rows)
        {
            var others = Rows.Where(r => r != row).Select(r => r.Binding);
            var c = ConflictDetector.CheckRecording(row.Binding, others, cancel);
            row.ErrorText = c is { Blocking: true } ? c.Message : "";
            row.WarningText = c is { Blocking: false } ? c.Message : "";
        }
        var cc = ConflictDetector.CheckCancel(CancelRecorder.Value, Rows.Select(r => r.Binding));
        CancelErrorText = cc?.Message ?? "";
    }

    // ------------------------------------------------------------------ recording

    public async Task BeginRecordingAsync(RecorderViewModel rec)
    {
        if (AnyRecording) return;
        _cts = new CancellationTokenSource();
        rec.IsRecording = true; AnyRecording = true;
        var row = Rows.FirstOrDefault(r => r.Recorder == rec);
        if (row is not null) row.ErrorText = row.WarningText = "";
        else { CancelErrorText = CancelWarningText = ""; }
        HotkeyBinding? captured = null;
        try { captured = await _s.Hotkeys.RecordShortcutAsync(rec.AllowEscape, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            rec.IsRecording = false; AnyRecording = false;
            _cts.Dispose(); _cts = null;
        }
        if (captured is null || !captured.HasBinding) { ValidateAll(); return; }

        if (row is not null)
        {
            var cand = HotkeyRules.Canonicalize(captured with { Id = row.Id, Mode = row.Mode });
            var cancel = CancelRecorder.Value.HasBinding ? CancelRecorder.Value : null;
            var c = ConflictDetector.CheckRecording(cand, Rows.Where(r => r != row).Select(r => r.Binding), cancel);
            if (c is { Blocking: true }) { ValidateAll(); row.ErrorText = c.Message; return; }
            if (TransformShortcutRules.TransformUsingShortcut(cand, _s.Settings.TextTransforms) is { } used)
            { ValidateAll(); row.ErrorText = TransformShortcutRules.UsedByTransformMessage(used.DisplayName); return; }
            rec.Value = cand;
            Commit();
        }
        else
        {
            var cand = HotkeyRules.Canonicalize(captured with { Id = CancelRecorder.Value.Id, Mode = HotkeyMode.HandsFreeToggle });
            // order: recording overlap, transform overlap, then the system conflict
            var recordings = Rows.Select(r => r.Binding).ToList();
            HotkeyConflict? c = recordings.Any(r => ConflictDetector.RecordingOverlapsCancel(r, cand))
                ? new HotkeyConflict(ConflictDetector.CancelVsRecordingMessage, true) : null;
            if (c is null && TransformShortcutRules.TransformUsingShortcut(cand, _s.Settings.TextTransforms) is { } used)
                c = new HotkeyConflict(TransformShortcutRules.CancelOverlapsTransformMessage(used.DisplayName), true);
            c ??= ConflictDetector.CheckCancel(cand, recordings);
            if (c is { Blocking: true }) { ValidateAll(); CancelErrorText = c.Message; return; }
            rec.Value = cand;
            CommitCancel();
        }
    }

    public void StopRecording() => _cts?.Cancel();

    public void ClearRecorder(RecorderViewModel rec)
    {
        if (AnyRecording) return;
        var row = Rows.FirstOrDefault(r => r.Recorder == rec);
        if (row is not null)
        {
            rec.Value = HotkeyBinding.Empty(row.Id, row.Mode);
            Commit();
        }
        else
        {
            rec.Value = HotkeyBinding.Empty(mode: HotkeyMode.HandsFreeToggle);
            CommitCancel();
        }
    }

    [RelayCommand]
    void AddShortcut()
    {
        if (!CanAdd) return;
        Rows.Add(new BindingRowViewModel(this, HotkeyBinding.Empty(), Rows.Count + 1));
        foreach (var r in Rows) r.NotifyIdle();
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(ShowAdd));
        Commit();
    }

    public void Remove(BindingRowViewModel row)
    {
        if (Rows.Count <= 1 || AnyRecording) return;
        Rows.Remove(row);
        // Renumber ("shortcut N" accessible names) by rebuilding from the written list.
        Commit();
        Load();
    }

    // ------------------------------------------------------------------ cancellation toggles

    public bool HoldToCancel
    {
        get => _s.Settings.HoldToCancel;
        set { _s.Settings.HoldToCancel = value; _s.Dictation.ApplySettingsChange(nameof(RemySettings.HoldToCancel)); OnPropertyChanged(); }
    }

    public bool PreserveCancelledSessions
    {
        get => _s.Settings.PreserveCancelledSessions;
        set { _s.Settings.PreserveCancelledSessions = value; OnPropertyChanged(); }
    }
}
