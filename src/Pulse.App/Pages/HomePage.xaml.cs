using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class HomePage : Page
{
    private readonly HomeViewModel _vm = new();

    public HomePage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sensör okuması yalnızca sayfa görünürken çalışsın.
        Loaded += (_, _) => _vm.Live ??= new MonitorViewModel(withFps: false);
        Unloaded += (_, _) => { _vm.Live?.Dispose(); _vm.Live = null; };
    }
}