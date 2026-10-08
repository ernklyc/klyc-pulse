using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class MonitorPage : Page
{
    private readonly MonitorViewModel _vm = new();

    public MonitorPage()
    {
        InitializeComponent();
        DataContext = _vm;
        Unloaded += (_, _) => _vm.Dispose();
    }
}
