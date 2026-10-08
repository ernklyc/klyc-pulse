namespace Pulse.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Kurulum paketi (Velopack) kurulum/güncelleme/kaldırma sırasında uygulamayı özel parametrelerle çağırabilir; burada karşılanır.
        // Velopack ile kurulmamış (zip / tek exe) çalıştırmada hiçbir şey yapmaz.
        Velopack.VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
