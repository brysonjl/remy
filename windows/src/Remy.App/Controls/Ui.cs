using System.Windows;

namespace Remy.App.Controls;

/// <summary>Attached properties used by the control templates in Themes/Controls.xaml.</summary>
public static class Ui
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(11)));
    public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, CornerRadius v) => d.SetValue(RadiusProperty, v);

    /// <summary>True when the OS asks to reduce motion (animations off).</summary>
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;
}
