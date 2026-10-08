using Pulse.Core.Diagnostics;

namespace Pulse.App;

/// <summary>
/// Modu korur: G-Helper, Armoury Crate ya da başka bir araç Windows güç ayarlarını (turbo, hız/güç dengesi) bozarsa
/// dakikada bir kontrol edip seçili modun değerlerini geri yazar. Kullanıcıyı bilgilendirir.
/// </summary>
public sealed class ModeKeeperService : IDisposable
{
    private readonly Timer _timer;
    private int _running;

    public event Action<string>? Notice;

    public ModeKeeperService() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(60));

    private async void Tick()
    {
        if (!AppServices.Settings.Current.KeepMode) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            // Sıcaklık sınırı açıksa işlemci üst sınırını kendisi değiştirir; onu "bozulma" sayma.
            var ignoreMax = AppServices.Heat.Enabled;
            if (await AppServices.Modes.RepairPowerAsync(ignoreMax))
                Notice?.Invoke("Başka bir program güç ayarlarını değiştirmişti. Seçtiğin modun ayarlarını geri düzelttim.");
        }
        catch (Exception ex) { Journal.Write("Mod koruyucu hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}