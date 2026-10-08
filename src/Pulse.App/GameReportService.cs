using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Oyun açıkken sensörleri (ısı, işlemci hızı, ekran kartı, bellek, FPS) kaydeder; oyun kapanınca sade dille bir rapor çıkarır.
/// Hiçbir ayarı değiştirmez. Rapor Oyunlar sayfasında görünür ve %LOCALAPPDATA%\Pulse altında saklanır.
/// </summary>
public sealed class GameReportService : IDisposable
{
    private readonly object _gate = new();
    private GameSessionRecorder? _recorder;
    private IDisposable? _subscription;

    public GameSessionReport? Last { get; private set; } = GameReportStore.Load();

    /// <summary>Yeni rapor hazır olunca (zamanlayıcı iş parçacığında tetiklenir).</summary>
    public event Action<GameSessionReport>? Ready;

    public void Start(string game)
    {
        lock (_gate)
        {
            if (_recorder is not null) return;
            _recorder = new GameSessionRecorder(game);
            _subscription = AppServices.Sensors.Subscribe(wantFps: true);
            AppServices.Sensors.Updated += OnSensors;
        }
    }

    private void OnSensors(SensorSnapshot s)
    {
        lock (_gate) _recorder?.Add(s, AppServices.Sensors.LatestFps);
    }

    public void Stop()
    {
        GameSessionRecorder? rec;
        lock (_gate)
        {
            rec = _recorder;
            _recorder = null;
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
            var report = rec.Build(AppServices.Sensors.BaseMhz, displayHz: hz, cpuCapMhz: cap, previous: previous);
            if (report is null) { Journal.Write($"Oyun raporu: oturum çok kısa ({rec.Count} sn), rapor yok."); return; }
            Last = report;
            GameReportStore.Save(report);
            Journal.Write($"Oyun raporu: {report.Title}");
            foreach (var f in report.Findings) Journal.Write("  - " + f);
            Ready?.Invoke(report);
        }
        catch (Exception ex) { Journal.Write("Oyun raporu hatası: " + ex.Message); }
    }

    public void Dispose() => Stop();
}
