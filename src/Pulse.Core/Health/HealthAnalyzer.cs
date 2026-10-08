using Microsoft.Win32;
using Pulse.Core.Modes;
using Pulse.Core.Platform;

namespace Pulse.Core.Health;

public enum FindingLevel { Good, Info, Warning, Bad }

public sealed record HealthFinding(FindingLevel Level, string Title, string Detail);

/// <summary>Ölçümlerden, kullanıcıya sade dille söylenecek bulgular çıkarır.</summary>
public static class HealthAnalyzer
{
    public static IReadOnlyList<HealthFinding> Analyze(HealthSample s)
    {
        var f = new List<HealthFinding>();

        // ---- Pil ----
        var b = s.Battery;
        if (!b.Present)
            f.Add(new(FindingLevel.Warning, "Pil algılanmıyor", "Windows pili görmüyor. Pilsiz çalışmak ASUS'ta performans kısıtlamasına yol açabilir."));
        else if (b.OnAc == true && b.RemainingMwh == 0 && b.ChargeRateMw == 0)
            f.Add(new(FindingLevel.Bad, "Pil şarj almıyor",
                $"Bilgisayar prizde ama pile akım gitmiyor (şarj akımı 0 mW, voltaj {b.Volts:N2} V, kalan 0 mWh). Pil koruma devresi şarjı kilitlemiş ya da pil bozulmuş olabilir. " +
                "Şişme belirtisi (alt kapakta kabarma, tuş takımı ya da dokunmatik yüzeyin yükselmesi) görürsen kullanmayı bırak ve servise götür. Uyurken ya da evde yokken prizde bırakma."));
        else if (b.Volts is < 6.0 || (b.RemainingMwh == 0 && b.FullChargeMwh is null or 0))
            f.Add(new(FindingLevel.Bad, "Pil sorunlu görünüyor",
                $"Voltaj {b.Volts:N2} V ve kalan kapasite {b.RemainingMwh ?? 0} mWh. Pil derin boşalmış ya da bozulmuş olabilir. " +
                "Alt kapağın kabarması, tuş takımı veya dokunmatik yüzeyin yükselmesi gibi şişme belirtisi görürsen kullanmayı bırak ve servise götür. Bu bir güvenlik konusudur."));
        else if (b.WearPercent is > 40)
            f.Add(new(FindingLevel.Warning, "Pil aşınmış", $"Kapasite tasarımın %{100 - b.WearPercent:N0}'ine düşmüş. Pil değişimi düşünülebilir."));
        else if (b.WearPercent is { } w)
            f.Add(new(FindingLevel.Good, "Pil sağlıklı", $"Aşınma %{w:N0}" + (b.Cycles is { } c ? $", {c} şarj döngüsü." : ".")));

        // ---- Isı ----
        if (s.CpuTempC is > 92)
            f.Add(new(FindingLevel.Bad, "İşlemci çok sıcak", $"{s.CpuTempC:N0} °C. Bu sıcaklıkta işlemci kendini kısar. Fan ızgarasını ve termal macunu kontrol et, bilgisayarı sert zeminde kullan."));
        else if (s.CpuTempC is > 85)
            f.Add(new(FindingLevel.Warning, "İşlemci sıcak", $"{s.CpuTempC:N0} °C. Uzun süre bu sıcaklıkta çalışmak ömrü kısaltır. Sessiz modu ya da soğutma altlığı yardımcı olur."));
        else if (s.CpuTempC is { } ct)
            f.Add(new(FindingLevel.Good, "İşlemci sıcaklığı normal", $"{ct:N0} °C (yaklaşık değer)."));

        if (s.GpuTempC is > 85)
            f.Add(new(FindingLevel.Warning, "Ekran kartı sıcak", $"{s.GpuTempC:N0} °C."));

        // ---- Disk ----
        try
        {
            var c = new DriveInfo("C");
            var freePct = 100.0 * c.AvailableFreeSpace / c.TotalSize;
            if (freePct < 10) f.Add(new(FindingLevel.Bad, "Disk neredeyse dolu", $"C: sürücüsünde %{freePct:N0} boş alan kaldı. Temizlik sayfasından yer aç."));
            else if (freePct < 15) f.Add(new(FindingLevel.Warning, "Disk dolmaya yaklaşıyor", $"C: sürücüsünde %{freePct:N0} boş alan var. %15'in altı SSD'yi yavaşlatabilir."));
            else f.Add(new(FindingLevel.Good, "Disk alanı yeterli", $"{c.AvailableFreeSpace / 1073741824.0:N0} GB boş (%{freePct:N0})."));
        }
        catch { }

        // ---- Disk sağlığı (SMART benzeri) ----
        try
        {
            foreach (var d in DiskHealth.Read())
            {
                var extra = string.Join(", ", new[]
                {
                    d.WearPercent is { } w ? $"aşınma %{w}" : null,
                    d.TempC is { } t ? $"{t} °C" : null,
                    d.PowerOnHours is { } h ? $"{h:N0} saat çalıştı" : null,
                }.Where(x => x is not null));
                if (!d.Healthy) f.Add(new(FindingLevel.Bad, $"Disk sağlığı: {d.HealthText}", $"{d.Name}. Önemli dosyalarını yedekle. {extra}"));
                else if (d.WearPercent is > 80) f.Add(new(FindingLevel.Warning, "SSD aşınmış", $"{d.Name}: %{d.WearPercent} aşınma. Yedeğini güncel tut, yenisini planla."));
                else if (d.Errors is > 0) f.Add(new(FindingLevel.Warning, "Diskte okuma/yazma hatası var", $"{d.Name}: {d.Errors} hata kaydı. Yedek al, durumu izle."));
                else f.Add(new(FindingLevel.Good, "Disk sağlıklı", $"{d.Name}" + (extra.Length > 0 ? $": {extra}." : ".")));
            }
        }
        catch { }

        // ---- Pil geçmişi ----
        try
        {
            var trend = BatteryHistory.Describe(s.Battery);
            if (trend is not null) f.Add(trend);
        }
        catch { }

        // ---- Sistem ----
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (up.TotalDays > 7) f.Add(new(FindingLevel.Warning, "Uzun süredir yeniden başlatılmadı", $"{up.TotalDays:N0} gündür açık. Yeniden başlatmak bellek sızıntılarını ve takılan süreçleri temizler."));
        if (PendingReboot()) f.Add(new(FindingLevel.Info, "Yeniden başlatma bekleniyor", "Bir güncelleme ya da kurulum yeniden başlatma istiyor."));

        var conflicts = ConflictService.Running();
        if (conflicts.Count > 0)
            f.Add(new(FindingLevel.Info, "Çakışabilecek uygulamalar çalışıyor", $"{string.Join(", ", conflicts)}. Mod uygularken bunları kapatmayı Ayarlar'dan açabilirsin."));

        var startup = StartupApprovedEnabledCount();
        if (startup > 8) f.Add(new(FindingLevel.Warning, "Açılışta çok program başlıyor", $"{startup} program. Uygulamalar sayfasından gereksizleri kapatabilirsin."));

        return f.OrderByDescending(x => x.Level).ToList();
    }

    public static (FindingLevel Overall, string Text) Summarize(IReadOnlyList<HealthFinding> findings)
    {
        var bad = findings.Count(x => x.Level == FindingLevel.Bad);
        var warn = findings.Count(x => x.Level == FindingLevel.Warning);
        if (bad > 0) return (FindingLevel.Bad, bad == 1 ? "1 ciddi sorun var" : $"{bad} ciddi sorun var");
        if (warn > 0) return (FindingLevel.Warning, warn == 1 ? "1 şey dikkat istiyor" : $"{warn} şey dikkat istiyor");
        return (FindingLevel.Good, "Her şey yolunda");
    }

    private static bool PendingReboot()
    {
        try
        {
            using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return a is not null || b is not null;
        }
        catch { return false; }
    }

    private static int StartupApprovedEnabledCount() =>
        Apps.StartupManager.List().Count(i => i.Enabled);
}
