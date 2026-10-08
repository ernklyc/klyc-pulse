using Pulse.Core.Localization;
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
            f.Add(new(FindingLevel.Warning, Loc.T("Pil algılanmıyor"), Loc.T("Windows pili görmüyor. Pilsiz çalışmak ASUS'ta performans kısıtlamasına yol açabilir.")));
        else if (b.OnAc == true && b.RemainingMwh == 0 && b.ChargeRateMw == 0)
            f.Add(new(FindingLevel.Bad, Loc.T("Pil şarj almıyor"),
                Loc.F("Bilgisayar prizde ama pile akım gitmiyor (şarj akımı 0 mW, voltaj {0:N2} V, kalan 0 mWh). Pil koruma devresi şarjı kilitlemiş ya da pil bozulmuş olabilir. ", b.Volts) +
                Loc.T("Şişme belirtisi (alt kapakta kabarma, tuş takımı ya da dokunmatik yüzeyin yükselmesi) görürsen kullanmayı bırak ve servise götür. Uyurken ya da evde yokken prizde bırakma.")));
        else if (b.Volts is < 6.0 || (b.RemainingMwh == 0 && b.FullChargeMwh is null or 0))
            f.Add(new(FindingLevel.Bad, Loc.T("Pil sorunlu görünüyor"),
                $"Voltaj {b.Volts:N2} V ve kalan kapasite {b.RemainingMwh ?? 0} mWh. Pil derin boşalmış ya da bozulmuş olabilir. " +
                Loc.T("Alt kapağın kabarması, tuş takımı veya dokunmatik yüzeyin yükselmesi gibi şişme belirtisi görürsen kullanmayı bırak ve servise götür. Bu bir güvenlik konusudur.")));
        else if (b.WearPercent is > 40)
            f.Add(new(FindingLevel.Warning, Loc.T("Pil aşınmış"), Loc.F("Kapasite tasarımın %{0:N0}'ine düşmüş. Pil değişimi düşünülebilir.", 100 - b.WearPercent)));
        else if (b.WearPercent is { } w)
            f.Add(new(FindingLevel.Good, Loc.T("Pil sağlıklı"), Loc.F("Aşınma %{0:N0}", w) + (b.Cycles is { } c ? Loc.F(", {0} şarj döngüsü.", c) : ".")));

        // ---- Isı ----
        if (s.CpuTempC is > 92)
            f.Add(new(FindingLevel.Bad, Loc.T("İşlemci çok sıcak"), Loc.F("{0:N0} °C. Bu sıcaklıkta işlemci kendini kısar. Fan ızgarasını ve termal macunu kontrol et, bilgisayarı sert zeminde kullan.", s.CpuTempC)));
        else if (s.CpuTempC is > 85)
            f.Add(new(FindingLevel.Warning, Loc.T("İşlemci sıcak"), Loc.F("{0:N0} °C. Uzun süre bu sıcaklıkta çalışmak ömrü kısaltır. Sessiz modu ya da soğutma altlığı yardımcı olur.", s.CpuTempC)));
        else if (s.CpuTempC is { } ct)
            f.Add(new(FindingLevel.Good, Loc.T("İşlemci sıcaklığı normal"), Loc.F("{0:N0} °C (yaklaşık değer).", ct)));

        if (s.GpuTempC is > 85)
            f.Add(new(FindingLevel.Warning, Loc.T("Ekran kartı sıcak"), $"{s.GpuTempC:N0} °C."));

        // ---- Disk ----
        try
        {
            var c = new DriveInfo("C");
            var freePct = 100.0 * c.AvailableFreeSpace / c.TotalSize;
            if (freePct < 10) f.Add(new(FindingLevel.Bad, Loc.T("Disk neredeyse dolu"), Loc.F("C: sürücüsünde %{0:N0} boş alan kaldı. Temizlik sayfasından yer aç.", freePct)));
            else if (freePct < 15) f.Add(new(FindingLevel.Warning, Loc.T("Disk dolmaya yaklaşıyor"), Loc.F("C: sürücüsünde %{0:N0} boş alan var. %15'in altı SSD'yi yavaşlatabilir.", freePct)));
            else f.Add(new(FindingLevel.Good, Loc.T("Disk alanı yeterli"), Loc.F("{0:N0} GB boş (%{1:N0}).", c.AvailableFreeSpace / 1073741824.0, freePct)));
        }
        catch { }

        // ---- Disk sağlığı (SMART benzeri) ----
        try
        {
            foreach (var d in DiskHealth.Read())
            {
                var extra = string.Join(", ", new[]
                {
                    d.WearPercent is { } w ? Loc.F("aşınma %{0}", w) : null,
                    d.TempC is { } t ? $"{t} °C" : null,
                    d.PowerOnHours is { } h ? Loc.F("{0:N0} saat çalıştı", h) : null,
                }.Where(x => x is not null));
                if (!d.Healthy) f.Add(new(FindingLevel.Bad, Loc.F("Disk sağlığı: {0}", d.HealthText), Loc.F("{0}. Önemli dosyalarını yedekle. {1}", d.Name, extra)));
                else if (d.WearPercent is > 80) f.Add(new(FindingLevel.Warning, Loc.T("SSD aşınmış"), Loc.F("{0}: %{1} aşınma. Yedeğini güncel tut, yenisini planla.", d.Name, d.WearPercent)));
                else if (d.Errors is > 0) f.Add(new(FindingLevel.Warning, Loc.T("Diskte okuma/yazma hatası var"), Loc.F("{0}: {1} hata kaydı. Yedek al, durumu izle.", d.Name, d.Errors)));
                else f.Add(new(FindingLevel.Good, Loc.T("Disk sağlıklı"), $"{d.Name}" + (extra.Length > 0 ? $": {extra}." : ".")));
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
        if (up.TotalDays > 7) f.Add(new(FindingLevel.Warning, Loc.T("Uzun süredir yeniden başlatılmadı"), Loc.F("{0:N0} gündür açık. Yeniden başlatmak bellek sızıntılarını ve takılan süreçleri temizler.", up.TotalDays)));
        if (PendingReboot()) f.Add(new(FindingLevel.Info, Loc.T("Yeniden başlatma bekleniyor"), Loc.T("Bir güncelleme ya da kurulum yeniden başlatma istiyor.")));

        var conflicts = ConflictService.Running();
        if (conflicts.Count > 0)
            f.Add(new(FindingLevel.Info, Loc.T("Çakışabilecek uygulamalar çalışıyor"), $"{string.Join(", ", conflicts)}. Mod uygularken bunları kapatmayı Ayarlar'dan açabilirsin."));

        var startup = StartupApprovedEnabledCount();
        if (startup > 8) f.Add(new(FindingLevel.Warning, Loc.T("Açılışta çok program başlıyor"), Loc.F("{0} program. Uygulamalar sayfasından gereksizleri kapatabilirsin.", startup)));

        return f.OrderByDescending(x => x.Level).ToList();
    }

    public static (FindingLevel Overall, string Text) Summarize(IReadOnlyList<HealthFinding> findings)
    {
        var bad = findings.Count(x => x.Level == FindingLevel.Bad);
        var warn = findings.Count(x => x.Level == FindingLevel.Warning);
        if (bad > 0) return (FindingLevel.Bad, bad == 1 ? Loc.T("1 ciddi sorun var") : Loc.F("{0} ciddi sorun var", bad));
        if (warn > 0) return (FindingLevel.Warning, warn == 1 ? Loc.T("1 şey dikkat istiyor") : Loc.F("{0} şey dikkat istiyor", warn));
        return (FindingLevel.Good, Loc.T("Her şey yolunda"));
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
