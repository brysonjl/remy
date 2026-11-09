using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Remy.App.Services;
using Remy.Core.Text;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Tray "Add Custom Vocabulary" panel (ui.md 3.10): small always-on-top utility window editing the same list as the cleanup page.</summary>
public partial class QuickVocabularyWindow : Window
{
    readonly AppServices _s;
    readonly ContentControl _body = new();
    readonly TextBlock _count;
    readonly Border _footerDivider = Divider();

    public QuickVocabularyWindow(AppServices services)
    {
        _s = services;
        InitializeComponent();

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(16, 16, 16, 14) };
        top.Children.Add(Text(VocabularyInput.QuickPanelCaption, margin: new Thickness(0, 0, 0, 10)));
        var add = VocabularyAddRow(_s.Settings, width: 276, autoFocus: true);
        top.Children.Add(add);
        rows.Children.Add(top);

        var d1 = Divider(); Grid.SetRow(d1, 1); rows.Children.Add(d1);
        Grid.SetRow(_body, 2); rows.Children.Add(_body);
        Grid.SetRow(_footerDivider, 3); rows.Children.Add(_footerDivider);
        _count = Text("", margin: new Thickness(16, 8, 16, 8));
        _count.FontSize = 11.5;
        Grid.SetRow(_count, 4); rows.Children.Add(_count);
        Layout.Children.Add(rows);

        Refresh();
        _s.Settings.PropertyChanged += OnSetting;
        Closed += (_, _) => _s.Settings.PropertyChanged -= OnSetting;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) { try { DragMove(); } catch (InvalidOperationException) { } } };
        SourceInitialized += (_, _) => { ThemeManager.RoundCorners(this); ThemeManager.SetCaptionDark(this, ThemeManager.IsDark); };
    }

    void OnSetting(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Remy.Core.Settings.RemySettings.CustomVocabulary)) Dispatcher.BeginInvoke(Refresh);
    }

    void Refresh()
    {
        var terms = _s.Settings.CustomVocabulary;
        if (terms.Count == 0)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = Icon("", 26); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 10);
            s.Children.Add(icon);
            var t = Label(VocabularyInput.EmptyState); t.HorizontalAlignment = HorizontalAlignment.Center;
            s.Children.Add(t);
            _body.Content = s;
            _count.Visibility = Visibility.Collapsed; _footerDivider.Visibility = Visibility.Collapsed;
            return;
        }
        var chips = TermChips(() => _s.Settings.CustomVocabulary, term => _s.Settings.CustomVocabulary = _s.Settings.CustomVocabulary.Where(x => x != term).ToList(), "Remove ");
        _body.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 16, 16, 10), Content = chips };
        _count.Text = VocabularyInput.CountLabel(terms.Count);
        _count.Visibility = Visibility.Visible; _footerDivider.Visibility = Visibility.Visible;
    }
}
