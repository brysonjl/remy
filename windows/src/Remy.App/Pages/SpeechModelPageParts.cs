using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Remy.App.Pages;

/// <summary>Themed confirmation alert (title, message, Cancel + confirm button).</summary>
public static class ConfirmDialog
{
    public static bool Ask(string title, string message, string confirmLabel, bool destructive)
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null,
        };
        if (win.Owner is null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.SetResourceReference(Control.BackgroundProperty, "Brush.BgCard");

        bool result = false;
        var titleText = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold };
        titleText.SetResourceReference(FrameworkElement.StyleProperty, "Text.Base");
        titleText.FontSize = 15; titleText.FontWeight = FontWeights.SemiBold;
        var body = new TextBlock { Text = message, Margin = new Thickness(0, 8, 0, 0) };
        body.SetResourceReference(FrameworkElement.StyleProperty, "Text.Caption");

        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = confirmLabel, IsDefault = true };
        ok.SetResourceReference(FrameworkElement.StyleProperty, destructive ? "Remy.DestructiveButton" : "Remy.PrimaryButton");
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) => { result = true; win.Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(titleText); root.Children.Add(body); root.Children.Add(buttons);
        win.Content = root;
        win.ShowDialog();
        return result;
    }
}

/// <summary>Small rotating arc used while a model operation is running (DSModelReadinessStatus spinner).</summary>
public sealed class Spinner : Decorator
{
    public Spinner()
    {
        Width = Height = 14;
        var arc = new System.Windows.Shapes.Path
        {
            StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.AccentDeep");
        Child = arc;
        Loaded += (_, _) =>
        {
            if (Controls.Ui.ReduceMotion) return;
            ((RotateTransform)arc.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        };
    }
}
