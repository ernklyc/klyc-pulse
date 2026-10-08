using System.ComponentModel;
using Pulse.App.Pages;
using Wpf.Ui.Controls;

namespace Pulse.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RootNavigation.Navigate(StartPage());
    }

    /// <summary>Pencereyi verilen sayfaya götürür (bildirime tıklayınca, turdan sonra).</summary>
    public void GoTo(Type page) => RootNavigation.Navigate(page);

    /// <summary>--page=ayarlar gibi bir anahtar varsa o sayfayla açılır (sınama ve kısayollar için).</summary>
    private static Type StartPage()
    {
        var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        return arg?[7..].ToLowerInvariant() switch
        {
            "oyunlar" => typeof(GamesPage),
            "temizlik" => typeof(CleanupPage),
            "uygulamalar" => typeof(AppsPage),
            "saglik" => typeof(HealthPage),
            "dizustu" => typeof(LaptopPage),
            "izleme" => typeof(MonitorPage),
            "suruculer" => typeof(DriversPage),
            "surecler" => typeof(ProcessesPage),
            "araclar" => typeof(ToolsPage),
            "ayarlar" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
    }

    /// <summary>Kapat düğmesi uygulamayı bitirmez, tepsiye küçültür.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && AppServices.Settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
