namespace Pulse.Core.Monitoring;

public enum GuardEvent
{
    None,
    /// <summary>Sıcaklık bir süredir yüksek: yalnızca uyar.</summary>
    Warn,
    /// <summary>Sıcaklık tehlikeli seviyede kaldı: modu serinletmek gerekir.</summary>
    Cool,
    /// <summary>Sıcaklık güvenli seviyeye indi: serinletme geri alınabilir.</summary>
    Recovered,
}

/// <summary>
/// Isı bekçisinin karar mantığı (saf, test edilebilir). Anlık sıcaklığa değil, süreye bakar: kısa tepe noktaları
/// (oyun yüklenirken) alarm vermez; uzun süre yüksek kalırsa uyarır, tehlikeliyse serinletir, düşünce bırakır.
/// </summary>
public sealed class ThermalGuard
{
    public double CpuWarnC { get; init; } = 90;
    public double CpuCoolC { get; init; } = 97;
    public double GpuWarnC { get; init; } = 83;
    public double GpuCoolC { get; init; } = 90;
    public double CpuSafeC { get; init; } = 82;
    public double GpuSafeC { get; init; } = 72;
    public TimeSpan WarnAfter { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CoolAfter { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SafeFor { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan WarnCooldown { get; init; } = TimeSpan.FromMinutes(10);

    private DateTime? _warnSince, _coolSince, _safeSince, _lastWarn;
    private bool _cooling;

    /// <summary>Serinletme şu an etkin mi (bekçi modu düşürdü ve henüz geri vermedi)?</summary>
    public bool IsCooling => _cooling;

    public GuardEvent Feed(double? cpuC, double? gpuC, DateTime now)
    {
        var cpuCool = cpuC >= CpuCoolC;
        var gpuCool = gpuC >= GpuCoolC;
        var warn = cpuC >= CpuWarnC || gpuC >= GpuWarnC;
        var cool = cpuCool || gpuCool;
        var safe = (cpuC ?? 0) < CpuSafeC && (gpuC ?? 0) < GpuSafeC;

        _coolSince = cool ? _coolSince ?? now : null;
        _warnSince = warn ? _warnSince ?? now : null;
        _safeSince = safe ? _safeSince ?? now : null;

        if (_cooling)
        {
            if (_safeSince is { } s && now - s >= SafeFor) { _cooling = false; _safeSince = null; return GuardEvent.Recovered; }
            return GuardEvent.None;
        }

        if (_coolSince is { } c && now - c >= CoolAfter)
        {
            _cooling = true;
            _coolSince = null;
            _safeSince = null;
            return GuardEvent.Cool;
        }

        if (_warnSince is { } w && now - w >= WarnAfter && (_lastWarn is null || now - _lastWarn >= WarnCooldown))
        {
            _lastWarn = now;
            return GuardEvent.Warn;
        }
        return GuardEvent.None;
    }
}
