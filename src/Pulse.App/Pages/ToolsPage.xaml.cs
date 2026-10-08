using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        DataContext = new ToolsViewModel();
    }
}
