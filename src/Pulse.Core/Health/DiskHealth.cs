using Pulse.Core.Localization;
namespace Pulse.Core.Health;

/// <param name="Note">Sıcaklık/aşınma/saat okunamadıysa NEDENİ (boşsa hepsi okundu ya da sağlık durumu dışında okunacak bir şey beklenmiyordu).</param>
public sealed record DiskInfo(string Name, bool Healthy, string HealthText, int? WearPercent, int? TempC, long? PowerOnHours, long? Errors, string Note = "");

/// <summary>Disk sağlığı: Windows'un Depolama Yönetimi (WMI) verileri, salt okunur. Sıcaklık/aşınma/çalışma saati için yönetici izni gerekir.</summary>
public static class DiskHealth
{
    public static IReadOnlyList<DiskInfo> Read()
    {
        const string ns = @"root\microsoft\windows\storage";
        var disks = WmiReader.Query(ns, "SELECT FriendlyName, HealthStatus, DeviceId FROM MSFT_PhysicalDisk");
        var counters = WmiReader.Query(ns, "SELECT DeviceId, Wear, Temperature, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal FROM MSFT_StorageReliabilityCounter");
        var result = Build(disks, counters, Cleanup.CleanupEngine.IsAdmin);
        if (!_logged && result.Count > 0)
        {
            // Gerçek kullanımda (yönetici) hangi değerlerin okunduğunu sonradan görmek için bir kez günlüğe yazılır
            _logged = true;
            foreach (var d in result)
                Diagnostics.Journal.Write($"Disk sağlığı: {d.Name}: {d.HealthText}, aşınma {d.WearPercent?.ToString() ?? "yok"}, sıcaklık {d.TempC?.ToString() ?? "yok"}, saat {d.PowerOnHours?.ToString() ?? "yok"}, hata {d.Errors?.ToString() ?? "yok"}" + (d.Note.Length > 0 ? " | " + d.Note : ""));
        }
        return result;
    }

    private static bool _logged;

    /// <summary>Saf birleştirme (WMI satırlarından): okunamayan değerin nedeni <see cref="DiskInfo.Note"/> içinde söylenir.</summary>
    public static IReadOnlyList<DiskInfo> Build(IReadOnlyList<Dictionary<string, object?>> disks, IReadOnlyList<Dictionary<string, object?>> counters, bool isAdmin)
    {
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
            if (temp is not > 0) temp = null;

            var missing = new List<string>();
            if (wear is null) missing.Add(Loc.T("aşınma"));
            if (temp is null) missing.Add(Loc.T("sıcaklık"));
            if (hours is null) missing.Add(Loc.T("çalışma saati"));
            var note = "";
            if (missing.Count > 0)
                note = !isAdmin
                    ? Loc.F("{0} okunamadı: bu veriler için Pulse'ın yönetici olarak çalışması gerekir.", string.Join(", ", missing))
                    : Loc.F("{0} okunamadı: bu disk (ya da USB/sürücü katmanı) bu veriyi Windows'a bildirmiyor; disk üreticisinin aracı gösterebilir.", string.Join(", ", missing));
            list.Add(new DiskInfo(d.TryGetValue("FriendlyName", out var n) ? n?.ToString() ?? "Disk" : "Disk", healthy, text, wear, temp, hours, errors, note));
        }
        return list;
    }
}
