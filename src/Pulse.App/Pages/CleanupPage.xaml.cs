using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class CleanupPage : Page
{
    public CleanupPage()
    {
        InitializeComponent();
        DataContext = new CleanupViewModel();
    }
}