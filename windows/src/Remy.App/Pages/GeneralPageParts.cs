using System.Windows;
using System.Windows.Controls;

namespace Remy.App.Pages;

/// <summary>A dropdown entry: value plus its verbatim label (bind SelectedValuePath="Value" DisplayMemberPath="Label").</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>DSFieldMessage: 12 pt icon + 12.5 pt text, tone-colored icon. Collapses when Text is empty.</summary>
public sealed class FieldMessage : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(FieldMessage),
        new PropertyMetadata("", (d, _) => ((FieldMessage)d).Update()));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Tone), typeof(FieldMessage),
        new PropertyMetadata(Tone.Error, (d, _) => ((FieldMessage)d).Update()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    readonly TextBlock _icon = new() { FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _text = new();

    public FieldMessage()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _icon.SetResourceReference(StyleProperty, "Text.Icon");
        _text.SetResourceReference(StyleProperty, "Text.Caption");
        SetColumn(_text, 1);
        Children.Add(_icon); Children.Add(_text);
        Update();
    }

    void Update()
    {
        Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = Text;
        (_icon.Text, string brush) = Tone switch
        {
            Tone.Error => ("", "Brush.Destructive"),
            Tone.Warning => ("", "Brush.AccentDeep"),
            Tone.Success => ("", "Brush.Success"),
            _ => ("", "Brush.TextSecondary"),
        };
        _icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _text.SetResourceReference(TextBlock.ForegroundProperty, Tone == Tone.Error ? "Brush.Destructive" : "Brush.TextSecondary");
        System.Windows.Automation.AutomationProperties.SetName(this, $"{Tone}: {Text}");
    }
}

/// <summary>DSHint: top-aligned accentDeep icon (default lightbulb) + 12.5 pt secondary text.</summary>
public sealed class Hint : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Hint),
        new PropertyMetadata("", (d, e) => ((Hint)d)._text.Text = (string)e.NewValue));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(Hint),
        new PropertyMetadata("", (d, e) => ((Hint)d)._icon.Text = (string)e.NewValue));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    readonly TextBlock _icon = new() { Text = "", FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _text = new();

    public Hint()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _icon.SetResourceReference(StyleProperty, "Text.Icon");
        _icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentDeep");
        _text.SetResourceReference(StyleProperty, "Text.Caption");
        SetColumn(_text, 1);
        Children.Add(_icon); Children.Add(_text);
    }
}

/// <summary>Collapses an element whose bound string is empty or null.</summary>
public sealed class NotEmptyToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
