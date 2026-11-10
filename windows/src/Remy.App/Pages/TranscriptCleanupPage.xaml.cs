using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Remy.App.Services;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Contracts;
using Remy.Core.Settings;
using Remy.Core.Text;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Transcript Cleanup settings (ui.md 3.5, Windows-adapted). The page is built in code: the sections shown depend on the selected method.</summary>
public partial class TranscriptCleanupPage : UserControl, IServicePage
{
    AppServices? _s;
    readonly Debouncer _debounce;
    bool _built;

    RemySettings S => _s!.Settings;
    public const string MethodCaption = "Choose a built-in model or a custom model provider. Local filler-word removal below runs before the selected method.";

    static readonly TranscriptPostProcessingMode[] Modes =
    [
        TranscriptPostProcessingMode.None, TranscriptPostProcessingMode.S1Mini, TranscriptPostProcessingMode.Profile,
        TranscriptPostProcessingMode.Ollama, TranscriptPostProcessingMode.OpenRouter, TranscriptPostProcessingMode.OpenAICompatible,
    ];

    static string ModeTitle(TranscriptPostProcessingMode m) => m switch
    {
        TranscriptPostProcessingMode.None => "Off",
        TranscriptPostProcessingMode.S1Mini => "S1-mini — English",
        TranscriptPostProcessingMode.Profile => "Saved Profile",
        TranscriptPostProcessingMode.Ollama => "Custom: Ollama",
        TranscriptPostProcessingMode.OpenRouter => "Custom: OpenRouter",
        TranscriptPostProcessingMode.OpenAICompatible => "Custom: OpenAI-compatible",
        _ => m.DisplayName(),
    };

    TranscriptPostProcessingMode CurrentMode
    {
        get
        {
            var m = S.TranscriptPostProcessingMode;
            return m.IsSupportedOnWindows() ? m : TranscriptPostProcessingMode.None;
        }
    }

    TextBlock? _modelValue;
    StackPanel _contextHost = new();
    WrapPanel? _fillerChips;

    public TranscriptCleanupPage()
    {
        InitializeComponent();
        _debounce = new Debouncer(Dispatcher);
        Loaded += (_, _) => { if (_s is not null) Subscribe(); };
        Unloaded += (_, _) => Unsubscribe();
    }

    public void Attach(AppServices services)
    {
        _s = services;
        Build();
        if (IsLoaded) Subscribe();
    }

    bool _subscribed;
    void Subscribe()
    {
        if (_subscribed || _s is null) return;
        _subscribed = true;
        S.PropertyChanged += OnSettingChanged;
        _s.Models.Changed += OnModelChanged;
        _s.Dictation.StateChanged += OnDictationState;
        if (_built) { RefreshS1Card(); }
    }

    void Unsubscribe()
    {
        if (!_subscribed || _s is null) return;
        _subscribed = false;
        S.PropertyChanged -= OnSettingChanged;
        _s.Models.Changed -= OnModelChanged;
        _s.Dictation.StateChanged -= OnDictationState;
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.PropertyName == nameof(RemySettings.TranscriptPostProcessingMode) && _builtMode != CurrentMode) Build();
            else if (e.PropertyName == nameof(RemySettings.FillerWordsToRemove) && _fillerChips is not null) RefreshChips(_fillerChips);
        });
    }

    void OnModelChanged(string id) => Dispatcher.BeginInvoke(RefreshS1Card);
    void OnDictationState(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshS1Card);

    // ------------------------------------------------------------------ page assembly

    TranscriptPostProcessingMode _builtMode;

    void Build()
    {
        if (_s is null) return;
        CancelWork();
        _builtMode = CurrentMode;
        _modelValue = null; _fillerChips = null;
        ResetProviderState();
        Root.Children.Clear();
        var mode = _builtMode;

        Root.Children.Add(Sec("Cleanup Method", MethodCard(mode)));

        switch (mode)
        {
            case TranscriptPostProcessingMode.None:
                Root.Children.Add(Sec("No Cleanup", Card(Pad(Text("Your transcript is pasted exactly as the speech model produced it, after the local filler-word removal below. Pick another method to turn on AI cleanup.")))));
                break;
            case TranscriptPostProcessingMode.S1Mini: BuildS1(); break;
            case TranscriptPostProcessingMode.Ollama: BuildOllama(); break;
            case TranscriptPostProcessingMode.OpenRouter: BuildOpenRouter(); break;
            case TranscriptPostProcessingMode.OpenAICompatible: BuildOpenAICompatible(); break;
            case TranscriptPostProcessingMode.Profile: BuildProfile(); break;
        }

        if (mode is TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenAICompatible && _s.Profiles is not null)
            Root.Children.Add(SaveAsProfileSection(mode));

        if (mode is TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenRouter or TranscriptPostProcessingMode.OpenAICompatible)
            Root.Children.Add(PreparationSection());

        Root.Children.Add(_contextHost = new StackPanel());
        BuildContext();
        Root.Children.Add(FillerSection());
        AddTextRuleSections();
        _built = true;
        if (_subscribed) RefreshS1Card();
        StartInitialRefresh(mode);
    }

    UIElement MethodCard(TranscriptPostProcessingMode mode)
    {
        var combo = Combo(Modes, ModeTitle, mode, m =>
        {
            if (m == S.TranscriptPostProcessingMode) return;
            S.TranscriptPostProcessingMode = m;
            Build();
        }, "Transcript processing method", 210);
        _modelValue = null;
        UIElement? modelRow = null;
        if (mode != TranscriptPostProcessingMode.None)
        {
            _modelValue = Text("", "Text.RowValue");
            _modelValue.TextAlignment = TextAlignment.Right;
            _modelValue.TextWrapping = TextWrapping.Wrap;
            _modelValue.MaxWidth = 420;
            modelRow = Row("Model", _modelValue, secondaryLabel: true);
            UpdateModelInfo();
        }
        return Card(Row("Method", combo, MethodCaption), modelRow);
    }

    string CurrentModelId => _builtMode switch
    {
        TranscriptPostProcessingMode.Ollama => S.OllamaModel,
        TranscriptPostProcessingMode.OpenRouter => S.OpenRouterModel,
        TranscriptPostProcessingMode.OpenAICompatible => S.OpenAICompatibleModel,
        TranscriptPostProcessingMode.Profile => _draft?.Model ?? "",
        _ => "",
    };

    void UpdateModelInfo()
    {
        if (_modelValue is null) return;
        if (_builtMode == TranscriptPostProcessingMode.S1Mini) { _modelValue.Text = "Superwhisper S1-mini — Qwen3 0.6B fine-tune"; return; }
        var m = CurrentModelId.Trim();
        _modelValue.Text = m.Length == 0 ? "Choose a model ID below" : m;
    }

    // ------------------------------------------------------------------ shared sections

    UIElement PromptSection(string label, Func<string> get, Action<string> set)
    {
        var def = CleanupPrompts.RecommendedTranscriptCleanupPrompt;
        var (host, box) = TextArea("Enter cleanup instructions.", get());
        var reset = Btn("Reset to Default", () => { set(def); box.Text = def; }, automationName: $"Reset {label} to default");
        reset.IsEnabled = box.Text != def;
        box.TextChanged += (_, _) => { set(box.Text); reset.IsEnabled = box.Text != def; };
        box.LostFocus += (_, _) => { var v = get(); if (box.Text != v) box.Text = v; };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Label(label));
        Grid.SetColumn(reset, 1);
        head.Children.Add(reset);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(host);
        return Sec("Prompt", Card(Pad(stack)));
    }

    UIElement VocabularySection(TranscriptPostProcessingMode mode)
    {
        var chips = VocabularyChips(S);
        var stack = new StackPanel();
        stack.Children.Add(chips);
        chips.Margin = new Thickness(0, 0, 0, 4);
        stack.Children.Add(VocabularyAddRow(S));
        return Sec("Custom Vocabulary", Card(Pad(stack), Pad(Text(VocabularyInput.FooterFor(mode) ?? ""))));
    }

    UIElement FillerSection()
    {
        var host = new StackPanel();
        void Render()
        {
            host.Children.Clear();
            var toggle = Switch(S.IsFillerWordRemovalEnabled, v => { S.IsFillerWordRemovalEnabled = v; Render(); }, "Remove selected filler words");
            var items = new List<UIElement?>
            {
                Row("Remove selected filler words", toggle,
                    "Runs locally on this PC before any AI processing — no AI involved. It removes only the editable words listed here."),
            };
            if (S.IsFillerWordRemovalEnabled) items.Add(Pad(FillerEditor()));
            host.Children.Add(Card(items.ToArray()));
        }
        Render();
        return Sec("Local Filler Word Removal", host);
    }

    UIElement FillerEditor()
    {
        var stack = new StackPanel();
        var label = Text("Words:", "Text.RowValue");
        label.FontSize = 12.5; label.FontWeight = FontWeights.Medium; label.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(label);
        _fillerChips = TermChips(() => S.FillerWordsToRemove,
            w => S.FillerWordsToRemove = S.FillerWordsToRemove.Where(x => x != w).ToList(), "Remove filler word ");
        stack.Children.Add(_fillerChips);

        var (host, box) = Input("Add word…", width: 220, automationName: "Add filler word");
        var add = Btn("Add", () => { });
        string Norm() => box.Text.Trim().ToLowerInvariant();
        void Sync() => add.IsEnabled = Norm().Length > 0 && !S.FillerWordsToRemove.Contains(Norm());
        void Commit()
        {
            if (!add.IsEnabled) return;
            S.FillerWordsToRemove = S.FillerWordsToRemove.Append(Norm()).ToList();
            box.Text = "";
        }
        box.TextChanged += (_, _) => Sync();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
        add.Click += (_, _) => Commit();
        var reset = Btn("Reset to Defaults", () => { S.FillerWordsToRemove = RemySettings.DefaultFillerWords.ToList(); Sync(); }, automationName: "Reset filler words to defaults");
        reset.IsEnabled = !S.FillerWordsToRemove.SequenceEqual(RemySettings.DefaultFillerWords);
        S.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemySettings.FillerWordsToRemove))
                Dispatcher.BeginInvoke(() => { reset.IsEnabled = !S.FillerWordsToRemove.SequenceEqual(RemySettings.DefaultFillerWords); Sync(); });
        };
        Sync();

        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(host);
        add.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(add, 1); row.Children.Add(add);
        Grid.SetColumn(reset, 3); row.Children.Add(reset);
        stack.Children.Add(row);
        return stack;
    }

    UIElement PreparationSection()
    {
        var pill = new Remy.App.Controls.StatusPill();
        var action = Btn("Prepare", () => RefreshProviderNow());
        _prepPill = pill; _prepButton = action;
        UpdatePreparation();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Label("Cleanup model"));
        pill.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(pill, 1); row.Children.Add(pill);
        Grid.SetColumn(action, 2); row.Children.Add(action);
        return Sec("Preparation", Card(Pad(row, v: 13),
            Pad(Text("Ready means the local model has been prepared. Preparing downloads missing local support files before first use. Remote services show Configured; preparation checks their connection and model information."))));
    }

    Remy.App.Controls.StatusPill? _prepPill;
    Button? _prepButton;

    void UpdatePreparation()
    {
        if (_prepPill is null || _prepButton is null) return;
        var hasModel = CurrentModelId.Trim().Length > 0;
        var ok = hasModel && _providerError is null;
        _prepPill.Text = ok ? "Configured" : "Not set up";
        _prepPill.Kind = ok ? Remy.App.Controls.PillKind.Ready : Remy.App.Controls.PillKind.Neutral;
        _prepButton.Content = _providerError is not null ? "Retry" : "Prepare";
        _prepButton.IsEnabled = !_providerChecking && _s!.Dictation.Status == DictationStatus.Idle;
    }

    // ------------------------------------------------------------------ saved profile from current setup

    UIElement SaveAsProfileSection(TranscriptPostProcessingMode mode)
    {
        var msg = Message("", Tone.Warning);
        msg.Margin = new Thickness(0, 8, 0, 0);
        msg.Visibility = Visibility.Collapsed;
        var stack = new StackPanel();
        stack.Children.Add(Text(ProfileStrings.ModeCaption));
        var btn = Btn("Save current setup as a profile", () => SaveCurrentSetupAsProfile(mode, msg));
        btn.Padding = new Thickness(12, 7, 12, 7);
        btn.Margin = new Thickness(0, 10, 0, 0);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(btn);
        stack.Children.Add(msg);
        return Sec("Saved profiles", Card(Pad(stack)));
    }

    void SaveCurrentSetupAsProfile(TranscriptPostProcessingMode mode, TextBlock msg)
    {
        var ollama = mode == TranscriptPostProcessingMode.Ollama;
        var url = (ollama ? S.OllamaBaseURL : S.OpenAICompatibleBaseURL).Trim();
        var model = (ollama ? S.OllamaModel : S.OpenAICompatibleModel).Trim();
        var draft = CleanupProfile.CreateNew() with
        {
            Name = ollama ? "Ollama" : "OpenAI-compatible",
            Adapter = ollama ? CleanupProfileAdapter.Ollama : CleanupProfileAdapter.OpenAICompatible,
            BaseUrl = url, Model = model,
            Prompt = ollama ? S.OllamaPostProcessingPrompt : S.OpenAICompatiblePostProcessingPrompt,
        };
        try
        {
            string? key = null;
            if (!ollama) { try { key = _s!.Credentials.Read(OaService, OaAccount); } catch { } }
            _s!.Profiles!.Load();
            _s.Profiles.Save(draft, string.IsNullOrWhiteSpace(key) ? null : key);
            S.TranscriptPostProcessingMode = TranscriptPostProcessingMode.Profile;
            Build();
        }
        catch (CleanupProfileException ex)
        {
            msg.Text = ProfileStrings.SaveMessage(ex.Error);
            msg.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            msg.Text = ProfileStrings.SaveFailed;
            msg.Visibility = Visibility.Visible;
        }
    }

    void CancelWork()
    {
        _debounce.Cancel();
        _refreshCts?.Cancel();
        _testCts?.Cancel();
    }
}
