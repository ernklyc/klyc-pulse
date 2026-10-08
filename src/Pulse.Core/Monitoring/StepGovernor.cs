namespace Pulse.Core.Monitoring;

/// <summary>
/// Isı hedefi karar mantığı (saf, test edilebilir): fan eğrisi yerine geçen yazılım dengesi.
/// Sıcaklık hedefin üstünde kalırsa bir kademe kısar (daha serin), hedefin yeterince altında kalırsa bir kademe geri verir.
/// Kademe 0 = tam performans. Kademeler arasında bekleme süresi vardır; böylece gidip gelme (titreşim) olmaz.
/// </summary>
public sealed class StepGovernor
{
    private readonly int _maxLevel;
    private DateTime? _hotSince, _coolSince, _lastChange;

    public StepGovernor(int maxLevel) => _maxLevel = maxLevel;

    /// <summary>Hedef sıcaklık (°C).</summary>
    public double TargetC { get; set; } = 85;
    public double ReleaseMarginC { get; init; } = 8;
    public TimeSpan CoolDownAfter { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RecoverAfter { get; init; } = TimeSpan.FromSeconds(40);
    public TimeSpan MinBetweenChanges { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Şu anki kademe (0 = tam performans).</summary>
    public int Level { get; private set; }

    public void Reset()
    {
        Level = 0;
        _hotSince = _coolSince = _lastChange = null;
    }

    /// <summary>Yeni bir ölçüm. Kademe değiştiyse yeni kademeyi, değişmediyse null döner.</summary>
    public int? Feed(double? tempC, DateTime now)
    {
        if (tempC is not { } t) { _hotSince = _coolSince = null; return null; }

        var hot = t > TargetC;
        var cool = t < TargetC - ReleaseMarginC;
        _hotSince = hot ? _hotSince ?? now : null;
        _coolSince = cool ? _coolSince ?? now : null;

        if (_lastChange is { } last && now - last < MinBetweenChanges) return null;

        if (_hotSince is { } h && now - h >= CoolDownAfter && Level < _maxLevel)
        {
            Level++;
            _hotSince = null;
            _lastChange = now;
            return Level;
        }
        if (_coolSince is { } c && now - c >= RecoverAfter && Level > 0)
        {
            Level--;
            _coolSince = null;
            _lastChange = now;
            return Level;
        }
        return null;
    }
}
