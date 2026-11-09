using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Remy.App.Controls;

/// <summary>Single-line text with letter spacing (WPF TextBlock has no tracking). Used for overlines and kickers.</summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty TrackingProperty = Reg(nameof(Tracking), typeof(double), 0.0);
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 10.5);
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Bold);
    public static readonly DependencyProperty FontFamilyProperty = Reg(nameof(FontFamily), typeof(FontFamily), new FontFamily("Cascadia Mono, Consolas"));
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty UpperCaseProperty = Reg(nameof(UpperCase), typeof(bool), true);

    static DependencyProperty Reg(string n, Type t, object def) => DependencyProperty.Register(n, t, typeof(TrackedText),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool UpperCase { get => (bool)GetValue(UpperCaseProperty); set => SetValue(UpperCaseProperty, value); }

    FormattedText Make(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    string Shown => UpperCase ? (Text ?? "").ToUpperInvariant() : Text ?? "";

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, h = 0;
        foreach (var ch in Shown) { var f = Make(ch.ToString()); w += f.WidthIncludingTrailingWhitespace + Tracking; h = Math.Max(h, f.Height); }
        if (h == 0) h = Make("A").Height;
        return new Size(Math.Max(0, w - Tracking), h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0;
        foreach (var ch in Shown) { var f = Make(ch.ToString()); dc.DrawText(f, new Point(x, 0)); x += f.WidthIncludingTrailingWhitespace + Tracking; }
    }
}
