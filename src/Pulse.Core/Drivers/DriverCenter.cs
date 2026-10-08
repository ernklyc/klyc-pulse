using System.Globalization;
using System.Runtime.InteropServices;
using Pulse.Core.Health;

namespace Pulse.Core.Drivers;

public sealed record DriverInfo(string Name, string Class, string Version, DateTime? Date, string Manufacturer)
{
    public double? AgeYears => Date is { } d ? (DateTime.Now - d).TotalDays / 365.25 : null;
}

public sealed record DriverUpdate(string Title, string Manufacturer, string Class, DateTime? Date);
public sealed record BiosInfo(string Model, string Version, DateTime? Date, string Manufacturer);

/// <summary>
/// Sürücü ve BIOS bilgisi (Intel Driver &amp; Support Assistant / MyASUS'un okuma kısmı). Yalnızca okur: kurulum yapmaz.
/// Bekleyen sürücü güncellemeleri Windows Update'in resmi arayüzünden sorgulanır; kurmak için Windows'un kendi
/// ekranı açılır (böylece imzalı, Microsoft'tan geçmiş paketler ve Windows'un geri alma desteği kullanılır).
/// </summary>
public static class DriverCenter
{
    private static readonly string[] Classes = ["DISPLAY", "NET", "BLUETOOTH", "MEDIA"];

    public static BiosInfo? ReadBios()
    {
        var bios = WmiReader.Query(@"root\cimv2", "SELECT SMBIOSBIOSVersion, ReleaseDate, Manufacturer FROM Win32_BIOS").FirstOrDefault();
        var cs = WmiReader.Query(@"root\cimv2", "SELECT Model FROM Win32_ComputerSystem").FirstOrDefault();
        if (bios is null) return null;
        return new BiosInfo(
            cs is not null && cs.TryGetValue("Model", out var m) ? m?.ToString() ?? "" : "",
            bios.TryGetValue("SMBIOSBIOSVersion", out var v) ? v?.ToString() ?? "" : "",
            bios.TryGetValue("ReleaseDate", out var d) ? ParseWmiDate(d?.ToString()) : null,
            bios.TryGetValue("Manufacturer", out var man) ? man?.ToString() ?? "" : "");
    }

    /// <summary>Ekran, ağ, Bluetooth ve ses sürücüleri. Aynı aygıt adı birden fazla geçerse en yenisi alınır.</summary>
    public static IReadOnlyList<DriverInfo> ReadInstalled()
    {
        var where = string.Join(" OR ", Classes.Select(c => $"DeviceClass='{c}'"));
        var rows = WmiReader.Query(@"root\cimv2", $"SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, Manufacturer FROM Win32_PnPSignedDriver WHERE {where}");
        var list = new List<DriverInfo>();
        foreach (var r in rows)
        {
            string S(string k) => r.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
            var name = S("DeviceName");
            if (name.Length == 0 || S("DriverVersion").Length == 0) continue;
            list.Add(new DriverInfo(name, S("DeviceClass"), S("DriverVersion"), ParseWmiDate(S("DriverDate")), S("Manufacturer")));
        }
        return list
            .GroupBy(d => d.Name)
            .Select(g => g.OrderByDescending(d => d.Date ?? DateTime.MinValue).First())
            .OrderBy(d => d.Class).ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>NVIDIA sürücü sürümü (Windows biçiminden marka biçimine: 32.0.16.1047 → 610.47). Bulunamazsa null.</summary>
    public static string? NvidiaVersion(IEnumerable<DriverInfo> drivers)
    {
        var gpu = drivers.FirstOrDefault(d => d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && d.Class == "DISPLAY");
        if (gpu is null) return null;
        var parts = gpu.Version.Split('.');
        if (parts.Length < 4) return gpu.Version;
        var tail = parts[2] + parts[3].PadLeft(4, '0');          // 16 + 1047 = 161047
        return tail.Length >= 5 ? $"{tail[^5..^2]}.{tail[^2..]}" : gpu.Version;
    }

    /// <summary>Windows Update'te bekleyen sürücü güncellemeleri (yalnızca arama, indirme/kurma yok). Dakikaya yakın sürebilir.</summary>
    public static (IReadOnlyList<DriverUpdate> Updates, string? Error) SearchWindowsUpdate()
    {
        object? session = null, searcher = null, result = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null) return ([], "Windows Update arayüzü bulunamadı.");
            session = Activator.CreateInstance(type);
            searcher = ((dynamic)session!).CreateUpdateSearcher();
            result = ((dynamic)searcher!).Search("IsInstalled=0 and Type='Driver'");
            var list = new List<DriverUpdate>();
            foreach (dynamic u in (System.Collections.IEnumerable)((dynamic)result!).Updates)
            {
                DateTime? date = null;
                try { date = (DateTime)u.DriverVerDate; } catch { }
                list.Add(new DriverUpdate((string)u.Title, SafeString(() => (string)u.DriverManufacturer), SafeString(() => (string)u.DriverClass), date));
            }
            return (list, null);
        }
        catch (Exception ex) { return ([], "Windows Update sorgulanamadı: " + ex.Message); }
        finally
        {
            foreach (var o in new[] { result, searcher, session })
                if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
    }

    /// <summary>Windows Update önerisi gerçekten yeni mi? Kurulu sürümden eski ya da aynı tarihliyse kurmak gerekmez.</summary>
    public static (bool IsNewer, string Note) Judge(DriverUpdate update, IReadOnlyList<DriverInfo> installed, BiosInfo? bios)
    {
        if (update.Date is not { } date) return (true, "Tarihi bilinmiyor.");
        DateTime? current = update.Class.Equals("Firmware", StringComparison.OrdinalIgnoreCase)
            ? bios?.Date
            : installed.Where(d => d.Class.Equals(WmiClass(update.Class), StringComparison.OrdinalIgnoreCase) && d.Name.Contains(FirstWord(update.Manufacturer), StringComparison.OrdinalIgnoreCase)).Select(d => d.Date).Where(d => d is not null).DefaultIfEmpty(null).Max();
        if (current is null) return (true, "Karşılaştırılacak kurulu sürüm bulunamadı.");
        return date > current
            ? (true, $"Kurulu sürümden yeni ({current:yyyy-MM-dd} yerine {date:yyyy-MM-dd}).")
            : (false, $"Kurulu sürüm daha yeni ya da aynı ({current:yyyy-MM-dd}). Kurmana gerek yok.");
    }

    /// <summary>Windows Update sınıf adını (Video, Net...) kurulu sürücü sınıfına (DISPLAY, NET...) çevirir.</summary>
    private static string WmiClass(string c) => c.ToLowerInvariant() switch { "video" or "display" => "DISPLAY", "net" or "network" => "NET", "media" or "audio" => "MEDIA", var x => x.ToUpperInvariant() };

    private static string FirstWord(string s) => s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    private static string SafeString(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    private static DateTime? ParseWmiDate(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 8) return null;
        return DateTime.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
