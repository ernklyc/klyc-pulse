using Pulse.Core.Localization;
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
    private int _paused;

    /// <summary>Kısa süreli güç ayarı denemeleri sırasında (ör. frekans sınırı denemesi) koruyucunun araya girmesini engeller.</summary>
    public IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume(this);
    }

    private sealed class Resume(ModeKeeperService owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._paused); }
    }

    public event Action<string>? Notice;

    public ModeKeeperService() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(60));

    private async void Tick()
    {
        if (!AppServices.Settings.Current.KeepMode || Volatile.Read(ref _paused) > 0) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            // Sıcaklık sınırı açıksa işlemci üst sınırını kendisi değiştirir; onu "bozulma" sayma.
            var ignoreMax = false;     // sıcaklık sınırı artık yalnızca frekans sınırını değiştirir; üst sınır % mod değerinde kalır
            if (await AppServices.Modes.RepairPowerAsync(ignoreMax))
                Notice?.Invoke(Loc.T("Başka bir program güç ayarlarını değiştirmişti. Seçtiğin modun ayarlarını geri düzelttim."));
        }
        catch (Exception ex) { Journal.Write("Mod koruyucu hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}