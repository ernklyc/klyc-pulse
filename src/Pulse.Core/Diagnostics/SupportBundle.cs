using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Pulse.Core.Health;
using Pulse.Core.Platform;

namespace Pulse.Core.Diagnostics;

/// <summary>
/// "Sorun bildir": hata ayıklamaya yetecek bilgiyi KİŞİSEL VERİ OLMADAN tek zipte toplar (masaüstüne): sürüm, Windows, donanım MODELİ, cihaz türü, ayar bayrakları,
/// son günlükler (kullanıcı adı, bilgisayar adı ve profil yolları temizlenmiş) ve son oyun raporu. Hiçbir şey kendiliğinden gönderilmez.
/// </summary>
public static class SupportBundle
{
    public static string Sanitize(string text, string userName, string machineName, string userProfile)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        if (!string.IsNullOrWhiteSpace(userProfile)) s = Regex.Replace(s, Regex.Escape(userProfile), @"C:\Users\<kullanici>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?i)([A-Z]:\\Users\\)([^\\/:*?""<>|\r\n]+)", "$1<kullanici>");
        // JSON içinde yollar \\ ile yazılır
        s = Regex.Replace(s, @"(?i)([A-Z]:\\\\Users\\\\)([^\\/:*?""<>|\r\n]+)", "$1<kullanici>");
        if (userName.Length >= 3) s = Regex.Replace(s, @"(?<![\p{L}\p{N}])" + Regex.Escape(userName) + @"(?![\p{L}\p{N}])", "<kullanici>", RegexOptions.IgnoreCase);
        if (machineName.Length >= 3) s = Regex.Replace(s, @"(?<![\p{L}\p{N}])" + Regex.Escape(machineName) + @"(?![\p{L}\p{N}])", "<bilgisayar>", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>info.txt (saf: dışarıdan gelen değerlerle).</summary>
    public static string BuildInfo(string version, bool admin, string os, string device, string cpu, string gpus, double ramGb, string culture, string konsol, string flags, DateTime now) =>
        string.Join(Environment.NewLine, new[]
        {
            "KLYC-Pulse sorun bildirimi", $"Tarih: {now:yyyy-MM-dd HH:mm}", "",
            $"Sürüm: {version}", $"Yönetici olarak çalışıyor: {(admin ? "evet" : "hayır")}", $"Windows: {os}", $"Cihaz: {device}",
            $"İşlemci: {cpu}", $"Ekran kartı: {gpus}", $"Bellek: {ramGb:0.#} GB", $"Dil/bölge: {culture}", $"KLYC Konsol: {konsol}", "",
            "[Ayar bayrakları]", flags, "",
            "Not: günlüklerde kullanıcı adı, bilgisayar adı ve profil yolları temizlenmiştir; oyun/uygulama adları geçebilir.",
        });

    /// <summary>Yalnızca bool/sayı ayarlar (yol, oyun listesi, kısayol atamaları yok).</summary>
    public static string DescribeFlags(Settings.AppSettings s) => string.Join(Environment.NewLine, new[]
    {
        $"AutoGameMode={s.AutoGameMode}", $"AutoTuneGames={s.AutoTuneGames}", $"CoolingFirst={s.CoolingFirst}", $"AutoQuietOnBattery={s.AutoQuietOnBattery}",
        $"CloseConflictingApps={s.CloseConflictingApps}", $"AutoClean={s.AutoClean}", $"ThermalGuard={s.ThermalGuard}", $"KeepMode={s.KeepMode}",
        $"Hotkeys={s.Hotkeys}", $"LinkWithKonsol={s.LinkWithKonsol}", $"HeatTarget={s.HeatTarget?.ToString() ?? "kapalı"}", $"Language={s.Language}",
        $"CheckUpdates={s.CheckUpdates}", $"GameProfiles={s.GameProfiles.Count}",
    });

    public static (string Cpu, string Gpus, double RamGb) ReadHardware()
    {
        string cpu = "?", gpus = "?"; double ram = 0;
        try { cpu = WmiReader.Query(@"root\cimv2", "SELECT Name FROM Win32_Processor").Select(r => (r.GetValueOrDefault("Name") as string)?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "?"; } catch { }
        try { gpus = string.Join(", ", WmiReader.Query(@"root\cimv2", "SELECT Name FROM Win32_VideoController").Select(r => r.GetValueOrDefault("Name") as string).Where(n => !string.IsNullOrWhiteSpace(n))); } catch { }
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0; } catch { }
        return (cpu, string.IsNullOrEmpty(gpus) ? "?" : gpus, ram);
    }

    /// <summary>Zipi yazar (masaüstü, yoksa geçici klasör) ve yolunu döner. <paramref name="logDir"/> / <paramref name="reportFile"/> testlerde yönlendirilir.</summary>
    public static string Create(string info, string? outDir = null, string? logDir = null, string? reportFile = null, int logDays = 3)
    {
        var user = Environment.UserName; var machine = Environment.MachineName;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        outDir ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir)) outDir = Path.GetTempPath();
        logDir ??= Journal.Directory;
        var path = Path.Combine(outDir, $"KLYC-Pulse-sorun-bildir-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(Sanitize(content, user, machine, profile));
        }
        Add("info.txt", info);
        try
        {
            if (Directory.Exists(logDir))
                foreach (var f in new DirectoryInfo(logDir).GetFiles("*.log").OrderByDescending(x => x.Name).Take(logDays))
                    try { Add("logs/" + f.Name, Tail(f.FullName, 400 * 1024)); } catch { }
        }
        catch { }
        reportFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "last_game_report.json");
        try { if (File.Exists(reportFile)) Add("last_game_report.json", Tail(reportFile, 200 * 1024)); } catch { }
        var konsolStatus = Companion.KonsolLink.StatusPath;
        try { if (File.Exists(konsolStatus)) Add("konsol-status.json", Tail(konsolStatus, 20 * 1024)); } catch { }
        return path;
    }

    private static string Tail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
