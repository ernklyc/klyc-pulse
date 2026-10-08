using System.Windows;
using Pulse.Core.Modes;
using Wpf.Ui.Appearance;

namespace Pulse.App;

public partial class App : Application
{
#if DEBUG
    private const string MutexName = "Pulse.SingleInstance.v1.dev";
    private const string ShowEventName = "Pulse.ShowWindow.v1.dev";
#else
    private const string MutexName = "Pulse.SingleInstance.v1";
    private const string ShowEventName = "Pulse.ShowWindow.v1";
#endif

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private MainWindow? _window;

    public static bool IsExiting { get; private set; }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Görev çubuğunda doğru simge/gruplama ve bildirimlerde doğru ad için
        SetCurrentProcessExplicitAppUserModelID("KLYC.Pulse");

        _mutex = new Mutex(true, MutexName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!isFirst)
        {
            // Zaten çalışıyor: var olan pencereyi öne getir ve çık.
            _showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Beklenmeyen hatalar kapanmaya yol açmasın, günlüğe yazılsın.
        DispatcherUnhandledException += (_, args) =>
        {
            Pulse.Core.Diagnostics.Journal.Write("Arayüz hatası: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Pulse.Core.Diagnostics.Journal.Write("Arka plan görevi hatası: " + args.Exception);
            args.SetObserved();
        };

        // Vurgu rengi: sistem mavisi yerine tema mürekkebi (anahtarlar, seçili öğeler).
        ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27), ApplicationTheme.Light);

        // Görünüm denemesi (geliştirici): gösterge + uyarı yazısını birkaç saniye gösterip çıkar.
        if (e.Args.Any(a => a.Equals("--notice-test", StringComparison.OrdinalIgnoreCase)))
        {
            AppServices.Overlay.Set(true);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                NoticeChip.Show("Isı yüksek: işlemci 96°C, ekran kartı 63°C.", true);
                var end = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                end.Tick += (_, _) => { end.Stop(); AppServices.Overlay.Set(false); Shutdown(); };
                end.Start();
            };
            t.Start();
            return;
        }

        var selfTest = e.Args.FirstOrDefault(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase));
        _window = new MainWindow();
        CreateTray();

        // Açılışta tepsiye başlatma: pencere gösterilmez.
        if (!e.Args.Contains("--tray") && selfTest is null) _window.Show();

        AppServices.Modes.Applied += OnModeApplied;
        AppServices.AutoClean.Completed += freed =>
            _tray?.ShowBalloonTip(3000, "KLYC-Pulse", freed > 0 ? $"Otomatik temizlik: {freed / 1048576.0:N0} MB gereksiz dosya temizlendi." : "Otomatik temizlik çalıştı, temizlenecek bir şey yoktu.", System.Windows.Forms.ToolTipIcon.Info);

        AppServices.Hotkeys.Message += text => Dispatcher.Invoke(() => _tray?.ShowBalloonTip(2000, "KLYC-Pulse", text, System.Windows.Forms.ToolTipIcon.Info));
        AppServices.Hotkeys.Enabled = AppServices.Settings.Current.Hotkeys;
        // Isı uyarıları: sağ alttaki büyük Windows balonu yerine sağ üstte küçük, kısa süreli yazı.
        AppServices.Guard.Notice += (text, warn) => Dispatcher.Invoke(() => NoticeChip.Show(text, warn));
        AppServices.Guard.Enabled = AppServices.Settings.Current.ThermalGuard;
        AppServices.Heat.Configure(AppServices.Settings.Current.HeatTarget);
        // Önceki oturumdan (çökme vb.) kalmış frekans sınırı varsa ve sıcaklık sınırı kapalıysa temizle; sessizce yavaş kalmasın.
        if (AppServices.Settings.Current.HeatTarget is null) _ = Task.Run(() => HeatTargetService.ClearStaleFrequencyCap());
        // Oyun raporu: oyun açılınca kayıt başlar, kapanınca küçük bir bilgi yazısı çıkar.
        AppServices.Auto.GameStarted += game => AppServices.GameReport.Start(game);
        AppServices.Auto.GameStopped += _ => AppServices.GameReport.Stop();
        AppServices.GameReport.Ready += r => Dispatcher.Invoke(() =>
            NoticeChip.Show(r.Severity == 0 ? "Oyun raporu hazır: sorun görülmedi (Oyunlar sayfası)." : "Oyun raporu hazır: dikkat edilecek şeyler var (Oyunlar sayfası).", r.Severity > 0));
        AppServices.Companion.Start();
        AppServices.Keeper.Notice += text => Dispatcher.Invoke(() => _tray?.ShowBalloonTip(4000, "KLYC-Pulse", text, System.Windows.Forms.ToolTipIcon.Info));
        _ = Task.Run(async () => { await Task.Delay(4000); await AppServices.Modes.ReapplyGpuAsync(); });

        // Kaydedilmiş pil limiti / klavye ışığı seçimleri firmware'e yeniden yazılır (arka planda).
        _ = Task.Run(() => Pulse.Core.Hardware.LaptopControl.ApplySaved(AppServices.Settings.Current));

        if (selfTest is not null)
        {
            // Kendini sınama: yönetici ortamında her özelliği dener, sonucu dosyaya yazar ve kapanır.
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);   // servislerin ayağa kalkması için
                try { await SelfTest.RunAsync(selfTest.Contains("gpu", StringComparison.OrdinalIgnoreCase)); }
                catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Kendini sınama çöktü: " + ex); }
                Dispatcher.Invoke(ExitApp);
            });
        }

        var listener = new Thread(() =>
        {
            while (!IsExiting)
            {
                if (_showEvent.WaitOne(500)) Dispatcher.Invoke(ShowWindow);
            }
        }) { IsBackground = true };
        listener.Start();
    }

    private void CreateTray()
    {
        var info = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();
        RebuildTrayMenu();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "KLYC-Pulse",
            Icon = new System.Drawing.Icon(info.Stream),
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow();
        };
    }

    private void RebuildTrayMenu()
    {
        if (_trayMenu is null) return;
        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("KLYC-Pulse'ı aç", null, (_, _) => ShowWindow());
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var current = AppServices.Modes.CurrentKey;
        foreach (var mode in Modes.All)
        {
            var key = mode.Key;
            var item = new System.Windows.Forms.ToolStripMenuItem($"{mode.Title} modu") { Checked = key == current };
            item.Click += async (_, _) => await AppServices.Modes.ApplyAsync(key);
            _trayMenu.Items.Add(item);
        }

        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Çıkış", null, (_, _) => ExitApp());
    }

    private void OnModeApplied(ModeResult result)
    {
        var bad = result.Steps.Count(s => s.Status == StepStatus.Failed);
        _tray?.ShowBalloonTip(
            2500,
            "KLYC-Pulse",
            bad == 0 ? $"{result.Mode.Title} modu uygulandı." : $"{result.Mode.Title} modu uygulandı, {bad} ayar doğrulanamadı.",
            bad == 0 ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Topmost = true;
        _window.Activate();
        _window.Topmost = false;
    }

    public void ExitApp()
    {
        IsExiting = true;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        AppServices.Shutdown();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
