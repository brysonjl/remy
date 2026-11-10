using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remy.App.ViewModels;

/// <summary>Sidebar pages in display order (parity/ui.md 2.3; Internal Prompts is dead/hidden and not ported).</summary>
public enum AppPage { SpeechModel, General, Shortcuts, TextOverlay, TranscriptCleanup, Transforms, History, About }

public sealed record PageDescriptor(AppPage Page, string Title, string Glyph, string Subtitle);

/// <summary>Footer "SERVICE STATUS" state machine (parity/ui.md 2.3). The wiring step computes this from app state.</summary>
public enum ServiceStatus
{
    FinishingCancelledCleanup,
    SetupNeeded,
    PreparingSpeechModel,
    ReadyToDictate,
    LoadsOnFirstDictation,
    Listening,
    Transcribing,
    Error,
}

/// <summary>Which footer dot color token to use.</summary>
public enum StatusDot { Secondary, AccentDeep, Success, Accent, Destructive }

public enum ThemeMode { System, Light, Dark }
public enum AppAppearance { TrayOnly, TaskbarAndTray }

/// <summary>
/// View model for the main window. Wiring contract:
///  - set <see cref="ServiceStatus"/>, <see cref="VersionText"/>;
///  - replace the contents of <see cref="AttentionIssues"/> (blocking first);
///  - subscribe to <see cref="PageRequested"/> only if navigation needs side effects; <see cref="SelectedPage"/> is two-way.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public static readonly IReadOnlyList<PageDescriptor> AllPages =
    [
        new(AppPage.SpeechModel, "Speech Model", "", "Speech recognition runs on your PC. Transcript cleanup uses the method you choose separately."),
        new(AppPage.General, "General", "", "How Remy starts, looks, sounds, and listens."),
        new(AppPage.Shortcuts, "Shortcuts", "", "Start and stop dictation from anywhere with a quick key press."),
        new(AppPage.TextOverlay, "Text & Overlay", "", "The floating overlay follows you while you dictate."),
        new(AppPage.TranscriptCleanup, "Transcript Cleanup", "", "Choose how your transcript is polished before it’s pasted."),
        new(AppPage.Transforms, "Transforms", "", "Select text in any app and press a shortcut. Remy rewrites the selection in place with your Transcript Cleanup provider."),
        new(AppPage.History, "History", "", "Your recent dictations, kept on this PC."),
        new(AppPage.About, "About", "", "About Remy"),
    ];

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    [ObservableProperty] private AppPage _selectedPage = AppPage.SpeechModel;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusDot))] private ServiceStatus _serviceStatus = ServiceStatus.LoadsOnFirstDictation;
    [ObservableProperty] private string _versionText = "Version 0.1.0";

    public ObservableCollection<AttentionIssue> AttentionIssues { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CurrentIssue), nameof(IssueCounterText), nameof(HasMultipleIssues))] private int _issueIndex;

    public event Action<AppPage>? PageRequested;

    public ICommand NavigateCommand { get; }
    public ICommand PreviousIssueCommand { get; }
    public ICommand NextIssueCommand { get; }
    public ICommand IssueActionCommand { get; }

    public ShellViewModel()
    {
        NavItems = AllPages.Select(p => new NavItemViewModel(this, p)).ToList();
        NavigateCommand = new RelayCommand<AppPage>(p => Navigate(p));
        PreviousIssueCommand = new RelayCommand(() => Step(-1));
        NextIssueCommand = new RelayCommand(() => Step(1));
        IssueActionCommand = new RelayCommand(() => CurrentIssue?.Action?.Invoke());
        AttentionIssues.CollectionChanged += (_, _) =>
        {
            if (IssueIndex >= AttentionIssues.Count) IssueIndex = 0;
            OnPropertyChanged(nameof(CurrentIssue)); OnPropertyChanged(nameof(IsBannerVisible));
            OnPropertyChanged(nameof(IssueCounterText)); OnPropertyChanged(nameof(HasMultipleIssues));
        };
    }

    public void Navigate(AppPage page) { SelectedPage = page; PageRequested?.Invoke(page); }

    partial void OnSelectedPageChanged(AppPage value) { foreach (var n in NavItems) n.Refresh(); }

    public PageDescriptor SelectedDescriptor => AllPages.First(p => p.Page == SelectedPage);

    // --- attention banner: one issue at a time with a pager ---
    public bool IsBannerVisible => AttentionIssues.Count > 0;
    public bool HasMultipleIssues => AttentionIssues.Count > 1;
    public AttentionIssue? CurrentIssue => AttentionIssues.Count == 0 ? null : AttentionIssues[Math.Clamp(IssueIndex, 0, AttentionIssues.Count - 1)];
    public string IssueCounterText => $"{IssueIndex + 1} of {AttentionIssues.Count}";
    /// <summary>Force a specific issue to be shown (e.g. a recording error).</summary>
    public void ShowIssue(AttentionIssue issue) { var i = AttentionIssues.IndexOf(issue); if (i >= 0) IssueIndex = i; }
    void Step(int d) { if (AttentionIssues.Count > 1) IssueIndex = (IssueIndex + d + AttentionIssues.Count) % AttentionIssues.Count; }

    // --- footer ---
    public string StatusText => ServiceStatus switch
    {
        ServiceStatus.FinishingCancelledCleanup => "Finishing cancelled cleanup…",
        ServiceStatus.SetupNeeded => "Setup needed",
        ServiceStatus.PreparingSpeechModel => "Preparing speech model…",
        ServiceStatus.ReadyToDictate => "Ready to dictate",
        ServiceStatus.LoadsOnFirstDictation => "Loads on first dictation",
        ServiceStatus.Listening => "Listening…",
        ServiceStatus.Transcribing => "Transcribing…",
        ServiceStatus.Error => "Something went wrong",
        _ => "",
    };

    public StatusDot StatusDot => ServiceStatus switch
    {
        ServiceStatus.SetupNeeded => StatusDot.AccentDeep,
        ServiceStatus.ReadyToDictate => StatusDot.Success,
        ServiceStatus.Listening or ServiceStatus.Transcribing => StatusDot.Accent,
        ServiceStatus.Error => StatusDot.Destructive,
        _ => StatusDot.Secondary,
    };
}

public sealed partial class NavItemViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    public PageDescriptor Descriptor { get; }
    public string Title => Descriptor.Title;
    public string Glyph => Descriptor.Glyph;
    public bool IsSelected => _shell.SelectedPage == Descriptor.Page;
    public ICommand Command { get; }
    public NavItemViewModel(ShellViewModel shell, PageDescriptor d) { _shell = shell; Descriptor = d; Command = new RelayCommand(() => shell.Navigate(d.Page)); }
    public void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
