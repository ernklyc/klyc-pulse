using System.Text.Json;
using Pulse.Core.Monitoring;

namespace Pulse.Core.Diagnostics;

/// <summary>Bir oyun oturumunun özeti: ısı, işlemci hızı, ekran kartı, bellek ve FPS; sade dille bulgular.</summary>
public sealed class GameSessionReport
{
    public string Game { get; init; } = "";
    public DateTime Start { get; init; }
    public double Minutes { get; init; }
    public double? AvgFps { get; init; }
    public double? AvgLowFps { get; init; }
    public double? CpuTempAvg { get; init; }
    public double? CpuTempMax { get; init; }
    public int CpuAbove90Percent { get; init; }
    public int CpuAbove95Percent { get; init; }
    public double? CpuMhzLoadedAvg { get; init; }
    public double? CpuMhzPeak { get; init; }
    public double? GpuUtilAvg { get; init; }
    public int? GpuTempMax { get; init; }
    public double RamPeakPercent { get; init; }

    /// <summary>Oturum sırasında uygulanan işlemci hızı sınırı (MHz); null = sınırsızdı.</summary>
    public int? CpuCapMhz { get; init; }

    /// <summary>Raporun önerdiği işlemci hızı sınırı (MHz); öneri yoksa null.</summary>
    public int? SuggestedCpuCapMhz { get; init; }

    /// <summary>"gpu" | "cpu" | "other" | "unknown": oyunu en çok neyin sınırladığı.</summary>
    public string Bottleneck { get; init; } = "unknown";

    /// <summary>0 = sorun yok, 1 = dikkat, 2 = sorun var.</summary>
    public int Severity { get; init; }

    public List<string> Findings { get; init; } = new();

    public string Title => $"{Game} · {Minutes:0} dk · {Start:d MMMM HH:mm}";
}

/// <summary>Oyun süresince saniyede bir örnek toplar, bitince <see cref="GameSessionReport"/> üretir. Hiçbir ayarı değiştirmez.</summary>
public sealed class GameSessionRecorder
{
    private sealed record Sample(double? CpuPercent, double? CpuMhz, double? CpuTempC, int? GpuUtil, int? GpuTemp, string? GpuThrottle, double RamPercent, double? Fps, double? LowFps);

    private const int MaxSamples = 4 * 3600;     // en fazla 4 saatlik oturum
    private readonly List<Sample> _samples = new();
    private readonly string _game;
    private readonly DateTime _start;

    public GameSessionRecorder(string game, DateTime? start = null)
    {
        _game = game;
        _start = start ?? DateTime.Now;
    }

    public int Count => _samples.Count;
    public string Game => _game;

    public void Add(SensorSnapshot s, FpsReading? fps)
    {
        if (_samples.Count >= MaxSamples) return;
        _samples.Add(new Sample(s.CpuPercent, s.CpuMhz, s.CpuTempC, s.Gpu?.UtilPercent, s.Gpu?.TempC, s.Gpu?.ThrottleText, s.RamPercent, fps?.Fps, fps?.LowFps));
    }

    /// <summary>Yeterli veri yoksa (varsayılan 90 sn'den kısa) null döner.</summary>
    public GameSessionReport? Build(double baseMhz, int minSamples = 90, int? displayHz = null, int? cpuCapMhz = null, GameSessionReport? previous = null)
    {
        if (_samples.Count < minSamples) return null;
        var n = _samples.Count;

        var temps = _samples.Where(s => s.CpuTempC is > 0).Select(s => s.CpuTempC!.Value).ToList();
        int Pct(int count, int of) => of == 0 ? 0 : (int)Math.Round(100.0 * count / of);
        var above90 = Pct(temps.Count(t => t >= 90), temps.Count);
        var above95 = Pct(temps.Count(t => t >= 95), temps.Count);
        double? tempMax = temps.Count > 0 ? temps.Max() : null;
        double? tempAvg = temps.Count > 0 ? temps.Average() : null;

        var loadedMhz = _samples.Where(s => s.CpuPercent is >= 50 && s.CpuMhz is > 0).Select(s => s.CpuMhz!.Value).ToList();
        double? mhzAvg = loadedMhz.Count >= 10 ? loadedMhz.Average() : null;
        double? mhzPeak = loadedMhz.Count >= 10 ? Percentile(loadedMhz, 0.95) : null;

        var gpuUtils = _samples.Where(s => s.GpuUtil is not null).Select(s => (double)s.GpuUtil!.Value).ToList();
        double? gpuAvg = gpuUtils.Count > 0 ? gpuUtils.Average() : null;
        int? gpuTempMax = _samples.Where(s => s.GpuTemp is not null).Select(s => s.GpuTemp!.Value).DefaultIfEmpty().Max() is var gm && gm > 0 ? gm : null;
        var cpuAvg = _samples.Where(s => s.CpuPercent is not null).Select(s => s.CpuPercent!.Value).DefaultIfEmpty(0).Average();
        var ramPeak = _samples.Max(s => s.RamPercent);

        var fpsList = _samples.Where(s => s.Fps is > 0).Select(s => s.Fps!.Value).ToList();
        var lowList = _samples.Where(s => s.LowFps is > 0).Select(s => s.LowFps!.Value).ToList();
        double? fpsAvg = fpsList.Count >= 10 ? fpsList.Average() : null;
        double? lowAvg = lowList.Count >= 10 ? lowList.Average() : null;

        // Oyunu ne sınırlıyor? (Toplam işlemci yüzdesi oyunun ana iş parçacığını tam yansıtmaz; bu yüzden "büyük olasılıkla".)
        var bottleneck = "unknown";
        if (gpuAvg is { } g)
            bottleneck = g >= 90 ? "gpu" : cpuAvg >= 45 ? "cpu" : "other";

        var findings = new List<string>();
        var severity = 0;

        // 1) Isı
        if (tempMax is { } tmax)
        {
            if (above95 >= 10 || above90 >= 40)
            {
                severity = 2;
                findings.Add($"İşlemci sürenin %{above90}'ında 90 °C'nin üstündeydi (en yüksek {tmax:0} °C). Bu ısıda işlemci kendini yavaşlatır ve oyun takılabilir. İç temizlik, termal macun yenileme ya da soğutma altlığı en etkili çözüm.");
            }
            else if (above90 >= 10 || tmax >= 90)
            {
                severity = Math.Max(severity, 1);
                findings.Add($"İşlemci zaman zaman 90 °C'ye çıktı (en yüksek {tmax:0} °C). Şimdilik sorun değil ama izlemeye değer.");
            }
        }

        // 2) İşlemci hızı yük altında düştü mü?
        if (mhzAvg is { } avg && mhzPeak is { } peak && peak > 0 && avg < peak * 0.8)
        {
            var heatRelated = above90 >= 20;
            severity = Math.Max(severity, heatRelated ? 2 : 1);
            findings.Add($"Yük altında işlemci hızı ortalama {avg / 1000:0.0} GHz'e düştü (tepe {peak / 1000:0.0} GHz). " +
                         (heatRelated ? "Isı yüzünden yavaşlamış." : "Isı yüksek değildi; güç veya mod sınırı olabilir."));
        }

        // 3) Ekran kartı kısıldı mı?
        var throttled = _samples.Where(s => s.GpuThrottle is not null && s.GpuUtil is > 50).ToList();
        if (throttled.Count >= Math.Max(10, n / 20))
        {
            var reason = throttled.GroupBy(s => s.GpuThrottle!).OrderByDescending(x => x.Count()).First();
            severity = Math.Max(severity, 1);
            findings.Add($"Ekran kartı sürenin %{Pct(throttled.Count, n)}'inde kısıldı: {reason.Key}.");
        }

        // 4) Bellek
        if (ramPeak >= 92)
        {
            severity = Math.Max(severity, 2);
            findings.Add($"Bellek %{ramPeak:0}'ye kadar doldu. Dolunca Windows diske taşır ve bu takılma yapar. Arka plandaki uygulamaları (tarayıcı, launcher) kapatmak iyi olur.");
        }
        else if (ramPeak >= 85)
        {
            severity = Math.Max(severity, 1);
            findings.Add($"Bellek %{ramPeak:0}'e çıktı; sınıra yakın. Arka plandaki uygulamaları kapatmak iyi olur.");
        }

        // 5) FPS ve takılma
        if (fpsAvg is { } f)
        {
            var line = $"Ortalama {f:0} FPS" + (lowAvg is { } l ? $", en kötü %1'lik karelerde {l:0} FPS." : ".");
            if (lowAvg is { } low && low < f * 0.6)
            {
                severity = Math.Max(severity, 1);
                line += " Takılma var: en kötü karelerin hızı ortalamanın çok altında.";
            }
            findings.Add(line);
        }

        // 5b) Ekranın gösterebileceğinden fazla kare üretmek ısıtır
        var heated = above90 >= 10 || (tempMax ?? 0) >= 92;
        if (fpsAvg is { } fa && displayHz is { } hz && hz > 0 && fa >= hz * 1.15 && heated)
        {
            severity = Math.Max(severity, 1);
            findings.Add($"Oyun ortalama {fa:0} FPS çalıştı ama ekran {hz} Hz. Ekranın gösteremeyeceği kareleri üretmek boşuna ısıtır. Oyunda FPS sınırını {hz}'e koyarsan ısı düşer, görüntü aynı kalır.");
        }

        // 5c) Isı yüksekse işlemci hızı sınırı önerisi: yalnızca oyunu ekran kartı/kare sınırı belirliyorsa FPS'i az etkiler
        int? suggestedCap = null;
        var veryHot = above90 >= 40 || above95 >= 10;
        if (veryHot && cpuCapMhz is null)
        {
            if (bottleneck is "gpu" or "other")
            {
                suggestedCap = 3500;
                findings.Add("Öneri: bu oyun için işlemci hızını en çok 3,5 GHz'e sınırla (aşağıdaki düğme). Oyunu ekran kartı sınırladığı için FPS neredeyse aynı kalır, ısı belirgin düşer. Sonraki oyunda rapor önceki oturumla karşılaştırır.");
            }
            else if (bottleneck == "cpu")
                findings.Add("Oyunu işlemci sınırladığı için işlemci hızını kısmak FPS'i düşürür. Önce soğutmayı düzeltmek (temizlik, macun, altlık) daha doğru.");
        }

        // 5d) Aynı oyunun önceki oturumuyla karşılaştırma: bir ayarın işe yarayıp yaramadığı ölçülür
        if (previous is not null && string.Equals(previous.Game, _game, StringComparison.OrdinalIgnoreCase))
        {
            var cmp = Compare(previous, tempAvg, mhzAvg, fpsAvg, cpuCapMhz);
            if (cmp is not null) findings.Add(cmp);
        }

        // 6) Neyin sınırladığı (karar vermeye yarar)
        findings.Add(bottleneck switch
        {
            "gpu" => $"Bu oyunu ekran kartı sınırlıyor (ortalama %{gpuAvg:0} çalıştı). İşlemci hızını kısmak FPS'i az etkiler ama ısıyı belirgin düşürür.",
            "cpu" => $"Ekran kartı ortalama %{gpuAvg:0} çalıştı; oyunu büyük olasılıkla işlemci sınırlıyor. İşlemci hızını kısmak FPS'i düşürür.",
            "other" => "Ekran kartı ve işlemci tam yüklenmedi; kare sınırı (V-Sync / FPS sınırı) ya da oyunun kendisi sınırlıyor olabilir.",
            _ => "Ekran kartı verisi alınamadı.",
        });

        if (severity == 0) findings.Insert(0, "Bu oyun boyunca belirgin bir sorun görülmedi.");

        return new GameSessionReport
        {
            Game = _game,
            Start = _start,
            Minutes = Math.Round(n / 60.0, 0),
            AvgFps = fpsAvg is null ? null : Math.Round(fpsAvg.Value, 0),
            AvgLowFps = lowAvg is null ? null : Math.Round(lowAvg.Value, 0),
            CpuTempAvg = tempAvg is null ? null : Math.Round(tempAvg.Value, 0),
            CpuTempMax = tempMax is null ? null : Math.Round(tempMax.Value, 0),
            CpuAbove90Percent = above90,
            CpuAbove95Percent = above95,
            CpuMhzLoadedAvg = mhzAvg is null ? null : Math.Round(mhzAvg.Value, 0),
            CpuMhzPeak = mhzPeak is null ? null : Math.Round(mhzPeak.Value, 0),
            GpuUtilAvg = gpuAvg is null ? null : Math.Round(gpuAvg.Value, 0),
            GpuTempMax = gpuTempMax,
            RamPeakPercent = Math.Round(ramPeak, 0),
            CpuCapMhz = cpuCapMhz,
            SuggestedCpuCapMhz = suggestedCap,
            Bottleneck = bottleneck,
            Severity = severity,
            Findings = findings,
        };
    }

    /// <summary>Önceki oturumla karşılaştırma satırı. Karşılaştırılacak ortak değer yoksa null.</summary>
    public static string? Compare(GameSessionReport prev, double? tempAvg, double? mhzAvg, double? fpsAvg, int? cpuCapMhz)
    {
        var parts = new List<string>();
        if (prev.CpuTempAvg is { } pt && tempAvg is { } ct) parts.Add($"ortalama ısı {pt:0} → {ct:0} °C");
        if (prev.CpuMhzLoadedAvg is { } pm && mhzAvg is { } cm) parts.Add($"yük altı hız {pm / 1000:0.0} → {cm / 1000:0.0} GHz");
        if (prev.AvgFps is { } pf && fpsAvg is { } cf) parts.Add($"ortalama FPS {pf:0} → {cf:0}");
        if (parts.Count == 0) return null;
        var capNote = prev.CpuCapMhz != cpuCapMhz
            ? $" (işlemci sınırı: {(prev.CpuCapMhz is { } a ? a + " MHz" : "yok")} → {(cpuCapMhz is { } b ? b + " MHz" : "yok")})"
            : "";
        return $"Önceki oturuma göre ({prev.Start:d MMM}){capNote}: {string.Join(", ", parts)}.";
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
    }
}

/// <summary>Son oyun raporunu %LOCALAPPDATA%\Pulse\last_game_report.json içinde saklar.</summary>
public static class GameReportStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "last_game_report.json");

    public static void Save(GameSessionReport report, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, JsonSerializer.Serialize(report, Options));
            // Geçmiş: aynı oyunun önceki oturumlarıyla karşılaştırma için son 20 rapor
            var histPath = HistoryPath(path);
            var list = LoadHistory(path);
            list.Add(report);
            if (list.Count > 20) list.RemoveRange(0, list.Count - 20);
            Write(histPath, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception ex) { Journal.Write("Oyun raporu kaydedilemedi: " + ex.Message); }
    }

    private static void Write(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
    }

    private static string HistoryPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, "game_reports.json");

    public static List<GameSessionReport> LoadHistory(string? path = null)
    {
        try
        {
            var h = HistoryPath(path ?? DefaultPath);
            return File.Exists(h) ? JsonSerializer.Deserialize<List<GameSessionReport>>(File.ReadAllText(h)) ?? new() : new();
        }
        catch { return new(); }
    }

    /// <summary>Verilen oyunun en son kaydedilmiş oturumu (karşılaştırma için); yoksa null.</summary>
    public static GameSessionReport? LastFor(string game, string? path = null) =>
        LoadHistory(path).LastOrDefault(r => string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase));

    public static GameSessionReport? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<GameSessionReport>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
