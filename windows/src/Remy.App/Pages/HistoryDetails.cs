using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Remy.Core.Cleanup;
using Remy.Core.History;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Lazy "Processing details" for one history row (ui.md 3.6).</summary>
public partial class HistoryPage
{
    UIElement DetailsHost(Guid id)
    {
        var host = new ContentControl();
        async void Load()
        {
            host.Content = LoadingRow();
            try
            {
                var res = await _s!.History().GetAsync(id);
                if (!res.Ok || res.Value is null) { host.Content = DetailsFailed(Load); return; }
                host.Content = DetailsBody(res.Value);
            }
            catch (Exception) { host.Content = DetailsFailed(Load); }
        }
        Load();
        return host;
    }

    static UIElement LoadingRow()
    {
        var t = Text("Loading processing details…");
        t.FontSize = 12;
        return t;
    }

    static UIElement DetailsFailed(Action retry)
    {
        var s = new StackPanel();
        s.Children.Add(Message("Processing details couldn't be loaded. Retry to read this session's log.", Tone.Warning));
        var b = Btn("Retry loading details", retry);
        b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 8, 0, 0);
        s.Children.Add(b);
        return s;
    }

    static TextBlock Meta(string label, string value)
    {
        var t = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        t.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Font.Ui");
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        t.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.Medium });
        t.Inlines.Add(new System.Windows.Documents.Run(value));
        return t;
    }

    static string Full(DateTimeOffset t, bool seconds) =>
        t.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) + " at " +
        t.LocalDateTime.ToString(seconds ? "T" : "t", CultureInfo.CurrentCulture);

    static string StatusText(HistorySessionStatus s) => s switch
    {
        HistorySessionStatus.Recognized => "Transcription saved; processing not completed",
        HistorySessionStatus.Completed => "Processing completed",
        _ => "Processing interrupted",
    };

    static string SelectedText(SelectionKind k) => k switch
    {
        SelectionKind.Raw => "Raw transcript selected",
        SelectionKind.Attempt => "AI pass output selected",
        SelectionKind.Fallback => "Raw transcript selected instead of AI cleanup",
        SelectionKind.Legacy => "Imported transcript; original processing details unavailable",
        _ => "Recovered text selected",
    };

    static string DeliveryText(DeliveryOutcome o) => o switch
    {
        DeliveryOutcome.Intent => "Not delivered yet",
        DeliveryOutcome.Copied => "Copied to the clipboard",
        DeliveryOutcome.Dispatched => "Paste sent",
        DeliveryOutcome.Verified => "Pasted",
        DeliveryOutcome.Uncertain => "Paste could not be confirmed",
        DeliveryOutcome.Failed => "Delivery failed",
        DeliveryOutcome.CopiedOnly => "Copied only (not pasted)",
        _ => "Paste blocked",
    };

    static string AttemptStatusText(CleanupAttemptRecord a) => a.Status switch
    {
        AttemptStatus.Succeeded => "Succeeded",
        AttemptStatus.Failed => "Failed",
        _ => a.Failure == RemyCleanupFailure.UseRaw ? "Skipped by you" : "Cancelled",
    };

    static string Duration(int? ms) => ms is not { } v ? "Not recorded"
        : v < 1000 ? $"{v} ms" : (v / 1000.0).ToString("0.00", CultureInfo.CurrentCulture) + " s";

    UIElement DetailsBody(HistorySession s)
    {
        var st = new StackPanel();
        st.Children.Add(Meta("Status", StatusText(s.Status)));
        st.Children.Add(Meta("Selected text", SelectedText(s.SelectionKind)));
        st.Children.Add(Meta("Transcription engine", s.Engine));
        st.Children.Add(Meta("Transcription model", s.Model));
        if (!string.IsNullOrWhiteSpace(s.Language)) st.Children.Add(Meta("Language", s.Language!));
        if (s.Finished is { } f) st.Children.Add(Meta("Finished", Full(f, true)));

        if (s.Attempts.Count == 0)
        {
            var none = Text("No AI cleanup was run for this dictation.", margin: new Thickness(0, 8, 0, 0)); none.FontSize = 12;
            st.Children.Add(none);
        }
        foreach (var a in s.Attempts.OrderBy(x => x.AttemptIndex)) st.Children.Add(AttemptBlock(s, a));

        st.Children.Add(Divider(8));
        if (s.Deliveries.Count == 0) { var n = Text(HistoryMessages.NoDeliveryRecorded); n.FontSize = 12; st.Children.Add(n); }
        else
        {
            var d = s.Deliveries.OrderBy(x => x.Created).Last();
            st.Children.Add(Meta("Delivery", DeliveryText(d.Outcome)));
            if (!string.IsNullOrWhiteSpace(d.Reason)) st.Children.Add(Meta("Delivery detail", d.Reason!));
        }
        return st;
    }

    static Border Divider(double margin)
    {
        var d = CleanupUi.Divider();
        d.Margin = new Thickness(0, margin, 0, margin);
        return d;
    }

    UIElement AttemptBlock(HistorySession s, CleanupAttemptRecord a)
    {
        var st = new StackPanel();
        st.Children.Add(Divider(8));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Text($"AI pass {a.AttemptIndex + 1} · {AttemptStatusText(a)}", "Text.Base"); title.FontSize = 12; title.FontWeight = FontWeights.SemiBold;
        head.Children.Add(title);
        if (s.SelectedAttemptId == a.Id)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.SuccessSoft");
            var bt = new TextBlock { Text = "Selected", FontSize = 11, FontWeight = FontWeights.SemiBold };
            bt.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SuccessText");
            badge.Child = bt;
            head.Children.Add(badge);
        }
        st.Children.Add(head);
        if (a.Status == AttemptStatus.Failed)
        {
            var m = Message(RemyCleanupFailure.MessageFor(a.Failure), Tone.Warning); m.FontSize = 12; m.Margin = new Thickness(0, 4, 0, 0);
            st.Children.Add(m);
        }
        if (!string.IsNullOrWhiteSpace(a.Provider)) st.Children.Add(Meta("Provider", a.Provider!));
        if (!string.IsNullOrWhiteSpace(a.Model)) st.Children.Add(Meta("Model", a.Model!));
        if (a.Started is { } started) st.Children.Add(Meta("Started", Full(started, true)));
        st.Children.Add(Meta("Recorded", Full(a.Created, true)));
        st.Children.Add(Meta("Duration", Duration(a.DurationMs)));
        if (a.Profile is { } p)
        {
            if (p.CleanupMode is not null) st.Children.Add(Meta("Cleanup mode", p.CleanupMode));
            if (p.ReasoningEnabled is { } r) st.Children.Add(Meta("Reasoning", r ? "On" : "Off"));
            if (p.ContextEnabled is { } c) st.Children.Add(Meta("App context setting", c ? "On" : "Off"));
            if (p.Style is not null) st.Children.Add(Meta("S1 style", p.Style));
            if (p.Structure is not null) st.Children.Add(Meta("S1 structure", p.Structure));
        }

        var input = a.Input; var output = a.Output; var prompt = a.Prompt;
        var d1 = Disclosure("AI input", () => SelectableText(input, size: 12.5, secondary: true));
        d1.Margin = new Thickness(0, 8, 0, 0);
        st.Children.Add(d1);
        if (string.IsNullOrEmpty(output)) { var n = Text("No AI output recorded."); n.FontSize = 12; n.Margin = new Thickness(0, 6, 0, 0); st.Children.Add(n); }
        else { var d2 = Disclosure("AI output", () => SelectableText(output, size: 12.5, secondary: true)); d2.Margin = new Thickness(0, 6, 0, 0); st.Children.Add(d2); }
        if (string.IsNullOrEmpty(prompt)) { var n = Text("AI instructions not recorded."); n.FontSize = 12; n.Margin = new Thickness(0, 6, 0, 0); st.Children.Add(n); }
        else
        {
            var d3 = Disclosure("Saved cleanup instructions", () =>
            {
                var b = new StackPanel();
                b.Children.Add(Text("Generated formatting instructions and app context aren't fully captured in this log.", margin: new Thickness(0, 0, 0, 6)));
                b.Children.Add(SelectableText(prompt, size: 12.5, secondary: true));
                return b;
            });
            d3.Margin = new Thickness(0, 6, 0, 0);
            st.Children.Add(d3);
        }
        return st;
    }
}
