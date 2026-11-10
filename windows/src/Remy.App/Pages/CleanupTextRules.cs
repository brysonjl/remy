using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Remy.Core.Settings;
using Remy.Core.Text;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>"Spelling Corrections" and "Snippets" sections of the Transcript Cleanup page (v0.2-features.md 9.1, 9.2).
/// Local text rules: shown for every cleanup method including Off.</summary>
public partial class TranscriptCleanupPage
{
    const string PencilGlyph = "", TrashGlyph = "";

    void AddTextRuleSections()
    {
        Root.Children.Add(SpellingCorrectionsSection());
        Root.Children.Add(SnippetsSection());
    }

    /// <summary>Re-runs <paramref name="render"/> when a setting changes while the host is loaded.</summary>
    void BindRender(FrameworkElement host, string property, Action render)
    {
        System.ComponentModel.PropertyChangedEventHandler h = (_, e) =>
        {
            if (e.PropertyName == property) host.Dispatcher.BeginInvoke(render);
        };
        host.Loaded += (_, _) => { S.PropertyChanged -= h; S.PropertyChanged += h; render(); };
        host.Unloaded += (_, _) => S.PropertyChanged -= h;
    }

    // ------------------------------------------------------------------ Spelling Corrections

    UIElement SpellingCorrectionsSection()
    {
        string? editingTerm = null;
        var importing = false;

        var list = new StackPanel();
        var (spellHost, spellBox) = Input("Heard as (comma-separated)…", width: 220, automationName: "Spellings to correct");
        var (termHost, termBox) = Input("Correct spelling…", width: 180, automationName: "Correct spelling");
        var arrow = Icon("→");
        arrow.Margin = new Thickness(10, 0, 10, 0);
        var save = Btn("Add", () => { });
        var cancel = Btn("Cancel", () => { });
        cancel.Visibility = Visibility.Collapsed;
        var lengthError = Message("Terms and spellings can be up to 60 characters.", Tone.Error);
        lengthError.Visibility = Visibility.Collapsed;
        var importButton = Btn("Import CSV…", () => { }, automationName: "Import vocabulary from a CSV file");
        var importMessage = Message("", Tone.Success);
        importMessage.Visibility = Visibility.Collapsed;
        importMessage.Margin = new Thickness(10, 0, 0, 0);
        importMessage.VerticalAlignment = VerticalAlignment.Center;

        IReadOnlyList<string> Spellings() => VocabularyInput.Terms(spellBox.Text);
        bool TooLong() => DictationTextRules.Length(termBox.Text.Trim()) > DictationTextRules.MaximumPhraseLength
            || Spellings().Any(x => DictationTextRules.Length(x) > DictationTextRules.MaximumPhraseLength);
        bool CanSave() => termBox.Text.Trim().Length > 0 && Spellings().Count > 0 && !TooLong();

        void Sync()
        {
            lengthError.Visibility = TooLong() ? Visibility.Visible : Visibility.Collapsed;
            save.IsEnabled = CanSave();
            save.Content = editingTerm is null ? "Add" : "Save";
            cancel.Visibility = editingTerm is null ? Visibility.Collapsed : Visibility.Visible;
            termBox.IsEnabled = editingTerm is null;
        }

        void ResetForm()
        {
            editingTerm = null;
            spellBox.Text = ""; termBox.Text = "";
            Sync();
        }

        void Commit()
        {
            if (!CanSave()) return;
            var term = termBox.Text.Trim();
            var spellings = Spellings().ToList();
            if (editingTerm is null)
            {
                var existing = S.CustomVocabulary.FirstOrDefault(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && S.VocabularyMisspellings.TryGetValue(existing, out var kept))
                    spellings = kept.Concat(spellings).ToList();
            }
            S.SetVocabularyCorrection(editingTerm ?? term, spellings);
            ResetForm();
        }

        void Render()
        {
            list.Children.Clear();
            foreach (var c in S.VocabularyCorrections)
            {
                var term = c.Term;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(IconBtn(PencilGlyph, () =>
                {
                    editingTerm = term;
                    termBox.Text = term;
                    spellBox.Text = string.Join(", ", c.Misspellings);
                    Sync();
                }, $"Edit correction for {term}", "Edit"));
                buttons.Children.Add(IconBtn(TrashGlyph, () =>
                {
                    S.SetVocabularyCorrection(term, []);
                    if (editingTerm == term) ResetForm();
                }, $"Remove correction for {term}", "Remove"));
                list.Children.Add(Row(term, buttons, "Heard as: " + string.Join(", ", c.Misspellings)));
                list.Children.Add(Divider());
            }
        }

        spellBox.TextChanged += (_, _) => Sync();
        termBox.TextChanged += (_, _) => Sync();
        KeyEventHandler enter = (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        spellBox.KeyDown += enter; termBox.KeyDown += enter;
        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => ResetForm();

        importButton.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "Import vocabulary CSV", Filter = "CSV files (*.csv)|*.csv", Multiselect = false };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            importing = true; importButton.IsEnabled = false;
            string text; Tone tone;
            try
            {
                var parsed = await Task.Run(() => VocabularyCsvImport.Read(dialog.FileName));
                var (imported, skipped) = S.ImportVocabulary(parsed);
                text = VocabularyCsvImport.SuccessMessage(imported, skipped); tone = Tone.Success;
            }
            catch (VocabularyCsvException ex) { text = VocabularyCsvImport.FailureMessage(ex.Failure); tone = Tone.Error; }
            catch (Exception) { text = VocabularyCsvImport.UnreadableMessage; tone = Tone.Error; }
            importing = false; importButton.IsEnabled = !importing;
            importMessage.Text = text; SetTone(importMessage, tone);
            importMessage.Visibility = Visibility.Visible;
        };

        var formRow = new StackPanel { Orientation = Orientation.Horizontal };
        formRow.Children.Add(spellHost);
        formRow.Children.Add(arrow);
        formRow.Children.Add(termHost);
        save.Margin = new Thickness(10, 0, 0, 0); cancel.Margin = new Thickness(8, 0, 0, 0);
        formRow.Children.Add(save); formRow.Children.Add(cancel);

        var importRow = new StackPanel { Orientation = Orientation.Horizontal };
        importRow.Children.Add(importButton);
        importRow.Children.Add(importMessage);

        var form = new StackPanel();
        formRow.Margin = new Thickness(0, 0, 0, 10);
        lengthError.Margin = new Thickness(0, 0, 0, 10);
        form.Children.Add(formRow);
        form.Children.Add(lengthError);
        form.Children.Add(importRow);

        var body = new StackPanel();
        body.Children.Add(list);
        body.Children.Add(Pad(form, 16, 12));
        BindRender(body, nameof(RemySettings.VocabularyCorrections), Render);
        Sync();
        Render();

        return Sec("Spelling Corrections", Card(body, Pad(Text(
            "Replaces a spelling you name with the saved term, on this PC, before any cleanup. Matching ignores case and only replaces whole words or phrases. Saved terms are also added to Custom Vocabulary. A CSV can list one term per row or misspelling,correction pairs (up to 1,000 rows, 3 MB)."))));
    }

    // ------------------------------------------------------------------ Snippets

    static string SnippetPreview(string expansion)
    {
        if (expansion.Length == 0) return "Removes the phrase";
        var nl = expansion.IndexOfAny(['\r', '\n']);
        var first = nl >= 0 ? expansion[..nl] : expansion;
        if (DictationTextRules.Length(first) > 120)
        {
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(first);
            var count = 0; var end = 0;
            while (e.MoveNext() && count < 120) { count++; end = e.ElementIndex + ((string)e.Current).Length; }
            return first[..end] + "…";
        }
        return nl >= 0 ? first + "…" : first;
    }

    UIElement SnippetsSection()
    {
        Guid? editingId = null;
        var list = new StackPanel();
        var (triggerHost, triggerBox) = Input("Trigger phrase, e.g. my calendar link", width: 320, automationName: "Snippet trigger phrase");
        var (textHost, textBox) = TextArea("Text to insert…", "", minHeight: 64, maxHeight: 200);
        System.Windows.Automation.AutomationProperties.SetName(textBox, "Snippet text");
        var save = Btn("Add Snippet", () => { });
        var cancel = Btn("Cancel", () => { });
        var counter = Text("", margin: null);
        counter.FontSize = 11.5;
        counter.VerticalAlignment = VerticalAlignment.Center;
        var error = Message("", Tone.Error);

        string? Validation()
        {
            var trigger = triggerBox.Text.Trim();
            if (DictationTextRules.Length(trigger) > DictationSnippet.MaximumTriggerLength) return "Trigger phrases can be up to 60 characters.";
            if (DictationTextRules.Length(textBox.Text.Trim()) > DictationSnippet.MaximumExpansionLength) return "Snippet text can be up to 4,000 characters.";
            if (trigger.Length > 0 && S.DictationSnippets.Any(x => x.Id != editingId && string.Equals(x.Trigger, trigger, StringComparison.OrdinalIgnoreCase)))
                return "Another snippet already uses this trigger phrase.";
            return null;
        }

        void Sync()
        {
            var v = Validation();
            error.Text = v ?? "";
            error.Visibility = v is null ? Visibility.Collapsed : Visibility.Visible;
            save.IsEnabled = v is null && triggerBox.Text.Trim().Length > 0;
            save.Content = editingId is null ? "Add Snippet" : "Save";
            cancel.Visibility = editingId is null ? Visibility.Collapsed : Visibility.Visible;
            counter.Text = $"{DictationTextRules.Length(textBox.Text):N0} / 4,000";
        }

        void ResetForm()
        {
            editingId = null;
            triggerBox.Text = ""; textBox.Text = "";
            Sync();
        }

        void Commit()
        {
            if (!save.IsEnabled) return;
            var snippet = new DictationSnippet(editingId ?? Guid.NewGuid(), triggerBox.Text, textBox.Text).Validated();
            if (snippet is null) return;
            var items = S.DictationSnippets.ToList();
            var i = items.FindIndex(x => x.Id == snippet.Id);
            if (i >= 0) items[i] = snippet; else items.Add(snippet);
            S.DictationSnippets = items;
            ResetForm();
        }

        void Render()
        {
            list.Children.Clear();
            foreach (var sn in S.DictationSnippets)
            {
                var snippet = sn;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(IconBtn(PencilGlyph, () =>
                {
                    editingId = snippet.Id;
                    triggerBox.Text = snippet.Trigger;
                    textBox.Text = snippet.Expansion;
                    Sync();
                }, $"Edit snippet {snippet.Trigger}", "Edit"));
                buttons.Children.Add(IconBtn(TrashGlyph, () =>
                {
                    S.DictationSnippets = S.DictationSnippets.Where(x => x.Id != snippet.Id).ToList();
                    if (editingId == snippet.Id) ResetForm();
                }, $"Delete snippet {snippet.Trigger}", "Delete"));
                list.Children.Add(Row(snippet.Trigger, buttons, SnippetPreview(snippet.Expansion)));
                list.Children.Add(Divider());
            }
        }

        triggerBox.TextChanged += (_, _) => Sync();
        textBox.TextChanged += (_, _) => Sync();
        triggerBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => ResetForm();

        var buttonRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonRow.Children.Add(save);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(cancel, 1); buttonRow.Children.Add(cancel);
        Grid.SetColumn(counter, 3); buttonRow.Children.Add(counter);

        var form = new StackPanel();
        triggerHost.HorizontalAlignment = HorizontalAlignment.Left;
        triggerHost.Margin = new Thickness(0, 0, 0, 10);
        error.Margin = new Thickness(0, 8, 0, 0);
        form.Children.Add(triggerHost);
        form.Children.Add(textHost);
        form.Children.Add(buttonRow);
        form.Children.Add(error);

        var body = new StackPanel();
        body.Children.Add(list);
        body.Children.Add(Pad(form, 16, 12));
        BindRender(body, nameof(RemySettings.DictationSnippets), Render);
        Sync();
        Render();

        return Sec("Snippets", Card(body, Pad(Text(
            "Say a trigger phrase to insert its text. Snippets expand on this PC after cleanup, so their text is never sent to a cleanup provider. Leave the text empty to remove the phrase."))));
    }
}
