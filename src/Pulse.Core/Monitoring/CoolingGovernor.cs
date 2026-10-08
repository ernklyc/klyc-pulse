namespace Pulse.Core.Monitoring;

public enum CoolingAction { None, FanOn, ThrottleOn, ThrottleOff, FanOff }

/// <summary>
/// "Önce soğut, sonra yavaşlat" karar mantığı (saf, test edilebilir).
/// Kademeler: <b>0 Normal</b> → sıcaklık uzun süre yüksekse <b>1 Fan desteği</b> (fan tam hızda) → fan yetmezse <b>2 Yavaşlatma</b>
/// (işlemci hızı kademeli düşer) → serinleyince aynı yoldan geri. Her geçişin bir bekleme süresi vardır: gidip gelme (titreşim) olmaz.
/// Oyun modunda yavaşlatma kademesine geçilmez (oyuncunun FPS'ini kendi kendine kısmaz; orada ısıyı Akıllı oyun ayarı yönetir).
/// </summary>
public sealed class CoolingGovernor
{
    public double FanOnC { get; init; } = 88;
    public double ThrottleOnC { get; init; } = 93;
    public double ThrottleOffC { get; init; } = 85;
    public double FanOffC { get; init; } = 78;

    public TimeSpan FanOnAfter { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ThrottleOnAfter { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan ThrottleOffAfter { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan FanOffAfter { get; init; } = TimeSpan.FromSeconds(90);

    private DateTime? _hotSince, _coolSince;

    /// <summary>Şu anki kademe: 0 normal, 1 fan desteği, 2 fan desteği + yavaşlatma.</summary>
    public int Stage { get; private set; }

    public void Reset() { Stage = 0; _hotSince = _coolSince = null; }

    /// <param name="allowThrottle">false ise (Oyun modu) yavaşlatma kademesine geçilmez; zaten oradaysa bırakılır.</param>
    public CoolingAction Feed(double? tempC, DateTime now, bool allowThrottle = true)
    {
        if (tempC is not { } t) { _hotSince = _coolSince = null; return CoolingAction.None; }

        // Oyuna girildi: yavaşlatmayı hemen bırak, fan desteği kalır
        if (Stage == 2 && !allowThrottle) { Stage = 1; _hotSince = _coolSince = null; return CoolingAction.ThrottleOff; }

        // Bu kademeden yukarı çıkma ve aşağı inme koşulları
        var up = Stage == 0 ? FanOnC : Stage == 1 && allowThrottle ? ThrottleOnC : double.PositiveInfinity;
        var upAfter = Stage == 0 ? FanOnAfter : ThrottleOnAfter;
        var down = Stage == 1 ? FanOffC : Stage == 2 ? ThrottleOffC : double.NegativeInfinity;
        var downAfter = Stage == 1 ? FanOffAfter : ThrottleOffAfter;

        _hotSince = t >= up ? _hotSince ?? now : null;
        _coolSince = t < down ? _coolSince ?? now : null;

        if (_hotSince is { } h && now - h >= upAfter)
        {
            Stage++;
            _hotSince = _coolSince = null;
            return Stage == 1 ? CoolingAction.FanOn : CoolingAction.ThrottleOn;
        }
        if (_coolSince is { } c && now - c >= downAfter)
        {
            var was = Stage;
            Stage--;
            _hotSince = _coolSince = null;
            return was == 2 ? CoolingAction.ThrottleOff : CoolingAction.FanOff;
        }
        return CoolingAction.None;
    }
}
