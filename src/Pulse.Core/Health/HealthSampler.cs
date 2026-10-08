using System.Diagnostics;
using Pulse.Core.Hardware;

namespace Pulse.Core.Health;

public sealed record BatteryInfo(
    bool Present,
    double? Volts,
    int? RemainingMwh,
    int? DesignMwh,
    int? FullChargeMwh,
    int? Cycles,
    bool? OnAc,
    bool? Charging,
    int? ChargeRateMw = null)
{
    /// <summary>Aşınma yüzdesi (tasarım kapasitesine göre kayıp). Hesaplanamazsa null.</summary>
    public double? WearPercent => DesignMwh is > 0 && FullChargeMwh is > 0 ? 100.0 - 100.0 * FullChargeMwh.Value / DesignMwh.Value : null;
}

public sealed record HealthSample(
    DateTime Time,
    double? CpuTempC,
    double? GpuTempC,
    int? GpuUtilPercent,
    int? CpuFanRpm,
    int? GpuFanRpm,
    BatteryInfo Battery);

/// <summary>Anlık sağlık ölçümleri. ASUS sürücüsü, nvidia-smi ve salt okunur WMI kullanır.</summary>
public sealed class HealthSampler : IDisposable
{
    private readonly AsusAcpi? _acpi = AsusAcpi.TryOpen();
    private readonly string? _nvidiaSmi = FindNvidiaSmi();
    private readonly Monitoring.Nvml? _nvml = Monitoring.Nvml.TryOpen();
    // Pil bilgisi yavaş değişir ve 4 ayrı WMI sorgusu ister; 15 sn'de bir okunur.
    private BatteryInfo? _battery;
    private DateTime _batteryAt = DateTime.MinValue;

    public HealthSample Sample()
    {
        if (_battery is null || DateTime.Now - _batteryAt > TimeSpan.FromSeconds(15)) { _battery = ReadBattery(); _batteryAt = DateTime.Now; }
        var gpuTemp = ReadGpu(out var util);
        return new(DateTime.Now, ReadCpuTemp(), gpuTemp, util, _acpi?.GetCpuFanRpm(), _acpi?.GetGpuFanRpm(), _battery);
    }

    /// <summary>ACPI termal bölgesi. Paket sıcaklığının yaklaşığıdır, ThrottleStop kadar hassas değildir.</summary>
    private static double? ReadCpuTemp()
    {
        var rows = WmiReader.Query(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        var temps = rows.Select(r => WmiReader.Get<double>(r, "CurrentTemperature")).Where(t => t is > 0).Select(t => t!.Value / 10.0 - 273.15).Where(c => c is > 5 and < 120).ToList();
        return temps.Count > 0 ? Math.Round(temps.Max(), 1) : null;
    }

    private double? ReadGpu(out int? util)
    {
        util = null;
        if (_nvml is not null)
        {
            var r = _nvml.Read();   // süreç başlatmadan, doğrudan sürücüden
            util = r.UtilPercent;
            return r.TempC;
        }
        if (_nvidiaSmi is null) return null;
        try
        {
            var psi = new ProcessStartInfo(_nvidiaSmi, "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            var parts = line?.Split(',');
            if (parts is { Length: >= 2 })
            {
                util = int.TryParse(parts[1].Trim(), out var u) ? u : null;
                return double.TryParse(parts[0].Trim(), out var t) ? t : null;
            }
        }
        catch { }
        return null;
    }

    public static BatteryInfo ReadBattery()
    {
        var status = WmiReader.Query(@"root\wmi", "SELECT * FROM BatteryStatus").FirstOrDefault();
        var design = WmiReader.Query(@"root\wmi", "SELECT * FROM BatteryStaticData").FirstOrDefault();
        var full = WmiReader.Query(@"root\wmi", "SELECT * FROM BatteryFullChargedCapacity").FirstOrDefault();
        var cycles = WmiReader.Query(@"root\wmi", "SELECT * FROM BatteryCycleCount").FirstOrDefault();
        if (status is null && design is null) return new BatteryInfo(false, null, null, null, null, null, null, null);

        return new BatteryInfo(
            true,
            status is not null && WmiReader.Get<double>(status, "Voltage") is { } mv && mv > 0 ? mv / 1000.0 : null,
            status is null ? null : WmiReader.Get<int>(status, "RemainingCapacity"),
            design is null ? null : WmiReader.Get<int>(design, "DesignedCapacity"),
            full is null ? null : WmiReader.Get<int>(full, "FullChargedCapacity"),
            cycles is null ? null : WmiReader.Get<int>(cycles, "CycleCount"),
            status is null ? null : (bool?)(status.TryGetValue("PowerOnline", out var po) && po is bool b1 && b1),
            status is null ? null : (bool?)(status.TryGetValue("Charging", out var ch) && ch is bool b2 && b2),
            status is null ? null : WmiReader.Get<int>(status, "ChargeRate"));
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => _acpi?.Dispose();
}
