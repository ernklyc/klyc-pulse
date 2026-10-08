using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Core.Apps;

namespace Pulse.Core.Cleanup;

/// <summary>Silinmiş bir uygulamadan kalmış olabilecek klasör. Kesin değildir; kullanıcı inceleyip karar verir.</summary>
public sealed record OrphanFolder(string Path, string Name, long Bytes, DateTime LastActivity, string Where);

/// <summary>
/// Eskiden kaldırılmış uygulamaların AppData / ProgramData içinde bıraktığı klasörleri bulur.
/// Çok temkinlidir: kurulu uygulama, çalışan süreç, servis, Steam/Epic oyunu, Başlat Menüsü kısayolu ya da Store paketiyle
/// eşleşen, sistem/üretici klasörü olan, yakın zamanda kullanılmış ya da küçük klasörler HİÇ gösterilmez.
/// Hiçbir şeyi silmez; silme Geri Dönüşüm Kutusu'na gider (geri alınabilir) ve her zaman kullanıcı onayıyla yapılır.
/// </summary>
public static class OrphanScanner
{
    public const int DefaultMinDays = 90;
    public const long DefaultMinBytes = 5L * 1024 * 1024;

    /// <summary>Hiçbir koşulda önerilmeyen klasör adları (sistem, sürücü ve üretici verileri). Karşılaştırma tam eşleşme, küçük harf.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Temp", "Programs", "Publishers", "ConnectedDevices", "D3DSCache", "CrashDumps",
        "PeerDistRepub", "VirtualStore", "History", "Comms", "TileDataLayer", "PlaceholderTileLogoFolder", "SystemApps",
        "Package Cache", "SoftwareDistribution", "USOShared", "USOPrivate", "Diagnostics", "ssh", "regid", "Packages",
        "NVIDIA", "NVIDIA Corporation", "Intel", "AMD", "ASUS", "ASUSTeK", "ASUSTeK COMPUTER INC.", "Realtek", "Steam", "Epic",
        "EpicGamesLauncher", "Discord", "Spotify", "Google", "Mozilla", "Apple Computer", "Apple", "Claude", "Anthropic",
        "Pulse", "Packages", "WindowsApps", "Application Data", "Desktop", "Documents", "Start Menu", "Templates", "Sun", "Oracle",
        "Adobe", "Dropbox", "OneDrive", "Zoom", "Skype", "Slack", "WhatsApp", "Telegram Desktop", "Notepad++", "Code", "JetBrains",
    };

    public static IReadOnlyList<OrphanFolder> Scan(
        IEnumerable<InstalledApp> apps,
        IEnumerable<string>? extraKnownNames = null,
        IEnumerable<string>? roots = null,
        DateTime? now = null,
        int minDays = DefaultMinDays,
        long minBytes = DefaultMinBytes,
        int max = 40,
        CancellationToken ct = default)
    {
        var tokens = BuildTokens(apps, extraKnownNames ?? KnownNamesFromSystem());
        var result = new List<OrphanFolder>();
        var clock = now ?? DateTime.Now;

        foreach (var root in roots ?? DefaultRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); } catch { continue; }
            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) return Finish(result, max);
                var name = System.IO.Path.GetFileName(dir);
                if (!IsCandidateName(name) || IsKnown(name, tokens)) continue;
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;       // bağlantı/junction: dokunma
                    var (bytes, newest) = Measure(di, ct);
                    if (bytes < minBytes) continue;
                    if ((clock - newest).TotalDays < minDays) continue;                      // yakın zamanda kullanılmış
                    result.Add(new OrphanFolder(dir, name, bytes, newest, System.IO.Path.GetFileName(root.TrimEnd('\\'))));
                }
                catch { /* okunamayan klasör: atla */ }
            }
        }
        return Finish(result, max);
    }

    private static IReadOnlyList<OrphanFolder> Finish(List<OrphanFolder> list, int max) =>
        list.OrderByDescending(o => o.Bytes).Take(max).ToList();

    public static IEnumerable<string> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return local;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return System.IO.Path.Combine(local, "Programs");                              // kullanıcı bazlı program klasörleri
    }

    /// <summary>Klasör adı adaydır: korumalı, GUID/süslü parantezli ya da çok kısa adlar elenir.</summary>
    public static bool IsCandidateName(string name)
    {
        if (name.Length < 4) return false;
        if (Protected.Contains(name)) return false;
        if (name.StartsWith('{') || name.StartsWith('.') || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Package", StringComparison.OrdinalIgnoreCase)) return false;
        if (Guid.TryParse(name.Trim('{', '}'), out _)) return false;
        return true;
    }

    /// <summary>Klasör adı bilinen bir uygulama/oyun/süreç adıyla eşleşiyor mu? Gevşek (içerme) eşleşme: şüphede "bilinen" say, gösterme.</summary>
    public static bool IsKnown(string folderName, IReadOnlyCollection<string> tokens)
    {
        var f = Normalize(folderName);
        if (f.Length < 3) return true;
        foreach (var t in tokens)
        {
            if (t.Length < 3) continue;
            if (f == t) return true;
            if (t.Length >= 4 && f.Contains(t, StringComparison.Ordinal)) return true;
            if (f.Length >= 4 && t.Contains(f, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static IReadOnlyCollection<string> BuildTokens(IEnumerable<InstalledApp> apps, IEnumerable<string> extra)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var n = Normalize(s);
            if (n.Length >= 3) set.Add(n);
        }
        foreach (var a in apps)
        {
            Add(a.Name);
            Add(a.Publisher);
            if (!string.IsNullOrWhiteSpace(a.InstallLocation))
            {
                var loc = a.InstallLocation!.TrimEnd('\\');
                Add(System.IO.Path.GetFileName(loc));
                Add(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(loc)));
            }
            // "Steam App 1234" gibi ad dışında, ürün adının ilk kelimesi de (ör. "Discord") tanıdık gelsin
            var first = a.Name.Split(' ', '-', '_')[0];
            if (first.Length >= 4) Add(first);
        }
        foreach (var e in extra) Add(e);
        return set;
    }

    /// <summary>Bu bilgisayardaki çalışan süreçler, servisler, Store paketleri, Başlat Menüsü kısayolları, Steam ve Epic oyunları.</summary>
    public static IReadOnlyList<string> KnownNamesFromSystem()
    {
        var names = new List<string>();
        try { foreach (var p in Process.GetProcesses()) { names.Add(p.ProcessName); p.Dispose(); } } catch { }

        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (svc is not null) names.AddRange(svc.GetSubKeyNames());
        }
        catch { }

        try
        {
            using var repo = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (repo is not null)
                foreach (var full in repo.GetSubKeyNames())
                {
                    var id = full.Split('_')[0];                      // Publisher.Product
                    names.Add(id);
                    var dot = id.LastIndexOf('.');
                    if (dot > 0) names.Add(id[(dot + 1)..]);
                }
        }
        catch { }

        foreach (var start in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(System.IO.Path.Combine(start, "Programs"), "*.lnk", SearchOption.AllDirectories))
                    names.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                foreach (var d in Directory.EnumerateDirectories(System.IO.Path.Combine(start, "Programs")))
                    names.Add(System.IO.Path.GetFileName(d));
            }
            catch { }
        }

        names.AddRange(SteamAndEpicGames());
        return names;
    }

    /// <summary>Steam kütüphanelerindeki ve Epic bildirimlerindeki oyun adları (kayıt defterinde görünmeyebilirler).</summary>
    private static IEnumerable<string> SteamAndEpicGames()
    {
        var libs = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string sp) libs.Add(sp.Replace('/', '\\'));
        }
        catch { }
        libs.Add(@"C:\Program Files (x86)\Steam");
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var vdf = System.IO.Path.Combine(lib, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (var line in File.ReadLines(vdf))
                        if (line.Contains("\"path\"")) { var p = line.Split('"').Where(s => s.Contains(':')).FirstOrDefault(); if (p is not null) libs.Add(p.Replace(@"\\", @"\")); }
            }
            catch { }
        }
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] games = [];
            try { var common = System.IO.Path.Combine(lib, "steamapps", "common"); if (Directory.Exists(common)) games = Directory.GetDirectories(common); } catch { }
            foreach (var g in games) yield return System.IO.Path.GetFileName(g);
        }

        var epic = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        string[] items = [];
        try { if (Directory.Exists(epic)) items = Directory.GetFiles(epic, "*.item"); } catch { }
        foreach (var f in items)
        {
            string? text = null;
            try { text = File.ReadAllText(f); } catch { }
            if (text is null) continue;
            foreach (var key in new[] { "\"DisplayName\"", "\"AppName\"" })
            {
                var i = text.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) continue;
                var parts = text[i..].Split('"');
                if (parts.Length > 3) yield return parts[3];
            }
        }
    }

    private static (long Bytes, DateTime Newest) Measure(DirectoryInfo dir, CancellationToken ct)
    {
        long bytes = 0;
        var newest = dir.LastWriteTime;
        var count = 0;
        foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            bytes += f.Length;
            if (f.LastWriteTime > newest) newest = f.LastWriteTime;
            if (++count > 60000 || ct.IsCancellationRequested) break;
        }
        return (bytes, newest);
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Seçilen klasörü Geri Dönüşüm Kutusu'na gönderir (geri alınabilir). Korumalı yollara asla dokunmaz.</summary>
    public static bool Remove(OrphanFolder f, Func<string, bool>? sendToRecycleBin = null)
    {
        sendToRecycleBin ??= RecycleBin.SendToRecycleBin;
        var p = f.Path.TrimEnd('\\');
        if (p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;   // örn. C:\Foo ya da C:\Users → çok sığ (ProgramData\Foo = 3 kademe, serbest)
        return sendToRecycleBin(p);
    }
}
