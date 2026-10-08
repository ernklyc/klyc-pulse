using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class HealthPage : Page
{
    private readonly HealthViewModel _vm = new();

    public HealthPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Unloaded += (_, _) => _vm.Dispose();
    }
}
