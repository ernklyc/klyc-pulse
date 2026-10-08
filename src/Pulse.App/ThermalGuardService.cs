using Pulse.Core.Localization;
using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Isı bekçisi: sıcaklık uzun süre tehlikeli seviyede kalırsa Sessiz moda geçer (turbo kapanır, ısı düşer),
/// güvenli seviyeye inince önceki moda döner. Önce uyarır, sonra serinletir. Ayarlardan kapatılabilir.
/// Oyun modundayken Sessiz moda geçmez (oyunu bozar), yalnızca uyarır.
/// </summary>
public sealed class ThermalGuardService : IDisposable
{
    private readonly ThermalGuard _guard = new();
    private IDisposable? _subscription;
    private string? _restoreTo;

    /// <summary>Kullanıcıya gösterilecek bildirim (metin, uyarı mı).</summary>
    public event Action<string, bool>? Notice;

    public bool Enabled
    {
        get => _subscription is not null;
        set
        {
            if (value == Enabled) return;
            if (value)
            {
                _subscription = AppServices.Sensors.Subscribe(wantFps: false);
                AppServices.Sensors.Updated += OnSensors;
            }
            else
            {
                AppServices.Sensors.Updated -= OnSensors;
                _subscription?.Dispose();
                _subscription = null;
            }
        }
    }

    private void OnSensors(SensorSnapshot s)
    {
        var ev = _guard.Feed(s.CpuTempC, s.Gpu?.TempC, DateTime.Now);
        if (ev == GuardEvent.None) return;
        _ = Handle(ev, s);
    }

    private async Task Handle(GuardEvent ev, SensorSnapshot s)
    {
        try
        {
            var hot = Loc.F("işlemci {0:0}°C, ekran kartı {1}°C", s.CpuTempC, s.Gpu?.TempC);
            switch (ev)
            {
                case GuardEvent.Warn:
                    Journal.Write($"Isı bekçisi: uyarı ({hot}).");
                    Notice?.Invoke(Loc.F("Isı yüksek: {0}.", hot), true);
                    break;
                case GuardEvent.Cool:
                    if (AppServices.Modes.CurrentKey == Modes.Game)
                    {
                        // Oyun ortasında Sessiz moda atlamak oyunu bozar; kademeli sınırlamayı "Sıcaklık sınırı" yapar.
                        Journal.Write($"Isı bekçisi: çok sıcak ({hot}); Oyun modunda olduğu için Sessiz moda geçilmedi.");
                        Notice?.Invoke(Loc.F("Çok sıcak: {0}.", hot), true);
                        break;
                    }
                    _restoreTo = AppServices.Modes.CurrentKey is { } cur && cur != Modes.Quiet ? cur : _restoreTo;
                    Journal.Write($"Isı bekçisi: serinletme ({hot}), önceki mod {_restoreTo}.");
                    Notice?.Invoke(Loc.F("Sıcaklık tehlikeli seviyede kaldı ({0}). Sessiz moda geçiliyor, ısı düşünce eski moda dönülecek.", hot), true);
                    await AppServices.Modes.ApplyAsync(Modes.Quiet);
                    break;
                case GuardEvent.Recovered:
                    Journal.Write("Isı bekçisi: sıcaklık düştü.");
                    if (_restoreTo is { } back && AppServices.Modes.CurrentKey == Modes.Quiet)
                    {
                        Notice?.Invoke(Loc.F("Sıcaklık normale döndü. {0} moduna geri dönülüyor.", back), false);
                        await AppServices.Modes.ApplyAsync(back);
                    }
                    _restoreTo = null;
                    break;
            }
        }
        catch (Exception ex) { Journal.Write("Isı bekçisi hatası: " + ex.Message); }
    }

    public void Dispose() => Enabled = false;
}