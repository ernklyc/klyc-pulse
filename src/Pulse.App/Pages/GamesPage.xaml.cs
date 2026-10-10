using System.Windows.Controls;
using Pulse.App.ViewModels;

namespace Pulse.App.Pages;

public partial class GamesPage : Page
{
    private readonly GamesViewModel _vm = new();

    public GamesPage()
    {
        InitializeComponent();
        DataContext = _vm;
        // Sayfa önbellekte kalır: görünmezken yalnızca ölçüm/yenileme durur, işlemler ve durum korunur
        Loaded += (_, _) => _vm.Resume();
        Unloaded += (_, _) => _vm.Pause();
    }
}
