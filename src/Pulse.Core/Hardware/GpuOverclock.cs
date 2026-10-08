using Pulse.Core.Localization;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Hardware;

public sealed record GpuOcState(bool Editable, int CoreMhz, int MemMhz, int CoreMin, int CoreMax, int MemMin, int MemMax);
public sealed record GpuOcResult(bool Ok, bool Verified, string Message, GpuOcState? State = null);

/// <summary>
/// Ekran kartı hız aşırtma (çekirdek ve bellek saat ofseti). NVIDIA'nın performans durumu arayüzünü (NvAPI, MSI Afterburner ve
/// NVIDIA Inspector'ın kullandığı yol) kullanır. Ofset yazıldıktan sonra sürücüden geri okunur; sürücünün izin verdiği aralığın
/// ve Pulse'ın kendi güvenli tavanının dışına çıkılmaz. Ofsetler kalıcı değildir: yeniden başlatınca ya da Reset ile sıfırlanır.
/// </summary>
public static class GpuOverclock
{
    /// <summary>Pulse'ın hiçbir koşulda aşmadığı tavanlar (MHz). Sürücünün izin verdiği aralık bundan geniş olabilir.</summary>
    public const int SafeCoreCeiling = 150;
    public const int SafeMemCeiling = 700;

    // Bellek ofseti NvAPI'de "etkin" hızın yarısı biriminde kHz olarak değil, doğrudan kHz'dir; birimi gerçek donanımda doğrulanır.
    private static PhysicalGPU? _gpu;
    private static readonly object Gate = new();

    /// <summary>NvAPI bir kez başlatılır ve ekran kartı tutamacı saklanır (tekrar tekrar başlatmak sürücüde hataya yol açabiliyor).</summary>
    private static PhysicalGPU? Gpu()
    {
        lock (Gate)
        {
            if (_gpu is not null) return _gpu;
            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                return _gpu = PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
            }
            catch (Exception ex) { Journal.Write("NvAPI başlatılamadı: " + ex.Message); return null; }
        }
    }

    /// <summary>
    /// Ekran kartı boştayken uykuya dalar (Optimus); uykudaki karta NvAPI "güç yok" (NVAPI_GPU_NOT_POWERED) der.
    /// Böyle olunca kartı NVML ile uyandırıp birkaç kez yeniden dener.
    /// </summary>
    public static GpuOcState? Read()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = ReadOnce(out var asleep);
            if (state is not null || !asleep) return state;
            if (attempt == 0) { try { Monitoring.Nvml.TryOpen()?.Read(); } catch { } }   // önce hafif yol
            else WakeWithNvidiaSmi();                                                       // olmazsa NVIDIA'nın kendi aracı kartı kesin uyandırır
            Thread.Sleep(700);
        }
        return null;
    }

    private static void WakeWithNvidiaSmi()
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) return;
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--query-gpu=name --format=csv,noheader") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p?.WaitForExit(6000);
        }
        catch { }
    }

    private static GpuOcState? ReadOnce(out bool asleep)
    {
        asleep = false;
        var gpu = Gpu();
        if (gpu is null) return null;
        try
        {
            var info = GPUApi.GetPerformanceStates20(gpu.Handle);
            var p0 = info.Clocks[PerformanceStateId.P0_3DPerformance];
            var core = p0.First(c => c.DomainId == PublicClockDomain.Graphics);
            var mem = p0.First(c => c.DomainId == PublicClockDomain.Memory);
            return new GpuOcState(
                core.IsEditable && mem.IsEditable,
                core.FrequencyDeltaInkHz.DeltaValue / 1000, mem.FrequencyDeltaInkHz.DeltaValue / 1000,
                core.FrequencyDeltaInkHz.DeltaRange.Minimum / 1000, core.FrequencyDeltaInkHz.DeltaRange.Maximum / 1000,
                mem.FrequencyDeltaInkHz.DeltaRange.Minimum / 1000, mem.FrequencyDeltaInkHz.DeltaRange.Maximum / 1000);
        }
        catch (Exception ex)
        {
            asleep = ex.Message.Contains("NOT_POWERED", StringComparison.OrdinalIgnoreCase);
            if (!asleep) Journal.Write("GPU ofset okunamadı: " + ex.Message);
            return null;
        }
    }

    /// <summary>Çekirdek ve bellek ofsetini (MHz) uygular, geri okuyup doğrular. 0/0 = fabrika hızı.</summary>
    public static GpuOcResult Apply(int coreMhz, int memMhz)
    {
        var gpu = Gpu();
        var before = Read();
        if (gpu is null || before is null) return new(false, false, Loc.T("NVIDIA ekran kartı bulunamadı."));
        if (!before.Editable) return new(false, false, Loc.T("Bu ekran kartında hız ayarı sürücü tarafından kapalı."), before);

        coreMhz = Math.Clamp(coreMhz, Math.Max(before.CoreMin, -SafeCoreCeiling), Math.Min(before.CoreMax, SafeCoreCeiling));
        memMhz = Math.Clamp(memMhz, Math.Max(before.MemMin, -SafeMemCeiling), Math.Min(before.MemMax, SafeMemCeiling));

        try
        {
            var clocks = new[]
            {
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(coreMhz * 1000)),
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memMhz * 1000)),
            };
            var state = new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, []);
            GPUApi.SetPerformanceStates20(gpu.Handle, new PerformanceStates20InfoV1([state], 2, 0));
        }
        catch (Exception ex)
        {
            Journal.Write("GPU ofset yazılamadı: " + ex.Message);
            return new(false, false, Loc.T("Sürücü ofseti kabul etmedi: ") + ex.Message, before);
        }

        Thread.Sleep(300);
        var after = Read();
        var verified = after is not null && after.CoreMhz == coreMhz && after.MemMhz == memMhz;
        Journal.Write($"GPU ofset: çekirdek {coreMhz:+0;-0;0}, bellek {memMhz:+0;-0;0} MHz; okunan {after?.CoreMhz}/{after?.MemMhz}.");
        return verified
            ? new(true, true, coreMhz == 0 && memMhz == 0 ? Loc.T("Fabrika hızına dönüldü (doğrulandı).") : Loc.F("Çekirdek {0:+0;-0;0} MHz, bellek {1:+0;-0;0} MHzuygulandı ve kontrol edildi.", coreMhz, memMhz), after)
            : new(true, false, Loc.F("Yazıldı ama okunan değerler farklı (çekirdek {0}, bellek {1}).", after?.CoreMhz, after?.MemMhz), after);
    }

    public static GpuOcResult Reset() => Apply(0, 0);
}
