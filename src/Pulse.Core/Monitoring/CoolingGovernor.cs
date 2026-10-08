namespace Pulse.Core.Monitoring;

public enum CoolingAction { None, FanOn, ThrottleOn, ThrottleOff, FanOff }

/// <summary>
/// "Önce soğut, sonra yavaşlat" karar mantığı (saf, test edilebilir).
/// Kademeler: <b>0 Normal</b> → sıcaklık uzun süre yüksekse <b>1 Fan desteği</b> (fan tam hızda) → fan yetmezse <b>2 Yavaşlatma</b>
/// (işlemci hızı küçük adımlarla düşer) → serinleyince aynı yoldan geri. Her geçişin bir bekleme süresi vardır: gidip gelme olmaz.
/// <para>Oyunda eşikler farklıdır: yumuşak bir "acil fren" olarak yalnızca daha yüksek sıcaklıkta ve daha çabuk devreye girer
/// (donanımın ~100 °C'deki ani kısmasından önce, küçük adımlarla). Oyun dışında daha erken ve sabırlıdır.</para>
/// </summary>
public sealed class CoolingGovernor
{
    public double FanOnC { get; init; } = 88;
    public double FanOffC { get; init; } = 78;

    // Oyun dışı
    public double ThrottleOnC { get; init; } = 93;
    public double ThrottleOffC { get; init; } = 85;
    public TimeSpan ThrottleOnAfter { get; init; } = TimeSpan.FromSeconds(40);

    // Oyun içi (yumuşak acil fren)
    public double GameThrottleOnC { get; init; } = 95;
    public double GameThrottleOffC { get; init; } = 88;
    public TimeSpan GameThrottleOnAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FanOnAfter { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ThrottleOffAfter { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan FanOffAfter { get; init; } = TimeSpan.FromSeconds(90);

    private DateTime? _hotSince, _coolSince;

    /// <summary>Şu anki kademe: 0 normal, 1 fan desteği, 2 fan desteği + yavaşlatma.</summary>
    public int Stage { get; private set; }

    public void Reset() { Stage = 0; _hotSince = _coolSince = null; }

    /// <param name="inGame">Oyun modundayken oyun eşikleri kullanılır.</param>
    /// <param name="throttleSettled">
    /// Yavaşlatmayı yöneten kademe (Isı hedefi) kendi geri verme işini bitirdi mi (kademe 0)? false iken yavaşlatma bırakılmaz;
    /// böylece hız bir anda tamamen geri verilip sıcaklık yeniden fırlamaz (kademeler tek tek, o kademenin kendi kuralıyla döner).
    /// </param>
    public CoolingAction Feed(double? tempC, DateTime now, bool inGame = false, bool throttleSettled = true)
    {
        if (tempC is not { } t) { _hotSince = _coolSince = null; return CoolingAction.None; }

        var throttleOn = inGame ? GameThrottleOnC : ThrottleOnC;
        var throttleOnAfter = inGame ? GameThrottleOnAfter : ThrottleOnAfter;
        var throttleOff = inGame ? GameThrottleOffC : ThrottleOffC;

        var up = Stage == 0 ? FanOnC : Stage == 1 ? throttleOn : double.PositiveInfinity;
        var upAfter = Stage == 0 ? FanOnAfter : throttleOnAfter;
        var down = Stage == 1 ? FanOffC : Stage == 2 ? throttleOff : double.NegativeInfinity;
        var downAfter = Stage == 1 ? FanOffAfter : ThrottleOffAfter;
        var canGoDown = Stage != 2 || throttleSettled;

        _hotSince = t >= up ? _hotSince ?? now : null;
        _coolSince = t < down && canGoDown ? _coolSince ?? now : null;

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
