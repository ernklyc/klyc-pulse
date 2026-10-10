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
        // Sayfa önbellekte kalır: görünmezken yalnızca ölçüm/yenileme durur, işlemler ve durum korunur
        Loaded += (_, _) => _vm.Resume();
        Unloaded += (_, _) => _vm.Pause();
    }
}
