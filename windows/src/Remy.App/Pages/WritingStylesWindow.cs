using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Remy.Core.Context;
using Remy.Core.Settings;
using static Remy.App.Pages.CleanupUi;

namespace Remy.App.Pages;

/// <summary>Writing Styles dialog (v0.2-features.md §9.5): edit built-in style instructions and manage custom styles.
/// Modal, owned by the main window. Changes are written to the settings as they are saved.</summary>
public sealed class WritingStylesWindow : Window
{
    private enum EditorKind { None, BuiltIn, New, Custom }

    private readonly RemySettings _settings;
    private readonly ContentControl _body = new();
    private readonly Button _done;

    private EditorKind _editor = EditorKind.None;
    private DictationWritingStyle _editingBuiltIn;
    private Guid _editingCustom;

    public WritingStylesWindow(RemySettings settings)
    {
        _settings = settings;
        Title = "Writing Styles";
        Width = 600; Height = 640;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.SetResourceReference(BackgroundProperty, "Brush.BgWindow");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        _body.Margin = new Thickness(28);

        _done = Btn("Done", Close, "Remy.PrimaryButton", "Done");
        _done.IsDefault = true;
        _done.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new StackPanel();
        footer.Children.Add(Divider());
        footer.Children.Add(new Border { Padding = new Thickness(28, 14, 28, 14), Child = _done });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(scroll);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _editor != EditorKind.None) { LeaveEditor(); e.Handled = true; }
        };
        Render();
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/> (when it is visible).</summary>
    public static void ShowFor(Window? owner, RemySettings settings)
    {
        var w = new WritingStylesWindow(settings);
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    private void LeaveEditor()
    {
        _editor = EditorKind.None;
        Render();
    }

    private void Render()
    {
        _done.IsEnabled = _editor == EditorKind.None;
        var stack = new StackPanel();
        var title = Text("Writing Styles", "Text.PageTitle");
        stack.Children.Add(title);
        stack.Children.Add(Text("Style instructions tell cleanup how the text should sound. Core cleanup rules always apply.", "Text.PageSubtitle", new Thickness(0, 4, 0, 18)));
        stack.Children.Add(_editor == EditorKind.None ? BuildList() : BuildEditor());
        _body.Content = stack;
    }

    // ------------------------------------------------------------------ list mode

    private UIElement BuildList()
    {
        var host = new StackPanel();
        var library = _settings.WritingStyleLibrary;

        var builtIns = new List<UIElement?>();
        foreach (var style in DictationWritingStyleLibrary.BuiltInStyles)
        {
            var s = style;
            builtIns.Add(StyleRow(s.DisplayName(), library.Instruction(s), library.IsCustomized(s),
                () => OpenEditor(EditorKind.BuiltIn, s, Guid.Empty), null));
        }
        host.Children.Add(Sec("Built-in Styles", Card(builtIns.ToArray())));

        var customs = new List<UIElement?>();
        if (library.CustomStyles.Count == 0)
            customs.Add(Pad(Text("Add a style to choose it for any destination.")));
        foreach (var custom in library.CustomStyles)
        {
            var c = custom;
            customs.Add(StyleRow(c.Name, c.Instruction, false,
                () => OpenEditor(EditorKind.Custom, default, c.Id), () => ConfirmDelete(c)));
        }
        var add = Btn("Add Style", () => OpenEditor(EditorKind.New, default, Guid.Empty), automationName: "Add Style");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        customs.Add(Pad(add));
        var customSection = Sec("Custom Styles", Card(customs.ToArray()));
        customSection.Margin = new Thickness(0, 18, 0, 0);
        host.Children.Add(customSection);
        return host;
    }

    private static UIElement StyleRow(string name, string instruction, bool edited, Action edit, Action? delete)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var n = Text(name, "Text.RowLabel");
        n.FontSize = 13.5; n.FontWeight = FontWeights.Medium;
        head.Children.Add(n);
        if (edited)
        {
            var chip = Chip("Edited");
            chip.Margin = new Thickness(8, 0, 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(chip);
        }
        left.Children.Add(head);
        var body = Text(instruction, margin: new Thickness(0, 3, 0, 0));
        body.FontSize = 12.5;
        body.TextWrapping = TextWrapping.Wrap;
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        body.LineHeight = 17; body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        body.MaxHeight = 34;
        left.Children.Add(body);
        g.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(IconBtn(((char)0xE70F).ToString(), edit, $"Edit {name}", "Edit"));
        if (delete is not null)
        {
            var del = IconBtn(((char)0xE74D).ToString(), delete, $"Delete {name}", "Delete");
            del.SetResourceReference(Control.ForegroundProperty, "Brush.Destructive");
            buttons.Children.Add(del);
        }
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        return Pad(g);
    }

    private void ConfirmDelete(DictationCustomWritingStyle style)
    {
        var used = _settings.CategoriesUsingCustomStyle(style.Id);
        var message = used.Count == 0
            ? "No destination uses this style."
            : "These destinations use this style and will return to their default style: "
              + string.Join(", ", used.Select(c => $"{RemyEnumNames.ContextCategory.ToDisplay(c)} → {RemySettings.DefaultStyle(c).DisplayName()}")) + ".";
        if (!Confirm(this, $"Delete “{style.Name}”?", message, "Delete Style")) return;
        _settings.DeleteCustomWritingStyle(style.Id);
        Render();
    }

    // ------------------------------------------------------------------ editor mode

    private void OpenEditor(EditorKind kind, DictationWritingStyle builtIn, Guid custom)
    {
        _editor = kind;
        _editingBuiltIn = builtIn;
        _editingCustom = custom;
        Render();
    }

    private UIElement BuildEditor()
    {
        var library = _settings.WritingStyleLibrary;
        var isBuiltIn = _editor == EditorKind.BuiltIn;
        var existing = _editor == EditorKind.Custom ? library.Custom(_editingCustom) : null;
        var overline = _editor switch
        {
            EditorKind.BuiltIn => "Edit Built-in Style",
            EditorKind.New => "New Custom Style",
            _ => "Edit Custom Style",
        };

        var stack = new StackPanel();
        TextBox? nameBox = null;
        if (isBuiltIn)
        {
            var n = Text(_editingBuiltIn.DisplayName(), "Text.RowLabel");
            n.FontSize = 13.5; n.FontWeight = FontWeights.SemiBold; n.Margin = new Thickness(0, 0, 0, 12);
            stack.Children.Add(n);
        }
        else
        {
            var (host, box) = Input("Style name", existing?.Name ?? "", automationName: "Style name");
            nameBox = box;
            stack.Children.Add(Stacked("Name", host));
        }

        var initial = isBuiltIn ? library.Instruction(_editingBuiltIn) : existing?.Instruction ?? "";
        var (areaHost, area) = TextArea("Describe the tone and wording, for example: Use short, friendly sentences.", initial, 140, 320);
        System.Windows.Automation.AutomationProperties.SetName(area, "Style instructions");
        stack.Children.Add(Stacked("Instructions", areaHost));

        var counter = Text("", margin: new Thickness(0, -6, 0, 0));
        counter.FontSize = 12;
        System.Windows.Documents.Typography.SetNumeralAlignment(counter, FontNumeralAlignment.Tabular);
        stack.Children.Add(counter);

        var error = Message("", Tone.Error);
        error.Margin = new Thickness(0, 10, 0, 0);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);

        var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button? reset = null;
        if (isBuiltIn)
        {
            reset = Btn("Reset to Default", () =>
            {
                var lib = _settings.WritingStyleLibrary;
                lib.ResetInstruction(_editingBuiltIn);
                _settings.WritingStyleLibrary = lib;
                LeaveEditor();
            }, automationName: "Reset to Default");
            buttons.Children.Add(reset);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Btn("Cancel", LeaveEditor, automationName: "Cancel"));
        var save = Btn("Save", () =>
        {
            var lib = _settings.WritingStyleLibrary;
            try
            {
                switch (_editor)
                {
                    case EditorKind.BuiltIn: lib.SetInstruction(area.Text, _editingBuiltIn); break;
                    case EditorKind.New: lib.AddCustomStyle(nameBox!.Text, area.Text); break;
                    default: lib.UpdateCustomStyle(_editingCustom, nameBox!.Text, area.Text); break;
                }
            }
            catch (DictationWritingStyleValidationException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                return;
            }
            _settings.WritingStyleLibrary = lib;
            LeaveEditor();
        }, "Remy.PrimaryButton", "Save");
        save.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(save);
        Grid.SetColumn(right, 2);
        buttons.Children.Add(right);
        stack.Children.Add(buttons);

        void Sync()
        {
            counter.Text = $"{DictationWritingStyleLibrary.Length(area.Text.Trim())}/{DictationWritingStyleLibrary.MaximumInstructionLength} characters";
            if (reset is not null)
                reset.IsEnabled = library.IsCustomized(_editingBuiltIn) || area.Text.Trim() != _editingBuiltIn.CleanupInstruction();
        }
        area.TextChanged += (_, _) => Sync();
        Sync();

        areaHost.Loaded += (_, _) => (nameBox ?? area).Focus();

        return Sec(overline, Card(Pad(stack, 16, 16)));
    }
}
