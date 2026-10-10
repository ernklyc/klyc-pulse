using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class ProcessesPage : Page
{
    private readonly ProcessesViewModel _vm = new();

    public ProcessesPage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sayfa önbellekte kalır: görünmezken yalnızca ölçüm/yenileme durur, işlemler ve durum korunur
        Loaded += (_, _) => _vm.Resume();
        // Fare listedeyken sıralama dondurulur: tıklanan satır kaymaz
        MouseEnter += (_, _) => _vm.HoldOrder = true;
        MouseLeave += (_, _) => _vm.HoldOrder = false;
        Unloaded += (_, _) => _vm.Pause();
    }
}
