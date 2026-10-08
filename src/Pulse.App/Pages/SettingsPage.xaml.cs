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
        Unloaded += (_, _) => _vm.Dispose();
    }
}