using System.Diagnostics.Eventing.Reader;
using Pulse.Core.Localization;

namespace Pulse.Core.Apps;

/// <summary>Açılışı yavaşlatan bir program/servis (Windows'un kendi ölçümü): adı ve açılışa kattığı gecikme (sn).</summary>
public sealed record BootSlowItem(string Name, double DelaySeconds);

/// <summary>Son Windows açılışı: toplam süre, ana yol (masaüstü gelene kadar), açılış sonrası yük ve en çok geciktirenler.</summary>
public sealed record BootInfo(DateTime Time, double TotalSeconds, double MainPathSeconds, double PostBootSeconds, IReadOnlyList<BootSlowItem> Slow);

/// <summary>
/// "Bilgisayar neden geç açılıyor?": Windows'un Diagnostics-Performance günlüğündeki açılış ölçümlerini (olay 100) ve açılışı yavaşlatan
/// öğeleri (olay 101) okur. Salt okunur. Günlük yönetici ister; okunamazsa nedeni söylenir. Hiçbir şey değiştirmez.
/// </summary>
public static class BootAnalyzer
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>Ham olay: kimlik, zaman ve alan adı → değer.</summary>
    public sealed record RawEvent(int Id, DateTime Time, IReadOnlyDictionary<string, string> Data);

    /// <summary>Saf birleştirme: en son açılış olayı (100) ve ondan sonraki 10 dakika içindeki yavaşlatan öğeler (101).</summary>
    public static BootInfo? Build(IEnumerable<RawEvent> events, int top = 5)
    {
        var list = events.ToList();
        var boot = list.Where(e => e.Id == 100).OrderByDescending(e => e.Time).FirstOrDefault();
        if (boot is null) return null;
        double Sec(RawEvent e, string key) => e.Data.TryGetValue(key, out var v) && double.TryParse(v, out var ms) ? ms / 1000.0 : 0;
        var slow = list.Where(e => e.Id == 101 && e.Time >= boot.Time && e.Time <= boot.Time.AddMinutes(10))
            .Select(e => new BootSlowItem(Friendly(e), Sec(e, "DegradationTime")))
            .Where(x => x.DelaySeconds > 0 && x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.DelaySeconds).First())
            .OrderByDescending(x => x.DelaySeconds).Take(top).ToList();
        return new BootInfo(boot.Time, Sec(boot, "BootTime"), Sec(boot, "MainPathBootTime"), Sec(boot, "BootPostBootTime"), slow);
    }

    private static string Friendly(RawEvent e)
    {
        var name = e.Data.TryGetValue("FriendlyName", out var f) && !string.IsNullOrWhiteSpace(f) ? f : e.Data.TryGetValue("Name", out var n) ? n : "";
        return name.Trim();
    }

    /// <summary>Kullanıcıya gösterilecek metin: ölçüm yoksa nedeni, varsa süre ve en çok geciktirenler.</summary>
    public static string Describe(BootInfo? info, string? error, int slowWarnSeconds = 60)
    {
        if (info is null) return error ?? Loc.T("Açılış süresi için Windows'un ölçümü bulunamadı.");
        var line = Loc.F("Son açılış ({0:d MMM HH:mm}): {1:0} sn toplam, masaüstü {2:0} sn'de geldi.", info.Time, info.TotalSeconds, info.MainPathSeconds);
        if (info.TotalSeconds >= slowWarnSeconds) line += " " + Loc.T("Bu yavaş sayılır.");
        if (info.Slow.Count > 0) line += " " + Loc.T("En çok geciktirenler: ") + string.Join(", ", info.Slow.Select(s => $"{s.Name} (+{s.DelaySeconds:0} sn)")) + ".";
        else line += " " + Loc.T("Windows açılışı belirgin geciktiren bir program bildirmemiş.");
        return line;
    }

    /// <summary>Gerçek günlük: erişilemezse (yönetici değil / günlük kapalı) <paramref name="error"/> nedenle dolar.</summary>
    public static BootInfo? Read(out string? error, int maxEvents = 200)
    {
        error = null;
        try
        {
            var q = new EventLogQuery(LogName, PathType.LogName, "*[System[(EventID=100 or EventID=101)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            var raw = new List<RawEvent>();
            for (var i = 0; i < maxEvents; i++)
            {
                using var ev = reader.ReadEvent();
                if (ev is null) break;
                var data = new Dictionary<string, string>();
                try
                {
                    var xml = System.Xml.Linq.XDocument.Parse(ev.ToXml());
                    foreach (var d in xml.Descendants().Where(x => x.Name.LocalName == "Data"))
                        if ((string?)d.Attribute("Name") is { } k) data[k] = d.Value;
                }
                catch { }
                raw.Add(new RawEvent(ev.Id, ev.TimeCreated ?? DateTime.MinValue, data));
            }
            var info = Build(raw);
            if (info is null) error = Loc.T("Windows bu bilgisayarda açılış süresini kaydetmemiş (günlük kapalı ya da henüz ölçüm yok).");
            return info;
        }
        catch (UnauthorizedAccessException) { error = Loc.T("Açılış süresi için KLYC-Pulse'ın yönetici olarak çalışması gerekir."); return null; }
        catch (EventLogNotFoundException) { error = Loc.T("Windows açılış günlüğü bu bilgisayarda yok."); return null; }
        catch (Exception ex) { error = Loc.F("Açılış süresi okunamadı: {0}", ex.Message); return null; }
    }
}
