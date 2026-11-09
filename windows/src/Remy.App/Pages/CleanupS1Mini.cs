using System.Windows;
using System.Windows.Controls;
using Remy.App.Controls;
using Remy.Core.Contracts;
using Remy.Core.Models;
using Remy.Core.Settings;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>S1-mini card: download/prepare/delete, plus the trained Cleanup Controls (ui.md 3.5.4).</summary>
public partial class TranscriptCleanupPage
{
    StatusPill? _s1Pill;
    TextBlock? _s1Progress, _s1Error;
    Button? _s1Primary;
    StackPanel? _s1DeleteHost;
    Grid? _s1BarHost;
    Action<double>? _s1BarSet;
    ModelProgress? _s1Download;
    bool _s1Busy, _s1Prepared, _s1Deleting;
    string? _s1ErrorText;
    CancellationTokenSource? _s1Cts;

    void BuildS1()
    {
        _s1Pill = new StatusPill();
        _s1Primary = new Button { Style = St("Remy.PrimaryButton") };
        _s1Primary.Click += (_, _) => S1PrimaryAction();
        _s1Progress = Text("", margin: new Thickness(0, 8, 0, 0));
        (_s1BarHost, _s1BarSet) = ProgressBar();
        _s1BarHost.Margin = new Thickness(0, 8, 0, 0);
        _s1Error = Message("", Tone.Error);
        _s1DeleteHost = new StackPanel();

        var readiness = new Grid();
        readiness.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        readiness.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readiness.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readiness.Children.Add(Label("Status"));
        _s1Pill.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_s1Pill, 1); readiness.Children.Add(_s1Pill);
        Grid.SetColumn(_s1Primary, 2); readiness.Children.Add(_s1Primary);

        var statusBlock = new StackPanel();
        statusBlock.Children.Add(readiness);
        statusBlock.Children.Add(_s1BarHost);
        statusBlock.Children.Add(_s1Progress);
        statusBlock.Children.Add(Text("Downloaded means the files are installed. Ready means the model has been loaded and prepared for cleanup.", margin: new Thickness(0, 8, 0, 0)));
        statusBlock.Children.Add(_s1Error);
        _s1Error.Margin = new Thickness(0, 8, 0, 0);

        var legal = new StackPanel();
        legal.Children.Add(Text("Downloads a pinned, integrity-checked copy directly from Hugging Face. Once installed, transcript cleanup runs entirely on this PC and does not send transcript text to a server. S1-mini uses CPU inference."));
        var link = LinkButton("View the model and its license on Hugging Face", "https://huggingface.co/superwhisper/s1-mini");
        link.Margin = new Thickness(0, 8, 0, 0);
        legal.Children.Add(link);

        Root.Children.Add(Sec("S1-mini by Superwhisper", Card(
            Row("Language", Text("English cleanup", "Text.RowValue"), secondaryLabel: true),
            Row("Model", Text("462 MB · English · Local transcript normalizer", "Text.RowValue"), secondaryLabel: true),
            Pad(statusBlock, v: 13),
            _s1DeleteHost,
            Pad(legal))));
        Root.Children.Add(S1ControlsSection());
    }

    UIElement S1ControlsSection()
    {
        var items = new List<UIElement?>();
        if (!S.DictationContextAwarenessEnabled)
            items.Add(Row("Styling",
                Combo(Enum.GetValues<S1MiniStyling>(), x => RemyEnumNames.S1Styling.ToDisplay(x), S.S1MiniStyling, x => S.S1MiniStyling = x, "S1-mini styling", 170),
                "Controls how casual or formal the normalized transcript should sound."));
        items.Add(Row("Structure",
            Combo(Enum.GetValues<S1MiniStructure>(), x => RemyEnumNames.S1Structure.ToDisplay(x), S.S1MiniStructure, x => S.S1MiniStructure = x, "S1-mini structure", 170),
            "Choose prose paragraphs or a list-oriented result."));
        items.Add(Row("Context",
            Combo(Enum.GetValues<S1MiniContextSetting>(), x => RemyEnumNames.S1Context.ToDisplay(x), S.S1MiniContextSetting, x => S.S1MiniContextSetting = x, "S1-mini context", 170),
            "Automatic uses email formatting for email destinations and general formatting elsewhere."));
        items.Add(Pad(Text("S1-mini uses only its fixed, trained controls—not an arbitrary prompt or custom vocabulary. Local filler-word removal still runs first.")));
        return Sec("Cleanup Controls", Card(items.ToArray()));
    }

    bool DictationIdle => _s!.Dictation.Status == DictationStatus.Idle;

    void RefreshS1Card()
    {
        if (_s1Pill is null || _s1Primary is null || _builtMode != TranscriptPostProcessingMode.S1Mini) return;
        var m = ModelCatalog.S1Mini;
        var r = _s!.Models.Inspect(m);
        var downloaded = r.State == ModelReadinessState.Downloaded;
        var working = r.IsWorking || _s1Busy || _s1Deleting;

        string title; var ready = false;
        if (_s1Deleting || r.State == ModelReadinessState.Deleting) title = "Deleting…";
        else if (r.State == ModelReadinessState.Downloading) title = "Downloading…";
        else if (r.State == ModelReadinessState.Verifying) title = "Verifying…";
        else if (_s1Busy) title = "Preparing…";
        else if (downloaded && _s1Prepared) { title = "Ready"; ready = true; }
        else if (downloaded) { title = "Downloaded"; ready = true; }
        else title = "Not downloaded";
        _s1Pill!.Text = title;
        _s1Pill.Kind = ready ? PillKind.Ready : PillKind.Neutral;

        var progress = r.State == ModelReadinessState.Downloading || r.State == ModelReadinessState.Verifying ? _s1Download : null;
        _s1BarHost!.Visibility = progress is null && !(r.State is ModelReadinessState.Downloading or ModelReadinessState.Verifying) ? Visibility.Collapsed : Visibility.Visible;
        _s1Progress!.Visibility = _s1BarHost.Visibility;
        if (progress is not null) { _s1Progress.Text = progress.Caption; _s1BarSet!(progress.Fraction); }
        else if (_s1BarHost.Visibility == Visibility.Visible) { _s1Progress.Text = r.State == ModelReadinessState.Verifying ? "Verifying model integrity…" : "0%"; }

        _s1Error!.Text = _s1ErrorText ?? "";
        _s1Error.Visibility = _s1ErrorText is null ? Visibility.Collapsed : Visibility.Visible;

        if (!downloaded) { _s1Primary.Content = "Download Model"; _s1Primary.Visibility = Visibility.Visible; }
        else if (_s1Prepared && _s1ErrorText is null) { _s1Primary.Visibility = Visibility.Collapsed; }
        else { _s1Primary.Content = _s1ErrorText is not null ? "Retry" : "Prepare Model"; _s1Primary.Visibility = Visibility.Visible; }
        _s1Primary.IsEnabled = !working && DictationIdle;

        _s1DeleteHost!.Children.Clear();
        if (downloaded || _s1Deleting)
        {
            _s1DeleteHost.Children.Add(Divider());
            var del = Btn(_s1Deleting ? "Deleting…" : "Delete Model…", DeleteS1, "Remy.DestructiveButton");
            del.IsEnabled = !working && DictationIdle;
            _s1DeleteHost.Children.Add(Row("Remove the downloaded model files from this PC.", del, secondaryLabel: true));
        }
    }

    async void S1PrimaryAction()
    {
        var m = ModelCatalog.S1Mini;
        _s1ErrorText = null;
        try
        {
            if (!_s!.Models.IsInstalled(m))
            {
                _s1Busy = true;
                _s1Download = null;
                _s1Cts = new CancellationTokenSource();
                RefreshS1Card();
                var progress = new Progress<ModelProgress>(p => { _s1Download = p; RefreshS1Card(); });
                await _s.Models.InstallAsync(m, progress, _s1Cts.Token);
                _s1Busy = false; _s1Download = null;
                RefreshS1Card();
                return;
            }
            _s1Busy = true;
            RefreshS1Card();
            await _s.Dictation.PrepareS1MiniAsync();
            _s1Prepared = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _s1ErrorText = ex.Message; }
        finally { _s1Busy = false; _s1Download = null; RefreshS1Card(); }
    }

    async void DeleteS1()
    {
        if (!Confirm(this, "Delete S1-mini by Superwhisper?",
                "This removes the local model and its downloaded license file. You can download them again later.", "Delete")) return;
        _s1ErrorText = null; _s1Deleting = true; RefreshS1Card();
        try
        {
            await _s!.Dictation.UnloadS1MiniAsync();
            await _s.Models.DeleteAsync(ModelCatalog.S1Mini);
            _s1Prepared = false;
        }
        catch (Exception ex) { _s1ErrorText = ex.Message; }
        finally { _s1Deleting = false; RefreshS1Card(); }
    }
}
