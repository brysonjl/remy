using ThemeMode = Remy.App.ViewModels.ThemeMode;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Remy.App.Controls;
using Remy.App.Services;
using Remy.App.ViewModels;

namespace Remy.App.Overlay;

/// <summary>The pill itself (no window). Reused by the Text and Overlay settings preview later.</summary>
public partial class OverlayPill : UserControl
{
    OverlayViewModel? _vm;
    OverlayMetrics _m = OverlayMetrics.For(OverlaySize.Comfortable);
    FrameworkElement? _active;
    string _modeKey = "";
    static readonly ImageBrush MarkBrush = MakeMark();

    public OverlayPill()
    {
        InitializeComponent();
        HeaderMark.OpacityMask = MarkBrush; WaveMark.OpacityMask = MarkBrush;
        ApplyTheme(false);
        ThemeManager.SystemThemeChanged += () => { if (_vm?.ThemeMode == ThemeMode.System) ApplyTheme(ResolveDark()); };
    }

    static ImageBrush MakeMark()
    {
        var b = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Assets/menubar-54.png"))) { Stretch = Stretch.Uniform };
        b.Freeze(); return b;
    }

    public void Attach(OverlayViewModel vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = vm; vm.PropertyChanged += OnVm;
        Refresh(animate: false);
    }

    bool ResolveDark() => _vm?.ThemeMode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => !ThemeManager.SystemIsLight() };

    public void ApplyTheme(bool dark)
    {
        OverlayPalette.Apply(Resources, dark);
        PillShadow.Opacity = dark ? 0.34 : 0.14;
    }

    void OnVm(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.ThemeMode)) ApplyTheme(ResolveDark());
        if (e.PropertyName == nameof(OverlayViewModel.Level)) { var l = _vm!.Level; WaveA.Level = l; WaveB.Level = l; return; }
        if (e.PropertyName == nameof(OverlayViewModel.HoldProgress)) { UpdateCancel(); return; }
        Refresh(animate: true);
    }

    static string Tail(string text, int limit)
    {
        var t = (text ?? "").Trim();
        return t.Length <= limit ? t : "…" + t[^limit..].TrimStart();
    }

    void UpdateCancel() { CancelFill.Width = Math.Max(0, Pill.Width - 2.5 - 32) * (_vm?.HoldProgress ?? 0); }

    public void Refresh(bool animate)
    {
        if (_vm == null) return;
        var vm = _vm;
        _m = OverlayMetrics.For(vm.Size);
        var m = _m;
        ApplyTheme(ResolveDark());
        WaveA.Level = vm.Level; WaveB.Level = vm.Level;

        FrameworkElement target; double w, radius = 14; double? h; double minH = 0; Thickness pad = new(0);
        string key;
        switch (vm.State)
        {
            case OverlayState.Listening when vm.ShowTextPreview:
                target = ListenText; w = m.Width; h = null; minH = m.MinHeight; radius = m.CornerRadius; pad = new Thickness(m.HPad, m.VPad, m.HPad, m.VPad); key = "lt" + vm.Size; break;
            case OverlayState.Listening:
                target = ListenWave; w = m.WaveformOnlyWidth; h = m.WaveformOnlyHeight; pad = new Thickness(m.HPad, 0, m.HPad, 0); key = "lw" + vm.Size; break;
            case OverlayState.Processing: target = ProcessingBadge; w = 31; h = 31; radius = 15.5; key = "p"; break;
            case OverlayState.Success: target = SuccessBadge; w = 31; h = 31; radius = 15.5; key = "s"; break;
            case OverlayState.CopyOnly: target = CopyPanel; w = 144; h = 38; key = "c"; break;
            case OverlayState.Error: target = ErrorPanel; w = Math.Min(m.Width + 20, 300); h = 54; key = "e"; break;
            case OverlayState.Cleaning: case OverlayState.Preparing: case OverlayState.Transforming: target = SpinnerText; w = Math.Min(m.Width, 240); h = 38; key = "sp" + vm.State; break;
            case OverlayState.Busy: target = SpinnerText; w = Math.Min(m.Width, 260); h = 44; key = "b"; break;
            case OverlayState.HoldToCancel: target = CancelPanel; w = 169; h = 42; key = "h"; break;
            default: return; // Hidden: window handles fade-out, content stays as is
        }

        // content
        CopyText.Text = vm.CopyHint;
        ErrorTitle.Text = vm.ErrorMessage; ErrorDetailText.Text = vm.ErrorDetail;
        ErrorDetailText.Visibility = string.IsNullOrEmpty(vm.ErrorDetail) ? Visibility.Collapsed : Visibility.Visible;
        SpinnerLabel.Text = vm.State switch { OverlayState.Cleaning => vm.CleaningText, OverlayState.Preparing => vm.PreparingText, OverlayState.Transforming => "Transforming…", _ => vm.NoticeText };
        SpinnerLabel.MaxWidth = Math.Max(0, w - 28 - 26);
        bool wrap = vm.State == OverlayState.Busy;
        SpinnerLabel.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap; SpinnerLabel.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        ErrorTitle.MaxWidth = ErrorDetailText.MaxWidth = Math.Max(0, w - 18 - 20);
        if (vm.State == OverlayState.Listening)
        {
            HeaderRow.Height = m.HeaderHeight;
            HeaderMark.Width = HeaderMark.Height = m.HeaderHeight;
            HeaderLabel.FontSize = m.HeaderFont;
            WaveA.Height = m.WaveformHeight; WaveA.Margin = new Thickness(0, m.TextWaveSpacing, 0, 0);
            WaveB.Height = m.WaveformHeight;
            TextViewport.Margin = new Thickness(0, m.HeaderSpacing, 0, 0);
            TextViewport.MaxTextHeight = m.MaxTextHeight + m.LineSpacing; TextViewport.PeekHeight = m.PeekHeight;
            var t = Tail(vm.TranscriptText, m.CharacterLimit);
            var empty = t.Length == 0;
            TranscriptBlock.Text = empty ? "Listening…" : t;
            TranscriptBlock.SetResourceReference(TextBlock.ForegroundProperty, empty ? "Ov.Secondary" : "Ov.Text");
            TranscriptBlock.FontFamily = OverlayMetrics.UiFont; TranscriptBlock.FontSize = m.FontSize;
            TranscriptBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            TranscriptBlock.LineHeight = m.LineHeight + m.LineSpacing;
            AutomationProperties.SetName(this, empty ? "Listening" : "Live transcript");
        }

        if (vm.State == OverlayState.Transforming)
            AutomationProperties.SetName(this, $"Transforming with {vm.TransformName}. Press the cancel shortcut to stop.");
        else if (vm.State == OverlayState.Error)
            AutomationProperties.SetName(this, string.IsNullOrEmpty(vm.ErrorDetail) ? vm.ErrorMessage : $"{vm.ErrorMessage}. {vm.ErrorDetail}");

        // geometry
        ContentGrid.Margin = pad;
        Pill.CornerRadius = new CornerRadius(radius);
        Pill.MinHeight = minH;
        bool anim = animate && !Ui.ReduceMotion && IsLoaded;
        SetSize(Pill, WidthProperty, w, anim);
        if (h is null) { Pill.BeginAnimation(HeightProperty, null); Pill.Height = double.NaN; }
        else if (anim && !double.IsNaN(Pill.Height) || anim && Pill.ActualHeight > 0) { var from = double.IsNaN(Pill.Height) ? Pill.ActualHeight : Pill.Height; Pill.BeginAnimation(HeightProperty, new DoubleAnimation(from, h.Value, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } }); }
        else { Pill.BeginAnimation(HeightProperty, null); Pill.Height = h.Value; }
        UpdateCancel();

        // crossfade between content categories
        if (_active != target)
        {
            foreach (var c in new FrameworkElement[] { ListenText, ListenWave, ProcessingBadge, SuccessBadge, CopyPanel, ErrorPanel, SpinnerText, CancelPanel })
                c.Visibility = c == target ? Visibility.Visible : Visibility.Collapsed;
            _active = target;
            if (anim) target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        else if (key != _modeKey && anim) { }
        _modeKey = key;
    }

    static void SetSize(FrameworkElement el, DependencyProperty dp, double v, bool anim)
    {
        if (anim && el.ActualWidth > 0) el.BeginAnimation(dp, new DoubleAnimation(double.IsNaN((double)el.GetValue(dp)) ? el.ActualWidth : (double)el.GetValue(dp), v, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        else { el.BeginAnimation(dp, null); el.SetValue(dp, v); }
    }

    /// <summary>Appear (opacity 1, scale 1) / disappear (opacity 0, scale 0.95 anchored at bottom).</summary>
    public void SetPresented(bool visible, Action? completed = null)
    {
        var dur = TimeSpan.FromMilliseconds(visible ? 350 : 300);
        double op = visible ? 1 : 0, sc = visible ? 1 : 0.95;
        if (Ui.ReduceMotion) { Pill.Opacity = op; PillScale.ScaleX = PillScale.ScaleY = sc; completed?.Invoke(); return; }
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        var a = new DoubleAnimation(op, dur) { EasingFunction = visible ? ease : new CubicEase() };
        if (completed != null) a.Completed += (_, _) => completed();
        Pill.BeginAnimation(OpacityProperty, a);
        var s = new DoubleAnimation(sc, dur) { EasingFunction = visible ? ease : new CubicEase() };
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, s); PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
    }

    /// <summary>Snap to the hidden visual state without animation (before the next show).</summary>
    public void SnapHidden() { Pill.BeginAnimation(OpacityProperty, null); Pill.Opacity = 0; PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); PillScale.ScaleX = PillScale.ScaleY = 0.95; }

    public OverlayMetrics Metrics => _m;
}
