using Pulse.Core.Localization;
using Pulse.Core.Automation;
using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Oyun açıkken sensörleri (ısı, işlemci hızı, ekran kartı, bellek, FPS) kaydeder; oyun kapanınca sade dille bir rapor çıkarır
/// ve (açıksa) oyuna özel işlemci hız sınırını kendi kendine ayarlar. Rapor Oyunlar sayfasında görünür.
/// </summary>
public sealed class GameReportService : IDisposable
{
    private readonly object _gate = new();
    private GameSessionRecorder? _recorder;
    private IDisposable? _subscription;
    private BackgroundLoadTracker? _background;
    private System.Threading.Timer? _bgTimer;
    private int _bgBusy;

    public GameSessionReport? Last { get; private set; } = GameReportStore.Load();

    /// <summary>Yeni rapor hazır olunca (ya da sonradan güncellenince) tetiklenir; zamanlayıcı iş parçacığında gelir.</summary>
    public event Action<GameSessionReport>? Ready;

    public void Start(string game)
    {
        lock (_gate)
        {
            if (_recorder is not null) return;
            _recorder = new GameSessionRecorder(game);
            _subscription = AppServices.Sensors.Subscribe(wantFps: true);
            AppServices.Sensors.Updated += OnSensors;
            // Arka plandaki programların yükü: 5 sn'de bir süreç sayaçları okunur (birkaç ms; oyunu etkilemez)
            _background = new BackgroundLoadTracker(Environment.ProcessorCount, game);
            _bgTimer = new System.Threading.Timer(_ => SampleBackground(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        // Pilde oynamak FPS'i yarıya kadar düşürebilir: oyun başlarken bir kez uyar.
        if (PowerSource.IsOnAc() == false)
            Dispatch(() => NoticeChip.Show(Loc.T("Pilde oynuyorsun. Prize takarsan FPS belirgin artar (pilde işlemci ve ekran kartı güç sınırına girer)."), true));
    }

    private static void Dispatch(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    private void OnSensors(SensorSnapshot s)
    {
        lock (_gate) _recorder?.Add(s, AppServices.Sensors.LatestFps, PowerSource.IsOnAc());
    }

    private void SampleBackground()
    {
        if (Interlocked.Exchange(ref _bgBusy, 1) == 1) return;      // önceki okuma bitmediyse atla
        try
        {
            var procs = ProcessSampler.Take();
            lock (_gate) _background?.Add(procs, DateTime.UtcNow);
        }
        catch (Exception ex) { Journal.Write("Arka plan ölçümü hatası: " + ex.Message); }
        finally { Volatile.Write(ref _bgBusy, 0); }
    }

    public void Stop()
    {
        GameSessionRecorder? rec;
        BackgroundSummary? background;
        lock (_gate)
        {
            rec = _recorder;
            _recorder = null;
            _bgTimer?.Dispose();
            _bgTimer = null;
            background = _background?.Summarize();
            _background = null;
            AppServices.Sensors.Updated -= OnSensors;
            _subscription?.Dispose();
            _subscription = null;
        }
        if (rec is null) return;

        try
        {
            var previous = GameReportStore.LastFor(rec.Game);
            var hz = Pulse.Core.Platform.DisplayService.GetRefreshRate();
            var cap = AppServices.Modes.ActiveDefinition?.CpuMaxMhz;
            var settings = AppServices.Settings;
            var s = settings.Current;
            var profile = settings.FindProfile(rec.Game);
            var auto = s.AutoTuneGames && profile is { AutoTune: true, Enabled: true };
            var report = rec.Build(AppServices.Sensors.BaseMhz, displayHz: hz, cpuCapMhz: cap, previous: previous, suggestCap: !auto,
                storage: DriveKindDetector.Detect(profile?.ExePath), suggestMhz: AppServices.GameLadder() is { Length: > 1 } lad ? lad[1] : null, background: background);
            if (report is null) { Journal.Write($"Oyun raporu: oturum çok kısa ({rec.Count} sn), rapor yok."); return; }

            // Yük altı tepe hızı öğren (sınırsız oturumlardan): kademeler bu bilgisayarın gerçek hızına göre kurulur
            if (cap is null && report.CpuMhzPeak is { } pk && pk > s.CpuPeakMhz) { s.CpuPeakMhz = pk; settings.Save(); }

            var brakeCount = AppServices.Cooling.TakeGameThrottles();
            // Sürekli yük altı hızı öğren (sınırsız ve fren kullanılmamış oturumlardan): kademeler bu hıza göre kurulur
            if (cap is null && brakeCount == 0 && report.CpuMhzLoadedAvg is { } sus && sus > 0)
            {
                s.CpuSustainedMhz = s.CpuSustainedMhz > 0 ? Math.Round(0.7 * s.CpuSustainedMhz + 0.3 * sus) : sus;
                settings.Save();
            }

            // Oyun sırasında Soğutma önceliğinin yumuşak acil freni devreye girdiyse raporda söyle
            if (brakeCount is > 0 and var brakes)
                report.Findings.Add(Loc.F("Oyun sırasında sıcaklık 95 °C'yi aştığı için işlemci hızı {0} kez küçük adımlarla kısıldı (donanımın ani kısmasını önlemek için). Bu, FPS'i hafifçe düşürmüş olabilir.", brakes));

            if (auto && profile is not null) RunAutoTune(profile, report, previous, cap);

            Publish(report);
        }
        catch (Exception ex) { Journal.Write("Oyun raporu hatası: " + ex.Message); }
    }

    /// <summary>Raporu saklar, günlüğe yazar ve arayüze bildirir.</summary>
    private void Publish(GameSessionReport report)
    {
        Last = report;
        GameReportStore.Save(report, replaceLast: true);
        Journal.Write($"Oyun raporu: {report.Title}");
        foreach (var f in report.Findings) Journal.Write("  - " + f);
        Ready?.Invoke(report);
    }

    private void RunAutoTune(Pulse.Core.Settings.GameProfile profile, GameSessionReport report, GameSessionReport? previous, int? cap)
    {
        var settings = AppServices.Settings;
        var s = settings.Current;
        string Note(string text) { report.Findings.Add("Otomatik ayar: " + text); profile.AutoTuneNote = text; settings.Save(); Journal.Write($"Otomatik ayar ({report.Game}): {text}"); return text; }

        // Sınır koyulmuşken hız yine de sınırı aşıyorsa: bu bilgisayar sınırı uygulamıyor
        if (cap is { } c && report.CpuMhzPeak is { } peak && peak > c * 1.12)
        {
            s.FreqCapSupported = false;
            s.FreqCapNote = Loc.F("Oyun sırasında hız {0:0} MHz'e çıktı ama sınır {1} MHz'di: bu bilgisayar sınırı uygulamıyor.", peak, c);
            profile.AutoTuneLocked = true;
            Note(s.FreqCapNote + Loc.T(" Otomatik ayar durduruldu (Oyunlar sayfasından yeniden denenebilir)."));
            return;
        }
        if (s.FreqCapSupported == false)
        {
            Note(Loc.T("Bu bilgisayar işlemci hız sınırını uygulamıyor (denemede ölçüldü); otomatik ayar yapılmıyor."));
            return;
        }

        var ladder = AppServices.GameLadder();
        var d = GameAutoTuner.Decide(profile.CpuMaxMhz, profile.AutoTuneLocked, report, previous, ladder);

        // İlk kez bir sınır koymadan önce, sınırın bu bilgisayarda gerçekten işe yaradığını ölç
        if (d.Changed && d.CapMhz is not null && s.FreqCapSupported != true)
        {
            if (PowerSource.IsOnAc() == false)
            {
                Note(Loc.T("Frekans sınırı denemesi prizde yapılır; prize takılıyken bir sonraki oyundan sonra denenecek."));
                return;
            }
            Note(Loc.T("Bu bilgisayarda işlemci hız sınırının işe yarayıp yaramadığı ölçülüyor (~20 sn, işlemci tam yüklenir). Sonuç gelince ayar uygulanır."));
            _ = Task.Run(async () =>
            {
                var r = await FreqCapService.RunAsync();
                if (r is null) return;
                var d2 = r.Supported == true ? GameAutoTuner.Decide(profile.CpuMaxMhz, profile.AutoTuneLocked, report, previous, AppServices.GameLadder()) : d;
                if (r.Supported == true && d2.Changed)
                {
                    profile.CpuMaxMhz = d2.CapMhz; profile.AutoTuneLocked = d2.Locked; report.AutoTuneChanged = true;
                    Note(d2.Note);
                }
                else Note(r.Supported == true ? d2.Note : r.Note + (r.Supported == false ? Loc.T(" Otomatik ayar yapılmıyor.") : " Sonraki oyundan sonra yeniden denenecek."));
                Publish(report);
            });
            return;
        }

        profile.AutoTuneLocked = d.Locked;
        if (d.Changed) { profile.CpuMaxMhz = d.CapMhz; report.AutoTuneChanged = true; }
        Note(d.Note);
    }

    public void Dispose() => Stop();
}
