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
        Unloaded += (_, _) => _vm.Dispose();
    }
}
