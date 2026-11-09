using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Remy.App.Pages;
using Remy.App.Services;
using Remy.App.ViewModels;

namespace Remy.App;

public partial class MainWindow : Window
{
    readonly ShellViewModel _vm;
    readonly Dictionary<AppPage, UserControl> _pages = [];

    /// <summary>Set true by the quit path so Closing is not turned into hide-to-tray.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Tray only: minimizing hides the window (no taskbar button). Taskbar and tray: normal minimize.</summary>
    public AppAppearance Appearance { get; set; } = AppAppearance.TrayOnly;

    public MainWindow(ShellViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.SelectedPage)) ShowPage(vm.SelectedPage); };
        ShowPage(vm.SelectedPage);
        SourceInitialized += (_, _) => { ThemeManager.RoundCorners(this); ThemeManager.SetCaptionDark(this, ThemeManager.IsDark); };
        StateChanged += OnStateChanged;
    }

    UserControl CreatePage(AppPage p) => p switch
    {
        AppPage.SpeechModel => new SpeechModelPage(),
        AppPage.General => new GeneralPage(),
        AppPage.Shortcuts => new ShortcutsPage(),
        AppPage.TextOverlay => new TextOverlayPage(),
        AppPage.TranscriptCleanup => new TranscriptCleanupPage(),
        AppPage.History => new HistoryPage(),
        _ => new AboutPage(),
    };

    /// <summary>Pages inherit the shell DataContext until the wiring step gives them their own view models.</summary>
    public UserControl GetPage(AppPage p) { if (!_pages.TryGetValue(p, out var c)) _pages[p] = c = CreatePage(p); return c; }

    void ShowPage(AppPage p) => PageHost.Content = GetPage(p);

    /// <summary>Show, restore and bring to front (tray left click, second launch, Open Remy).</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        Topmost = true; Topmost = false; // reliable foreground bring-to-front
        Focus();
    }

    void OnStateChanged(object? s, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        if (WindowState == WindowState.Minimized && Appearance == AppAppearance.TrayOnly) { Hide(); WindowState = WindowState.Normal; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    void Sidebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        if (e.ClickCount == 2) { Maximize_Click(sender, e); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = System.Windows.Media.VisualTreeHelper.GetParent(d); }
        return null;
    }

    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();
}
