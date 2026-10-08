using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Soğutma önceliği: "önce soğut, sonra yavaşlat". Sıcaklık uzun süre yüksek kalırsa önce <b>fan desteği</b> (ASUS Turbo profili, fan devri
/// artar; ölçüldü: aynı yükte ~%14 fazla), fan yetmezse işlemci hızını kademeli düşürür (Isı hedefiyle aynı yöntem),
/// serinleyince aynı yoldan geri döner. Oyunda yalnızca yumuşak bir acil fren olarak, daha yüksek sıcaklıkta (95 °C üstü, 10 sn) ve küçük
/// adımlarla devreye girer (donanımın ~100 °C'deki ani kısmasından önce). En son çare olan Isı bekçisi (97 °C) ayrıca çalışmaya devam eder.
/// İşlemci sıcaklığı için yönetici yetkisi gerekir. Ayarlardan kapatılabilir.
/// </summary>
public sealed class CoolingService : IDisposable
{
    private readonly CoolingGovernor _gov = new();
    private IDisposable? _subscription;
    private bool _heatOwned;                 // Isı hedefini biz mi açtık? (kullanıcı kendi ayarını yaptıysa ona dokunmayız)
    private int _busy;
    private int _paused;

    /// <summary>Ölçüm denemeleri (ör. ısı denemesi) sırasında soğutma önceliğinin araya girmesini engeller.</summary>
    public IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume(this);
    }

    private sealed class Resume(CoolingService owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._paused); }
    }
    private int _gameThrottles;

    /// <summary>Oyun sırasında acil frenin kaç kez devreye girdiği (oyun raporu için). Okununca sıfırlanır.</summary>
    public int TakeGameThrottles() => Interlocked.Exchange(ref _gameThrottles, 0);

    public bool Enabled => _subscription is not null;
    public int Stage => _gov.Stage;

    public void Configure(bool on)
    {
        if (on == Enabled) return;
        if (on)
        {
            _subscription = AppServices.Sensors.Subscribe(wantFps: false);
            AppServices.Sensors.Updated += OnSensors;
            AppServices.Modes.Applied += OnModeApplied;
        }
        else
        {
            AppServices.Sensors.Updated -= OnSensors;
            AppServices.Modes.Applied -= OnModeApplied;
            _subscription?.Dispose();
            _subscription = null;
            ReleaseAll(wait: false);
        }
    }

    /// <summary>Yeni bir mod seçilince o mod kendi ASUS profilini ve hız sınırını yazar; kademeleri sıfırla.</summary>
    private void OnModeApplied(ModeResult _)
    {
        _gov.Reset();
        if (_heatOwned) { _heatOwned = false; AppServices.Heat.Configure(null); }
    }

    private void OnSensors(SensorSnapshot s)
    {
        if (Volatile.Read(ref _paused) > 0) return;
        var inGame = AppServices.Modes.CurrentKey == Modes.Game;
        // Yavaşlatmayı Isı hedefi yönetiyorsa, o kendi kademelerini geri vermeden (kademe 0) biz bırakmayız
        var settled = !_heatOwned || AppServices.Heat.CpuLevel == 0;
        var action = _gov.Feed(s.CpuTempC, DateTime.Now, inGame, settled);
        if (action == CoolingAction.None) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) { _gov.Reset(); return; }   // önceki eylem sürüyor: bu sefer atla, sayaç yeniden başlasın
        _ = Task.Run(async () =>
        {
            try { await Handle(action, s.CpuTempC, inGame); }
            catch (Exception ex) { Journal.Write("Soğutma önceliği hatası: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private async Task Handle(CoolingAction action, double? temp, bool inGame)
    {
        var t = temp is { } v ? $"{v:0} °C" : "yüksek";
        switch (action)
        {
            case CoolingAction.FanOn:
                var r = await AppServices.Modes.SetFanBoostAsync(true);
                if (r is null) { _gov.Reset(); return; }                         // mod uygulanıyordu: sonra yeniden denenir
                Journal.Write($"Soğutma önceliği: {t} uzun süre; fan desteği ({r.Status}).");
                if (!inGame) Notify($"Isı yüksek ({t}): önce fan desteği açıldı.");
                break;

            case CoolingAction.ThrottleOn:
                var target = inGame ? 92 : 90;
                if (!AppServices.Heat.Enabled) { AppServices.Heat.Configure(target); _heatOwned = true; }
                if (inGame) Interlocked.Increment(ref _gameThrottles);
                Journal.Write($"Soğutma önceliği: {(inGame ? "oyunda " : "")}fan yetmedi ({t}); işlemci hızı kademeli düşürülüyor (hedef {target} °C).");
                Notify(inGame
                    ? $"Çok sıcak ({t}): işlemci hızı küçük adımlarla kısılıyor, serinleyince geri verilir."
                    : $"Fan yetmedi ({t}): işlemci hızı kademeli düşürülüyor, serinleyince geri verilir.");
                break;

            case CoolingAction.ThrottleOff:
                if (_heatOwned) { _heatOwned = false; AppServices.Heat.Configure(null); }
                Journal.Write("Soğutma önceliği: sıcaklık düştü, işlemci hızı serbest.");
                break;

            case CoolingAction.FanOff:
                await AppServices.Modes.SetFanBoostAsync(false);
                Journal.Write("Soğutma önceliği: sıcaklık normale döndü, fan desteği kapatıldı.");
                break;
        }
    }

    private static void Notify(string text) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => NoticeChip.Show(text, true));

    /// <param name="wait">true ise (uygulama kapanırken) fan desteğinin geri alınması beklenir: Turbo profili bilgisayarda kalıcıdır, takılı kalmasın.</param>
    private void ReleaseAll(bool wait)
    {
        if (_heatOwned) { _heatOwned = false; AppServices.Heat.Configure(null); }
        if (_gov.Stage >= 1)
        {
            var t = AppServices.Modes.SetFanBoostAsync(false);
            if (wait) { try { t.Wait(6000); } catch { } }
        }
        _gov.Reset();
    }

    public void Dispose()
    {
        if (!Enabled) return;
        AppServices.Sensors.Updated -= OnSensors;
        AppServices.Modes.Applied -= OnModeApplied;
        _subscription?.Dispose();
        _subscription = null;
        ReleaseAll(wait: true);
    }
}