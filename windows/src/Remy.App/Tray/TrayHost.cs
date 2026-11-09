using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Remy.App.Tray;

/// <summary>Creates the tray icon and context menu (parity/ui.md section 5). Left click = open Remy, right click = menu.</summary>
public sealed class TrayHost : IDisposable
{
    readonly TrayViewModel _vm;
    readonly TaskbarIcon _icon;
    readonly MenuItem _micMenu;

    public TrayHost(TrayViewModel vm)
    {
        _vm = vm;
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("Remy.ContextMenu") };
        var itemStyle = (Style)Application.Current.FindResource("Remy.MenuItem");

        MenuItem Item(string header, System.Windows.Input.ICommand cmd, string? visibleProp = null)
        {
            var mi = new MenuItem { Header = header, Command = cmd, Style = itemStyle };
            if (visibleProp != null)
                mi.SetBinding(UIElement.VisibilityProperty, new Binding(visibleProp) { Source = vm, Converter = new BooleanToVisibilityConverter() });
            return mi;
        }

        var copy = Item("Copy Last Transcript", vm.CopyCommand);
        menu.Items.Add(Item("Open Remy", vm.OpenCommand));
        menu.Items.Add(copy);
        menu.Items.Add(Item("Stop Dictation", vm.StopCommand, nameof(TrayViewModel.IsStopVisible)));
        menu.Items.Add(Item("Use Raw Now", vm.UseRawCommand, nameof(TrayViewModel.IsUseRawVisible)));
        menu.Items.Add(Item("Cancel Dictation", vm.CancelCommand, nameof(TrayViewModel.IsCancelVisible)));
        menu.Items.Add(Item("Add Custom Vocabulary", vm.VocabularyCommand, nameof(TrayViewModel.IsAddVocabularyVisible)));
        menu.Items.Add(new Separator());
        _micMenu = new MenuItem { Header = "Microphone", Style = itemStyle };
        _micMenu.SubmenuOpened += (_, _) => { vm.NotifyMenuOpening(); RebuildMics(); };
        menu.Items.Add(_micMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", vm.QuitCommand));
        menu.Opened += (_, _) => { vm.NotifyMenuOpening(); RebuildMics(); };
        vm.Microphones.CollectionChanged += (_, _) => RebuildMics();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrayViewModel.IsAutomaticSelected) or nameof(TrayViewModel.AutomaticTitle)) RebuildMics();
            if (e.PropertyName == nameof(TrayViewModel.Tooltip)) _icon!.ToolTipText = vm.Tooltip;
        };
        RebuildMics();

        _icon = new TaskbarIcon
        {
            ToolTipText = vm.Tooltip,
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/remy.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = vm.OpenCommand,
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    void RebuildMics()
    {
        var itemStyle = (Style)Application.Current.FindResource("Remy.MenuItem");
        _micMenu.Items.Clear();
        var auto = new MenuItem { Header = _vm.AutomaticTitle, IsChecked = _vm.IsAutomaticSelected, Style = itemStyle, Command = _vm.SelectMicrophoneCommand, CommandParameter = null };
        _micMenu.Items.Add(auto);
        if (_vm.Microphones.Count > 0) _micMenu.Items.Add(new Separator());
        foreach (var mic in _vm.Microphones)
            _micMenu.Items.Add(new MenuItem { Header = mic.Title, IsChecked = mic.IsSelected, Style = itemStyle, Command = _vm.SelectMicrophoneCommand, CommandParameter = mic.Id });
    }

    public void Dispose() => _icon.Dispose();
}
