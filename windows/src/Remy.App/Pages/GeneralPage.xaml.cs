using System.Windows.Controls;
using Remy.App.Services;

namespace Remy.App.Pages;

public partial class GeneralPage : UserControl, IServicePage
{
    public GeneralPage() => InitializeComponent();

    public void Attach(AppServices services) => DataContext = new GeneralPageViewModel(services);
}
