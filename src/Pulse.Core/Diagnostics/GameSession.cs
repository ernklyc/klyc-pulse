using Pulse.Core.Localization;
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
    public double? VramPeakPercent { get; init; }
    public int OnBatteryPercent { get; init; }

    /// <summary>Oturum sırasında uygulanan işlemci hızı sınırı (MHz); null = sınırsızdı.</summary>
    public int? CpuCapMhz { get; init; }

    /// <summary>Raporun önerdiği işlemci hızı sınırı (MHz); öneri yoksa null.</summary>
    public int? SuggestedCpuCapMhz { get; init; }

    /// <summary>Otomatik ayar bu oturumdan sonra işlemci sınırını değiştirdi mi?</summary>
    public bool AutoTuneChanged { get; set; }

    /// <summary>Oyun sırasında arka planda en çok yük bindiren programlar (oyun ve Pulse hariç); ölçülemediyse boş.</summary>
    public List<BackgroundItem> Background { get; init; } = new();

    /// <summary>Oyun dışındaki tüm programların toplam işlemci ortalaması (%); ölçülemediyse null.</summary>
    public double? BackgroundCpuAvgPercent { get; init; }

    /// <summary>"gpu" | "cpu" | "other" | "unknown": oyunu en çok neyin sınırladığı.</summary>
    public string Bottleneck { get; init; } = "unknown";

    /// <summary>0 = sorun yok, 1 = dikkat, 2 = sorun var.</summary>
    public int Severity { get; init; }

    public List<string> Findings { get; init; } = new();

    public string Title => Loc.F("{0} · {1:0} dk · {2:d MMMM HH:mm}", Game, Minutes, Start);
}

/// <summary>Oyun süresince saniyede bir örnek toplar, bitince <see cref="GameSessionReport"/> üretir. Hiçbir ayarı değiştirmez.</summary>
public sealed class GameSessionRecorder
{
    private sealed record Sample(double? CpuPercent, double? CpuMhz, double? CpuTempC, int? GpuUtil, int? GpuTemp, string? GpuThrottle, double RamPercent, double? Fps, double? LowFps, bool? OnAc, double? VramPercent);

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

    public void Add(SensorSnapshot s, FpsReading? fps, bool? onAc = null)
    {
        if (_samples.Count >= MaxSamples) return;
        // Ekran kartı kullanımı: NVML varsa o, yoksa (AMD/Intel) Windows'un GPU Engine sayaçları
        var gpuUtil = s.Gpu?.UtilPercent ?? (s.GpuEnginePercent is { } e ? (int?)Math.Round(e) : null);
        double? vram = s.Gpu is { VramTotalBytes: > 0, VramUsedBytes: { } used } g ? 100.0 * used / g.VramTotalBytes!.Value : null;
        _samples.Add(new Sample(s.CpuPercent, s.CpuMhz, s.CpuTempC, gpuUtil, s.Gpu?.TempC, s.Gpu?.ThrottleText, s.RamPercent, fps?.Fps, fps?.LowFps, onAc, vram));
    }

    /// <summary>Yeterli veri yoksa (varsayılan 90 sn'den kısa) null döner.</summary>
    public GameSessionReport? Build(double baseMhz, int minSamples = 90, int? displayHz = null, int? cpuCapMhz = null, GameSessionReport? previous = null, bool suggestCap = true,
        DriveKind storage = DriveKind.Unknown, int? suggestMhz = null, BackgroundSummary? background = null)
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

        // 0) Pilde oynandı mı? (en büyük tek FPS kaybı nedeni)
        var acKnown = _samples.Count(s => s.OnAc is not null);
        var onBatteryPct = acKnown > 0 ? Pct(_samples.Count(s => s.OnAc == false), acKnown) : 0;
        if (onBatteryPct >= 30)
        {
            severity = 2;
            findings.Add(Loc.F("Oyunun %{0}'i pilde oynandı. Pilde işlemci ve ekran kartı güç sınırına girer; FPS yarıya kadar düşebilir. Prize takıp oyna.", onBatteryPct));
        }

        // 1) Isı
        if (temps.Count == 0)
            findings.Add(Loc.T("İşlemci sıcaklığı okunamadı (Pulse yönetici olarak çalışmıyor ya da bu bilgisayar sıcaklığı sunmuyor); ısı değerlendirmesi ve otomatik ısı ayarı yapılamadı."));
        if (tempMax is { } tmax)
        {
            if (above95 >= 10 || above90 >= 40)
            {
                severity = 2;
                findings.Add(Loc.F("İşlemci sürenin %{0}'ında 90 °C'nin üstündeydi (en yüksek {1:0} °C). Bu ısıda işlemci kendini yavaşlatır ve oyun takılabilir. İç temizlik, termal macun yenileme ya da soğutma altlığı en etkili çözüm.", above90, tmax));
            }
            else if (above90 >= 10 || tmax >= 90)
            {
                severity = Math.Max(severity, 1);
                findings.Add(Loc.F("İşlemci zaman zaman 90 °C'ye çıktı (en yüksek {0:0} °C). Şimdilik sorun değil ama izlemeye değer.", tmax));
            }
        }

        // 2) İşlemci hızı yük altında düştü mü?
        if (mhzAvg is { } avg && mhzPeak is { } peak && peak > 0 && avg < peak * 0.8)
        {
            var heatRelated = above90 >= 20;
            severity = Math.Max(severity, heatRelated ? 2 : 1);
            findings.Add(Loc.F("Yük altında işlemci hızı ortalama {0:0.0} GHz'e düştü (tepe {1:0.0} GHz). ", avg / 1000, peak / 1000) +
                         (heatRelated ? Loc.T("Isı yüzünden yavaşlamış.") : Loc.T("Isı yüksek değildi; güç veya mod sınırı olabilir.")));
        }

        // 3) Ekran kartı kısıldı mı?
        var throttled = _samples.Where(s => s.GpuThrottle is not null && s.GpuUtil is > 50).ToList();
        if (throttled.Count >= Math.Max(10, n / 20))
        {
            var reason = throttled.GroupBy(s => s.GpuThrottle!).OrderByDescending(x => x.Count()).First();
            if (reason.Key == "Güç sınırı")
            {
                // Dizüstü ekran kartlarının sabit bir güç bütçesi vardır; dolunca sürücü hızı biraz kısar. Normaldir, sorun değildir.
                findings.Add(Loc.F("Ekran kartı sürenin %{0}'inde güç bütçesine ulaştı ('Güç sınırı'). Dizüstü ekran kartlarının sabit bir güç bütçesi vardır; dolunca hızı biraz kısılır. Bu normaldir, ısı sorunu değildir.", Pct(throttled.Count, n)));
            }
            else
            {
                severity = Math.Max(severity, 1);
                findings.Add(Loc.F("Ekran kartı sürenin %{0}'inde kısıldı: {1}.", Pct(throttled.Count, n), Loc.T(reason.Key)));
            }
        }

        // 4) Bellek
        if (ramPeak >= 92)
        {
            severity = Math.Max(severity, 2);
            findings.Add(Loc.F("Bellek %{0:0}'ye kadar doldu. Dolunca Windows diske taşır ve bu takılma yapar. Arka plandaki uygulamaları (tarayıcı, launcher) kapatmak iyi olur.", ramPeak));
        }
        else if (ramPeak >= 85)
        {
            severity = Math.Max(severity, 1);
            findings.Add(Loc.F("Bellek %{0:0}'e çıktı; sınıra yakın. Arka plandaki uygulamaları kapatmak iyi olur.", ramPeak));
        }

        // 4b) Ekran kartı belleği (VRAM): dolunca dokular sistem belleğine taşar ve takılma yapar (4 GB'lık kartlarda sık)
        var vramPeak = _samples.Where(s => s.VramPercent is not null).Select(s => s.VramPercent!.Value).DefaultIfEmpty(0).Max();
        if (vramPeak >= 95)
        {
            severity = Math.Max(severity, 2);
            findings.Add(Loc.F("Ekran kartı belleği %{0:0}'e kadar doldu. Dolunca dokular sistem belleğine taşar ve oyun takılır. Oyunda doku kalitesini bir kademe düşür.", vramPeak));
        }
        else if (vramPeak >= 90)
        {
            severity = Math.Max(severity, 1);
            findings.Add(Loc.F("Ekran kartı belleği %{0:0}'e çıktı; sınıra yakın. Takılma olursa doku kalitesini bir kademe düşür.", vramPeak));
        }

        // 4c) Oyun HDD'de mi? (yükleme ve açık dünya akışında takılma yapar; SSD'ye taşımak çözer, FPS'i artırmaz)
        if (storage == DriveKind.Hdd)
        {
            severity = Math.Max(severity, 1);
            findings.Add(Loc.T("Oyun yavaş bir HDD'de duruyor. Yüklemeleri ve açık dünyada akış takılmalarını yavaşlatır; oyunu SSD'ye taşımak çözer (FPS'i artırmaz)."));
        }

        // 4d) Arka plandaki programlar: oyunla işlemci, disk ve belleği paylaşırlar (takılmanın sık görülen sebebi)
        if (background is { Items.Count: > 0 })
        {
            foreach (var line in BackgroundFindings(background, bottleneck, ramPeak, ref severity)) findings.Add(line);
        }

        // 5) FPS ve takılma
        if (fpsAvg is { } f)
        {
            var line = $"Ortalama {f:0} FPS" + (lowAvg is { } l ? Loc.F(", en kötü %1'lik karelerde {0:0} FPS.", l) : ".");
            // 1% düşük 60 FPS'in üstündeyse oyun akıcıdır (ortalamadan uzak olsa bile); yalnızca gerçekten düşükse "takılma" denir.
            if (lowAvg is { } low && low < f * 0.6 && low < 60)
            {
                severity = Math.Max(severity, 1);
                line += Loc.T(" Takılma var: en kötü karelerin hızı ortalamanın çok altında.");
            }
            else if (lowAvg is { } ok && ok >= 60)
                line += Loc.T(" En kötü karelerde bile akıcı.");
            findings.Add(line);
        }

        // 5b) Ekranın gösterebileceğinden fazla kare üretmek ısıtır
        var heated = above90 >= 10 || (tempMax ?? 0) >= 92;
        if (fpsAvg is { } fa && displayHz is { } hz && hz > 0 && fa >= hz * 1.15 && heated)
        {
            severity = Math.Max(severity, 1);
            findings.Add(Loc.F("Oyun ortalama {0:0} FPS çalıştı ama ekran {1} Hz. Ekranın gösteremeyeceği kareleri üretmek boşuna ısıtır. Oyunda FPS sınırını {2}'e koyarsan ısı düşer, görüntü aynı kalır.", fa, hz, hz));
        }

        // 5c) Isı yüksekse işlemci hızı sınırı önerisi: yalnızca oyunu ekran kartı/kare sınırı belirliyorsa FPS'i az etkiler
        int? suggestedCap = null;
        var veryHot = above90 >= 40 || above95 >= 10;
        if (veryHot && cpuCapMhz is null && suggestCap)
        {
            if (bottleneck is "gpu" or "other" && suggestMhz is { } sm)
            {
                suggestedCap = sm;
                findings.Add(Loc.F("Öneri: bu oyun için işlemci hızını en çok {0:0.0} GHz'e sınırla (aşağıdaki düğme). Oyunu ekran kartı sınırladığı için FPS neredeyse aynı kalır, ısı belirgin düşer. Sonraki oyunda rapor önceki oturumla karşılaştırır.", sm / 1000.0));
            }
            else if (bottleneck == "cpu")
                findings.Add(Loc.T("Oyunu işlemci sınırladığı için işlemci hızını kısmak FPS'i düşürür. Önce soğutmayı düzeltmek (temizlik, macun, altlık) daha doğru."));
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
            "gpu" => Loc.F("Bu oyunu ekran kartı sınırlıyor (ortalama %{0:0} çalıştı). İşlemci hızını kısmak FPS'i az etkiler ama ısıyı belirgin düşürür.", gpuAvg),
            "cpu" => Loc.F("Ekran kartı ortalama %{0:0} çalıştı; oyunu büyük olasılıkla işlemci sınırlıyor. İşlemci hızını kısmak FPS'i düşürür.", gpuAvg),
            "other" => Loc.T("Ekran kartı ve işlemci tam yüklenmedi; kare sınırı (V-Sync / FPS sınırı) ya da oyunun kendisi sınırlıyor olabilir."),
            _ => Loc.T("Ekran kartı kullanımı okunamadı; oyunu neyin sınırladığı belirlenemedi."),
        });

        if (severity == 0) findings.Insert(0, Loc.T("Bu oyun boyunca belirgin bir sorun görülmedi."));

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
            VramPeakPercent = vramPeak > 0 ? Math.Round(vramPeak, 0) : null,
            OnBatteryPercent = onBatteryPct,
            Background = background?.Items ?? new(),
            BackgroundCpuAvgPercent = background is { Items.Count: > 0 } ? background.TotalCpuAvgPercent : null,
            CpuCapMhz = cpuCapMhz,
            SuggestedCpuCapMhz = suggestedCap,
            Bottleneck = bottleneck,
            Severity = severity,
            Findings = findings,
        };
    }

    /// <summary>
    /// Arka plan yükünden sade bulgular çıkarır. Yalnızca gerçekten anlamlı olanları söyler (en çok 3 satır); hiçbir şeyi kendisi kapatmaz.
    /// Windows'un kendi işleri (Defender, Update vb.) için kapat demez, ne olduğunu söyler.
    /// </summary>
    public static List<string> BackgroundFindings(BackgroundSummary bg, string bottleneck, double ramPeakPercent, ref int severity)
    {
        var lines = new List<string>();
        string Advice(BackgroundItem i) => i.Kind == "launcher"
            ? Loc.T(" Bu bir oyun başlatıcısı; oyun açıkken kapatma (oyun kapanabilir). İndirme ya da güncelleme varsa oyun sırasında duraklat.")
            : i.Kind == "app"
            ? bottleneck switch
            {
                "cpu" => Loc.T(" Oyunu işlemci sınırlıyor, bu yüzden FPS'i düşürmüş olabilir; oyundan önce kapat."),
                "gpu" => Loc.T(" Oyunu ekran kartı sınırladığı için FPS'e etkisi az; yine de ısıyı azaltmak için oyundan önce kapatabilirsin."),
                _ => Loc.T(" Oyundan önce kapatman iyi olur."),
            }
            : i.Name.ToLowerInvariant() switch
            {
                "msmpeng" or "mpdefendercoreservice" => Loc.T(" Windows Defender oyun sırasında tarama yapmış; tarama saatini oyun saatlerinden uzağa almak Windows Güvenliği'nden yapılır (Pulse güvenlik ayarlarına dokunmaz)."),
                "tiworker" or "trustedinstaller" or "wuauclt" or "musnotification" => Loc.T(" Windows Update arka planda çalışıyor; Windows'un 'etkin saatler' ayarına oyun saatini yazarsan oyun sırasında çalışmaz."),
                "searchindexer" or "searchhost" => Loc.T(" Windows arama dizinleyicisi çalışıyordu; kendiliğinden biter."),
                "compattelrunner" => Loc.T(" Windows'un veri toplama işi çalışıyordu; kendiliğinden biter."),
                _ => i.Kind == "system" ? Loc.T(" Windows/sürücü işi; genelde kendiliğinden biter.") : Loc.T(" Oyun sırasında arka planda çalışıyordu."),
            };

        var cpuItems = bg.Items
            .Where(i => i.CpuAvgPercent >= 3 || (i.CpuPeakPercent >= 20 && i.ActivePercent >= 15))
            .OrderByDescending(i => i.CpuAvgPercent).Take(3).ToList();
        foreach (var i in cpuItems)
        {
            lines.Add(Loc.F("Arka planda {0} oyun boyunca ortalama %{1:0.#} işlemci kullandı (en çok %{2:0}).{3}", i.Display, i.CpuAvgPercent, i.CpuPeakPercent, Advice(i)));
            if (i.Kind == "app" && i.CpuAvgPercent >= 8 && bottleneck == "cpu") severity = Math.Max(severity, 1);
        }

        if (lines.Count < 3)
        {
            foreach (var i in bg.Items.Where(i => i.DiskAvgMBps >= 10 && !cpuItems.Contains(i)).OrderByDescending(i => i.DiskAvgMBps).Take(3 - lines.Count))
                lines.Add(Loc.F("Arka planda {0} oyun boyunca ortalama {1:0} MB/sn disk okuma/yazma yaptı. Oyun diskten veri akıtıyorsa takılma yapabilir.{2}", i.Display, i.DiskAvgMBps, i.Kind == "app" ? Loc.T(" Oyundan önce kapatırsan disk oyuna kalır.") : Advice(i)));
        }

        // Bellek: yalnızca bellek gerçekten daralmışken, büyük kapatılabilir programlar
        if (ramPeakPercent >= 80 && lines.Count < 3)
        {
            var big = bg.Items.Where(i => i.Kind == "app" && i.RamPeakMB >= 1000 && !cpuItems.Contains(i)).OrderByDescending(i => i.RamPeakMB).FirstOrDefault();
            if (big is not null)
                lines.Add(Loc.F("Bellek %{0:0} dolmuşken {1} arka planda yaklaşık {2:0.0} GB bellek tutuyordu. Oyundan önce kapatırsan bellek boşalır.", ramPeakPercent, big.Display, big.RamPeakMB / 1024));
        }

        if (bg.TotalCpuAvgPercent >= 15 && lines.Count > 0)
            lines.Add(Loc.F("Arka plandaki programlar toplamda ortalama %{0:0} işlemci harcadı.", bg.TotalCpuAvgPercent));
        return lines;
    }

    /// <summary>Önceki oturumla karşılaştırma satırı. Karşılaştırılacak ortak değer yoksa null.</summary>
    public static string? Compare(GameSessionReport prev, double? tempAvg, double? mhzAvg, double? fpsAvg, int? cpuCapMhz)
    {
        var parts = new List<string>();
        if (prev.CpuTempAvg is { } pt && tempAvg is { } ct) parts.Add(Loc.F("ortalama ısı {0:0} → {1:0} °C", pt, ct));
        if (prev.CpuMhzLoadedAvg is { } pm && mhzAvg is { } cm) parts.Add(Loc.F("yük altı hız {0:0.0} → {1:0.0} GHz", pm / 1000, cm / 1000));
        if (prev.AvgFps is { } pf && fpsAvg is { } cf) parts.Add(Loc.F("ortalama FPS {0:0} → {1:0}", pf, cf));
        if (parts.Count == 0) return null;
        var capNote = prev.CpuCapMhz != cpuCapMhz
            ? Loc.F(" (işlemci sınırı: {0} → {1})", prev.CpuCapMhz is { } a ? a + " MHz" : Loc.T("yok"), cpuCapMhz is { } b ? b + " MHz" : Loc.T("yok"))
            : "";
        return Loc.F("Önceki oturuma göre ({0:d MMM}){1}: {2}.", prev.Start, capNote, string.Join(", ", parts));
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

    public static void Save(GameSessionReport report, string? path = null, bool replaceLast = false)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, JsonSerializer.Serialize(report, Options));
            // Geçmiş: aynı oyunun önceki oturumlarıyla karşılaştırma için son 20 rapor
            var histPath = HistoryPath(path);
            var list = LoadHistory(path);
            // Sonradan güncellenen rapor (ör. frekans denemesi bitince) geçmişte ikiye bölünmesin
            if (replaceLast && list.Count > 0 && list[^1].Start == report.Start && string.Equals(list[^1].Game, report.Game, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(list.Count - 1);
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
