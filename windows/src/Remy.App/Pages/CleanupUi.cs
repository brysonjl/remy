using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remy.App.Controls;
using Remy.Core.Settings;
using Remy.Core.Text;

namespace Remy.App.Pages;

public enum Tone { Info, Success, Warning, Error }

/// <summary>Code-side builders for the Transcript Cleanup, History and Quick Vocabulary pages. Only existing styles and tokens.</summary>
internal static class CleanupUi
{
    public static Style St(string key) => (Style)Application.Current.FindResource(key);

    public static void Res(FrameworkElement e, DependencyProperty p, string key) => e.SetResourceReference(p, key);

    // ------------------------------------------------------------------ text

    public static TextBlock Text(string text, string style = "Text.Caption", Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, Style = St(style) };
        if (margin is { } m) t.Margin = m;
        return t;
    }

    public static TextBlock Label(string text) => Text(text, "Text.RowLabel");

    public static TextBlock Message(string text, Tone tone = Tone.Info)
    {
        var t = Text(text);
        SetTone(t, tone);
        return t;
    }

    public static void SetTone(TextBlock t, Tone tone)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, tone switch
        {
            Tone.Success => "Brush.SuccessText",
            Tone.Warning => "Brush.SignalWarm",
            Tone.Error => "Brush.Destructive",
            _ => "Brush.TextSecondary",
        });
    }

    public static TextBlock Icon(string glyph, double size = 13.5, string brush = "Brush.TextSecondary")
    {
        var t = new TextBlock { Text = glyph, Style = St("Text.Icon"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    // ------------------------------------------------------------------ containers

    public static Border Divider() => new() { Style = St("Remy.Divider") };

    /// <summary>Card with the given items separated by dividers. Null items are skipped.</summary>
    public static Border Card(params UIElement?[] items)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var i in items)
        {
            if (i is null) continue;
            if (!first) stack.Children.Add(Divider());
            stack.Children.Add(i);
            first = false;
        }
        return new Border { Style = St("Remy.Card"), Child = stack };
    }

    public static Border Pad(UIElement child, double h = 16, double v = 12) => new() { Padding = new Thickness(h, v, h, v), Child = child };

    public static Remy.App.Controls.Section Sec(string overline, UIElement content) => new() { Overline = overline, Content = content };

    /// <summary>Label (+ optional caption) on the left, control on the right. DetailRow / InfoRow.</summary>
    public static Border Row(string label, UIElement? trailing, string? caption = null, bool secondaryLabel = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(secondaryLabel ? Text(label, "Text.RowValue") : Label(label));
        if (caption is not null) left.Children.Add(Text(caption, margin: new Thickness(0, 3, 0, 0)));
        g.Children.Add(left);
        if (trailing is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
            g.Children.Add(fe);
        }
        return new Border { Style = St("Remy.DetailRow"), Child = g };
    }

    /// <summary>Label on the left, field (width 320) on the right.</summary>
    public static Border Field(string label, UIElement field, double width = 320)
    {
        if (field is FrameworkElement fe) fe.Width = width;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Label(label);
        l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        Grid.SetColumn((UIElement)field, 1);
        g.Children.Add(field);
        return new Border { Padding = new Thickness(16, 10, 16, 10), Child = g };
    }

    /// <summary>Labeled field stacked: label above control (profile editor).</summary>
    public static StackPanel Stacked(string label, UIElement control, string? tooltip = null)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var l = Text(label, "Text.RowLabel");
        l.FontSize = 13; l.FontWeight = FontWeights.SemiBold; l.Margin = new Thickness(0, 0, 0, 5);
        s.Children.Add(l);
        s.Children.Add(control);
        if (tooltip is not null) { ((FrameworkElement)control).ToolTip = tooltip; }
        return s;
    }

    // ------------------------------------------------------------------ controls

    public static ToggleButton Switch(bool isOn, Action<bool> changed, string? automationName = null)
    {
        var t = new ToggleButton { Style = St("Remy.Switch"), IsChecked = isOn };
        if (automationName is not null) System.Windows.Automation.AutomationProperties.SetName(t, automationName);
        t.Click += (_, _) => changed(t.IsChecked == true);
        return t;
    }

    public static ComboBox Combo<T>(IReadOnlyList<T> items, Func<T, string> name, T selected, Action<T> changed, string? automationName = null, double minWidth = 150)
    {
        var c = new ComboBox { MinWidth = minWidth };
        foreach (var i in items) c.Items.Add(name(i));
        c.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i, selected)));
        if (automationName is not null) System.Windows.Automation.AutomationProperties.SetName(c, automationName);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0 && c.SelectedIndex < items.Count) changed(items[c.SelectedIndex]); };
        return c;
    }

    public static Button Btn(string text, Action onClick, string style = "Remy.SecondaryButton", string? automationName = null)
    {
        var b = new Button { Content = text, Style = St(style) };
        if (automationName is not null) System.Windows.Automation.AutomationProperties.SetName(b, automationName);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button IconBtn(string glyph, Action onClick, string automationName, string? tooltip = null)
    {
        var b = new Button { Content = glyph, Style = St("Remy.IconButton") };
        System.Windows.Automation.AutomationProperties.SetName(b, automationName);
        if (tooltip is not null) b.ToolTip = tooltip;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    public static Button LinkButton(string text, string url)
    {
        var b = new Button { Style = St("Remy.CompactButton"), HorizontalAlignment = HorizontalAlignment.Left };
        var t = new TextBlock { Text = text, FontWeight = FontWeights.Medium };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentDeep");
        b.Content = t;
        b.Click += (_, _) => OpenUrl(url);
        return b;
    }

    /// <summary>TextBox with a placeholder overlay.</summary>
    public static (Grid Host, TextBox Box) Input(string placeholder, string text = "", double? width = null, string? automationName = null)
    {
        var box = new TextBox { Text = text };
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, Margin = new Thickness(27, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Opacity = 0.6 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        hint.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        if (automationName is not null) System.Windows.Automation.AutomationProperties.SetName(box, automationName);
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(hint);
        if (width is { } w) g.Width = w;
        return (g, box);
    }

    /// <summary>Password box in a Border that matches the text field look.</summary>
    public static (Border Host, PasswordBox Box) Secret(string placeholder, string? automationName = null)
    {
        var box = new PasswordBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(12, 7, 12, 7), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Ink");
        box.SetResourceReference(PasswordBox.CaretBrushProperty, "Brush.Ink");
        box.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Opacity = 0.6 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        hint.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (automationName is not null) System.Windows.Automation.AutomationProperties.SetName(box, automationName);
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(hint);
        var host = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = g };
        host.SetResourceReference(Border.BackgroundProperty, "Brush.BgCard");
        host.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        box.GotKeyboardFocus += (_, _) => host.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentDeep");
        box.LostKeyboardFocus += (_, _) => host.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return (host, box);
    }

    static readonly ControlTemplate TextAreaTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
        "<Grid><Border Background='{DynamicResource Brush.BgCard}' BorderBrush='{DynamicResource Brush.Border}' BorderThickness='1' CornerRadius='12'>" +
        "<ScrollViewer x:Name='PART_ContentHost' Margin='12,10' VerticalAlignment='Top'/></Border>" +
        "<Border x:Name='Ring' CornerRadius='12' BorderThickness='2' BorderBrush='{DynamicResource Brush.AccentDeep}' Visibility='Collapsed' IsHitTestVisible='False'/></Grid>" +
        "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Ring' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    /// <summary>Multi-line plain text area (SettingsMultilineTextArea, Windows-simplified: no resize grip, grows to 240).</summary>
    public static (Grid Host, TextBox Box) TextArea(string placeholder, string text, double minHeight = 90, double maxHeight = 240)
    {
        var (host, box) = Input(placeholder, text);
        box.Template = TextAreaTemplate;
        box.Padding = new Thickness(0); box.AcceptsReturn = true; box.AcceptsTab = false; box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.MinHeight = minHeight; box.MaxHeight = maxHeight;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        if (host.Children[1] is TextBlock hint) { hint.VerticalAlignment = VerticalAlignment.Top; hint.Margin = new Thickness(14, 11, 0, 0); }
        return (host, box);
    }

    // ------------------------------------------------------------------ chips

    public static WrapPanel Flow() => new() { Orientation = Orientation.Horizontal };

    /// <summary>Chip (Remy.Chip / Remy.ChipSelected) with optional select click and optional remove glyph.</summary>
    public static Border Chip(string text, Action? onClick = null, Action? onRemove = null, bool selected = false, string? removeName = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Brush.OnAccent" : "Brush.Ink");
        row.Children.Add(t);
        var chip = new Border { Style = St(selected ? "Remy.ChipSelected" : "Remy.Chip"), Margin = new Thickness(0, 0, 6, 6), Child = row };
        if (onClick is not null)
        {
            chip.Cursor = Cursors.Hand;
            chip.MouseLeftButtonUp += (_, e) => { if (!e.Handled) onClick(); };
        }
        if (onRemove is not null)
        {
            var x = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"), FontSize = 9, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            x.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Brush.OnAccent" : "Brush.TextSecondary");
            if (removeName is not null) System.Windows.Automation.AutomationProperties.SetName(x, removeName);
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; onRemove(); };
            row.Children.Add(x);
        }
        return chip;
    }

    /// <summary>Removable chips for a string list; call Refresh after the list changes.</summary>
    public static WrapPanel TermChips(Func<IReadOnlyList<string>> source, Action<string> remove, string removeNamePrefix)
    {
        var flow = Flow();
        void Fill()
        {
            flow.Children.Clear();
            foreach (var term in source()) flow.Children.Add(Chip(term, onRemove: () => remove(term), removeName: removeNamePrefix + term));
        }
        flow.Tag = (Action)Fill;
        Fill();
        return flow;
    }

    public static void RefreshChips(WrapPanel p) { if (p.Tag is Action a) a(); }

    // ------------------------------------------------------------------ vocabulary editor (page + quick panel)

    /// <summary>"Add word or phrase…" field + Add button. Parsed through VocabularyInput.Terms against the current list.</summary>
    public static UIElement VocabularyAddRow(RemySettings settings, double width = 260, bool autoFocus = false)
    {
        var (host, box) = Input(VocabularyInput.AddPlaceholder, width: width, automationName: "Add custom vocabulary word or phrase");
        var add = new Button { Content = "Add", Style = St("Remy.SecondaryButton") };
        System.Windows.Automation.AutomationProperties.SetName(add, "Add custom vocabulary");
        void Sync() => add.IsEnabled = VocabularyInput.Terms(box.Text, settings.CustomVocabulary).Count > 0;
        void Commit()
        {
            var terms = VocabularyInput.Terms(box.Text, settings.CustomVocabulary);
            if (terms.Count == 0) return;
            settings.CustomVocabulary = settings.CustomVocabulary.Concat(terms).ToList();
            box.Text = "";
            box.Focus();
        }
        box.TextChanged += (_, _) => Sync();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        add.Click += (_, _) => Commit();
        Sync();
        if (autoFocus) host.Loaded += (_, _) => box.Focus();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(host);
        add.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(add);
        return row;
    }

    /// <summary>Chips bound to settings.CustomVocabulary; refreshes itself while loaded.</summary>
    public static WrapPanel VocabularyChips(RemySettings settings)
    {
        WrapPanel? flow = null;
        flow = TermChips(() => settings.CustomVocabulary,
            term => settings.CustomVocabulary = settings.CustomVocabulary.Where(t => t != term).ToList(),
            "Remove ");
        System.ComponentModel.PropertyChangedEventHandler h = (_, e) =>
        {
            if (e.PropertyName == nameof(RemySettings.CustomVocabulary)) flow.Dispatcher.BeginInvoke(() => RefreshChips(flow));
        };
        flow.Loaded += (_, _) => { settings.PropertyChanged -= h; settings.PropertyChanged += h; RefreshChips(flow); };
        flow.Unloaded += (_, _) => settings.PropertyChanged -= h;
        return flow;
    }

    // ------------------------------------------------------------------ misc

    /// <summary>Thin accent progress bar (0..1).</summary>
    public static (Grid Host, Action<double> Set) ProgressBar()
    {
        var track = new Border { Height = 5, CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.BgInset");
        var fill = new Border { Height = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        var g = new Grid();
        g.Children.Add(track);
        g.Children.Add(fill);
        g.SizeChanged += (_, _) => { };
        return (g, f => fill.Width = Math.Max(0, g.ActualWidth * Math.Clamp(f, 0, 1)));
    }

    /// <summary>Small modal confirmation (title, message, destructive confirm button, Cancel).</summary>
    public static bool Confirm(DependencyObject? from, string title, string message, string confirmText, bool destructive = true)
    {
        var owner = from is null ? null : Window.GetWindow(from);
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.Height, Width = 420, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        if (owner is not null && owner.IsVisible) w.Owner = owner;
        w.SetResourceReference(Window.BackgroundProperty, "Brush.BgWindow");
        var ok = false;
        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        var t = Text(title, "Text.RowLabel");
        t.FontSize = 15; t.FontWeight = FontWeights.SemiBold;
        stack.Children.Add(t);
        stack.Children.Add(Text(message, "Text.Caption", new Thickness(0, 8, 0, 18)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Btn("Cancel", () => w.Close());
        cancel.IsCancel = true;
        var go = Btn(confirmText, () => { ok = true; w.Close(); }, destructive ? "Remy.DestructiveButton" : "Remy.PrimaryButton");
        go.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        stack.Children.Add(buttons);
        w.Content = stack;
        w.ShowDialog();
        return ok;
    }

    /// <summary>Runs <paramref name="action"/> after <paramref name="ms"/> unless re-armed first.</summary>
    public sealed class Debouncer(Dispatcher d)
    {
        DispatcherTimer? _t;
        public void Run(int ms, Action action)
        {
            _t?.Stop();
            _t = new DispatcherTimer(DispatcherPriority.Background, d) { Interval = TimeSpan.FromMilliseconds(ms) };
            _t.Tick += (_, _) => { _t!.Stop(); action(); };
            _t.Start();
        }
        public void Cancel() => _t?.Stop();
    }

    static readonly ControlTemplate PlainTextTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Focusable='False' VerticalScrollBarVisibility='Disabled' HorizontalScrollBarVisibility='Disabled'/></ControlTemplate>");

    /// <summary>Selectable, wrapping, read-only text (a chrome-less TextBox).</summary>
    public static TextBox SelectableText(string text, string style = "Text.Base", double? size = null, bool secondary = false, int? maxLines = null)
    {
        var tb = new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, Template = PlainTextTemplate, FontSize = size ?? 13.5, Cursor = Cursors.IBeam,
        };
        tb.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        tb.SetResourceReference(TextBox.ForegroundProperty, secondary ? "Brush.TextSecondary" : "Brush.Ink");
        tb.SelectionBrush = (Brush)Application.Current.FindResource("Brush.Accent");
        if (maxLines is { } n) tb.MaxLines = n;
        return tb;
    }

    /// <summary>Chevron + header that expands content built lazily on first open.</summary>
    public static StackPanel Disclosure(string header, Func<UIElement> build, Action? onFirstOpen = null)
    {
        var root = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var chev = Icon("", 9);
        chev.Margin = new Thickness(0, 0, 6, 0);
        var t = new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.Medium };
        t.FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui");
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        head.Children.Add(chev); head.Children.Add(t);
        var body = new ContentControl { Margin = new Thickness(14, 6, 0, 0), Visibility = Visibility.Collapsed };
        var built = false;
        head.MouseLeftButtonUp += (_, _) =>
        {
            var open = body.Visibility != Visibility.Visible;
            if (open && !built) { body.Content = build(); built = true; onFirstOpen?.Invoke(); }
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chev.Text = open ? "" : "";
        };
        root.Children.Add(head);
        root.Children.Add(body);
        return root;
    }

    /// <summary>Copy text to the clipboard, retrying once if it is locked. Returns false on failure.</summary>
    public static bool CopyText(string text)
    {
        for (var i = 0; i < 2; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
        }
        return false;
    }
}
