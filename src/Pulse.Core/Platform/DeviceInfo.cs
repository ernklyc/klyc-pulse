using Pulse.Core.Health;

namespace Pulse.Core.Platform;

public enum DeviceKind { Unknown, Laptop, Tablet, Desktop, AllInOne, MiniPc }

/// <summary>Bilgisayarın türü ve pil durumu: arayüz ve bulgular buna göre dizüstüne ya da masaüstüne uyar.</summary>
public sealed record DeviceProfile(DeviceKind Kind, bool HasBattery, string? Manufacturer = null, string? Model = null)
{
    /// <summary>Taşınabilir (dizüstü, tablet): pil, kapak, fiş çekince mod gibi şeyler anlamlıdır.</summary>
    public bool IsPortable => Kind is DeviceKind.Laptop or DeviceKind.Tablet;

    /// <summary>Pil sağlığı, pil şarj sınırı ve "fişi çekince" gibi özellikler yalnızca pilli taşınabilir bilgisayarda gösterilir
    /// (masaüstüne bağlı bir UPS pil gibi görünebilir; o pil sayılmaz).</summary>
    public bool ShowBatteryFeatures => IsPortable && HasBattery;

    public string Label => Kind switch
    {
        DeviceKind.Laptop => "Dizüstü",
        DeviceKind.Tablet => "Tablet",
        DeviceKind.AllInOne => "Hepsi bir arada",
        DeviceKind.MiniPc => "Mini bilgisayar",
        DeviceKind.Desktop => "Masaüstü",
        _ => "Bilinmiyor",
    };
}

/// <summary>
/// Bilgisayar türünü Windows'un kasa türünden (SMBIOS "ChassisTypes") okur; kasa türü belirsizse (sanal makine, bazı kartlar)
/// Windows'un "PCSystemType" değerine ve pil varlığına bakar. Sonuç oturum boyunca saklanır.
/// Sınama için <c>KLYC_PULSE_DEVICE=desktop|laptop</c> ortam değişkeni türü zorlar.
/// </summary>
public static class DeviceInfo
{
    // SMBIOS kasa türleri (DMTF): https://www.dmtf.org/standards/smbios
    private static readonly HashSet<int> LaptopTypes = [8, 9, 10, 11, 14, 31];          // Portable, Laptop, Notebook, Handheld, Sub Notebook, Convertible
    private static readonly HashSet<int> TabletTypes = [30, 32];                          // Tablet, Detachable
    private static readonly HashSet<int> AllInOneTypes = [13];
    private static readonly HashSet<int> MiniPcTypes = [34, 35, 36];                      // Embedded PC, Mini PC, Stick PC
    private static readonly HashSet<int> DesktopTypes = [3, 4, 5, 6, 7, 15, 16, 17, 23, 24, 25, 28];

    private static readonly Lazy<DeviceProfile> Cache = new(Read);
    public static DeviceProfile Current => Cache.Value;

    /// <summary>Saf sınıflandırma (WMI'ye dokunmaz): kasa türleri, pil var mı, Windows'un PCSystemType değeri (2 = mobil).</summary>
    public static DeviceProfile Classify(IEnumerable<int> chassisTypes, bool hasBattery, int? pcSystemType, string? manufacturer = null, string? model = null)
    {
        var types = chassisTypes.ToHashSet();
        DeviceKind kind;
        if (types.Overlaps(LaptopTypes)) kind = DeviceKind.Laptop;
        else if (types.Overlaps(TabletTypes)) kind = DeviceKind.Tablet;
        else if (types.Overlaps(AllInOneTypes)) kind = DeviceKind.AllInOne;
        else if (types.Overlaps(MiniPcTypes)) kind = DeviceKind.MiniPc;
        else if (types.Overlaps(DesktopTypes)) kind = DeviceKind.Desktop;
        else if (pcSystemType == 2) kind = DeviceKind.Laptop;                             // Windows "mobil" diyorsa
        else if (pcSystemType is 1 or 3 or 4 or 5 or 7) kind = DeviceKind.Desktop;      // masaüstü, iş istasyonu, kurumsal sunucu...
        else kind = hasBattery ? DeviceKind.Laptop : DeviceKind.Desktop;                  // hiçbir ipucu yoksa pile bak
        return new DeviceProfile(kind, hasBattery, manufacturer, model);
    }

    private static DeviceProfile Read()
    {
        var forced = Environment.GetEnvironmentVariable("KLYC_PULSE_DEVICE")?.Trim().ToLowerInvariant();
        if (forced is "desktop") return new DeviceProfile(DeviceKind.Desktop, false);
        if (forced is "laptop") return new DeviceProfile(DeviceKind.Laptop, true);

        try
        {
            var types = new List<int>();
            foreach (var row in WmiReader.Query(@"root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                if (row.TryGetValue("ChassisTypes", out var v) && v is Array arr)
                    foreach (var o in arr) { if (o is not null && int.TryParse(o.ToString(), out var n)) types.Add(n); }
            }
            var hasBattery = WmiReader.Query(@"root\cimv2", "SELECT Name FROM Win32_Battery").Count > 0;
            int? pcType = null; string? maker = null, model = null;
            var cs = WmiReader.Query(@"root\cimv2", "SELECT PCSystemType, Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
            if (cs is not null)
            {
                pcType = WmiReader.Get<ushort>(cs, "PCSystemType");
                maker = cs.TryGetValue("Manufacturer", out var m) ? m?.ToString() : null;
                model = cs.TryGetValue("Model", out var md) ? md?.ToString() : null;
            }
            return Classify(types, hasBattery, pcType, maker, model);
        }
        catch { return new DeviceProfile(DeviceKind.Unknown, false); }
    }
}
