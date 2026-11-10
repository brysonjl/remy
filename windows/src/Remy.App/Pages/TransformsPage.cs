using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Remy.App.Controls;
using Remy.App.Services;
using Remy.App.ViewModels;
using Remy.Core.Hotkeys;
using Remy.Core.Settings;
using Remy.Core.Transforms;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Transforms page (v0.2-features.md §9.6), built in code: provider row, one card per transform, add button, hint.</summary>
public sealed class TransformsPage : UserControl, IServicePage
{
    const string Subtitle = "Select text in any app and press a shortcut. Remy rewrites the selection in place with your Transcript Cleanup provider.";
    const string ProviderCaption = "Running a transform sends the selected text and its instructions to this provider. A local endpoint keeps them on this PC; remote endpoints must use HTTPS.";
    const string HintText = "Transforms accept 1–3,000 selected words. Your cancel shortcut stops a running transform; use Ctrl+Z in the app to undo a replacement. If focus moves before the result is ready, Remy copies it instead of pasting. Transforms aren't saved to History.";

    readonly StackPanel _root = new();
    AppServices? _s;
    TransformStore? _store;
    bool _recording;
    CancellationTokenSource? _cts;
    readonly Dictionary<Guid, string> _errors = [];
    readonly List<FrameworkElement> _cards = [];
    Button? _addButton;

    public TransformsPage()
    {
        Content = new PageScaffold { Title = "Transforms", Subtitle = Subtitle, Content = _root };
        Loaded += (_, _) => { if (_s is not null) _s.Settings.PropertyChanged += OnSettings; };
        Unloaded += (_, _) => { if (_s is not null) _s.Settings.PropertyChanged -= OnSettings; };
    }

    public void Attach(AppServices services)
    {
        _s = services;
        _store = new TransformStore(services.Settings);
        if (IsLoaded) services.Settings.PropertyChanged += OnSettings;
        Build();
    }

    void OnSettings(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemySettings.TranscriptPostProcessingMode))
            Dispatcher.BeginInvoke(() => { if (!_recording) Build(); });
    }

    void OpenCleanup() => (Window.GetWindow(this)?.DataContext as ShellViewModel)?.Navigate(AppPage.TranscriptCleanup);

    void Reconfigure() => _s!.Dictation.ApplySettingsChange(nameof(RemySettings.TextTransforms));

    // ------------------------------------------------------------------ build

    void Build()
    {
        if (_s is null || _store is null) return;
        _root.Children.Clear();
        _cards.Clear();
        _errors.Where(kv => _store.Transform(kv.Key) is null).Select(kv => kv.Key).ToList().ForEach(k => _errors.Remove(k));

        _root.Children.Add(Sec("Provider", ProviderCard()));

        foreach (var t in _store.Transforms)
        {
            var card = TransformCard(t);
            _cards.Add(card);
            _root.Children.Add(Sec(t.BuiltIn is null ? "Custom" : "Built-in", card));
        }

        if (_store.CanAddCustom)
        {
            _addButton = new Button { Style = St("Remy.AddButton"), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Icon("", 12.5, "Brush.AccentDeep"));
            var label = new TextBlock { Text = "Add custom transform", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(StyleProperty, "Text.Base");
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentDeep");
            row.Children.Add(label);
            _addButton.Content = row;
            _addButton.Click += (_, _) => { if (_store.AddCustom() is not null) { Reconfigure(); Build(); } };
            _root.Children.Add(_addButton);
        }
        else _addButton = null;

        _root.Children.Add(Text($"{_store.CustomCount} of {TransformStore.MaximumCustomTransforms} custom transforms.", margin: new Thickness(0, 0, 0, 12)));
        _root.Children.Add(new Hint { Glyph = "", Text = HintText });
        ApplyRecordingState();
    }

    UIElement ProviderCard()
    {
        var mode = _s!.Settings.TranscriptPostProcessingMode;
        var notice = TransformProviderRoute.ModeNotice(mode);
        return notice is not null
            ? Card(Row(notice.Title, Btn("Open Transcript Cleanup", OpenCleanup), notice.Detail))
            : Card(Row(TransformProviderRoute.ModeDisplayName(mode), Btn("Change", OpenCleanup), ProviderCaption));
    }

    FrameworkElement TransformCard(TextTransform t)
    {
        var id = t.Id;
        var name = t.DisplayName;

        // header
        var header = new Grid { Margin = new Thickness(16) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement title;
        if (t.BuiltIn is null)
        {
            var (host, box) = Input("Transform name", t.Name, automationName: "Transform name");
            box.TextChanged += (_, _) => _store!.SetName(box.Text, id);
            host.Margin = new Thickness(0, 0, 16, 0);
            title = host;
        }
        else
        {
            var tb = Text(name, "Text.RowLabel");
            tb.FontSize = 13.5; tb.FontWeight = FontWeights.Medium; tb.VerticalAlignment = VerticalAlignment.Center;
            title = tb;
        }
        header.Children.Add(title);
        var sw = Switch(t.IsEnabled, on => { _store!.SetEnabled(on, id); Reconfigure(); }, $"Enable {name}");
        Grid.SetColumn(sw, 1);
        header.Children.Add(sw);
        header.Tag = sw;

        // shortcut
        var shortcutRow = Row("Shortcut", ShortcutCell(t), "Select text in another app, then press this shortcut.");
        var messages = new StackPanel { Margin = new Thickness(16, 0, 16, 10) };
        if (_errors.TryGetValue(id, out var err))
            messages.Children.Add(new FieldMessage { Text = err, Tone = Tone.Error, Margin = new Thickness(0, 0, 0, 4) });
        if (t.Shortcut is { HasBinding: true } sc)
        {
            if (ConflictDetector.SystemConflictMessage(sc) is { } sys)
                messages.Children.Add(new FieldMessage { Text = sys, Tone = Tone.Warning, Margin = new Thickness(0, 0, 0, 4) });
            if (TransformShortcutRules.TypingCharacterWarning(sc) is { } typing)
                messages.Children.Add(new FieldMessage { Text = typing, Tone = Tone.Warning, Margin = new Thickness(0, 0, 0, 4) });
        }

        // instructions
        var (area, prompt) = TextArea("Describe how to rewrite the selected text.", t.Prompt);
        System.Windows.Automation.AutomationProperties.SetName(prompt, $"{name} instructions");
        Button action;
        if (t.BuiltIn is not null)
        {
            action = Btn("Reset to default", () => { _store!.ResetPrompt(id); prompt.Text = _store.Transform(id)?.Prompt ?? ""; }, automationName: $"Reset {name} to default");
            action.IsEnabled = t.HasCustomizedPrompt && !_recording;
            prompt.TextChanged += (_, _) =>
            {
                _store!.SetPrompt(prompt.Text, id);
                action.IsEnabled = _store.Transform(id)?.HasCustomizedPrompt == true && !_recording;
            };
        }
        else
        {
            action = Btn("Delete transform", () =>
            {
                var current = _store!.Transform(id)?.DisplayName ?? name;
                if (!Confirm(this, $"Delete “{current}”?", "Its instructions and shortcut are removed.", "Delete")) return;
                _store.RemoveCustom(id);
                Reconfigure();
                Build();
            }, "Remy.DestructiveButton", $"Delete {name}");
            action.IsEnabled = !_recording;
            prompt.TextChanged += (_, _) => _store!.SetPrompt(prompt.Text, id);
        }
        action.HorizontalAlignment = HorizontalAlignment.Right;
        action.Margin = new Thickness(0, 10, 0, 0);
        var promptStack = new StackPanel();
        promptStack.Children.Add(area);
        promptStack.Children.Add(action);

        var card = Card(header, shortcutRow, messages.Children.Count > 0 ? messages : null, Pad(promptStack, 16, 16));
        card.Tag = new ValueTuple<Guid, ToggleButton, Button>(id, sw, action);
        return card;
    }

    // ------------------------------------------------------------------ shortcut recorder

    UIElement ShortcutCell(TextTransform t)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var id = t.Id;
        if (_recording && _activeId == id)
        {
            var prompt = new TextBlock { Text = "Press any key combo…", FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            prompt.SetResourceReference(StyleProperty, "Text.Base");
            prompt.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentDeep");
            panel.Children.Add(prompt);
            var cancel = Btn("Cancel", () => _cts?.Cancel(), automationName: $"Cancel recording {t.DisplayName} shortcut");
            cancel.Margin = new Thickness(14, 0, 0, 0);
            panel.Children.Add(cancel);
            return panel;
        }
        var recordName = $"{t.DisplayName} shortcut";
        if (t.Shortcut is { HasBinding: true } s)
        {
            foreach (var cap in RecorderViewModel.Tokenize(s.DisplayName))
                panel.Children.Add(new ContentControl { Content = cap, Style = St("Remy.Keycap"), Margin = new Thickness(0, 0, 5, 0) });
            var change = Btn("Change", () => _ = RecordAsync(t), "Remy.PrimaryButton", $"Change {recordName}");
            change.Margin = new Thickness(9, 0, 0, 0);
            var clear = Btn("Clear", () => { _errors.Remove(id); _store!.ClearShortcut(id); Reconfigure(); Build(); }, automationName: $"Clear {recordName}");
            clear.Margin = new Thickness(14, 0, 0, 0);
            panel.Children.Add(change);
            panel.Children.Add(clear);
        }
        else
        {
            var none = new TextBlock { Text = "Not set", FontSize = 13, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            none.SetResourceReference(StyleProperty, "Text.Base");
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            panel.Children.Add(none);
            var rec = Btn("Record Shortcut", () => _ = RecordAsync(t), "Remy.PrimaryButton", $"Record {recordName}");
            rec.Margin = new Thickness(14, 0, 0, 0);
            panel.Children.Add(rec);
        }
        return panel;
    }

    Guid _activeId;

    async Task RecordAsync(TextTransform t)
    {
        if (_recording || _s is null || _store is null) return;
        _recording = true; _activeId = t.Id;
        _cts = new CancellationTokenSource();
        _errors.Remove(t.Id);
        Build();
        HotkeyBinding? captured = null;
        try { captured = await _s.Hotkeys.RecordShortcutAsync(allowEscape: false, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { _recording = false; _cts.Dispose(); _cts = null; }

        if (captured is { HasBinding: true })
        {
            var cand = HotkeyRules.Canonicalize(captured with { Id = t.Id, Mode = HotkeyMode.HandsFreeToggle });
            var cancel = _s.Settings.CancelShortcut is { HasBinding: true } c ? c : null;
            var conflict = TransformShortcutRules.TransformShortcutConflict(cand, t.Id, _store.Transforms, _s.Settings.HotkeyBindings, cancel);
            if (conflict is not null) _errors[t.Id] = conflict;
            else
            {
                _errors.Remove(t.Id);
                _store.SetShortcut(t.Id, cand.VirtualKey, cand.Modifiers);
                Reconfigure();
            }
        }
        Build();
    }

    /// <summary>While a recorder runs, every other card and the add button are disabled.</summary>
    void ApplyRecordingState()
    {
        foreach (var card in _cards)
        {
            if (card.Tag is not ValueTuple<Guid, ToggleButton, Button> tag) continue;
            var mine = tag.Item1 == _activeId && _recording;
            card.IsEnabled = !_recording || mine;
            if (mine) { tag.Item2.IsEnabled = false; tag.Item3.IsEnabled = false; }
        }
        if (_addButton is not null) _addButton.IsEnabled = !_recording;
    }
}
