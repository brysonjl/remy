using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Remy.App.Services;
using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Recovery;
using Remy.Core.Settings;
using Remy.Core.Storage;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>History page (ui.md 3.6): storage settings, search, recovery sessions and the entries list.</summary>
public partial class HistoryPage : UserControl, IServicePage
{
    AppServices? _s;
    readonly Debouncer _debounce;
    readonly StackPanel _storageHost = new(), _recoveryHost = new(), _entriesHost = new();
    TextBox? _searchBox;
    Button? _clearBtn;

    List<HistorySession> _items = [];
    HistoryCursor? _next;
    List<RecoverySession> _recovery = [];
    string _query = "";
    int _loadVersion;
    bool _loadFailed, _loading, _clearing, _deletionPending, _retrying;
    string? _deletionMessage, _recoveryMessage;
    Guid? _continuing, _recovering;
    bool _sawRecording;
    bool _subscribed;

    RemySettings S => _s!.Settings;
    bool Idle => _s!.Dictation.Status == DictationStatus.Idle;

    public HistoryPage()
    {
        InitializeComponent();
        _debounce = new Debouncer(Dispatcher);
        Loaded += (_, _) => { Subscribe(); if (_s is not null) _ = ReloadAsync(); };
        Unloaded += (_, _) => Unsubscribe();
    }

    public void Attach(AppServices services)
    {
        _s = services;
        Root.Children.Clear();
        Root.Children.Add(Sec("History storage", _storageHost));
        Root.Children.Add(BuildSearchRow());
        Root.Children.Add(_recoveryHost);
        Root.Children.Add(_entriesHost);
        RenderStorage();
        RenderRecovery();
        RenderEntries();
        if (IsLoaded) { Subscribe(); _ = ReloadAsync(); }
    }

    void Subscribe()
    {
        if (_subscribed || _s is null) return;
        _subscribed = true;
        S.PropertyChanged += OnSetting;
        _s.Dictation.StateChanged += OnDictationState;
    }

    void Unsubscribe()
    {
        if (!_subscribed || _s is null) return;
        _subscribed = false;
        S.PropertyChanged -= OnSetting;
        _s.Dictation.StateChanged -= OnDictationState;
    }

    void OnSetting(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemySettings.RemyHistoryEnabled)) Dispatcher.BeginInvoke(() => { RenderStorage(); RenderEntries(); });
    }

    DictationStatus _lastStatus = DictationStatus.Idle;

    void OnDictationState(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        var st = _s!.Dictation.Status;
        if (st == DictationStatus.Recording && _continuing is not null) _sawRecording = true;
        if (st == DictationStatus.Idle && _continuing is not null && _sawRecording) { _continuing = null; _sawRecording = false; }
        var finished = st == DictationStatus.Idle && _lastStatus != DictationStatus.Idle;
        _lastStatus = st;
        if (finished) _ = ReloadAsync(); else { RenderRecovery(); if (_clearBtn is not null) _clearBtn.IsEnabled = CanClear; }
    });

    bool CanClear => !_loading && !_clearing && _recovering is null && _continuing is null && Idle;

    // ------------------------------------------------------------------ static pieces

    UIElement BuildSearchRow()
    {
        var (host, box) = Input("Search your dictations", automationName: "Search transcript history");
        _searchBox = box;
        box.TextChanged += (_, _) => _debounce.Run(250, () => { _query = box.Text.Trim(); _ = ReloadAsync(); });
        _clearBtn = Btn("Clear All…", ClearAll, "Remy.DestructiveButton");
        var g = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(host);
        _clearBtn.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_clearBtn, 1); g.Children.Add(_clearBtn);
        return g;
    }

    static UIElement Hint(string glyph, string text)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icon(glyph, 13); icon.VerticalAlignment = VerticalAlignment.Top; icon.HorizontalAlignment = HorizontalAlignment.Left; icon.Margin = new Thickness(0, 2, 0, 0);
        g.Children.Add(icon);
        var t = Text(text); Grid.SetColumn(t, 1); g.Children.Add(t);
        return Pad(g);
    }

    void RenderStorage()
    {
        if (_s is null) return;
        _storageHost.Children.Clear();
        var items = new List<UIElement?>
        {
            Hint("", RecoveryMessages.StorageHint),
            Row("Save new dictations",
                Switch(S.RemyHistoryEnabled, v => { S.RemyHistoryEnabled = v; RenderStorage(); RenderEntries(); }, "Save new dictations"),
                "Changes apply to your next recording. Turning this off keeps existing history and disables recovery audio for new sessions."),
        };
        if (!S.RemyHistoryEnabled) items.Add(Hint("", HistoryMessages.HistoryOffHint));
        if (_loadFailed) items.Add(Pad(Message(HistoryMessages.NotSavedWarning, Tone.Warning)));
        if (_deletionMessage is not null && !_deletionPending) items.Add(Pad(Message(_deletionMessage, Tone.Warning)));
        if (_deletionPending)
        {
            var st = new StackPanel();
            st.Children.Add(Message(HistoryMessages.DeletionNotConfirmed, Tone.Warning));
            if (_deletionMessage is not null) st.Children.Add(Message(_deletionMessage, Tone.Warning));
            var retry = Btn(_retrying ? "Retrying deletion…" : "Retry deletion from disk", RetryDeletion);
            retry.IsEnabled = !_retrying && Idle;
            retry.ToolTip = "Retries the earlier deletion. Dictations saved after Clear All are kept.";
            retry.HorizontalAlignment = HorizontalAlignment.Left; retry.Margin = new Thickness(0, 10, 0, 0);
            st.Children.Add(retry);
            items.Add(Pad(st));
        }
        _storageHost.Children.Add(Card(items.ToArray()));
        _storageHost.Margin = new Thickness(0, 0, 0, 24);
    }

    // ------------------------------------------------------------------ loading

    async Task ReloadAsync()
    {
        if (_s is null) return;
        var version = ++_loadVersion;
        _loading = true;
        if (_clearBtn is not null) _clearBtn.IsEnabled = CanClear;
        try
        {
            var repo = _s.History();
            var res = await repo.QueryAsync(new HistoryQuery(_query.Length == 0 ? null : _query, 100));
            var rec = await Task.Run(() => _s.Recovery.List().OrderByDescending(r => r.CreatedAt).ToList());
            if (version != _loadVersion) return;
            _loadFailed = !res.Ok;
            _items = res.Value?.Items.ToList() ?? [];
            _next = res.Value?.NextCursor;
            _recovery = rec.Where(r => _query.Length == 0
                || (r.Preview ?? "").Contains(_query, StringComparison.OrdinalIgnoreCase)
                || "Cancelled dictation".Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception)
        {
            if (version != _loadVersion) return;
            _loadFailed = true; _items = []; _next = null;
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
                if (_clearBtn is not null) _clearBtn.IsEnabled = CanClear;
                RenderStorage(); RenderRecovery(); RenderEntries();
            }
        }
    }

    async void LoadMore()
    {
        if (_next is null || _s is null) return;
        try
        {
            var res = await _s.History().QueryAsync(new HistoryQuery(_query.Length == 0 ? null : _query, 100, _next));
            if (res.Value is { } page) { _items.AddRange(page.Items); _next = page.NextCursor; RenderEntries(); }
        }
        catch (Exception) { _loadFailed = true; RenderStorage(); }
    }

    // ------------------------------------------------------------------ clear all

    async void ClearAll()
    {
        if (!CanClear) return;
        if (!Confirm(this, "Clear all stored history?", HistoryMessages.ClearAllConfirmMessage, "Clear All")) return;
        await RunClear();
    }

    async void RetryDeletion()
    {
        _retrying = true; RenderStorage();
        await RunClear();
        _retrying = false; RenderStorage();
    }

    async Task RunClear()
    {
        _clearing = true; _clearBtn!.IsEnabled = false;
        try
        {
            var repo = _s!.History();
            var r = await Task.Run(() => StoredHistoryDeletion.ClearAllAsync(repo, _s.Recovery, RemyPaths.HistoryDirectory));
            _deletionPending = !r.Complete;
            _deletionMessage = r.Message;
        }
        catch (Exception) { _deletionPending = true; _deletionMessage = HistoryMessages.ClearPartialFailure; }
        _clearing = false;
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ recovery sessions

    static string Stamp(DateTimeOffset t) =>
        t.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture) + " at " + t.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);

    static string Expires(DateTimeOffset at)
    {
        var d = at - DateTimeOffset.Now;
        if (d <= TimeSpan.Zero) return "Expires soon";
        if (d.TotalHours >= 1) return $"Expires in {(int)Math.Round(d.TotalHours)} hr";
        return $"Expires in {Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";
    }

    void RenderRecovery()
    {
        if (_s is null) return;
        _recoveryHost.Children.Clear();
        if (_recovery.Count == 0) return;
        var items = new List<UIElement?> { Hint("", RecoveryMessages.SessionsHint) };
        foreach (var r in _recovery) items.Add(RecoveryRow(r));
        if (_recoveryMessage is not null) items.Add(Pad(Message(_recoveryMessage, Tone.Warning)));
        _recoveryHost.Children.Add(Sec("Recovery sessions", Card(items.ToArray())));
    }

    UIElement RecoveryRow(RecoverySession r)
    {
        var stamp = Stamp(r.CreatedAt);
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var head = Text($"Saved for recovery · {stamp}", "Text.Base"); head.FontSize = 12; head.FontWeight = FontWeights.SemiBold;
        left.Children.Add(head);
        var preview = Text(string.IsNullOrWhiteSpace(r.Preview) ? RecoveryMessages.AudioSavedForRecovery : r.Preview!, "Text.RowValue", new Thickness(0, 5, 0, 0));
        preview.FontSize = 13; preview.MaxHeight = 60; preview.TextTrimming = TextTrimming.CharacterEllipsis;
        left.Children.Add(preview);
        var meta = Text($"{(int)Math.Round(r.DurationSeconds)} sec · {Expires(r.ExpiresAt)}", margin: new Thickness(0, 5, 0, 0));
        meta.FontSize = 11.5;
        left.Children.Add(meta);
        if (r.Warning is not null) left.Children.Add(Message(r.Warning, Tone.Warning));

        var isContinuing = _continuing == r.Id;
        var busy = _recovering is not null || _continuing is not null;
        string cont = "Continue";
        var st = _s!.Dictation.Status;
        if (isContinuing) cont = st == DictationStatus.Recording ? "Stop" : st == DictationStatus.Processing ? "Finishing…" : "Preparing…";
        var contBtn = Btn(cont, () => ContinueSession(r), "Remy.PrimaryButton");
        contBtn.IsEnabled = isContinuing ? st == DictationStatus.Recording : !busy && Idle && r.HasAudio;
        contBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        var recBtn = Btn(_recovering == r.Id ? "Recovering…" : "Recover text", () => RecoverText(r));
        recBtn.IsEnabled = !busy && Idle && r.HasAudio;
        recBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        recBtn.Margin = new Thickness(0, 8, 0, 0);
        var del = IconBtn("", () => DeleteRecovery(r), $"Delete cancelled session from {stamp}", "Delete session");
        del.IsEnabled = !busy;
        del.HorizontalAlignment = HorizontalAlignment.Right; del.Margin = new Thickness(0, 8, 0, 0);
        var right = new StackPanel { Width = 130 };
        right.Children.Add(contBtn); right.Children.Add(recBtn); right.Children.Add(del);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return Pad(g, v: 16);
    }

    async void ContinueSession(RecoverySession r)
    {
        if (_continuing == r.Id) { try { await _s!.Dictation.StopAsync(); } catch (Exception) { } return; }
        _recoveryMessage = null; _continuing = r.Id; _sawRecording = false;
        RenderRecovery(); if (_clearBtn is not null) _clearBtn.IsEnabled = CanClear;
        try { await _s!.Dictation.ContinueRecoverySessionAsync(r.Id); }
        catch (Exception ex) { _recoveryMessage = RecoveryMessages.ContinueFailed(ex.Message); _continuing = null; }
        if (_s!.Dictation.Status == DictationStatus.Idle && !_sawRecording) _continuing = null;
        RenderRecovery();
        if (_continuing is null) await ReloadAsync();
    }

    async void RecoverText(RecoverySession r)
    {
        if (!S.RemyHistoryEnabled) { _recoveryMessage = RecoveryMessages.RecoverNeedsHistory; RenderRecovery(); return; }
        _recoveryMessage = null; _recovering = r.Id;
        RenderRecovery(); if (_clearBtn is not null) _clearBtn.IsEnabled = CanClear;
        try { _recoveryMessage = await _s!.Dictation.RecoverTextAsync(r.Id); }
        catch (Exception ex) { _recoveryMessage = RecoveryMessages.RecoveryFailed(ex.Message); }
        _recovering = null;
        await ReloadAsync();
    }

    async void DeleteRecovery(RecoverySession r)
    {
        try { await Task.Run(() => _s!.Recovery.Delete(r.Id)); } catch (Exception) { }
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ entries

    void RenderEntries()
    {
        if (_s is null) return;
        _entriesHost.Children.Clear();
        if (_items.Count == 0)
        {
            if (_recovery.Count == 0 || _query.Length > 0) _entriesHost.Children.Add(EmptyState());
            return;
        }
        var rows = new List<UIElement?>();
        foreach (var it in _items) rows.Add(EntryRow(it));
        var stack = new StackPanel();
        stack.Children.Add(Card(rows.ToArray()));
        if (_next is not null)
        {
            var more = Btn("Load more", LoadMore);
            more.HorizontalAlignment = HorizontalAlignment.Center; more.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(more);
        }
        _entriesHost.Children.Add(stack);
    }

    UIElement EmptyState()
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14) };
        var icon = Icon("", 26); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 10);
        s.Children.Add(icon);
        var title = Label(_query.Length > 0 ? "No matches" : "No transcripts"); title.HorizontalAlignment = HorizontalAlignment.Center; title.FontSize = 15; title.FontWeight = FontWeights.SemiBold;
        s.Children.Add(title);
        var msg = Text(_query.Length > 0 ? $"No dictations match “{_query}”." : "Completed dictations will appear here.", margin: new Thickness(0, 4, 0, 0));
        msg.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(msg);
        if (!S.RemyHistoryEnabled && _query.Length == 0)
        {
            var off = Message(HistoryMessages.HistoryOffLabel, Tone.Warning); off.HorizontalAlignment = HorizontalAlignment.Center; off.Margin = new Thickness(0, 8, 0, 0);
            s.Children.Add(off);
        }
        return Card(new Border { Padding = new Thickness(20), Child = s });
    }

    UIElement EntryRow(HistorySession s)
    {
        var stamp = Stamp(s.Created);
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var ts = Text(stamp, "Text.RowValue"); ts.FontSize = 12; ts.FontWeight = FontWeights.Medium;
        left.Children.Add(ts);
        string? warn = s.SelectionKind == SelectionKind.Fallback ? "AI cleanup wasn't applied. Raw transcript used."
            : s.Status == HistorySessionStatus.Interrupted ? "Processing was interrupted. Review the details below." : null;
        if (warn is not null) { var w = Message(warn, Tone.Warning); w.Margin = new Thickness(0, 4, 0, 0); left.Children.Add(w); }
        var body = SelectableText(s.Selected);
        body.Margin = new Thickness(0, 5, 0, 0);
        left.Children.Add(body);

        if (!string.IsNullOrEmpty(s.Raw) && s.Raw != s.Selected)
        {
            var raw = s.Raw!;
            var d = Disclosure("Raw transcript", () =>
            {
                var st = new StackPanel();
                st.Children.Add(SelectableText(raw, size: 13, secondary: true));
                var (btn, _) = CopyButton("Copy raw", "Copied raw", raw);
                btn.HorizontalAlignment = HorizontalAlignment.Left; btn.Margin = new Thickness(0, 8, 0, 0);
                st.Children.Add(btn);
                return st;
            });
            d.Margin = new Thickness(0, 10, 0, 0);
            left.Children.Add(d);
        }
        var details = Disclosure("Processing details", () => DetailsHost(s.Id));
        details.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(details);

        var (copy, _) = CopyButton("Copy", "Copied", s.Selected);
        var del = IconBtn("", () => DeleteEntry(s), $"Delete transcript from {stamp}", "Delete transcript");
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(copy);
        del.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(del);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return Pad(g, v: 14);
    }

    (Button, DispatcherTimer?) CopyButton(string label, string doneLabel, string text)
    {
        var btn = new Button { Content = label, Style = St("Remy.InsetButton") };
        System.Windows.Automation.AutomationProperties.SetName(btn, label);
        DispatcherTimer? timer = null;
        btn.Click += (_, _) =>
        {
            if (!CopyText(text)) return;
            btn.Content = doneLabel;
            timer?.Stop();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) => { timer!.Stop(); btn.Content = label; };
            timer.Start();
        };
        return (btn, timer);
    }

    async void DeleteEntry(HistorySession s)
    {
        try
        {
            var ack = await _s!.History().DeleteAsync(s.Id);
            if (ack.Kind == HistoryAckKind.NotSaved) { _loadFailed = true; RenderStorage(); return; }
            _items.RemoveAll(x => x.Id == s.Id);
            RenderEntries();
        }
        catch (Exception) { _loadFailed = true; RenderStorage(); }
    }
}
