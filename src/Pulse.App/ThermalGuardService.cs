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
            var hot = $"işlemci {s.CpuTempC:0}°C, ekran kartı {s.Gpu?.TempC}°C";
            switch (ev)
            {
                case GuardEvent.Warn:
                    Journal.Write($"Isı bekçisi: uyarı ({hot}).");
                    Notice?.Invoke($"Isı yüksek: {hot}.", true);
                    break;
                case GuardEvent.Cool:
                    if (AppServices.Modes.CurrentKey == Modes.Game)
                    {
                        // Oyun ortasında Sessiz moda atlamak oyunu bozar; kademeli sınırlamayı "Sıcaklık sınırı" yapar.
                        Journal.Write($"Isı bekçisi: çok sıcak ({hot}); Oyun modunda olduğu için Sessiz moda geçilmedi.");
                        Notice?.Invoke($"Çok sıcak: {hot}.", true);
                        break;
                    }
                    _restoreTo = AppServices.Modes.CurrentKey is { } cur && cur != Modes.Quiet ? cur : _restoreTo;
                    Journal.Write($"Isı bekçisi: serinletme ({hot}), önceki mod {_restoreTo}.");
                    Notice?.Invoke($"Sıcaklık tehlikeli seviyede kaldı ({hot}). Sessiz moda geçiliyor, ısı düşünce eski moda dönülecek.", true);
                    await AppServices.Modes.ApplyAsync(Modes.Quiet);
                    break;
                case GuardEvent.Recovered:
                    Journal.Write("Isı bekçisi: sıcaklık düştü.");
                    if (_restoreTo is { } back && AppServices.Modes.CurrentKey == Modes.Quiet)
                    {
                        Notice?.Invoke($"Sıcaklık normale döndü. {back} moduna geri dönülüyor.", false);
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