using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class SettingsPage : Page
{
    private SettingsViewModel _vm = new();
    private bool _stopped;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sayfa önbellekte kalır: ekrandan çıkınca ölçümler durur, geri gelince yeni ViewModel ile yeniden başlar
        Loaded += (_, _) => { if (_stopped) { _vm = new SettingsViewModel(); DataContext = _vm; _stopped = false; } };
        Unloaded += (_, _) => { _vm.Dispose(); _stopped = true; };
    }
}