using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class LaptopPage : Page
{
    public LaptopPage()
    {
        InitializeComponent();
        DataContext = new LaptopViewModel();
    }
}
