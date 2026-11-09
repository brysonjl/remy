using System.Windows;
using System.Windows.Controls;

namespace Remy.App.Controls;

/// <summary>DSPage + DSSectionHeader: scrolling pane, 820 max content column, accent-bar title and subtitle, then the page content.</summary>
public sealed class PageScaffold : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageScaffold), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageScaffold), new PropertyMetadata(""));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    static PageScaffold() => DefaultStyleKeyProperty.OverrideMetadata(typeof(PageScaffold), new FrameworkPropertyMetadata(typeof(PageScaffold)));
}

/// <summary>Section: monospace overline above a content block (DSSection). Content goes inside a card by the page author.</summary>
public sealed class Section : ContentControl
{
    public static readonly DependencyProperty OverlineProperty = DependencyProperty.Register(nameof(Overline), typeof(string), typeof(Section), new PropertyMetadata(""));
    public string Overline { get => (string)GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }
    static Section() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Section), new FrameworkPropertyMetadata(typeof(Section)));
}

public enum PillKind { Ready, Neutral }

/// <summary>DSStatusPill / model readiness badge: 7px dot + text. Ready = green, Neutral = grey.</summary>
public sealed class StatusPill : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(""));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(PillKind), typeof(StatusPill), new PropertyMetadata(PillKind.Ready));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public PillKind Kind { get => (PillKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    static StatusPill() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(typeof(StatusPill)));
}
