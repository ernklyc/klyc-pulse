using System.Runtime.InteropServices;

namespace Pulse.Core.Monitoring;

public sealed record GpuReading(
    int? TempC,
    int? UtilPercent,
    int? MemUtilPercent,
    int? CoreMhz,
    int? MemMhz,
    double? PowerW,
    long? VramUsedBytes,
    long? VramTotalBytes,
    ulong ThrottleReasons)
{
    /// <summary>GPU'nun neden hızını kıstığını sade dille söyler. Kısılma yoksa null.</summary>
    public string? ThrottleText
    {
        get
        {
            var r = ThrottleReasons;
            if ((r & 0x40) != 0) return "Aşırı ısı (donanım)";
            if ((r & 0x20) != 0) return "Isı sınırı";
            if ((r & 0x80) != 0) return "Güç freni (donanım)";
            if ((r & 0x8) != 0) return "Donanım yavaşlatma";
            if ((r & 0x4) != 0) return "Güç sınırı";
            return null;
        }
    }

    public bool IsIdleClocks => (ThrottleReasons & 0x1) != 0;
}

/// <summary>
/// NVIDIA sürücüsünün resmi NVML arayüzü (nvml.dll, sürücüyle gelir). Yalnızca okur; hiçbir ayarı değiştirmez.
/// Süreç başlatmaz, bu yüzden her saniye okumak ucuzdur.
/// </summary>
public sealed class Nvml
{
    private readonly IntPtr _device;

    private Nvml(IntPtr device) => _device = device;

    public static bool IsAvailable => TryOpen() is not null;

    public static Nvml? TryOpen()
    {
        try
        {
            if (nvmlInit_v2() != 0) return null;
            return nvmlDeviceGetHandleByIndex_v2(0, out var h) == 0 ? new Nvml(h) : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public GpuReading Read()
    {
        int? temp = null, util = null, memUtil = null, core = null, mem = null;
        double? power = null;
        long? used = null, total = null;
        ulong reasons = 0;

        if (nvmlDeviceGetTemperature(_device, 0, out var t) == 0) temp = (int)t;
        if (nvmlDeviceGetUtilizationRates(_device, out var u) == 0) { util = (int)u.Gpu; memUtil = (int)u.Memory; }
        if (nvmlDeviceGetClockInfo(_device, 0, out var c) == 0) core = (int)c;
        if (nvmlDeviceGetClockInfo(_device, 2, out var m) == 0) mem = (int)m;
        if (nvmlDeviceGetPowerUsage(_device, out var p) == 0) power = p / 1000.0;
        if (nvmlDeviceGetMemoryInfo(_device, out var mi) == 0) { used = (long)mi.Used; total = (long)mi.Total; }
        nvmlDeviceGetCurrentClocksThrottleReasons(_device, out reasons);

        return new GpuReading(temp, util, memUtil, core, mem, power, used, total, reasons);
    }

    /// <summary>Sürücünün desteklediği çekirdek saatleri (MHz), yüksekten düşüğe. Okunamazsa boş.</summary>
    public IReadOnlyList<int> SupportedCoreClocks()
    {
        if (nvmlDeviceGetMaxClockInfo(_device, 2, out var mem) != 0) return [];
        uint count = 256;
        var buf = new uint[256];
        return nvmlDeviceGetSupportedGraphicsClocks(_device, mem, ref count, buf) == 0 ? buf.Take((int)count).Select(x => (int)x).ToList() : [];
    }

    /// <summary>Çekirdek saatini [min, max] aralığına kilitler (sürücü sınırı). Yönetici gerekir. NVML dönüş kodu 0 ise kabul edilmiştir.</summary>
    public bool LockCoreClocks(int minMhz, int maxMhz) => nvmlDeviceSetGpuLockedClocks(_device, (uint)minMhz, (uint)maxMhz) == 0;

    /// <summary>Saat kilidini kaldırır (sürücü varsayılanı).</summary>
    public bool ResetCoreClocks() => nvmlDeviceResetGpuLockedClocks(_device) == 0;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetSupportedGraphicsClocks(IntPtr device, uint memMhz, ref uint count, uint[] clocks);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization u);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo info);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
