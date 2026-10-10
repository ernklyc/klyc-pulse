using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core.Diagnostics;
using Pulse.Core.Localization;

namespace Pulse.Core.Processes;

/// <summary>Kapatma önerisi: Suggested = ön seçili (zararsız), Optional = istersen, Careful = açık iş/kayıt kaybı riski (ön seçili değil).</summary>
public enum AppAdvice { Suggested, Optional, Careful }

/// <summary>Açık bir uygulama (aynı programın tüm süreçleri tek satırda) ve neden kapatılabileceği.</summary>
public sealed record OpenApp(
    string Name, string Display, string? Path, int Count, long MemoryBytes, double CpuPercent,
    bool HasWindow, AppAdvice Advice, string Why, IReadOnlyList<int> Pids, IReadOnlyList<string>? Names = null)
{
    /// <summary>Bu satırdaki tüm süreç adları (aynı uygulamanın farklı exe'leri tek satırda birleşebilir).</summary>
    public IReadOnlyList<string> AllNames => Names ?? [Name];
}

public sealed record OpenAppsScan(IReadOnlyList<OpenApp> Apps, long TotalBytes, long SuggestedBytes)
{
    public IEnumerable<OpenApp> Suggested => Apps.Where(a => a.Advice == AppAdvice.Suggested);
}

/// <summary>
/// "Açık uygulamaları kapat" için aday listesini çıkarır. Önce neyin ASLA listeye girmeyeceği belirlenir (sistem, güvenlik, sürücü/donanım
/// yardımcıları, ekrandaki uygulama, çalışan oyun, Pulse), kalanlar bilinen uygulama kataloğuna göre önerilir. Hiçbir şey kapatmaz.
/// Saf mantık: süreç gruplarını dışarıdan alır, bu yüzden sahte verilerle sınanır.
/// </summary>
public static class OpenAppsAdvisor
{
    private const long MinWindowedBytes = 30L << 20;     // pencereli uygulama: 30 MB altı listeyi kalabalıklaştırır
    private const long MinBackgroundBytes = 80L << 20;   // pencerisiz arka plan programı: 80 MB altı önemsiz

    // ---- Bilinen uygulamalar: (görünen ad, öneri, neden) ----
    private static readonly Dictionary<string, (string Display, AppAdvice Advice, string Why)> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        // Önerilen: sohbet, senkron, duvar kağıdı, Windows'un işe yaramaz yan uygulamaları (kapatınca veri kaybı olmaz, istendiğinde yeniden açılır)
        ["discord"] = ("Discord", AppAdvice.Suggested, "Sohbet uygulaması; arka planda bellek ve işlemci harcar."),
        ["spotify"] = ("Spotify", AppAdvice.Suggested, "Müzik uygulaması; çalıyorsa müzik durur."),
        ["teams"] = ("Microsoft Teams", AppAdvice.Suggested, "Toplantı/sohbet uygulaması; arka planda ağır çalışır."),
        ["ms-teams"] = ("Microsoft Teams", AppAdvice.Suggested, "Toplantı/sohbet uygulaması; arka planda ağır çalışır."),
        ["slack"] = ("Slack", AppAdvice.Suggested, "Sohbet uygulaması."),
        ["whatsapp"] = ("WhatsApp", AppAdvice.Suggested, "Sohbet uygulaması."),
        ["whatsapp.root"] = ("WhatsApp", AppAdvice.Suggested, "Sohbet uygulaması."),
        ["telegram"] = ("Telegram", AppAdvice.Suggested, "Sohbet uygulaması."),
        ["zoom"] = ("Zoom", AppAdvice.Suggested, "Toplantı uygulaması."),
        ["skype"] = ("Skype", AppAdvice.Suggested, "Sohbet uygulaması."),
        ["onedrive"] = ("OneDrive", AppAdvice.Suggested, "Dosya eşitleme; kapatınca eşitleme durur, sonra kendiliğinden açılır."),
        ["dropbox"] = ("Dropbox", AppAdvice.Suggested, "Dosya eşitleme; kapatınca eşitleme durur."),
        ["googledrivefs"] = ("Google Drive", AppAdvice.Suggested, "Dosya eşitleme; kapatınca eşitleme durur."),
        ["wallpaper32"] = ("Wallpaper Engine", AppAdvice.Suggested, "Canlı duvar kağıdı; ekran kartını ve işlemciyi yorar."),
        ["wallpaper64"] = ("Wallpaper Engine", AppAdvice.Suggested, "Canlı duvar kağıdı; ekran kartını ve işlemciyi yorar."),
        ["phoneexperiencehost"] = ("Telefon Bağlantısı", AppAdvice.Suggested, "Windows'un telefon eşleme uygulaması; çoğu kişi kullanmaz."),
        ["yourphone"] = ("Telefon Bağlantısı", AppAdvice.Suggested, "Windows'un telefon eşleme uygulaması; çoğu kişi kullanmaz."),
        ["widgets"] = ("Windows Pano (Widgets)", AppAdvice.Suggested, "Haber/hava durumu panosu; arka planda bellek tutar."),
        ["widgetservice"] = ("Windows Pano (Widgets)", AppAdvice.Suggested, "Haber/hava durumu panosu; arka planda bellek tutar."),
        ["copilot"] = ("Copilot", AppAdvice.Suggested, "Yapay zekâ asistanı arka plan uygulaması."),
        ["ccxprocess"] = ("Adobe arka plan işi", AppAdvice.Suggested, "Adobe'nin güncelleme/bildirim yardımcısı."),
        ["adobeipcbroker"] = ("Adobe arka plan işi", AppAdvice.Suggested, "Adobe'nin yardımcısı."),
        ["jusched"] = ("Java güncelleyici", AppAdvice.Suggested, "Java'nın güncelleme denetçisi."),
        ["microsoftedgeupdate"] = ("Edge güncelleyici", AppAdvice.Suggested, "Edge'in güncelleme denetçisi."),
        ["googleupdate"] = ("Google güncelleyici", AppAdvice.Suggested, "Google'ın güncelleme denetçisi."),

        // İsteğe bağlı: pencereli kullanıcı uygulamaları
        ["chrome"] = ("Google Chrome", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar; Chrome çoğu zaman sekmeleri geri açar."),
        ["msedge"] = ("Microsoft Edge", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar; Edge sekmeleri geri açabilir."),
        ["firefox"] = ("Firefox", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar."),
        ["brave"] = ("Brave", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar."),
        ["opera"] = ("Opera", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar."),
        ["vivaldi"] = ("Vivaldi", AppAdvice.Optional, "Sekmeler açıksa kaydedilmemiş bir şey için sorar."),
        ["vlc"] = ("VLC", AppAdvice.Optional, "Medya oynatıcı."),
        ["itunes"] = ("iTunes", AppAdvice.Optional, "Medya uygulaması."),
        ["taskmgr"] = ("Görev Yöneticisi", AppAdvice.Optional, "Kapatılması zararsız."),
        ["steam"] = ("Steam", AppAdvice.Optional, "Oyun başlatıcı; açık Steam oyunu varsa o da kapanır. Oyunu açınca yeniden açılır."),
        ["steamwebhelper"] = ("Steam (arayüz)", AppAdvice.Optional, "Steam'in tarayıcı parçası; oyun başlatınca yeniden açılır."),
        ["epicgameslauncher"] = ("Epic Games Launcher", AppAdvice.Optional, "Oyun başlatıcı; oyunu açınca yeniden açılır."),
        ["eadesktop"] = ("EA uygulaması", AppAdvice.Optional, "Oyun başlatıcı; oyunu açınca yeniden açılır."),
        ["ubisoftconnect"] = ("Ubisoft Connect", AppAdvice.Optional, "Oyun başlatıcı; oyunu açınca yeniden açılır."),
        ["upc"] = ("Ubisoft Connect", AppAdvice.Optional, "Oyun başlatıcı; oyunu açınca yeniden açılır."),
        ["battle.net"] = ("Battle.net", AppAdvice.Optional, "Oyun başlatıcı; oyunu açınca yeniden açılır."),
        ["galaxyclient"] = ("GOG Galaxy", AppAdvice.Optional, "Oyun başlatıcı."),
        ["riotclientservices"] = ("Riot Client", AppAdvice.Optional, "Valorant/LoL başlatıcısı; oyunu açınca yeniden açılır."),

        // Dikkat: kaydedilmemiş iş olabilir
        ["notepad"] = ("Not Defteri", AppAdvice.Careful, "Kaydedilmemiş not olabilir; kapatırken sana sorar."),
        ["notepad++"] = ("Notepad++", AppAdvice.Careful, "Kaydedilmemiş dosya olabilir; kapatırken sana sorar."),
        ["winword"] = ("Word", AppAdvice.Careful, "Kaydedilmemiş belge olabilir; kapatırken sana sorar."),
        ["excel"] = ("Excel", AppAdvice.Careful, "Kaydedilmemiş tablo olabilir; kapatırken sana sorar."),
        ["powerpnt"] = ("PowerPoint", AppAdvice.Careful, "Kaydedilmemiş sunum olabilir; kapatırken sana sorar."),
        ["outlook"] = ("Outlook", AppAdvice.Careful, "Yazılmakta olan e-posta olabilir."),
        ["onenote"] = ("OneNote", AppAdvice.Careful, "Kaydedilmemiş not olabilir."),
        ["acrobat"] = ("Adobe Acrobat", AppAdvice.Careful, "Açık belge olabilir."),
        ["code"] = ("Visual Studio Code", AppAdvice.Careful, "Kaydedilmemiş dosya olabilir; kapatırken sana sorar."),
        ["devenv"] = ("Visual Studio", AppAdvice.Careful, "Kaydedilmemiş kod olabilir; kapatırken sana sorar."),
        ["cursor"] = ("Cursor", AppAdvice.Careful, "Kaydedilmemiş kod olabilir."),
        ["idea64"] = ("IntelliJ IDEA", AppAdvice.Careful, "Kaydedilmemiş kod olabilir."),
        ["pycharm64"] = ("PyCharm", AppAdvice.Careful, "Kaydedilmemiş kod olabilir."),
        ["windowsterminal"] = ("Windows Terminal", AppAdvice.Careful, "Çalışan bir komut/oturum olabilir."),
        ["cmd"] = ("Komut İstemi", AppAdvice.Careful, "Çalışan bir komut olabilir."),
        ["powershell"] = ("PowerShell", AppAdvice.Careful, "Çalışan bir komut olabilir."),
        ["pwsh"] = ("PowerShell", AppAdvice.Careful, "Çalışan bir komut olabilir."),
        ["claude"] = ("Claude", AppAdvice.Careful, "Yapay zekâ asistanı; kapatırsan açık sohbet/çalışma oturumu da biter."),
        ["codex"] = ("Codex", AppAdvice.Careful, "Yapay zekâ asistanı; kapatırsan açık oturum da biter."),
        ["obs64"] = ("OBS Studio", AppAdvice.Careful, "Kayıt ya da yayın sürüyor olabilir."),
        ["photoshop"] = ("Photoshop", AppAdvice.Careful, "Açık proje kaybolabilir."),
        ["blender"] = ("Blender", AppAdvice.Careful, "Açık proje kaybolabilir."),
        ["krita"] = ("Krita", AppAdvice.Careful, "Açık çizim kaybolabilir."),
        ["resolve"] = ("DaVinci Resolve", AppAdvice.Careful, "Açık proje kaybolabilir."),
        ["premiere pro"] = ("Premiere Pro", AppAdvice.Careful, "Açık proje kaybolabilir."),
    };

    // Asla listeye girmeyenler: başka uygulamaların çalışması için gereken parçalar, güvenlik yazılımları, Pulse'ın yönettiği araçlar
    private static readonly HashSet<string> NeverNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2", "webviewhost", "ghelper", "msiafterburner", "rtss", "rtsshookloader", "rtsshooksloader64", "throttlestop",
        "avastui", "avastsvc", "avgui", "avgsvc", "bdagent", "bdservicehost", "vsserv", "ekrn", "egui", "mbam", "mbamtray", "sophosui", "ccsvchst",
        "nortonui", "mcuicnt", "mcshield", "kavtray", "avp", "msseces", "sdrservice", "dllhost", "openconsole", "sdxhelper", "searchprotocolhost",
        "searchfilterhost", "usocoreworker", "musnotification", "tabtip", "inputapp", "systemsettings", "applicationframehost", "textinputhost",
        "windowsinternal.composableshell.experiences.textinput.inputapp", "gamingservices", "gamingservicesnet", "xboxpcapp", "xboxpcappft",
    };

    // Dizüstü ve masaüstü donanımına bağlı yardımcılar (Fn tuşları, ses efektleri, RGB, ekran kartı paneli, dokunmatik yüzey...): kapatınca donanım özelliği bozulabilir
    private static readonly string[] HardwareNamePrefixes =
    [
        "asus", "armoury", "rtk", "realtek", "nahimic", "nvidia", "nvcpl", "nvtray", "nvidia share", "igfx", "intel", "amd", "radeon", "amdrs",
        "logi", "lghub", "razer", "icue", "corsair", "steelseries", "synaptics", "syntp", "etd", "elan", "waves", "dolby", "msi", "dragoncenter",
        "lenovo", "vantage", "hotkey", "hpsystem", "hpaudio", "dell", "supportassist", "acer", "quickaccess", "gigabyte", "aorus", "wacom", "tobii",
        "killer", "rivet", "bangolufsen", "sonicstudio", "conexant", "dts", "cyberlink", "signalrgb", "openrgb", "nzxt", "cam_helper",
    ];

    private static readonly string[] HardwarePathParts =
    [
        @"\asus\", @"\asustek", @"\armoury", @"\msi\", @"\lenovo\", @"\hp\", @"\hewlett", @"\dell\", @"\acer\", @"\gigabyte\", @"\razer", @"\logitech",
        @"\corsair", @"\steelseries", @"\realtek", @"\synaptics", @"\elantech", @"\nahimic", @"\intel\", @"\nvidia", @"\amd\", @"\waves", @"\dolby",
        @"\conexant", @"\bang & olufsen", @"\killer networks", @"\rivet networks", @"\wacom", @"\tobii", @"\nzxt",
    ];

    /// <summary>Katalogdaki tüm görünen adlar ve nedenler (çeviri sözlüğü sınaması için).</summary>
    public static IEnumerable<string> CatalogTexts() => Catalog.Values.SelectMany(v => new[] { v.Display, v.Why }).Distinct();

    /// <summary>Hardware/sürücü yardımcısı mı? (Fn tuşları, ses, RGB, ekran kartı paneli gibi: kapatılmaz, listeye girmez.)</summary>
    public static bool IsHardwareHelper(string name, string? path)
    {
        var n = name.ToLowerInvariant();
        if (HardwareNamePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))) return true;
        if (path is null) return false;
        var lp = path.ToLowerInvariant();
        return HardwarePathParts.Any(lp.Contains);
    }

    /// <param name="foregroundPid">Şu an ekranda öndeki pencerenin süreci (kullanıcı onu kullanıyor; listelenmez).</param>
    /// <param name="gameName">Çalışan oyunun exe adı (uzantısız); oyun ve onun klasöründeki süreçler listelenmez, başlatıcılar da korunur.</param>
    /// <param name="gameDir">Çalışan oyunun klasörü.</param>
    public static OpenAppsScan Build(IReadOnlyList<ProcGroup> groups, int? foregroundPid = null, string? gameName = null, string? gameDir = null,
        Func<string, string?, string>? displayName = null)
    {
        var apps = new List<OpenApp>();
        var gameRunning = !string.IsNullOrEmpty(gameName);
        foreach (var g in groups)
        {
            if (g.Protected) continue;
            if (g.Name.Equals("KLYC-Pulse", StringComparison.OrdinalIgnoreCase)) continue;
            if (NeverNames.Contains(g.Name) || IsHardwareHelper(g.Name, g.Path)) continue;
            if (foregroundPid is { } fp && g.Pids.Contains(fp)) continue;                       // şu an kullandığın uygulama
            if (gameRunning && (g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase)
                                || (gameDir is not null && g.Path is not null && g.Path.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase)))) continue;

            var known = Catalog.TryGetValue(g.Name, out var c);
            // Oyun çalışırken oyun başlatıcıları (Steam, EA, Ubisoft...) oyunun parçasıdır: listeye girmez
            if (gameRunning && known && IsLauncher(g.Name)) continue;

            var min = g.HasWindow ? MinWindowedBytes : MinBackgroundBytes;
            if (!known && g.MemoryBytes < min && g.CpuPercent < 2) continue;
            if (known && g.MemoryBytes < 15L << 20) continue;                                    // bilinen ama bellek tutmayan: gösterme

            string display, why; AppAdvice advice;
            if (known) { (display, advice, why) = (Loc.T(c.Display), c.Advice, Loc.T(c.Why)); }
            else
            {
                display = displayName?.Invoke(g.Name, g.Path) ?? g.Name;
                advice = AppAdvice.Optional;
                why = g.HasWindow ? Loc.T("Açık bir uygulama; kapatırken kaydedilmemiş iş için sana sorar.")
                                  : Loc.T("Arka planda çalışan program; kapatınca sonlandırılır, ihtiyaç olunca yeniden açılır.");
            }
            apps.Add(new OpenApp(g.Name, display, g.Path, g.Count, g.MemoryBytes, g.CpuPercent, g.HasWindow, advice, why, g.Pids));
        }

        // Aynı uygulamanın birden çok exe'si (örn. Widgets + WidgetService) tek satır olur
        apps = apps.GroupBy(a => a.Display, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() == 1 ? g.First() : g.First() with
        {
            Count = g.Sum(x => x.Count), MemoryBytes = g.Sum(x => x.MemoryBytes), CpuPercent = g.Sum(x => x.CpuPercent),
            HasWindow = g.Any(x => x.HasWindow), Pids = g.SelectMany(x => x.Pids).ToList(), Names = g.SelectMany(x => x.AllNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }).ToList();

        var ordered = apps.OrderBy(a => a.Advice).ThenByDescending(a => a.MemoryBytes).ToList();
        return new OpenAppsScan(ordered, ordered.Sum(a => a.MemoryBytes), ordered.Where(a => a.Advice == AppAdvice.Suggested).Sum(a => a.MemoryBytes));
    }

    private static bool IsLauncher(string name) => name.ToLowerInvariant() is "steam" or "steamwebhelper" or "epicgameslauncher" or "eadesktop"
        or "ubisoftconnect" or "upc" or "battle.net" or "galaxyclient" or "riotclientservices";

    /// <summary>Şu an ekranda öndeki pencerenin süreç numarası (yoksa null).</summary>
    public static int? ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}

public enum CloseOutcome { Closed, WaitingForUser, Failed, Skipped }

public sealed record CloseResult(OpenApp App, CloseOutcome Outcome, string Note);

public sealed record CloseReport(IReadOnlyList<CloseResult> Results, long FreeBefore, long FreeAfter)
{
    public long FreedBytes => Math.Max(0, FreeAfter - FreeBefore);
    public int Closed => Results.Count(r => r.Outcome == CloseOutcome.Closed);
    public IEnumerable<CloseResult> Waiting => Results.Where(r => r.Outcome == CloseOutcome.WaitingForUser);
    public IEnumerable<CloseResult> Failed => Results.Where(r => r.Outcome == CloseOutcome.Failed);
}

/// <summary>
/// Seçilen uygulamaları gerçekten kapatır ve sonucu doğrular. Pencereli uygulamalara önce NAZİKÇE kapat komutu gider (kaydedilmemiş iş varsa
/// uygulama kendisi sorar, o zaman "senden kayıt bekliyor" denir, zorla kapatılmaz). Pencerisiz arka plan programları önce kapat komutunu dener,
/// cevap vermezse sonlandırılır. Zorla sonlandırma ayrı, açık bir kullanıcı kararıdır (<see cref="ForceClose"/>).
/// </summary>
public sealed class OpenAppsCloser
{
    private const uint WmClose = 0x0010;

    public CloseReport Close(IReadOnlyList<OpenApp> apps, IProgress<string>? progress = null, int waitMs = 8000)
    {
        var before = FreeRam();
        var results = new List<CloseResult>();
        var live = new List<(OpenApp App, List<Process> Procs)>();

        foreach (var app in apps)
        {
            var (prot, _) = ProcessInspector.Protection(app.Name, app.Path, app.HasWindow, false);
            if (prot || OpenAppsAdvisor.IsHardwareHelper(app.Name, app.Path)) { results.Add(new(app, CloseOutcome.Skipped, Loc.T("Korumalı; dokunulmadı."))); continue; }
            var procs = AliveSameName(app);
            if (procs.Count == 0) { results.Add(new(app, CloseOutcome.Closed, Loc.T("Zaten kapanmıştı."))); continue; }
            progress?.Report(Loc.F("{0} kapatılıyor…", app.Display));
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
            PostCloseToWindows(procs.Select(p => p.Id));
            live.Add((app, procs));
        }

        // Hepsi aynı anda beklenir: toplam süre uygulama sayısıyla uzamaz
        var end = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < end && live.Any(l => l.Procs.Any(p => !Exited(p)))) Thread.Sleep(150);

        foreach (var (app, procs) in live)
        {
            var remaining = procs.Where(p => !Exited(p)).ToList();
            if (remaining.Count > 0 && !app.HasWindow)
            {
                // Pencerisiz arka plan programı kapat komutuna cevap vermedi: sonlandır
                foreach (var p in remaining) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
                remaining = procs.Where(p => !Exited(p)).ToList();
            }
            var outcome = remaining.Count == 0 ? CloseOutcome.Closed : app.HasWindow ? CloseOutcome.WaitingForUser : CloseOutcome.Failed;
            var note = outcome switch
            {
                CloseOutcome.Closed => Loc.T("Kapandı."),
                CloseOutcome.WaitingForUser => Loc.T("Senden kayıt bekliyor ya da yanıt vermiyor; zorla kapatılmadı."),
                _ => Loc.T("Kapatılamadı."),
            };
            results.Add(new(app, outcome, note));
            Journal.Write($"Açık uygulama kapatma: {app.Name} → {outcome}");
            foreach (var p in procs) p.Dispose();
        }

        Thread.Sleep(900);                         // Windows belleği geri almak için kısa süre
        return new CloseReport(results, before, FreeRam());
    }

    /// <summary>Kapanmayan uygulamaları zorla sonlandırır. Kaydedilmemiş veri kaybolur; yalnızca kullanıcı onayıyla çağrılmalı.</summary>
    public CloseReport ForceClose(IReadOnlyList<OpenApp> apps)
    {
        var before = FreeRam();
        var results = new List<CloseResult>();
        foreach (var app in apps)
        {
            var (prot, _) = ProcessInspector.Protection(app.Name, app.Path, app.HasWindow, false);
            if (prot || OpenAppsAdvisor.IsHardwareHelper(app.Name, app.Path)) { results.Add(new(app, CloseOutcome.Skipped, Loc.T("Korumalı; dokunulmadı."))); continue; }
            var procs = AliveSameName(app);
            foreach (var p in procs) { try { p.Kill(); p.WaitForExit(2500); } catch { } }
            var left = procs.Count(p => !Exited(p));
            results.Add(new(app, left == 0 ? CloseOutcome.Closed : CloseOutcome.Failed, left == 0 ? Loc.T("Zorla sonlandırıldı.") : Loc.T("Sonlandırılamadı.")));
            Journal.Write($"Açık uygulama zorla kapatma: {app.Name} → {(left == 0 ? "tamam" : "olmadı")}");
            foreach (var p in procs) p.Dispose();
        }
        Thread.Sleep(900);
        return new CloseReport(results, before, FreeRam());
    }

    /// <summary>Listedeki süreç numaraları arasından hâlâ yaşayan ve ADI AYNI olanlar (numara başka bir programa geçmiş olabilir).</summary>
    private static List<Process> AliveSameName(OpenApp app)
    {
        var list = new List<Process>();
        foreach (var pid in app.Pids)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (app.AllNames.Any(n => p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)) && !p.HasExited) list.Add(p); else p.Dispose();
            }
            catch { }
        }
        return list;
    }

    private static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }

    public static long FreeRam()
    {
        var m = new MemoryStatus { dwLength = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? (long)m.ullAvailPhys : 0;
    }

    // ---- Pencerelere WM_CLOSE (CloseMainWindow yalnızca ana pencereyi kapatır; çok pencereli uygulamalar için hepsine) ----
    private static void PostCloseToWindows(IEnumerable<int> pids)
    {
        var set = pids.ToHashSet();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (set.Contains((int)pid) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero) PostMessage(h, WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus m);
}
