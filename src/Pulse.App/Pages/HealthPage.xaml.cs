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
        // Sayfa önbellekte kalır: görünmezken yalnızca ölçüm/yenileme durur, işlemler ve durum korunur
        Loaded += (_, _) => _vm.Resume();
        Unloaded += (_, _) => _vm.Pause();
    }
}
