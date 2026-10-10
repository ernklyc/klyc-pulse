using System.Windows;
using Pulse.App.ViewModels;

namespace Pulse.App;

/// <summary>"Açık uygulamaları kapat" penceresi (Ana ekran düğmesi ve tepsi menüsünden açılır).</summary>
public partial class BoostWindow : Window
{
    public BoostWindow()
    {
        InitializeComponent();
        DataContext = new BoostViewModel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
