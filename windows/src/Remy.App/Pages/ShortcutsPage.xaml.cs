using System.Windows.Controls;
using Remy.App.Services;

namespace Remy.App.Pages;

public partial class ShortcutsPage : UserControl, IServicePage
{
    public ShortcutsPage() => InitializeComponent();

    public void Attach(AppServices services) => DataContext = new ShortcutsPageViewModel(services);
}
