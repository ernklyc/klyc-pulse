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
        Unloaded += (_, _) => _vm.Dispose();
    }
}
