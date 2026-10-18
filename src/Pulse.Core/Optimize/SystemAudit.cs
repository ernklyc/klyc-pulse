using Microsoft.Win32;
using Pulse.Core.Diagnostics;
using Pulse.Core.Health;
using Pulse.Core.Localization;

namespace Pulse.Core.Optimize;

/// <summary>Sistem denetiminin tek bulgusu. Severity: 0 bilgi, 1 dikkat.</summary>
public sealed record AuditFinding(string Id, int Severity, string Title, string Detail);

/// <summary>Denetimin okuduğu sistem gerçekleri (testlerde sahte verilir). Hepsi salt okunurdur.</summary>
public sealed record AuditFacts(long RamBytes, bool? PagefileAuto, long? PagefileMaxMb, bool? VbsOn);

/// <summary>
/// Sistem denetimi: hiçbir ayarı DEĞİŞTİRMEZ; oyun performansını etkileyen ve yazılımla çözülemeyen/kullanıcıya ait durumları nedeniyle söyler
/// (sayfa dosyası, sanallaştırma tabanlı güvenlik, ekran kartı belleği taşması, ısı, arka plan yükü, yanlış Windows oyun ayarları, FPS sınırı önerisi).
/// Kural mantığı saftır; gerçek okuma <see cref="ReadFacts"/> içindedir. Gerekçeler: docs/OPTIMIZASYON-ARASTIRMA.md.
/// </summary>
public static class SystemAudit
{
    /// <summary>Raporlara bakılan son oturum sayısı.</summary>
    public const int RecentReports = 10;

    public static IReadOnlyList<AuditFinding> Evaluate(AuditFacts f, IReadOnlyList<GameSessionReport> history, IReadOnlyList<GameSetting>? settings = null)
    {
        var list = new List<AuditFinding>();
        var recent = history.TakeLast(RecentReports).ToList();
        var ramGb = f.RamBytes / 1073741824.0;

        // 1) Sayfa dosyası: elle sabitlenmiş ve RAM'e göre çok küçükse büyük oyunlar bellek hatası verebilir
        if (f.PagefileAuto == false && f.PagefileMaxMb is { } mx && mx < Math.Min(8192, ramGb * 1024 / 2))
            list.Add(new("pagefile", 1, Loc.T("Sayfa dosyası çok küçük"),
                Loc.F("Sayfa dosyası en çok {0:0.#} GB, bellek {1:0} GB. Büyük oyunlar (GTA V, RDR2…) bellek ayırırken hata verebilir. Gelişmiş sistem ayarları → Performans → Sanal bellek → \"Sayfa dosyası boyutunu otomatik yönet\" önerilir. (Pulse bu ayara dokunmaz.)", mx / 1024.0, ramGb)));

        // 2) Ekran kartı belleği taşması: dokular sistem belleğine taşar, kare süresi sıçrar
        var vram = recent.Where(r => r.VramPeakPercent >= 97).Select(r => r.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (vram.Count > 0)
            list.Add(new("vram", 1, Loc.T("Ekran kartı belleği doluyor"),
                Loc.F("{0}: ekran kartı belleği %97'nin üstüne çıktı. Dolunca dokular sistem belleğine taşar ve oyun takılır. Oyunda doku kalitesini bir kademe düşürmek en ucuz akıcılık kazancıdır.", string.Join(", ", vram))));

        // 3) Isı: işlemci oyunun anlamlı bir bölümünde 95 °C üstündeyse yazılım ancak hafifletir; asıl çözüm soğutma
        var hot = recent.Where(r => r.CpuAbove95Percent >= 10).ToList();
        if (hot.Count > 0)
        {
            var worst = hot.Max(r => r.CpuAbove95Percent);
            list.Add(new("heat", 1, Loc.T("İşlemci sık 95 °C'nin üstüne çıkıyor"),
                Loc.F("{0}: işlemci oyunun %{1}'ine kadar 95 °C üstünde kaldı. Fanlar zaten tam hızdaysa en büyük kazanç yazılımda değil: toz temizliği, termal macun yenileme, dizüstü altlığı. Ayrıca FPS'i ekran hızına sınırlamak ısıyı ve kısılmayı azaltır.", string.Join(", ", hot.Select(r => r.Game).Distinct(StringComparer.OrdinalIgnoreCase)), worst)));
        }

        // 4) Arka plan yükü
        // Oyunun kendi süreçleri (ör. Riot başlatıcısı raporu "oyun" sayılınca asıl oyun arka plan görünür) ve başlatıcılar arka plan yükü sayılmaz
        var gameNames = history.Select(r => Norm(r.Game)).ToHashSet();
        double BgLoad(GameSessionReport r) => r.Background.Where(b => b.Kind != "launcher" && !gameNames.Contains(Norm(b.Name)) && !gameNames.Contains(Norm(b.Display))).Sum(b => b.CpuAvgPercent);
        var bg = recent.Where(r => BgLoad(r) >= 5).ToList();
        if (bg.Count > 0)
        {
            var names = bg.SelectMany(r => r.Background).Where(b => b.Kind != "launcher" && !gameNames.Contains(Norm(b.Name)) && !gameNames.Contains(Norm(b.Display)))
                .GroupBy(b => b.Display, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(b => b.CpuAvgPercent)).Take(3).Select(g => g.Key).ToList();
            list.Add(new("background", 1, Loc.T("Oyun sırasında arka plan işlemciyi tutuyor"),
                Loc.F("Oyun boyunca arka planda ortalama %{0:0} işlemci kullanıldı{1}. 4 çekirdekli bir işlemcide bu fark eder; gerekmiyorsa oyun sırasında kapat.", bg.Max(BgLoad), names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")));
        }

        // 5) FPS sınırı önerisi
        var cap = recent.LastOrDefault(r => r.SuggestedFpsCap is not null);
        if (cap is not null)
            list.Add(new("fpscap", 0, Loc.T("FPS sınırı önerisi"), Loc.F("{0}: oyun ekranın gösterebileceğinden fazla kare üretiyor. Oyun içinde FPS'i {1}'e sınırlamak ısıyı ve gücü düşürür, akıcılığı korur.", cap.Game, cap.SuggestedFpsCap)));

        // 6) Yanlış Windows oyun ayarları (Hızlandır düzeltebilir)
        if (settings is not null)
        {
            var bad = settings.Where(s => !s.IsGood).Select(s => s.Name).ToList();
            if (bad.Count > 0)
                list.Add(new("winsettings", 1, Loc.T("Windows oyun ayarları düzeltilebilir"), Loc.F("{0}. Hızlandır bunları güvenli biçimde düzeltir (her değişiklik doğrulanır).", string.Join(", ", bad))));
        }

        // 7) VBS: yalnızca bilgi; güvenlik özelliği olduğu için Pulse kapatmaz
        if (f.VbsOn == true)
            list.Add(new("vbs", 0, Loc.T("Sanallaştırma tabanlı güvenlik (VBS) açık"),
                Loc.T("Oyunlarda %0–5 kadar maliyeti olabilir (kaynaklar tutarsız, bu bilgisayarda ölçülmedi). Bir güvenlik özelliğidir; kapatmayı önermiyoruz, karar senin.")));

        return list;
    }

    private static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Kısa özet: "3 bulgu: Sayfa dosyası çok küçük; …" ya da sorun yoksa olumlu cümle.</summary>
    public static string Describe(IReadOnlyList<AuditFinding> findings)
    {
        if (findings.Count == 0) return Loc.T("Sistem denetimi: dikkat edilecek bir şey bulunmadı.");
        var attention = findings.Count(f => f.Severity > 0);
        return Loc.F("Sistem denetimi: {0} bulgu ({1} dikkat). ", findings.Count, attention) + string.Join("; ", findings.OrderByDescending(f => f.Severity).Take(3).Select(f => f.Title)) + ".";
    }

    /// <summary>Gerçek sistemden okur (kayıt defteri + WMI). Okunamayan değer null kalır, denetim o kuralı atlar.</summary>
    public static AuditFacts ReadFacts()
    {
        long ram = Optimizer.TotalRamBytes();
        bool? auto = null; long? maxMb = null; bool? vbs = null;
        try
        {
            var cs = WmiReader.Query(@"root\cimv2", "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
            if (cs.Count > 0 && cs[0].TryGetValue("AutomaticManagedPagefile", out var a) && a is bool ab) auto = ab;
            var settings = WmiReader.Query(@"root\cimv2", "SELECT MaximumSize, InitialSize FROM Win32_PageFileSetting");
            var usage = WmiReader.Query(@"root\cimv2", "SELECT AllocatedBaseSize FROM Win32_PageFileUsage");
            long sum = 0; var any = false;
            foreach (var row in settings)
            {
                var m = WmiReader.Get<uint>(row, "MaximumSize"); var i = WmiReader.Get<uint>(row, "InitialSize");
                var v = m is > 0 ? m.Value : i ?? 0; if (v > 0) { sum += v; any = true; }
            }
            if (!any) foreach (var row in usage) { var u = WmiReader.Get<uint>(row, "AllocatedBaseSize"); if (u is > 0) { sum += u.Value; any = true; } }
            if (any) maxMb = sum;
        }
        catch (Exception ex) { Journal.Write("Sistem denetimi: sayfa dosyası okunamadı: " + ex.Message); }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard");
            vbs = k?.GetValue("EnableVirtualizationBasedSecurity") is int v ? v == 1 : null;
        }
        catch { }
        return new AuditFacts(ram, auto, maxMb, vbs);
    }

    public static IReadOnlyList<AuditFinding> Run() =>
        Evaluate(ReadFacts(), GameReportStore.LoadHistory(), WindowsGameSettings.Read());
}
