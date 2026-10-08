using Pulse.Core.Diagnostics;
using Pulse.Core.Hardware;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;
using Pulse.Core.Platform;

namespace Pulse.App;

/// <summary>
/// Sıcaklık sınırı: fan eğrisi bu modelde donanım tarafından kilitli olduğu için aynı sonucu yazılımla sağlar.
/// İşlemcinin en yüksek frekansını (MHz, ~300 MHz'lik küçük kademeler) ve ekran kartının saat sınırını sıcaklığa göre kademeli ayarlar;
/// sıcaklık düşünce geri verir. Mod değişince sıfırlanır. Yönetici ve işlemci sıcaklığı sensörü gerekir.
/// Neden frekans: Windows'ta "üst sınır %"ı 100'ün altına çekmek turbo'yu tamamen kapatır (bu bilgisayarda ölçüldü: 4,1 → 2,5 GHz tek adımda).
/// Frekans sınırı ise MHz olarak kademeli çalışır (ölçüldü: 3000 sınırı → 2995, 2400 sınırı → 2300 MHz).
/// </summary>
public sealed class HeatTargetService : IDisposable
{
    /// <summary>Kademe 0 = sınırsız (0), sonra sırayla düşen en yüksek frekanslar (MHz). Bu bilgisayarın gerçek hızından türetilir.</summary>
    private int[] _caps = [0, 3800, 3500, 3200, 2900, 2700, 2500];
    private int _paused;
    private const int GpuStepMhz = 150;
    private const int GpuMaxLevel = 6;

    private StepGovernor _cpu = new(6);
    private readonly StepGovernor _gpu = new(GpuMaxLevel);
    private IDisposable? _subscription;
    private int _busy;

    public bool Enabled => _subscription is not null;

    /// <summary>Kısa süreli güç ayarı denemeleri sırasında kademe değişikliklerini bekletir.</summary>
    public IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume(this);
    }

    private sealed class Resume(HeatTargetService owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._paused); }
    }
    public int CpuLevel => _cpu.Level;
    public int GpuLevel => _gpu.Level;
    public event Action<string>? Notice;

    public void Configure(int? targetC)
    {
        if (targetC is not { } t)
        {
            if (!Enabled) return;
            AppServices.Sensors.Updated -= OnSensors;
            _subscription?.Dispose();
            _subscription = null;
            AppServices.Modes.Applied -= OnModeApplied;
            ResetAndRestore();
            return;
        }

        _gpu.TargetC = Math.Max(65, t - 5);
        if (Enabled) { _cpu.TargetC = t; return; }
        // Kademeleri bu bilgisayarın gerçek hızından türet (yalnızca açılırken)
        var ladder = AppServices.CpuLadderFor(CpuLadder.HeatFactors);
        _caps = ladder.Select(x => x ?? 0).ToArray();
        _cpu = new StepGovernor(Math.Max(1, _caps.Length - 1)) { TargetC = t };
        _subscription = AppServices.Sensors.Subscribe(wantFps: false);
        AppServices.Sensors.Updated += OnSensors;
        AppServices.Modes.Applied += OnModeApplied;
    }

    private void OnModeApplied(ModeResult _) { _cpu.Reset(); _gpu.Reset(); }

    private void OnSensors(SensorSnapshot s)
    {
        if (Volatile.Read(ref _paused) > 0) return;
        var cpuChange = _cpu.Feed(s.CpuTempC, DateTime.Now);
        var gpuChange = _gpu.Feed(s.Gpu?.TempC, DateTime.Now);
        if (cpuChange is null && gpuChange is null) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (cpuChange is { } cl) ApplyCpu(cl, s.CpuTempC);
                if (gpuChange is { } gl) ApplyGpu(gl, s.Gpu?.TempC);
            }
            catch (Exception ex) { Journal.Write("Isı hedefi hatası: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private static ModeDefinition? CurrentMode() => AppServices.Modes.CurrentKey is { } k ? Modes.Get(k) : null;

    private void ApplyCpu(int level, double? temp)
    {
        var mode = CurrentMode();
        if (mode is null) return;
        // Oyun profili zaten bir sınır koyduysa (baseline) sıcaklık sınırı ondan daha gevşek olamaz; kademe 0 = o taban sınır.
        var baseline = AppServices.Modes.ActiveDefinition?.CpuMaxMhz ?? 0;
        var ladder = _caps[Math.Min(level, _caps.Length - 1)];
        var value = ladder == 0 ? baseline : baseline == 0 ? ladder : Math.Min(baseline, ladder);
        var scheme = Powercfg.ActiveScheme();
        if (scheme is null) return;
        WriteFrequencyCap(scheme, value);
        var read = Powercfg.GetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxFrequency);
        var text = value == 0 ? "sınırsız" : $"{value} MHz";
        Journal.Write($"Sıcaklık sınırı: işlemci {temp:0}°C → en yüksek frekans {text} (okunan {read}).");
        Notice?.Invoke(read == value
            ? $"Sıcaklık sınırı: işlemci {temp:0}°C, en yüksek frekans {text}."
            : $"Sıcaklık sınırı: frekans sınırı {text} yazıldı ama {read} okundu.");
    }

    /// <summary>Açılışta: sıcaklık sınırı kapalıyken kalmış bir frekans sınırı varsa kaldırır (çökme sonrası sessizce yavaş kalmasın).</summary>
    public static void ClearStaleFrequencyCap()
    {
        try
        {
            var scheme = Powercfg.ActiveScheme();
            if (scheme is null) return;
            var ac = Powercfg.GetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxFrequency);
            var dc = Powercfg.GetDc(scheme, Powercfg.SubProcessor, Powercfg.MaxFrequency);
            if (ac is null or 0 && dc is null or 0) return;
            WriteFrequencyCap(scheme, 0);
            Journal.Write($"Önceden kalmış işlemci frekans sınırı kaldırıldı (prizde {ac}, pilde {dc} MHz).");
        }
        catch (Exception ex) { Journal.Write("Frekans sınırı temizlenemedi: " + ex.Message); }
    }

    /// <summary>Frekans sınırını (MHz, 0 = yok) prizde ve pilde yazar, planı yeniden etkinleştirir.</summary>
    private static void WriteFrequencyCap(string scheme, int mhz)
    {
        Powercfg.SetFrequencyCap(scheme, mhz);
        Powercfg.SetActive(scheme);
    }

    private void ApplyGpu(int level, double? temp)
    {
        var mode = CurrentMode();
        if (mode is null) return;
        var top = mode.GpuCapMhz ?? 2100;
        if (level == 0)
        {
            var r0 = mode.GpuCapMhz is { } c ? GpuClocks.Cap(c) : GpuClocks.Release();
            Journal.Write($"Sıcaklık sınırı: ekran kartı sınırı geri verildi ({r0.Message}).");
            return;
        }
        var cap = Math.Max(900, top - level * GpuStepMhz);
        var r = GpuClocks.Cap(cap);
        Journal.Write($"Sıcaklık sınırı: ekran kartı {temp:0}°C → saat sınırı {cap} MHz ({r.Message}).");
        Notice?.Invoke($"Sıcaklık sınırı: ekran kartı {temp:0}°C, saat sınırı {cap} MHz.");
    }

    /// <summary>Kapatılınca (ya da mod değişince) sınırları modun kendi değerlerine döndürür.</summary>
    private void ResetAndRestore()
    {
        _cpu.Reset();
        _gpu.Reset();
        _ = Task.Run(() =>
        {
            var mode = CurrentMode();
            if (mode is null) return;
            try
            {
                var scheme = Powercfg.ActiveScheme();
                if (scheme is not null) WriteFrequencyCap(scheme, AppServices.Modes.ActiveDefinition?.CpuMaxMhz ?? 0);
                if (mode.GpuCapMhz is { } c) GpuClocks.Cap(c); else GpuClocks.Release();
            }
            catch (Exception ex) { Journal.Write("Isı hedefi geri verme hatası: " + ex.Message); }
        });
    }

    public void Dispose() => Configure(null);
}
