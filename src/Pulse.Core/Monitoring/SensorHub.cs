using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Core.Hardware;
using Pulse.Core.Health;
using Pulse.Core.Optimize;

namespace Pulse.Core.Monitoring;

public sealed record SensorSnapshot(
    DateTime Time,
    double? CpuPercent,
    double? CpuMhz,
    double? CpuTempC,
    GpuReading? Gpu,
    long RamUsedBytes,
    long RamTotalBytes,
    int? CpuFanRpm,
    int? GpuFanRpm)
{
    public double RamPercent => RamTotalBytes > 0 ? 100.0 * RamUsedBytes / RamTotalBytes : 0;

    /// <summary>İşlemci kısıtlanıyor olabilir mi? Yük yüksekken saat, taban hızın belirgin altına düşmüşse.</summary>
    public string? CpuThrottleHint(double baseMhz)
    {
        if (CpuMhz is not { } f || CpuPercent is not { } load) return null;
        if (load > 70 && f < baseMhz * 0.9) return CpuTempC is > 85 ? "Isı yüzünden yavaşlıyor olabilir" : "Güç veya mod sınırı yüzünden yavaşlıyor olabilir";
        return null;
    }
}

/// <summary>
/// Tüm canlı sensörleri tek yerden, süreç başlatmadan okur: işlemci (performans sayaçları), ekran kartı (NVML),
/// bellek, fan (ASUS). ThrottleStop/Afterburner'ın izleme kısmının karşılığıdır. Hiçbir ayarı değiştirmez.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly Nvml? _nvml = Nvml.TryOpen();
    private readonly AsusAcpi? _acpi = AsusAcpi.TryOpen();
    private readonly PerformanceCounter? _cpuUtility;
    private readonly PerformanceCounter? _cpuPerf;
    private readonly PerformanceCounter? _cpuFreq;

    public SensorHub()
    {
        try
        {
            _cpuUtility = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            _cpuFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
            _cpuUtility.NextValue(); _cpuPerf.NextValue(); _cpuFreq.NextValue();
        }
        catch { _cpuUtility = _cpuPerf = _cpuFreq = null; }
        BaseMhz = ReadBaseMhz();
    }

    public bool HasGpu => _nvml is not null;
    public double BaseMhz { get; }

    /// <summary>cpuTemp false ise (WMI sorgusu pahalıdır) işlemci sıcaklığı atlanır.</summary>
    public SensorSnapshot Read(bool cpuTemp = true)
    {
        double? util = null, mhz = null;
        try { util = _cpuUtility is null ? null : Math.Min(100, _cpuUtility.NextValue()); } catch { }
        try
        {
            if (_cpuFreq is not null && _cpuPerf is not null)
                mhz = _cpuFreq.NextValue() * _cpuPerf.NextValue() / 100.0;
        }
        catch { }

        var total = Optimizer.TotalRamBytes();
        return new SensorSnapshot(
            DateTime.Now,
            util is null ? null : Math.Round(util.Value, 0),
            mhz is null ? null : Math.Round(mhz.Value, 0),
            cpuTemp ? ReadCpuTemp() : null,
            _nvml?.Read(),
            total - Optimizer.AvailableRamBytes(), total,
            _acpi?.GetCpuFanRpm(), _acpi?.GetGpuFanRpm());
    }

    private static double? ReadCpuTemp()
    {
        var rows = WmiReader.Query(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        var temps = rows.Select(r => WmiReader.Get<double>(r, "CurrentTemperature")).Where(t => t is > 0).Select(t => t!.Value / 10.0 - 273.15).Where(c => c is > 5 and < 120).ToList();
        return temps.Count > 0 ? Math.Round(temps.Max(), 1) : null;
    }

    private static double ReadBaseMhz()
    {
        try { return Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("~MHz") is int v ? v : 2500; }
        catch { return 2500; }
    }

    public void Dispose()
    {
        _acpi?.Dispose();
        _cpuUtility?.Dispose();
        _cpuPerf?.Dispose();
        _cpuFreq?.Dispose();
    }
}
