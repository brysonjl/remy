using ThemeMode = Remy.Core.Settings.ThemeMode;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remy.App.Overlay;
using Remy.App.Services;
using Remy.Core.Settings;
using VmOverlayState = Remy.App.ViewModels.OverlayState;
using VmOverlaySize = Remy.App.ViewModels.OverlaySize;
using VmTheme = Remy.App.ViewModels.ThemeMode;

namespace Remy.App.Pages;

/// <summary>Text and Overlay page (ui.md 3.4): live pill preview, size, theme, text preview toggle.</summary>
public sealed partial class TextOverlayPageViewModel : ObservableObject, IDisposable
{
    public const string SampleText = "A little more room for your train of thought, so a quick glance shows exactly what was heard.";
    readonly AppServices _s;
    readonly Dispatcher _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    DispatcherTimer? _timer;

    public ViewModels.OverlayViewModel Preview { get; } = new();

    /// <summary>Height of the preview frame: the Large pill's canvas so the layout never jumps.</summary>
    public double PreviewHeight => OverlayMetrics.For(VmOverlaySize.Large).CanvasHeight;

    public TextOverlayPageViewModel(AppServices s)
    {
        _s = s;
        s.Settings.PropertyChanged += OnSettings;
        Sync();
    }

    public void Dispose()
    {
        _s.Settings.PropertyChanged -= OnSettings;
        _timer?.Stop();
    }

    void OnSettings(object? sender, PropertyChangedEventArgs e) => _ui.BeginInvoke(() =>
    {
        Sync();
        OnPropertyChanged(string.Empty);
    });

    void Sync()
    {
        Preview.Size = _s.Settings.OverlaySize switch { OverlaySize.Compact => VmOverlaySize.Compact, OverlaySize.Large => VmOverlaySize.Large, _ => VmOverlaySize.Comfortable };
        Preview.ThemeMode = _s.Settings.OverlayThemeMode switch { ThemeMode.Dark => VmTheme.Dark, ThemeMode.Light => VmTheme.Light, _ => VmTheme.System };
        Preview.ShowTextPreview = _s.Settings.ShowTextPreview;
        if (!IsPlaying) Rest();
    }

    void Rest() => Preview.ShowListening(0, SampleText);

    // ------------------------------------------------------------------ settings

    public static IReadOnlyList<Choice<ThemeMode>> ThemeOptions { get; } =
        [.. new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark }.Select(m => new Choice<ThemeMode>(m, m.DisplayName()))];

    public ThemeMode OverlayTheme { get => _s.Settings.OverlayThemeMode; set => _s.Settings.OverlayThemeMode = value; }
    public bool ShowTextPreview { get => _s.Settings.ShowTextPreview; set => _s.Settings.ShowTextPreview = value; }

    /// <summary>Stepped slider value: 0 compact, 1 comfortable, 2 large.</summary>
    public double SizeIndex
    {
        get => _s.Settings.OverlaySize switch { OverlaySize.Compact => 0, OverlaySize.Large => 2, _ => 1 };
        set => _s.Settings.OverlaySize = value < 0.5 ? OverlaySize.Compact : value < 1.5 ? OverlaySize.Comfortable : OverlaySize.Large;
    }

    public string SizeName => _s.Settings.OverlaySize.DisplayName();
    public bool IsCompact => _s.Settings.OverlaySize == OverlaySize.Compact;
    public bool IsComfortable => _s.Settings.OverlaySize == OverlaySize.Comfortable;
    public bool IsLarge => _s.Settings.OverlaySize == OverlaySize.Large;

    // ------------------------------------------------------------------ replay

    [NotifyPropertyChangedFor(nameof(ReplayLabel), nameof(CanReplay))] [ObservableProperty] private bool _isPlaying;
    public string ReplayLabel => IsPlaying ? "Playing…" : "Replay sample";
    public bool CanReplay => !IsPlaying;

    [RelayCommand]
    void Replay()
    {
        if (IsPlaying) return;
        IsPlaying = true;
        var words = SampleText.Split(' ');
        int total = words.Length * 4, tick = 0;
        int phase = 0, wait = 0; // 0 listening, 1 processing, 2 success
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) =>
        {
            switch (phase)
            {
                case 0:
                    if (tick >= total) { phase = 1; wait = 0; Preview.State = VmOverlayState.Processing; break; }
                    int n = Math.Min(words.Length, tick / 4 + 1);
                    Preview.ShowListening(0.12 + 0.55 * Math.Abs(Math.Sin(tick * 0.7)), string.Join(' ', words.Take(n)));
                    tick++;
                    break;
                case 1:
                    if (++wait * 70 >= 450) { phase = 2; wait = 0; Preview.State = VmOverlayState.Success; }
                    break;
                default:
                    if (++wait * 70 >= 700) { _timer!.Stop(); IsPlaying = false; Rest(); }
                    break;
            }
        };
        Preview.ShowListening(0.12, "");
        _timer.Start();
    }
}
