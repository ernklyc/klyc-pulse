using Pulse.Core.Diagnostics;
using Pulse.Core.Hardware;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;
using Pulse.Core.Platform;

namespace Pulse.App;

/// <summary>
/// Sıcaklık sınırı: fan eğrisi bu modelde donanım tarafından kilitli olduğu için aynı sonucu yazılımla sağlar.
/// İşlemcinin üst sınırını (%100 → %60, 5'er kademe) ve ekran kartının saat sınırını sıcaklığa göre kademeli ayarlar;
/// sıcaklık düşünce geri verir. Mod değişince sıfırlanır. Yönetici ve işlemci sıcaklığı sensörü gerekir.
/// </summary>
public sealed class HeatTargetService : IDisposable
{
    private static readonly int[] CpuSteps = [0, 5, 10, 15, 20, 25, 30, 35, 40];       // üst sınırdan düşülecek %
    private const int GpuStepMhz = 150;
    private const int GpuMaxLevel = 6;

    private readonly StepGovernor _cpu = new(CpuSteps.Length - 1);
    private readonly StepGovernor _gpu = new(GpuMaxLevel);
    private IDisposable? _subscription;
    private int _busy;

    public bool Enabled => _subscription is not null;
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

        _cpu.TargetC = t;
        _gpu.TargetC = Math.Max(65, t - 5);
        if (Enabled) return;
        _subscription = AppServices.Sensors.Subscribe(wantFps: false);
        AppServices.Sensors.Updated += OnSensors;
        AppServices.Modes.Applied += OnModeApplied;
    }

    private void OnModeApplied(ModeResult _) { _cpu.Reset(); _gpu.Reset(); }

    private void OnSensors(SensorSnapshot s)
    {
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
        var value = Math.Max(60, mode.MaxState - CpuSteps[Math.Min(level, CpuSteps.Length - 1)]);
        var scheme = Powercfg.ActiveScheme();
        if (scheme is null) return;
        Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, value);
        Powercfg.SetActive(scheme);
        var read = Powercfg.GetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState);
        Journal.Write($"Sıcaklık sınırı: işlemci {temp:0}°C → üst sınır %{value} (okunan %{read}).");
        Notice?.Invoke(read == value
            ? $"Sıcaklık sınırı: işlemci {temp:0}°C, üst sınır %{value}."
            : $"Sıcaklık sınırı: işlemci üst sınırı %{value} yazıldı ama %{read} okundu.");
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
                if (scheme is not null)
                {
                    Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, mode.MaxState);
                    Powercfg.SetActive(scheme);
                }
                if (mode.GpuCapMhz is { } c) GpuClocks.Cap(c); else GpuClocks.Release();
            }
            catch (Exception ex) { Journal.Write("Isı hedefi geri verme hatası: " + ex.Message); }
        });
    }

    public void Dispose() => Configure(null);
}
