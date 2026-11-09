using System.IO;
using System.Windows;
using System.Windows.Controls;
using Remy.Core.Cleanup;
using Remy.Core.Settings;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Ollama, OpenRouter and OpenAI-compatible method sections (ui.md 3.5.9 - 3.5.11).</summary>
public partial class TranscriptCleanupPage
{
    const string OrService = "io.github.brysonjl.remy.openrouter", OrAccount = "openrouter-api-key";
    const string OaService = "io.github.brysonjl.remy.openai-compatible", OaAccount = "openai-compatible-api-key";

    CancellationTokenSource? _refreshCts, _testCts;
    bool _providerChecking;
    string? _providerError;

    TextBlock? _statusBlock, _keyState, _keyError;
    Button? _refreshBtn, _clearKeyBtn;
    StackPanel? _chipsHost, _reasoningHost, _searchHost;
    TextBox? _modelBox;

    // Ollama
    List<string> _ollamaModels = [];
    bool _ollamaChecked;
    OllamaReasoningInfo? _ollamaReasoning;
    // OpenRouter
    IReadOnlyList<OpenRouterModelEntry> _orEntries = [];
    bool _orFetched;
    OpenRouterModelInfo? _orInfo;
    // OpenAI-compatible
    List<string> _oaModels = [];
    bool _oaChecked;

    void ResetProviderState()
    {
        _providerChecking = false; _providerError = null;
        _statusBlock = _keyState = _keyError = null;
        _refreshBtn = _clearKeyBtn = null;
        _chipsHost = _reasoningHost = _searchHost = null;
        _modelBox = null;
        _ollamaModels = []; _ollamaChecked = false; _ollamaReasoning = null;
        _orEntries = []; _orFetched = false; _orInfo = null;
        _oaModels = []; _oaChecked = false;
        _prepPill = null; _prepButton = null;
    }

    void StartInitialRefresh(TranscriptPostProcessingMode mode)
    {
        if (mode is TranscriptPostProcessingMode.Ollama or TranscriptPostProcessingMode.OpenRouter or TranscriptPostProcessingMode.OpenAICompatible)
            RefreshProviderNow();
    }

    async void RefreshProviderNow()
    {
        try
        {
            switch (_builtMode)
            {
                case TranscriptPostProcessingMode.Ollama: await RefreshOllamaAsync(true); break;
                case TranscriptPostProcessingMode.OpenRouter: await RefreshOpenRouterAsync(true); break;
                case TranscriptPostProcessingMode.OpenAICompatible: await RefreshOpenAIAsync(); break;
            }
        }
        catch (Exception) { /* status already shows the failure */ }
    }

    void ScheduleRefresh(bool relist) => _debounce.Run(350, async () =>
    {
        try
        {
            switch (_builtMode)
            {
                case TranscriptPostProcessingMode.Ollama: await RefreshOllamaAsync(relist); break;
                case TranscriptPostProcessingMode.OpenRouter: await RefreshOpenRouterAsync(relist); break;
                case TranscriptPostProcessingMode.OpenAICompatible: await RefreshOpenAIAsync(); break;
            }
        }
        catch (Exception) { }
    });

    string? StoredKey(string svc, string acct)
    {
        try { var k = _s!.Credentials.Read(svc, acct); return string.IsNullOrWhiteSpace(k) ? null : k; } catch { return null; }
    }

    // ------------------------------------------------------------------ shared row builders

    /// <summary>Status line on the left, buttons on the right, optional extra content below (chips).</summary>
    UIElement StatusBlock(IEnumerable<Button> buttons, UIElement? below = null)
    {
        _statusBlock = Message("");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusBlock.Margin = new Thickness(0, 0, 14, 0);
        g.Children.Add(_statusBlock);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 0, 8); b.HorizontalAlignment = HorizontalAlignment.Stretch; right.Children.Add(b); }
        if (right.Children.Count > 0) ((FrameworkElement)right.Children[^1]).Margin = new Thickness(0);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        var s = new StackPanel();
        s.Children.Add(g);
        if (below is not null) s.Children.Add(below);
        return Pad(s, v: 13);
    }

    void SetStatus(string text, Tone tone)
    {
        if (_statusBlock is null) return;
        _statusBlock.Text = text;
        SetTone(_statusBlock, tone);
    }

    Button MakeRefreshButton()
    {
        _refreshBtn = Btn("Refresh Models", RefreshProviderNow);
        return _refreshBtn;
    }

    void UpdateRefreshButton()
    {
        if (_refreshBtn is null) return;
        _refreshBtn.Content = _providerChecking ? "Checking…" : "Refresh Models";
        _refreshBtn.IsEnabled = !_providerChecking;
    }

    UIElement ModelField(string placeholder, string automation, Func<string> get, Action<string> set)
    {
        var (host, box) = Input(placeholder, get(), automationName: automation);
        _modelBox = box;
        box.TextChanged += (_, _) =>
        {
            if (box.Text == get()) return;
            set(box.Text);
            UpdateModelInfo();
            BuildContextIfSupportChanged();
            OnModelTextChanged();
        };
        return Field("Model ID", host);
    }

    Remy.Core.Settings.TranscriptPostProcessingMode _ctxModeBuilt;
    CtxSupport _ctxSupportBuilt;

    void BuildContextIfSupportChanged()
    {
        var sup = Support(_builtMode);
        if (sup != _ctxSupportBuilt) { _ctxSupportBuilt = sup; BuildContext(); }
    }

    void OnModelTextChanged()
    {
        switch (_builtMode)
        {
            case TranscriptPostProcessingMode.Ollama: RenderOllama(); ScheduleRefresh(false); break;
            case TranscriptPostProcessingMode.OpenRouter: RenderOpenRouter(); ScheduleRefresh(false); break;
            case TranscriptPostProcessingMode.OpenAICompatible: RenderOpenAI(); break;
        }
        UpdatePreparation();
    }

    UIElement UrlField(string label, string placeholder, string automation, Func<string> get, Action<string> set)
    {
        var (host, box) = Input(placeholder, get(), automationName: automation);
        box.TextChanged += (_, _) =>
        {
            if (box.Text == get()) return;
            set(box.Text);
            BuildContextIfSupportChanged();
            ScheduleRefresh(true);
        };
        return Field(label, host);
    }

    /// <summary>Password field row. Stored keys are never shown; Enter, focus loss or Save writes the entry to Credential Manager.</summary>
    UIElement KeyRow(string label, string placeholder, string svc, string acct, string automation)
    {
        var (host, box) = Secret(placeholder, automation);
        var save = Btn("Save", () => SaveKey());
        save.Margin = new Thickness(8, 0, 0, 0);
        _keyState = Text("", margin: new Thickness(0, 6, 0, 0));
        _keyError = Message("", Tone.Error);
        _keyError.Margin = new Thickness(0, 4, 0, 0);
        _keyError.Visibility = Visibility.Collapsed;
        UpdateKeyUi(svc, acct);

        void SaveKey()
        {
            var v = box.Password.Trim();
            if (v.Length == 0) return;
            try
            {
                _s!.Credentials.Write(svc, acct, v);
                box.Password = "";
                _keyError!.Visibility = Visibility.Collapsed;
            }
            catch (InvalidOperationException ex) { _keyError!.Text = ex.Message; _keyError.Visibility = Visibility.Visible; }
            catch (Exception) { _keyError!.Text = "The key could not be saved to Credential Manager."; _keyError.Visibility = Visibility.Visible; }
            UpdateKeyUi(svc, acct);
            if (_builtMode == TranscriptPostProcessingMode.OpenRouter) RenderOpenRouter();
            ScheduleRefresh(true);
        }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveKey(); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => SaveKey();

        var line = new Grid { Width = 320 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(host);
        Grid.SetColumn(save, 1); line.Children.Add(save);
        var right = new StackPanel { Width = 320 };
        right.Children.Add(line);
        right.Children.Add(_keyState);
        right.Children.Add(_keyError);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Label(label); l.VerticalAlignment = VerticalAlignment.Top; l.Margin = new Thickness(0, 8, 0, 0);
        g.Children.Add(l);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return Pad(g, v: 10);
    }

    Button MakeClearKeyButton(string svc, string acct)
    {
        _clearKeyBtn = Btn("Clear Stored Key", () =>
        {
            try { _s!.Credentials.Delete(svc, acct); } catch { }
            UpdateKeyUi(svc, acct);
            if (_builtMode == TranscriptPostProcessingMode.OpenRouter) RenderOpenRouter();
            ScheduleRefresh(true);
        }, "Remy.DestructiveButton");
        _clearKeyBtn.Visibility = StoredKey(svc, acct) is null ? Visibility.Collapsed : Visibility.Visible;
        return _clearKeyBtn;
    }

    void UpdateKeyUi(string svc, string acct)
    {
        var has = StoredKey(svc, acct) is not null;
        if (_keyState is not null) _keyState.Text = has ? "A key is stored in Credential Manager." : "No key stored.";
        if (_clearKeyBtn is not null) _clearKeyBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    UIElement ReasoningSection(string name, bool shown, bool value, bool forcedOn, Action<bool> set, string caption)
    {
        if (!shown) return new StackPanel();
        var toggle = Switch(forcedOn || value, set, $"Allow {name} cleanup reasoning");
        toggle.IsEnabled = !forcedOn;
        return Sec("Cleanup Reasoning", Card(Row("Allow reasoning", toggle, caption)));
    }

    // ------------------------------------------------------------------ Ollama

    static bool OllamaInstalled()
    {
        try
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (local is not null && File.Exists(Path.Combine(local, "Programs", "Ollama", "ollama.exe"))) return true;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(dir.Trim('"'), "ollama.exe"))) return true;
        }
        catch { }
        return false;
    }

    void BuildOllama()
    {
        _ctxSupportBuilt = Support(TranscriptPostProcessingMode.Ollama);
        var items = new List<UIElement?>();
        if (!OllamaInstalled())
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Icon("", 18, "Brush.AccentDeep"); icon.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(icon);
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Label("Not installed"); title.FontWeight = FontWeights.SemiBold;
            t.Children.Add(title);
            t.Children.Add(Text("Local models · Free · Runs on this PC"));
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var dl = Btn("Download Ollama", () => OpenUrl("https://ollama.com/download"), "Remy.PrimaryButton");
            Grid.SetColumn(dl, 2); g.Children.Add(dl);
            items.Add(Pad(g));
            items.Add(Pad(Text("Install Ollama to run your own cleanup model. Choose an installed model by its exact ID. You can also leave it uninstalled and point the server URL below at a remote Ollama server.")));
        }
        items.Add(UrlField("Server URL", "http://127.0.0.1:11434", "Ollama server URL", () => S.OllamaBaseURL, v => S.OllamaBaseURL = v));
        items.Add(ModelField("Enter an installed model name", "Ollama model", () => S.OllamaModel, v => S.OllamaModel = v));
        _chipsHost = new StackPanel();
        items.Add(StatusBlock([MakeRefreshButton()], _chipsHost));
        items.Add(Pad(Text("Ollama is the provider; the Model ID above selects the cleanup model. Use an installed model trained to preserve names, numbers and meaning. The server URL determines where processing runs.")));
        Root.Children.Add(Sec("Ollama", Card(items.ToArray())));
        Root.Children.Add(_reasoningHost = new StackPanel());
        Root.Children.Add(PromptSection("Ollama prompt", () => S.OllamaPostProcessingPrompt, v => S.OllamaPostProcessingPrompt = v));
        Root.Children.Add(VocabularySection(TranscriptPostProcessingMode.Ollama));
        RenderOllama();
    }

    async Task RefreshOllamaAsync(bool relist)
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var ct = cts.Token;
        var url = S.OllamaBaseURL.Trim();
        try
        {
            if (relist)
            {
                if (url.Length == 0) { _ollamaChecked = false; _ollamaModels = []; _providerError = null; _ollamaReasoning = null; RenderOllama(); return; }
                _providerChecking = true; RenderOllama();
                var r = await _s!.CleanupDiscovery.ListOllamaModelsAsync(url, null, ct);
                ct.ThrowIfCancellationRequested();
                if (r.Ok) { _providerError = null; _ollamaChecked = true; _ollamaModels = r.Models.ToList(); }
                else { _providerError = r.Failure?.Message ?? RemyCleanupFailure.UnknownMessage; _ollamaChecked = false; _ollamaModels = []; }
            }
            _ollamaReasoning = null;
            var match = CleanupDiscovery.MatchingInstalledModel(S.OllamaModel, _ollamaModels);
            if (match is not null && _providerError is null)
            {
                var (info, _) = await _s!.CleanupDiscovery.GetOllamaReasoningAsync(url, match, null, ct);
                ct.ThrowIfCancellationRequested();
                _ollamaReasoning = info;
            }
        }
        catch (OperationCanceledException) { return; }
        finally { if (_refreshCts == cts) { _providerChecking = false; RenderOllama(); } }
    }

    void RenderOllama()
    {
        if (_statusBlock is null) return;
        UpdateRefreshButton();
        var model = S.OllamaModel.Trim();
        var match = CleanupDiscovery.MatchingInstalledModel(model, _ollamaModels);
        (string, Tone) st;
        if (_providerChecking) st = ("Checking Ollama…", Tone.Info);
        else if (_providerError is not null) st = (_providerError, Tone.Error);
        else if (_ollamaChecked && _ollamaModels.Count == 0) st = ("Connected, but no Ollama models are installed yet.", Tone.Warning);
        else if (_ollamaChecked && model.Length == 0) st = ("Connected. Choose an installed model below or enter one manually.", Tone.Success);
        else if (_ollamaChecked && match is not null) st = ($"Connected. {match} is available.", Tone.Success);
        else if (_ollamaChecked) st = ($"Connected, but {model} is not installed on this Ollama server.", Tone.Warning);
        else st = ("Enter your Ollama server URL to check connectivity.", Tone.Info);
        SetStatus(st.Item1, st.Item2);

        if (_chipsHost is not null)
        {
            _chipsHost.Children.Clear();
            if (_ollamaModels.Count > 0)
            {
                var l = Text("Installed Models", "Text.RowValue", new Thickness(0, 12, 0, 8));
                l.FontSize = 12; l.FontWeight = FontWeights.SemiBold;
                _chipsHost.Children.Add(l);
                var flow = Flow();
                foreach (var m in _ollamaModels)
                {
                    var name = m;
                    flow.Children.Add(Chip(name, () => { if (_modelBox is not null) _modelBox.Text = name; else S.OllamaModel = name; }, selected: name == match));
                }
                _chipsHost.Children.Add(flow);
            }
        }

        if (_reasoningHost is not null)
        {
            _reasoningHost.Children.Clear();
            var cap = _ollamaReasoning?.Capability ?? OllamaReasoningCapability.Unsupported;
            var required = cap == OllamaReasoningCapability.Required;
            _reasoningHost.Children.Add(ReasoningSection("Ollama", cap != OllamaReasoningCapability.Unsupported, S.OllamaReasoningEnabled, required,
                v => S.OllamaReasoningEnabled = v,
                required ? "This model cannot turn reasoning off. Its model/server recommendation is preserved."
                         : "On preserves the model/server recommendation. Off can reduce wait time, but may affect corrections."));
        }
        UpdatePreparation();
    }

    // ------------------------------------------------------------------ OpenRouter

    string OrEnvName => string.IsNullOrWhiteSpace(S.OpenRouterAPIKeyEnvironmentVariable) ? "OPENROUTER_API_KEY" : S.OpenRouterAPIKeyEnvironmentVariable.Trim();

    /// <summary>Which credential applies, in the Mac order: stored key, literal key typed into the variable field, environment variable.</summary>
    (bool Has, string Sentence) OrCredential()
    {
        if (StoredKey(OrService, OrAccount) is not null) return (true, "OpenRouter API key is stored securely in Credential Manager.");
        var raw = S.OpenRouterAPIKeyEnvironmentVariable.Trim();
        if (raw.StartsWith("sk-or-", StringComparison.Ordinal)) return (true, "OpenRouter API key was pasted directly into the environment variable field.");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OrEnvName))) return (true, $"OpenRouter API key was loaded from {OrEnvName}.");
        return (false, "No OpenRouter API key is configured.");
    }

    void BuildOpenRouter()
    {
        _ctxSupportBuilt = Support(TranscriptPostProcessingMode.OpenRouter);
        var items = new List<UIElement?>
        {
            KeyRow("API key", "Paste OpenRouter API key", OrService, OrAccount, "OpenRouter API key"),
        };
        var (envHost, envBox) = Input("OPENROUTER_API_KEY", S.OpenRouterAPIKeyEnvironmentVariable, automationName: "OpenRouter API key variable");
        envBox.TextChanged += (_, _) => { S.OpenRouterAPIKeyEnvironmentVariable = envBox.Text; RenderOpenRouter(); };
        items.Add(Field("API key variable (optional)", envHost));
        items.Add(ModelField("openai/gpt-5-mini", "OpenRouter model", () => S.OpenRouterModel, v => S.OpenRouterModel = v));
        var browse = Btn("Browse Models", () => OpenUrl("https://openrouter.ai/models"));
        items.Add(StatusBlock([MakeRefreshButton(), browse, MakeClearKeyButton(OrService, OrAccount)]));
        items.Add(Pad(Text("Runs transcript cleanup through OpenRouter's cloud API. Paste a key directly to store it in Credential Manager, or leave the API key field blank and use the optional environment variable setting instead.")));
        Root.Children.Add(Sec("OpenRouter", Card(items.ToArray())));
        Root.Children.Add(_searchHost = new StackPanel());
        Root.Children.Add(_reasoningHost = new StackPanel());
        Root.Children.Add(PromptSection("OpenRouter prompt", () => S.OpenRouterPostProcessingPrompt, v => S.OpenRouterPostProcessingPrompt = v));
        Root.Children.Add(VocabularySection(TranscriptPostProcessingMode.OpenRouter));
        RenderOpenRouter();
    }

    async Task RefreshOpenRouterAsync(bool relist)
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            if (relist)
            {
                _providerChecking = true; RenderOpenRouter();
                var (models, failure) = await _s!.CleanupDiscovery.ListOpenRouterModelsAsync(ct, refresh: true);
                ct.ThrowIfCancellationRequested();
                if (failure is null) { _providerError = null; _orEntries = models; _orFetched = true; }
                else { _providerError = failure.Message; _orEntries = []; _orFetched = false; }
            }
            _orInfo = null;
            if (_orFetched && S.OpenRouterModel.Trim().Length > 0)
            {
                _orInfo = await _s!.CleanupDiscovery.GetOpenRouterModelAsync(S.OpenRouterModel, ct);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { return; }
        finally { if (_refreshCts == cts) { _providerChecking = false; RenderOpenRouter(); } }
    }

    (string, Tone) OpenRouterStatus()
    {
        if (_providerChecking) return ("Checking OpenRouter…", Tone.Info);
        if (_providerError is not null) return (_providerError, Tone.Error);
        var (has, sentence) = OrCredential();
        if (!has) return ($"No OpenRouter API key is configured yet. Paste one above or set {OrEnvName} in the app environment.", Tone.Warning);
        if (!_orFetched) return ("Refresh models to validate your OpenRouter setup and search the available catalog.", Tone.Info);
        var model = S.OpenRouterModel.Trim();
        if (model.Length == 0) return ($"{sentence} Enter a model id above or search the fetched catalog below.", Tone.Success);
        var lookup = OpenRouterModelInfo.CatalogLookupId(model);
        var entry = _orEntries.FirstOrDefault(e => e.Id.Equals(lookup, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return ($"{sentence} {model} was not found in the latest OpenRouter model refresh.", Tone.Warning);
        var reference = OpenRouterModelInfo.HasVariantSuffix(model)
            ? $"{model} is valid on OpenRouter and uses the {entry.Id} catalog entry"
            : $"{entry.Id} is available on OpenRouter";
        return entry.SupportsStructuredOutputs
            ? ($"{sentence} {reference}, which advertises structured output support.", Tone.Success)
            : ($"{sentence} {reference}, but it does not advertise structured outputs. Remy will fall back to prompt-based JSON parsing if needed.", Tone.Warning);
    }

    void RenderOpenRouter()
    {
        if (_statusBlock is null) return;
        UpdateRefreshButton();
        var (text, tone) = OpenRouterStatus();
        SetStatus(text, tone);

        if (_searchHost is not null)
        {
            _searchHost.Children.Clear();
            if (_orFetched) _searchHost.Children.Add(OpenRouterSearch());
        }
        if (_reasoningHost is not null)
        {
            _reasoningHost.Children.Clear();
            var info = _orInfo;
            var mandatory = info?.ReasoningMandatory == true;
            var shown = info is not null && info.ReasoningPresent && (info.CanDisableReasoning || mandatory);
            _reasoningHost.Children.Add(ReasoningSection("OpenRouter", shown, S.OpenRouterReasoningEnabled, mandatory,
                v => S.OpenRouterReasoningEnabled = v,
                mandatory ? "This model requires reasoning, so it cannot be turned off."
                          : "On preserves the provider's recommended reasoning setting and effort. Off can reduce wait time, but may affect corrections."));
        }
        UpdatePreparation();
    }

    UIElement OpenRouterSearch()
    {
        var q = S.OpenRouterModel.Trim();
        var items = new List<UIElement?>();
        var matches = q.Length == 0 ? [] : _orEntries
            .Where(e => e.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Id.Equals(q, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.SupportsStructuredOutputs)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Take(12).ToList();
        if (matches.Count == 0)
            items.Add(Pad(Text(q.Length == 0
                ? "Type part of a model id to filter the fetched OpenRouter catalog, or use Browse Models to pick one from openrouter.ai."
                : $"No fetched OpenRouter models matched {q}. Try a broader search term or browse the full model directory.")));
        foreach (var m in matches)
        {
            var id = m.Id;
            var g = new Grid { Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            var idText = Text(id, "Text.Base"); idText.FontSize = 13; left.Children.Add(idText);
            if (!m.SupportsStructuredOutputs) left.Children.Add(Text("Prompt-only fallback", "Text.RowValue", new Thickness(0, 2, 0, 0)));
            foreach (var c in left.Children.OfType<TextBlock>().Skip(1)) c.FontSize = 12;
            g.Children.Add(left);
            if (id.Equals(S.OpenRouterModel.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var sel = new TextBlock { Text = "Selected", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                sel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentDeep");
                Grid.SetColumn(sel, 1); g.Children.Add(sel);
            }
            g.MouseLeftButtonUp += (_, _) => { if (_modelBox is not null) _modelBox.Text = id; else S.OpenRouterModel = id; };
            items.Add(Pad(g, v: 10));
        }
        if (_orEntries.Count > 0)
            items.Add(Pad(Text($"Fetched {_orEntries.Count} OpenRouter models. {_orEntries.Count(e => e.SupportsStructuredOutputs)} currently advertise structured output support.")));
        return Sec("Model Search", Card(items.ToArray()));
    }

    // ------------------------------------------------------------------ OpenAI-compatible

    void BuildOpenAICompatible()
    {
        _ctxSupportBuilt = Support(TranscriptPostProcessingMode.OpenAICompatible);
        _chipsHost = new StackPanel();
        var items = new List<UIElement?>
        {
            UrlField("Server URL", "http://127.0.0.1:1234/v1", "OpenAI-compatible server URL", () => S.OpenAICompatibleBaseURL, v => S.OpenAICompatibleBaseURL = v),
            KeyRow("API key (optional)", "Leave blank for local servers without auth", OaService, OaAccount, "OpenAI-compatible API key"),
            ModelField("local-model", "OpenAI-compatible model", () => S.OpenAICompatibleModel, v => S.OpenAICompatibleModel = v),
            StatusBlock([MakeRefreshButton(), MakeClearKeyButton(OaService, OaAccount)], _chipsHost),
            Pad(Text("Runs transcript cleanup through your selected OpenAI-compatible server. Remote servers receive the transcript and vocabulary hints. Use the base URL and model name reported by that server.")),
        };
        Root.Children.Add(Sec("OpenAI Compatible", Card(items.ToArray())));
        Root.Children.Add(PromptSection("OpenAI-compatible prompt", () => S.OpenAICompatiblePostProcessingPrompt, v => S.OpenAICompatiblePostProcessingPrompt = v));
        Root.Children.Add(VocabularySection(TranscriptPostProcessingMode.OpenAICompatible));
        RenderOpenAI();
    }

    async Task RefreshOpenAIAsync()
    {
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var ct = cts.Token;
        var url = S.OpenAICompatibleBaseURL.Trim();
        try
        {
            if (url.Length == 0) { _oaChecked = false; _oaModels = []; _providerError = null; RenderOpenAI(); return; }
            _providerChecking = true; RenderOpenAI();
            var r = await _s!.CleanupDiscovery.ListOpenAICompatibleModelsAsync(url, StoredKey(OaService, OaAccount), ct);
            ct.ThrowIfCancellationRequested();
            if (r.Ok) { _providerError = null; _oaChecked = true; _oaModels = r.Models.ToList(); }
            else { _providerError = r.Failure?.Message ?? RemyCleanupFailure.UnknownMessage; _oaChecked = false; _oaModels = []; }
        }
        catch (OperationCanceledException) { return; }
        finally { if (_refreshCts == cts) { _providerChecking = false; RenderOpenAI(); } }
    }

    void RenderOpenAI()
    {
        if (_statusBlock is null) return;
        UpdateRefreshButton();
        var model = S.OpenAICompatibleModel.Trim();
        (string, Tone) st;
        if (_providerChecking) st = ("Checking server…", Tone.Info);
        else if (_providerError is not null) st = (_providerError, Tone.Error);
        else if (_oaChecked && _oaModels.Count == 0) st = ("Connected, but the server did not report any models.", Tone.Warning);
        else if (_oaChecked && model.Length == 0) st = ("Connected. Choose a model below or enter one manually.", Tone.Success);
        else if (_oaChecked && CleanupDiscovery.SelectedModelIsAvailable(model, _oaModels)) st = ($"Connected. {model} is available.", Tone.Success);
        else if (_oaChecked) st = ($"Connected, but {model} was not listed by this server.", Tone.Warning);
        else st = ("Enter a server URL and refresh models to check connectivity.", Tone.Info);
        SetStatus(st.Item1, st.Item2);

        if (_chipsHost is not null)
        {
            _chipsHost.Children.Clear();
            if (_oaModels.Count > 0)
            {
                var l = Text("Available Models", "Text.RowValue", new Thickness(0, 12, 0, 8));
                l.FontSize = 12; l.FontWeight = FontWeights.SemiBold;
                _chipsHost.Children.Add(l);
                var flow = Flow();
                foreach (var m in _oaModels)
                {
                    var name = m;
                    flow.Children.Add(Chip(name, () => { if (_modelBox is not null) _modelBox.Text = name; else S.OpenAICompatibleModel = name; },
                        selected: name.Equals(model, StringComparison.OrdinalIgnoreCase)));
                }
                _chipsHost.Children.Add(flow);
            }
        }
        UpdatePreparation();
    }
}
