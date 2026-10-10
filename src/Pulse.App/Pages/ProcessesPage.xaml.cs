using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class ProcessesPage : Page
{
    private ProcessesViewModel _vm = new();
    private bool _stopped;

    public ProcessesPage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sayfa önbellekte kalır: ekrandan çıkınca ölçümler durur, geri gelince yeni ViewModel ile yeniden başlar
        Loaded += (_, _) => { if (_stopped) { _vm = new ProcessesViewModel(); DataContext = _vm; _stopped = false; } };
        // Fare listedeyken sıralama dondurulur: tıklanan satır kaymaz
        MouseEnter += (_, _) => _vm.HoldOrder = true;
        MouseLeave += (_, _) => _vm.HoldOrder = false;
        Unloaded += (_, _) => { _vm.Dispose(); _stopped = true; };
    }
}
