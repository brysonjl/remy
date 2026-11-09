using System.Windows.Controls;
using Remy.App.Services;

namespace Remy.App.Pages;

public partial class SpeechModelPage : UserControl, IServicePage
{
    public SpeechModelPage() => InitializeComponent();

    public void Attach(AppServices services) => DataContext = new SpeechModelPageViewModel(services);
}
