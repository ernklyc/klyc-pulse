using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class AppsPage : Page
{
    public AppsPage()
    {
        InitializeComponent();
        DataContext = new AppsViewModel();
    }
}
