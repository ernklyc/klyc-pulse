using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class DriversPage : Page
{
    public DriversPage()
    {
        InitializeComponent();
        DataContext = new DriversViewModel();
    }
}
