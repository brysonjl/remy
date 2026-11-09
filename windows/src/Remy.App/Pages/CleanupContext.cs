using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Remy.Core.Cleanup;
using Remy.Core.Context;
using Remy.Core.Settings;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Context Awareness, Writing Style, S1-mini App Styling and App Categories (ui.md 3.5.6).</summary>
public partial class TranscriptCleanupPage
{
    enum CtxSupport { None, CategoryOnDevice, FullLocal, RemoteOptional }

    static readonly DictationContextCategory[] Categories = Enum.GetValues<DictationContextCategory>();

    static string CategoryCaption(DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => "Used in mail apps and webmail.",
        DictationContextCategory.WorkMessaging => "Used in Slack, Teams, Discord, and similar work chat.",
        DictationContextCategory.PersonalMessaging => "Used in Messages, WhatsApp, Telegram, and similar personal chat.",
        DictationContextCategory.Terminal => "Shell input; preserves commands, paths, flags and literal punctuation.",
        DictationContextCategory.TerminalAgent => "Claude Code, Codex and similar terminal agents; preserves natural-language requests and technical terms.",
        DictationContextCategory.Browser => "General browser fields; known email, messaging and AI sites use their own categories.",
        DictationContextCategory.CodingAgent => "Codex, Cursor and similar coding apps; preserves code references and requests.",
        DictationContextCategory.AiChat => "ChatGPT, Claude, Gemini and similar AI chat apps and sites.",
        _ => "Used when no specific app or website category matches.",
    };

    static string ModeName(TranscriptPostProcessingMode m) => m switch
    {
        TranscriptPostProcessingMode.S1Mini => "S1-mini by Superwhisper",
        TranscriptPostProcessingMode.Ollama => "Ollama",
        TranscriptPostProcessingMode.OpenRouter => "OpenRouter",
        TranscriptPostProcessingMode.OpenAICompatible => "OpenAI Compatible",
        TranscriptPostProcessingMode.Profile => "Saved Profile",
        _ => m.DisplayName(),
    };

    static bool IsLocalEndpoint(string? url, string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m.EndsWith("-cloud") || m.EndsWith(":cloud")) return false;
        return CleanupEndpointIdentity.TryCreate(url, false, out var id) && id!.IsLoopback;
    }

    CtxSupport Support(TranscriptPostProcessingMode mode) => mode switch
    {
        TranscriptPostProcessingMode.S1Mini => CtxSupport.CategoryOnDevice,
        TranscriptPostProcessingMode.OpenRouter => CtxSupport.RemoteOptional,
        TranscriptPostProcessingMode.Ollama => IsLocalEndpoint(S.OllamaBaseURL, S.OllamaModel) ? CtxSupport.FullLocal : CtxSupport.RemoteOptional,
        TranscriptPostProcessingMode.OpenAICompatible => IsLocalEndpoint(S.OpenAICompatibleBaseURL, S.OpenAICompatibleModel) ? CtxSupport.FullLocal : CtxSupport.RemoteOptional,
        TranscriptPostProcessingMode.Profile => _s!.Profiles?.Collection.Selected is { } p && IsLocalEndpoint(p.BaseUrl, p.Model) ? CtxSupport.FullLocal : CtxSupport.RemoteOptional,
        _ => CtxSupport.None,
    };

    void BuildContext()
    {
        _contextHost.Children.Clear();
        var mode = _builtMode;
        var support = Support(mode);
        if (support == CtxSupport.None) return;
        var name = ModeName(mode);

        var caption = support switch
        {
            CtxSupport.CategoryOnDevice => "Detects the app category and cursor position locally. S1-mini receives only its trained email/general context value; surrounding text stays outside the model. Password fields and excluded apps are never read.",
            CtxSupport.FullLocal => "Reads a bounded snapshot around the cursor and supplies it to the configured local server. Password fields and excluded apps are never read.",
            _ => "Sends the app name, destination category and writing style with your transcript. Window titles, page URLs and surrounding text are shared only when enabled below. Password fields and excluded apps are never read.",
        };

        var items = new List<UIElement?>
        {
            Row("Adapt to the current app and text field",
                Switch(S.DictationContextAwarenessEnabled, v => { S.DictationContextAwarenessEnabled = v; BuildContext(); }, "Adapt to the current app and text field"),
                caption),
        };
        if (S.DictationContextAwarenessEnabled)
        {
            if (support == CtxSupport.RemoteOptional)
                items.Add(Row($"Share surrounding text with {name}",
                    Switch(S.ShareDictationContextWithRemoteProviders, v => S.ShareDictationContextWithRemoteProviders = v, $"Share surrounding text with {name}"),
                    "Off by default. App name, category and writing style are sent while context awareness is on. Enable this to also send window titles, page URLs and the text around your cursor to the selected provider."));
            else
                items.Add(Pad(Text(support == CtxSupport.CategoryOnDevice
                    ? "S1-mini by Superwhisper receives only the destination category, so its Automatic context control can choose email or general formatting. The text around your cursor is never passed to the model, and nothing leaves this PC."
                    : $"The configured {name} endpoint is on this PC, so requests include surrounding text automatically. This assumes the server processes the selected model locally. Remote endpoints use the separate sharing toggle.")));
        }
        _contextHost.Children.Add(Sec("Context Awareness", Card(items.ToArray())));
        if (!S.DictationContextAwarenessEnabled) return;

        if (mode == TranscriptPostProcessingMode.S1Mini) _contextHost.Children.Add(S1AppStylingSection());
        else _contextHost.Children.Add(WritingStyleSection(name));
        _contextHost.Children.Add(AppCategoriesSection(mode));
    }

    UIElement WritingStyleSection(string modeName)
    {
        var rows = new List<UIElement?>();
        foreach (var c in Categories)
        {
            var cat = c;
            var combo = Combo(cat.StyleOptions(), st => RemyEnumNames.WritingStyle.ToDisplay(st), S.GetWritingStyle(cat),
                st => S.SetWritingStyle(cat, st), $"Writing style for {RemyEnumNames.ContextCategory.ToDisplay(cat)}", 170);
            rows.Add(Row(RemyEnumNames.ContextCategory.ToDisplay(cat), combo, CategoryCaption(cat)));
        }
        rows.Add(Pad(Text($"{modeName} receives the app name, destination category and selected style. Terminal and AI destinations preserve technical terms and prompt wording.")));
        return Sec("Writing Style", Card(rows.ToArray()));
    }

    // ---- S1-mini per-category styling

    static S1MiniStyling GetS1(S1MiniAppStyling s, DictationContextCategory c) => c switch
    {
        DictationContextCategory.Email => s.Email,
        DictationContextCategory.WorkMessaging => s.WorkMessaging,
        DictationContextCategory.PersonalMessaging => s.PersonalMessaging,
        DictationContextCategory.Other => s.Other,
        _ => s.ExtendedStyles is not null && s.ExtendedStyles.TryGetValue(c, out var v) ? v : s.Other,
    };

    static S1MiniAppStyling SetS1(S1MiniAppStyling s, DictationContextCategory c, S1MiniStyling v) => c switch
    {
        DictationContextCategory.Email => s with { Email = v },
        DictationContextCategory.WorkMessaging => s with { WorkMessaging = v },
        DictationContextCategory.PersonalMessaging => s with { PersonalMessaging = v },
        DictationContextCategory.Other => s with { Other = v },
        _ => s with
        {
            ExtendedStyles = new Dictionary<DictationContextCategory, S1MiniStyling>(s.ExtendedStyles ?? new Dictionary<DictationContextCategory, S1MiniStyling>()) { [c] = v },
        },
    };

    static readonly S1MiniStyling[] S1Stylings = Enum.GetValues<S1MiniStyling>();

    UIElement S1AppStylingSection()
    {
        var rows = new List<UIElement?>();
        foreach (var c in Categories)
        {
            var cat = c;
            rows.Add(Row(RemyEnumNames.ContextCategory.ToDisplay(cat),
                Combo(S1Stylings, x => RemyEnumNames.S1Styling.ToDisplay(x), GetS1(S.S1MiniAppStyling, cat),
                    x => S.S1MiniAppStyling = SetS1(S.S1MiniAppStyling, cat, x), $"S1-mini style for {RemyEnumNames.ContextCategory.ToDisplay(cat)}", 170),
                CategoryCaption(cat)));
        }
        rows.Add(Row("Fallback",
            Combo(S1Stylings, x => RemyEnumNames.S1Styling.ToDisplay(x), S.S1MiniStyling, x => S.S1MiniStyling = x, "S1-mini fallback style", 170),
            "Used only when no destination app can be captured."));
        rows.Add(Pad(Text("Every option shown here is one of S1-mini's trained Styling values. Remy selects the value locally from the destination category without changing the model prompt format.")));
        return Sec("S1-mini App Styling", Card(rows.ToArray()));
    }

    // ---- App categories (per-app overrides)

    UIElement AppCategoriesSection(TranscriptPostProcessingMode mode)
    {
        var host = new StackPanel();
        void Render()
        {
            host.Children.Clear();
            var caption = mode == TranscriptPostProcessingMode.S1Mini
                ? "Common email and messaging apps and sites are categorized automatically. Categories select only S1-mini's trained styling values, and its Automatic context maps email to Email and every other category to General. Add an override to recategorize an app or stop reading its field context."
                : "Email, messaging, browsers, terminals, coding agents and AI chat apps such as Claude and ChatGPT are categorized automatically. Claude Code or Codex in a terminal needs a recognizable window title; otherwise the Terminal category applies. Add an app override to choose a category or exclude its context.";
            var items = new List<UIElement?> { Pad(Text(caption)) };
            foreach (var rule in S.DictationAppRules)
            {
                var r = rule;
                var cat = r.Category ?? DictationContextClassifier.AutoCategory(r.ProcessName, null, null);
                var name = Label(r.ProcessName);
                name.FontSize = 13.5;
                var exe = Text(r.ProcessName + ".exe");
                exe.FontSize = 11.5;
                var left = new StackPanel(); left.Children.Add(name); left.Children.Add(exe);
                var combo = Combo(Categories, x => RemyEnumNames.ContextCategory.ToDisplay(x), cat, x => Replace(r, r with { Category = x }), $"Category for {r.ProcessName}", 190);
                var read = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var rl = Label("Read field context"); rl.Margin = new Thickness(0, 0, 10, 0); rl.VerticalAlignment = VerticalAlignment.Center;
                read.Children.Add(rl);
                read.Children.Add(Switch(!r.Excluded, v => Replace(r, r with { Excluded = !v }, false), $"Read field context for {r.ProcessName}"));
                var del = IconBtn("", () => { S.DictationAppRules = S.DictationAppRules.Where(x => x != r).ToList(); Render(); }, $"Remove {r.ProcessName} override", "Remove override");
                del.Margin = new Thickness(10, 0, 0, 0);
                del.SetResourceReference(Control.ForegroundProperty, "Brush.Destructive");
                read.Children.Add(del);
                var line = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.Children.Add(combo);
                Grid.SetColumn(read, 1); line.Children.Add(read);
                var box = new StackPanel(); box.Children.Add(left); box.Children.Add(line);
                items.Add(Pad(box, v: 13));
            }
            items.Add(Pad(AddRunningAppButton(Render)));
            host.Children.Add(Card(items.ToArray()));

            void Replace(DictationAppRule old, DictationAppRule updated, bool rerender = true)
            {
                S.DictationAppRules = S.DictationAppRules.Select(x => x == old ? updated : x).ToList();
                if (rerender) Render();
            }
        }
        Render();
        return Sec("App Categories", host);
    }

    UIElement AddRunningAppButton(Action rerender)
    {
        var btn = new Button { Style = St("Remy.SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 7, 12, 7) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Icon("", 11, "Brush.Ink"));
        var t = new TextBlock { Text = "Add Running App", Margin = new Thickness(8, 0, 0, 0) };
        content.Children.Add(t);
        btn.Content = content;
        btn.Click += (_, _) =>
        {
            var menu = new ContextMenu { Style = St("Remy.ContextMenu"), PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var existing = S.DictationAppRules.Select(r => DictationContextClassifier.NormalizeProcessName(r.ProcessName)).ToHashSet();
            var self = Process.GetCurrentProcess().ProcessName;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var n = p.ProcessName;
                    if (n.Equals(self, StringComparison.OrdinalIgnoreCase) || existing.Contains(DictationContextClassifier.NormalizeProcessName(n))) continue;
                    names.Add(n);
                }
                catch { /* process exited or access denied */ }
                finally { p.Dispose(); }
            }
            if (names.Count == 0) menu.Items.Add(new MenuItem { Header = "No other running apps", IsEnabled = false, Style = St("Remy.MenuItem") });
            foreach (var n in names)
            {
                var name = n;
                var mi = new MenuItem { Header = name, Style = St("Remy.MenuItem") };
                mi.Click += (_, _) =>
                {
                    var cat = DictationContextClassifier.AutoCategory(name, null, null);
                    S.DictationAppRules = S.DictationAppRules.Append(new DictationAppRule(name, cat, false)).ToList();
                    rerender();
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        };
        return btn;
    }
}
