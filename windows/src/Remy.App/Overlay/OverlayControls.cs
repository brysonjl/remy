using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Remy.App.Controls;

namespace Remy.App.Overlay;

/// <summary>Two-layer sine ribbon (parity/ui.md 4.5). 30 fps while active; frozen when level &lt; 0.015 or not visible.</summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(WaveformView), new PropertyMetadata(0.0));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    double _shown, _frozenT, _last;
    bool _hooked;
    TimeSpan _lastRender;

    public WaveformView()
    {
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.2));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.8));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();
        OpacityMask = mask;
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
    }

    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? s, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if ((now - _lastRender).TotalMilliseconds < 33) return;
        _lastRender = now;
        var target = double.IsFinite(Level) ? Math.Clamp(Level, 0, 1) : 0;
        _shown += (target - _shown) * 0.25; // ~0.12 s ease-out at 30 fps
        if (Ui.ReduceMotion || (target < 0.015 && _shown < 0.015)) { if (_last != -1) { _last = -1; InvalidateVisual(); } return; }
        _last = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double amp = 0.06 + Math.Sqrt(Math.Clamp(_shown, 0, 1)) * 0.88;
        bool frozen = Ui.ReduceMotion || _last == -1;
        double t = frozen ? _frozenT : Clock.Elapsed.TotalSeconds;
        if (!frozen) _frozenT = t;
        if (Ui.ReduceMotion) t = 0;
        for (int layer = 0; layer < 2; layer++)
        {
            double phase = (t % 60) * 2.5 + layer * 1.7, freq = layer == 0 ? 1.15 : 1.8;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (int i = 0; i <= 64; i++)
                {
                    double x = i / 64.0, env = Math.Sin(Math.PI * x), wave = Math.Sin(x * 2 * Math.PI * freq + phase);
                    var p = new Point(x * w, h / 2 - wave * env * amp * h / 2);
                    if (i == 0) c.BeginFigure(p, true, false); else c.LineTo(p, true, false);
                }
                c.LineTo(new Point(w, h / 2), false, false);
            }
            g.Freeze();
            // fill: region between wave and center line (closing the figure draws back to the start point).
            var fillGeo = new StreamGeometry();
            using (var c = fillGeo.Open())
            {
                for (int i = 0; i <= 64; i++)
                {
                    double x = i / 64.0, env = Math.Sin(Math.PI * x), wave = Math.Sin(x * 2 * Math.PI * freq + phase);
                    var p = new Point(x * w, h / 2 - wave * env * amp * h / 2);
                    if (i == 0) c.BeginFigure(p, true, true); else c.LineTo(p, true, false);
                }
                c.LineTo(new Point(w, h / 2), true, false);
            }
            fillGeo.Freeze();
            dc.PushOpacity(layer == 0 ? 0.09 : 0.04); dc.DrawGeometry(Tint, null, fillGeo); dc.Pop();
            dc.PushOpacity(layer == 0 ? 0.9 : 0.38);
            dc.DrawGeometry(null, new Pen(Tint, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
            dc.Pop();
        }
    }
}

/// <summary>Processing spinner: 12% ring, rotating 16%..82% arc every 1.15 s, center dot (parity/ui.md 4.6).</summary>
public sealed class SpinnerView : FrameworkElement
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(SpinnerView),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    bool _hooked;

    public SpinnerView()
    {
        Width = 16; Height = 16;
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
    }
    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }
    void OnFrame(object? s, EventArgs e) { if (!Ui.ReduceMotion) InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double sz = Math.Min(ActualWidth, ActualHeight), r = sz / 2 - 1, cx = ActualWidth / 2, cy = ActualHeight / 2;
        var center = new Point(cx, cy);
        dc.PushOpacity(0.12); dc.DrawEllipse(null, new Pen(Tint, 1), center, r, r); dc.Pop();
        double rot = Ui.ReduceMotion ? 0 : (Clock.Elapsed.TotalSeconds % 1.15) / 1.15 * 360;
        double a0 = (rot - 90 + 0.16 * 360) * Math.PI / 180, a1 = (rot - 90 + 0.82 * 360) * Math.PI / 180;
        var p0 = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)); var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(p0, false, false); c.ArcTo(p1, new Size(r, r), 0, (0.82 - 0.16) > 0.5, SweepDirection.Clockwise, true, false); }
        dc.PushOpacity(0.9);
        dc.DrawGeometry(null, new Pen(Tint, 1.35) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        dc.DrawEllipse(Tint, null, center, 1.5, 1.5);
        dc.Pop();
    }
}

/// <summary>Success animation: expanding ring, progress arc, checkmark.circle.fill popping in (parity/ui.md 4.6).</summary>
public sealed class SuccessView : FrameworkElement
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(SuccessView),
        new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CheckBackProperty = DependencyProperty.Register(nameof(CheckBack), typeof(Brush), typeof(SuccessView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(SuccessView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    /// <summary>Color of the check stroke cut into the filled circle (the pill surface).</summary>
    public Brush CheckBack { get => (Brush)GetValue(CheckBackProperty); set => SetValue(CheckBackProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public SuccessView()
    {
        Width = 16; Height = 16;
        IsVisibleChanged += (_, _) => { if (IsVisible) Restart(); };
    }

    void Restart()
    {
        if (Ui.ReduceMotion) { Progress = 1; return; }
        var a = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.5, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        BeginAnimation(ProgressProperty, a);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double p = Progress, pc = Math.Clamp(p, 0, 1), sz = Math.Min(ActualWidth, ActualHeight), r = sz / 2 - 1.5;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.PushOpacity(0.12); dc.DrawEllipse(null, new Pen(Tint, 1), center, r, r); dc.Pop();
        double sc = 0.72 + 0.56 * pc;
        dc.PushOpacity(0.8 * (1 - pc) * 0.42); dc.DrawEllipse(null, new Pen(Tint, 1), center, r * sc, r * sc); dc.Pop();
        double trim = 0.12 + 0.88 * pc;
        if (trim >= 0.999) { dc.PushOpacity(0.9); dc.DrawEllipse(null, new Pen(Tint, 1.4), center, r, r); dc.Pop(); }
        else
        {
            double a1 = (-90 + trim * 360) * Math.PI / 180, a0 = -90 * Math.PI / 180;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(center.X + r * Math.Cos(a0), center.Y + r * Math.Sin(a0)), false, false);
                c.ArcTo(new Point(center.X + r * Math.Cos(a1), center.Y + r * Math.Sin(a1)), new Size(r, r), 0, trim > 0.5, SweepDirection.Clockwise, true, false);
            }
            dc.PushOpacity(0.9); dc.DrawGeometry(null, new Pen(Tint, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g); dc.Pop();
        }
        // checkmark.circle.fill
        double s2 = 0.72 + 0.28 * Math.Min(1, p), cr = (sz * 0.44) * s2;
        dc.PushOpacity(pc);
        dc.DrawEllipse(Tint, null, center, cr, cr);
        var k = cr / 7.0;
        var chk = new StreamGeometry();
        using (var c = chk.Open())
        {
            c.BeginFigure(new Point(center.X - 3.2 * k, center.Y + 0.2 * k), false, false);
            c.LineTo(new Point(center.X - 0.9 * k, center.Y + 2.6 * k), true, false);
            c.LineTo(new Point(center.X + 3.4 * k, center.Y - 2.4 * k), true, false);
        }
        dc.DrawGeometry(null, new Pen(CheckBack, 1.6 * Math.Max(0.6, k)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, chk);
        dc.Pop();
    }
}

/// <summary>
/// Bottom-anchored viewport: reports min(child height, MaxHeight), aligns the child to the bottom (newest line visible)
/// and fades the top <see cref="PeekHeight"/> when the child overflows.
/// </summary>
public sealed class BottomAnchoredPanel : Panel
{
    public static readonly DependencyProperty MaxTextHeightProperty = DependencyProperty.Register(nameof(MaxTextHeight), typeof(double), typeof(BottomAnchoredPanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty PeekHeightProperty = DependencyProperty.Register(nameof(PeekHeight), typeof(double), typeof(BottomAnchoredPanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MaxTextHeight { get => (double)GetValue(MaxTextHeightProperty); set => SetValue(MaxTextHeightProperty, value); }
    public double PeekHeight { get => (double)GetValue(PeekHeightProperty); set => SetValue(PeekHeightProperty, value); }
    double _childH;

    public BottomAnchoredPanel() { ClipToBounds = true; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = availableSize.Width, h = 0;
        foreach (UIElement c in InternalChildren) { c.Measure(new Size(w, double.PositiveInfinity)); h = Math.Max(h, c.DesiredSize.Height); }
        _childH = h;
        return new Size(double.IsInfinity(w) ? 0 : w, Math.Min(h, MaxTextHeight + PeekHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement c in InternalChildren)
            c.Arrange(new Rect(0, finalSize.Height - c.DesiredSize.Height, finalSize.Width, c.DesiredSize.Height));
        if (_childH > MaxTextHeight + 1 && finalSize.Height > 0)
        {
            var m = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            m.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            m.GradientStops.Add(new GradientStop(Colors.Black, Math.Min(1, PeekHeight / finalSize.Height)));
            m.GradientStops.Add(new GradientStop(Colors.Black, 1));
            OpacityMask = m;
        }
        else OpacityMask = null;
        return finalSize;
    }
}
