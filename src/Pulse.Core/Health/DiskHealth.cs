using Pulse.Core.Localization;
namespace Pulse.Core.Health;

public sealed record DiskInfo(string Name, bool Healthy, string HealthText, int? WearPercent, int? TempC, long? PowerOnHours, long? Errors);

/// <summary>Disk sağlığı: Windows'un Depolama Yönetimi (WMI) verileri, salt okunur. Yönetici ister.</summary>
public static class DiskHealth
{
    public static IReadOnlyList<DiskInfo> Read()
    {
        const string ns = @"root\microsoft\windows\storage";
        var disks = WmiReader.Query(ns, "SELECT FriendlyName, HealthStatus, DeviceId FROM MSFT_PhysicalDisk");
        var counters = WmiReader.Query(ns, "SELECT DeviceId, Wear, Temperature, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal FROM MSFT_StorageReliabilityCounter");
        var list = new List<DiskInfo>();
        foreach (var d in disks)
        {
            var id = d.TryGetValue("DeviceId", out var di) ? di?.ToString() : null;
            var c = counters.FirstOrDefault(x => x.TryGetValue("DeviceId", out var ci) && ci?.ToString() == id);
            var status = WmiReader.Get<int>(d, "HealthStatus") ?? 5;
            var (healthy, text) = status switch { 0 => (true, Loc.T("Sağlıklı")), 1 => (false, Loc.T("Uyarı")), 2 => (false, Loc.T("Sağlıksız")), _ => (true, Loc.T("Bilinmiyor")) };
            int? wear = c is null ? null : WmiReader.Get<int>(c, "Wear");
            int? temp = c is null ? null : WmiReader.Get<int>(c, "Temperature");
            long? hours = c is null ? null : WmiReader.Get<long>(c, "PowerOnHours");
            long? errors = c is null ? null : (WmiReader.Get<long>(c, "ReadErrorsTotal") ?? 0) + (WmiReader.Get<long>(c, "WriteErrorsTotal") ?? 0);
            list.Add(new DiskInfo(d.TryGetValue("FriendlyName", out var n) ? n?.ToString() ?? "Disk" : "Disk", healthy, text, wear, temp is > 0 ? temp : null, hours, errors));
        }
        return list;
    }
}