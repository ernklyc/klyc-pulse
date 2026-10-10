using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = new();

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sayfa önbellekte kalır: görünmezken yalnızca ölçüm/yenileme durur, işlemler ve durum korunur
        Loaded += (_, _) => _vm.Resume();
        Unloaded += (_, _) => _vm.Pause();
    }
}