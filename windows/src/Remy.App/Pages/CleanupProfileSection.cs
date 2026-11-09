using System.Windows;
using System.Windows.Controls;
using Remy.Core.Cleanup;
using Remy.Core.Settings;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Saved Profile editor (ui.md 3.5.12, cleanup.md 4.6).</summary>
public partial class TranscriptCleanupPage
{
    CleanupProfile? _draft, _saved;
    string? _profileMsg;
    Tone _profileMsgTone = Tone.Info;
    bool _removeKey, _testing;
    StackPanel? _profileHost;
    PasswordBox? _newKeyBox;
    TextBlock? _routeText, _noteText, _profileMessageBlock;
    Button? _testBtn;

    CleanupProfileController? Ctl => _s?.Profiles;

    void BuildProfile()
    {
        if (Ctl is null)
        {
            Root.Children.Add(Sec("Saved profiles", Card(Pad(Text(ProfileStrings.UnavailableWindows)))));
            return;
        }
        _profileMsg = null;
        try { Ctl.Load(); }
        catch (Exception) { _profileMsg = ProfileStrings.LoadFailed; _profileMsgTone = Tone.Warning; }
        LoadSelection();
        _profileHost = new StackPanel();
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = Label(ProfileStrings.Header); title.FontSize = 15; title.FontWeight = FontWeights.SemiBold;
        head.Children.Add(title);
        head.Children.Add(Text(ProfileStrings.Subheader, margin: new Thickness(0, 2, 0, 0)));
        Root.Children.Add(head);
        Root.Children.Add(_profileHost);
        RenderProfile();
        Root.Children.Add(VocabularySection(TranscriptPostProcessingMode.Profile));
    }

    void LoadSelection()
    {
        _saved = Ctl!.Collection.Selected;
        _draft = _saved ?? CleanupProfile.CreateNew();
        _removeKey = false;
    }

    bool ProfileDirty => _draft != _saved;

    void RenderProfile()
    {
        if (_profileHost is null || _draft is null) return;
        _profileHost.Children.Clear();
        var col = Ctl!.Collection;
        var stack = new StackPanel();

        // picker row
        var names = new List<string> { ProfileStrings.UnsavedProfileItem };
        names.AddRange(col.Profiles.Select(p => p.Name));
        var pickerIdx = _saved is null ? 0 : 1 + col.Profiles.ToList().FindIndex(p => p.Id == _saved.Id);
        var combo = new ComboBox { MinWidth = 220 };
        foreach (var n in names) combo.Items.Add(n);
        combo.SelectedIndex = Math.Max(0, pickerIdx);
        System.Windows.Automation.AutomationProperties.SetName(combo, ProfileStrings.PickerLabel);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex == Math.Max(0, pickerIdx)) return;
            SelectProfile(combo.SelectedIndex == 0 ? null : col.Profiles[combo.SelectedIndex - 1].Id);
        };
        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        pickerRow.Children.Add(combo);
        var newBtn = Btn(ProfileStrings.New, () => { _draft = CleanupProfile.CreateNew(); _saved = null; _removeKey = false; _profileMsg = null; RenderProfile(); });
        newBtn.Margin = new Thickness(8, 0, 0, 0);
        var reloadBtn = Btn(ProfileStrings.Reload, ReloadProfiles);
        reloadBtn.Margin = new Thickness(8, 0, 0, 0);
        pickerRow.Children.Add(newBtn); pickerRow.Children.Add(reloadBtn);
        stack.Children.Add(Stacked(ProfileStrings.PickerLabel, pickerRow));
        var discard = Text(ProfileStrings.DiscardNote, margin: new Thickness(0, -6, 0, 14));
        discard.FontSize = 12;
        stack.Children.Add(discard);

        // fields
        var (nameHost, nameBox) = Input(ProfileStrings.DefaultName, _draft.Name, automationName: ProfileStrings.ProfileNameLabel);
        nameBox.TextChanged += (_, _) => Edit(d => d with { Name = nameBox.Text });
        stack.Children.Add(Stacked(ProfileStrings.ProfileNameLabel, nameHost));

        var adapters = new[] { CleanupProfileAdapter.Ollama, CleanupProfileAdapter.OpenAICompatible };
        var proto = Combo(adapters, a => a == CleanupProfileAdapter.Ollama ? ProfileStrings.ProtocolOllama : ProfileStrings.ProtocolOpenAICompatible,
            _draft.Adapter, a => Edit(d => d with
            {
                Adapter = a,
                // Transport must stay valid for the adapter (Ollama has no SSE, OpenAI-compatible no NDJSON).
                Capabilities = d.Capabilities with { Transport = CleanupTransport.CompleteJSON },
            }), ProfileStrings.ProtocolLabel, 220);
        var protoHost = new StackPanel { Orientation = Orientation.Horizontal };
        protoHost.Children.Add(proto);
        stack.Children.Add(Stacked(ProfileStrings.ProtocolLabel, protoHost));

        var (urlHost, urlBox) = Input(ProfileStrings.DefaultBaseUrl, _draft.BaseUrl, automationName: ProfileStrings.BaseUrlLabel);
        urlBox.TextChanged += (_, _) => Edit(d => d with { BaseUrl = urlBox.Text });
        stack.Children.Add(Stacked(ProfileStrings.BaseUrlLabel, urlHost, ProfileStrings.BaseUrlHelp));

        var (modelHost, modelBox) = Input("Model identifier", _draft.Model, automationName: ProfileStrings.ModelIdLabel);
        modelBox.TextChanged += (_, _) => { Edit(d => d with { Model = modelBox.Text }); UpdateModelInfo(); UpdatePreparation(); };
        stack.Children.Add(Stacked(ProfileStrings.ModelIdLabel, modelHost, ProfileStrings.ModelIdHelp));

        // key
        var hasKey = _saved is not null && Ctl.HasStoredKey(_saved);
        var keyCap = Text(hasKey ? ProfileStrings.ApiKeyConfigured : ProfileStrings.ApiKeyNotConfigured, margin: new Thickness(0, 0, 0, 6));
        var (keyHost, keyBox) = Secret(ProfileStrings.NewApiKeyLabel, ProfileStrings.NewApiKeyLabel);
        _newKeyBox = keyBox;
        keyHost.ToolTip = ProfileStrings.NewApiKeyHelp;
        keyBox.PasswordChanged += (_, _) => UpdateProfileDerived();
        var keyBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        keyBlock.Children.Add(keyCap);
        keyBlock.Children.Add(keyHost);
        if (_saved?.Credential is not null)
        {
            var rm = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            rm.Children.Add(Switch(_removeKey, v => { _removeKey = v; UpdateProfileDerived(); }, ProfileStrings.RemoveKeyToggle));
            var rl = Text(ProfileStrings.RemoveKeyToggle, "Text.RowLabel"); rl.Margin = new Thickness(10, 0, 0, 0); rl.VerticalAlignment = VerticalAlignment.Center;
            rm.Children.Add(rl);
            keyBlock.Children.Add(rm);
        }
        _noteText = Text(ProfileStrings.EndpointChangeNote, margin: new Thickness(0, 8, 0, 0));
        keyBlock.Children.Add(_noteText);
        stack.Children.Add(keyBlock);

        // instructions
        var (promptHost, promptBox) = TextArea("Enter cleanup instructions.", _draft.Prompt, 140);
        promptBox.TextChanged += (_, _) => Edit(d => d with { Prompt = promptBox.Text });
        stack.Children.Add(Stacked(ProfileStrings.InstructionsLabel, promptHost));

        // timeout stepper
        var timeoutText = Text(ProfileStrings.TimeoutValue((int)_draft.TimeoutSeconds), "Text.RowValue");
        timeoutText.VerticalAlignment = VerticalAlignment.Center; timeoutText.Margin = new Thickness(10, 0, 10, 0);
        void Step(int d)
        {
            var v = Math.Clamp((int)_draft!.TimeoutSeconds + d, (int)CleanupProfile.MinTimeout, (int)CleanupProfile.MaxTimeout);
            Edit(x => x with { TimeoutSeconds = v });
            timeoutText.Text = ProfileStrings.TimeoutValue(v);
        }
        var trow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var tl = Label(ProfileStrings.TimeoutLabel); tl.VerticalAlignment = VerticalAlignment.Center; tl.Margin = new Thickness(0, 0, 14, 0);
        trow.Children.Add(tl);
        trow.Children.Add(IconBtn("", () => Step(-1), "Decrease timeout"));
        trow.Children.Add(timeoutText);
        trow.Children.Add(IconBtn("", () => Step(1), "Increase timeout"));
        stack.Children.Add(trow);

        _routeText = Text("", margin: new Thickness(0, 0, 0, 12));
        stack.Children.Add(_routeText);

        // buttons
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var save = Btn(ProfileStrings.SaveProfile, SaveProfile, "Remy.PrimaryButton");
        buttons.Children.Add(save);
        _testBtn = Btn(ProfileStrings.TestConnection, TestProfile);
        _testBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_testBtn, 1); buttons.Children.Add(_testBtn);
        if (_saved is not null)
        {
            var del = Btn(ProfileStrings.Delete, DeleteProfile, "Remy.DestructiveButton");
            Grid.SetColumn(del, 3); buttons.Children.Add(del);
        }
        stack.Children.Add(buttons);

        _profileMessageBlock = Text("", margin: new Thickness(0, 12, 0, 0));
        stack.Children.Add(_profileMessageBlock);

        _profileHost.Children.Add(Card(new Border { Padding = new Thickness(18), Child = stack }));
        _profileHost.Margin = new Thickness(0, 0, 0, 24);
        UpdateProfileDerived();
        UpdateModelInfo();
        BuildContext();
    }

    void Edit(Func<CleanupProfile, CleanupProfile> f)
    {
        if (_draft is null) return;
        _draft = f(_draft);
        UpdateProfileDerived();
    }

    void UpdateProfileDerived()
    {
        if (_draft is null) return;
        if (_routeText is not null)
        {
            _routeText.Text = CleanupEndpointIdentity.TryCreate(_draft.BaseUrl, false, out var id)
                ? (id!.IsLoopback ? ProfileStrings.RouteLoopbackWindows : ProfileStrings.RouteRemote)
                : ProfileStrings.RouteUnverified;
        }
        if (_noteText is not null)
            _noteText.Visibility = _saved is not null && _saved.BaseUrl != _draft.BaseUrl ? Visibility.Visible : Visibility.Collapsed;
        if (_testBtn is not null)
        {
            var clean = _saved is not null && !ProfileDirty && string.IsNullOrEmpty(_newKeyBox?.Password) && !_removeKey;
            _testBtn.Content = _testing ? ProfileStrings.TestConnectionBusy : ProfileStrings.TestConnection;
            _testBtn.IsEnabled = clean && !_testing && DictationIdle;
            _testBtn.ToolTip = ProfileStrings.TestConnectionTooltip;
        }
        if (_profileMessageBlock is not null)
        {
            _profileMessageBlock.Text = _profileMsg ?? "";
            _profileMessageBlock.Visibility = _profileMsg is null ? Visibility.Collapsed : Visibility.Visible;
            SetTone(_profileMessageBlock, _profileMsgTone);
        }
    }

    void SetProfileMessage(string? text, Tone tone = Tone.Info)
    {
        _profileMsg = text; _profileMsgTone = tone;
        UpdateProfileDerived();
    }

    void SelectProfile(Guid? id)
    {
        try
        {
            Ctl!.Select(id);
            LoadSelection();
            _profileMsg = null;
        }
        catch (Exception) { _profileMsg = ProfileStrings.SelectFailed; _profileMsgTone = Tone.Warning; }
        RenderProfile();
    }

    void ReloadProfiles()
    {
        try { Ctl!.Load(); LoadSelection(); _profileMsg = null; }
        catch (Exception) { _profileMsg = ProfileStrings.ReloadFailed; _profileMsgTone = Tone.Warning; }
        RenderProfile();
    }

    void SaveProfile()
    {
        if (_draft is null) return;
        var key = _newKeyBox?.Password;
        try
        {
            var saved = Ctl!.Save(_draft, string.IsNullOrWhiteSpace(key) ? null : key, _removeKey);
            _saved = saved; _draft = saved; _removeKey = false;
            _profileMsg = ProfileStrings.Saved; _profileMsgTone = Tone.Success;
        }
        catch (CleanupProfileException ex) { _profileMsg = ProfileStrings.SaveMessage(ex.Error); _profileMsgTone = Tone.Warning; }
        catch (Exception) { _profileMsg = ProfileStrings.SaveFailed; _profileMsgTone = Tone.Warning; }
        RenderProfile();
    }

    void DeleteProfile()
    {
        if (_saved is null) return;
        if (!Confirm(this, ProfileStrings.DeleteConfirmTitle, ProfileStrings.DeleteNote, ProfileStrings.DeleteConfirmButton)) return;
        try
        {
            Ctl!.Remove(_saved.Id);
            LoadSelection();
            _profileMsg = null;
        }
        catch (Exception) { _profileMsg = ProfileStrings.DeleteFailed; _profileMsgTone = Tone.Warning; }
        RenderProfile();
    }

    async void TestProfile()
    {
        if (_saved is null || _testing) return;
        var saved = _saved;
        _testing = true; SetProfileMessage(null); UpdateProfileDerived();
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource();
        try
        {
            var policy = new ContextPolicy(false, false, S.WritingStylesSnapshot(), S.S1MiniAppStyling, S.DictationAppRules);
            var runtime = Ctl!.BuildRuntime(saved, policy);
            var result = await CleanupDiscovery.TestConnectionAsync(_s!.CleanupRouter, runtime, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (ProfileDirty) SetProfileMessage(ProfileStrings.ConnectionFinishedSaveFirst);
            else if (result.Passed) SetProfileMessage(ProfileStrings.ConnectionPassed, Tone.Success);
            else SetProfileMessage(ProfileStrings.ConnectionFailed, Tone.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { SetProfileMessage(ProfileStrings.ConnectionFailed, Tone.Warning); }
        finally { _testing = false; UpdateProfileDerived(); }
    }
}
