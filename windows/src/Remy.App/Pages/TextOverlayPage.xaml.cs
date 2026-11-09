using System.Windows.Controls;
using Remy.App.Services;

namespace Remy.App.Pages;

public partial class TextOverlayPage : UserControl, IServicePage
{
    public TextOverlayPage() => InitializeComponent();

    public void Attach(AppServices services)
    {
        var vm = new TextOverlayPageViewModel(services);
        DataContext = vm;
        Pill.Attach(vm.Preview);
    }
}
