using System.Text.Json;

namespace Pulse.Core.Health;

/// <summary>Pil kapasitesinin günlük kaydı; zamanla ne kadar aştığını gösterir (her gün en fazla bir kayıt).</summary>
public static class BatteryHistory
{
    private sealed record Entry(DateTime Date, int FullChargeMwh, int? DesignMwh, int? Cycles);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "battery_history.json");

    public static HealthFinding? Describe(BatteryInfo b, string? pathOverride = null, DateTime? now = null)
    {
        if (b.FullChargeMwh is not > 0) return null;
        var path = pathOverride ?? FilePath;
        var today = (now ?? DateTime.Now).Date;
        var list = Load(path);
        if (!list.Any(e => e.Date == today))
        {
            list.Add(new Entry(today, b.FullChargeMwh.Value, b.DesignMwh, b.Cycles));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(list.TakeLast(400).ToList()));
        }
        var old = list.Where(e => (today - e.Date).TotalDays >= 30).OrderByDescending(e => e.Date).FirstOrDefault();
        if (old is null) return null;
        var drop = 100.0 * (old.FullChargeMwh - b.FullChargeMwh.Value) / old.FullChargeMwh;
        var days = (int)(today - old.Date).TotalDays;
        return drop > 3
            ? new(FindingLevel.Warning, "Pil hızlı yaşlanıyor", $"Son {days} günde tam şarj kapasitesi %{drop:N0} düştü. Sürekli prizde ve %100'de bırakmak bunu hızlandırır; Dizüstü sayfasından şarj limitini %60-80'e ayarla.")
            : new(FindingLevel.Good, "Pil kapasitesi kararlı", $"Son {days} günde kapasite değişimi %{Math.Max(drop, 0):N1}.");
    }

    private static List<Entry> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }
}