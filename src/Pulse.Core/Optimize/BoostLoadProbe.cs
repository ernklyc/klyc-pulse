using Pulse.Core.Localization;

namespace Pulse.Core.Optimize;

/// <summary>Arka plan işlemci yükü: iki ölçüm arasındaki fark (saf mantık, testlenebilir). Hiçbir şeyi kapatmaz; yalnızca "kim yük bindiriyor?" der.</summary>
public static class BoostLoadProbe
{
    public sealed record Entry(string Name, double CpuPercent);

    /// <summary>
    /// <paramref name="first"/>/<paramref name="second"/>: süreç kimliği → (ad, toplam işlemci süresi sn). Aynı ada sahip süreçler toplanır.
    /// Yüzde, tek çekirdeğe göre değil tüm işlemciye göre (çekirdek sayısına bölünür) hesaplanır.
    /// </summary>
    public static List<Entry> Top(IReadOnlyDictionary<int, (string Name, double CpuSeconds)> first, IReadOnlyDictionary<int, (string Name, double CpuSeconds)> second,
        double elapsedSeconds, int cores, ISet<string> exclude, int take = 3, double minPercent = 1.0)
    {
        if (elapsedSeconds <= 0 || cores <= 0) return new();
        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, b) in second)
        {
            if (!first.TryGetValue(pid, out var a) || a.Name != b.Name) continue;   // ikinci ölçümde yeni başlayan ya da kimliği yeniden kullanılan süreç: atla
            if (exclude.Contains(b.Name)) continue;
            var d = b.CpuSeconds - a.CpuSeconds;
            if (d <= 0) continue;
            byName[b.Name] = byName.GetValueOrDefault(b.Name) + d;
        }
        return byName.Select(kv => new Entry(kv.Key, kv.Value / elapsedSeconds / cores * 100.0))
            .Where(e => e.CpuPercent >= minPercent).OrderByDescending(e => e.CpuPercent).Take(take).ToList();
    }

    public static string Describe(IReadOnlyList<Entry> top) => top.Count == 0
        ? Loc.T("Arka planda dikkat çekecek işlemci yükü yok.")
        : Loc.F("Arka planda en çok işlemci kullananlar: {0}. Gerekmiyorsa Süreçler sayfasından kapatabilirsin (Hızlandır bunları kapatmaz).", string.Join(", ", top.Select(e => $"{e.Name} %{e.CpuPercent:0.#}")));
}
