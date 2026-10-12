using Pulse.Core.Localization;

namespace Pulse.Core.Diagnostics;

/// <summary>Son 7 günün özeti: oyun oturumları, en sıcak oyun, ortalama FPS, sorunlu oturumlar ve son otomatik temizlik.</summary>
public sealed record WeeklySummary(int Sessions, double Hours, double? HottestCpuC, string? HottestGame, double? AvgFps, int ProblemSessions, string? MostPlayed, DateTime? LastClean, long LastCleanBytes);

/// <summary>Haftalık bakım özeti. Saf hesap (kayıtlı raporlardan); hiçbir şey ölçmez/değiştirmez.</summary>
public static class WeeklyReport
{
    public static WeeklySummary Build(IEnumerable<GameSessionReport> history, DateTime now, DateTime? lastClean = null, long lastCleanBytes = 0, int days = 7)
    {
        var from = now.AddDays(-days);
        var week = history.Where(r => r.Start >= from && r.Start <= now.AddMinutes(5)).ToList();
        var hottest = week.Where(r => r.CpuTempMax is not null).OrderByDescending(r => r.CpuTempMax).FirstOrDefault();
        var fps = week.Where(r => r.AvgFps is > 0).Select(r => r.AvgFps!.Value).ToList();
        var most = week.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(r => r.Minutes)).FirstOrDefault()?.Key;
        var cleanInWeek = lastClean is { } c && c >= from;
        return new WeeklySummary(week.Count, Math.Round(week.Sum(r => r.Minutes) / 60.0, 1), hottest?.CpuTempMax, hottest?.Game,
            fps.Count == 0 ? null : Math.Round(fps.Average()), week.Count(r => r.Severity >= 1), most,
            cleanInWeek ? lastClean : null, cleanInWeek ? lastCleanBytes : 0);
    }

    public static string Describe(WeeklySummary w)
    {
        if (w.Sessions == 0 && w.LastClean is null) return Loc.T("Bu hafta kayıtlı oyun oturumu ya da otomatik temizlik yok.");
        var parts = new List<string>();
        if (w.Sessions > 0)
        {
            parts.Add(Loc.F("{0} oyun oturumu, toplam {1:0.#} saat", w.Sessions, w.Hours));
            if (w.MostPlayed is { } mp) parts.Add(Loc.F("en çok oynanan: {0}", mp));
            if (w.AvgFps is { } f) parts.Add(Loc.F("ortalama {0:0} FPS", f));
            if (w.HottestCpuC is { } t) parts.Add(Loc.F("en yüksek işlemci ısısı {0:0} °C ({1})", t, w.HottestGame ?? "?"));
            parts.Add(w.ProblemSessions == 0 ? Loc.T("hiçbir oturumda sorun görülmedi") : Loc.F("{0} oturumda dikkat edilecek şey vardı", w.ProblemSessions));
        }
        if (w.LastClean is { } c) parts.Add(Loc.F("son otomatik temizlik {0:d MMM}: {1:0} MB", c, w.LastCleanBytes / 1048576.0));
        return Loc.T("Bu hafta: ") + string.Join(" · ", parts) + ".";
    }
}
