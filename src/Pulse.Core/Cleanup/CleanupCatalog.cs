namespace Pulse.Core.Cleanup;

/// <summary>
/// Temizlenebilecek yerler. Bilinçli olarak YOK: kayıt defteri temizleyici (fayda yok, bozma riski var),
/// DirectX/NVIDIA shader önbelleği (silinirse oyunlarda kasma yapar), kullanıcı belgeleri.
/// </summary>
public static class CleanupCatalog
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static IReadOnlyList<CleanupCategory> Build()
    {
        var list = new List<CleanupCategory>
        {
            new()
            {
                Id = "temp-user", Name = "Kullanıcı geçici dosyaları",
                Description = "Uygulamaların bıraktığı geçici dosyalar. 2 günden eski olanlar.",
                Roots = [Path.GetTempPath()], MinAgeDays = 2, AutoSafe = true,
                SkipPathContains = [@"\claude\"],
            },
            new()
            {
                Id = "temp-win", Name = "Windows geçici dosyaları",
                Description = "Sistem geçici klasörü. 2 günden eski olanlar.",
                Roots = [@"C:\Windows\Temp"], MinAgeDays = 2, NeedsAdmin = true, AutoSafe = true,
            },
            new()
            {
                Id = "wu-cache", Name = "Windows Update indirme önbelleği",
                Description = "Kurulmuş güncellemelerin indirme kalıntıları. Gerekirse yeniden iner.",
                Roots = [@"C:\Windows\SoftwareDistribution\Download"], NeedsAdmin = true,
                StopServices = ["wuauserv", "bits"],
            },
            new()
            {
                Id = "delivery-opt", Name = "Teslim Optimizasyonu önbelleği",
                Description = "Windows'un güncelleme paylaşım önbelleği.",
                Roots = [@"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"],
                NeedsAdmin = true, StopServices = ["DoSvc"],
            },
            new()
            {
                Id = "error-reports", Name = "Hata raporları ve çökme dökümleri",
                Description = "Windows hata raporları, mini dökümler, kurulum günlükleri. 7 günden eski olanlar.",
                Roots = [@"C:\ProgramData\Microsoft\Windows\WER", @"C:\Windows\Minidump", @"C:\Windows\LiveKernelReports", @"C:\Windows\Logs\CBS",
                         Path.Combine(Local, "CrashDumps"), Path.Combine(Local, "Microsoft", "Windows", "WER")],
                MinAgeDays = 7, NeedsAdmin = true, AutoSafe = true,
            },
            new()
            {
                Id = "prefetch", Name = "Prefetch (uygulama başlatma önbelleği)",
                Description = "Windows'un uygulamaları hızlı açmak için tuttuğu kayıtlar. Silmek zararsızdır ama faydası yoktur: Windows yeniden oluşturur, silince ilk açılışlar biraz yavaşlar. Önerilmez.",
                Roots = [@"C:\Windows\Prefetch"], MinAgeDays = 0, NeedsAdmin = true, SelectedByDefault = false,
                Filter = f => f.Extension.Equals(".pf", StringComparison.OrdinalIgnoreCase),
            },
            new()
            {
                Id = "recycle", Name = "Geri dönüşüm kutusu",
                Description = "Silinmiş ama kutuda bekleyen dosyalar. Kalıcı olarak silinir.",
                Roots = [], Special = "recycle", SelectedByDefault = true,
            },
            new()
            {
                Id = "old-installers", Name = "İndirilenler: eski kurulum dosyaları",
                Description = "30 günden eski .exe, .msi, .zip, .iso, .7z, .rar. Silinmez, 7 gün karantinada tutulur.",
                Roots = [Path.Combine(Profile, "Downloads")], MinAgeDays = 30,
                Safety = CleanupSafety.Quarantine, SelectedByDefault = false,
                Filter = f => f.Extension.ToLowerInvariant() is ".exe" or ".msi" or ".zip" or ".iso" or ".7z" or ".rar" or ".msix" or ".appx",
            },
            new()
            {
                Id = "dev-caches", Name = "Geliştirici önbellekleri",
                Description = "Gradle, npm, pip ve NuGet önbellekleri. Sonraki derleme biraz yavaş olur.",
                Roots = [Path.Combine(Profile, ".gradle", "caches"), Path.Combine(Local, "npm-cache"), Path.Combine(Local, "pip", "Cache"), Path.Combine(Local, "NuGet", "v3-cache")],
                SelectedByDefault = false,
            },
            new()
            {
                Id = "dism", Name = "Windows bileşen deposu temizliği",
                Description = "Eski güncelleme kalıntılarını DISM ile temizler. Birkaç dakika sürer.",
                Roots = [], Special = "dism", NeedsAdmin = true, SelectedByDefault = false,
            },
        };

        // Tarayıcı önbellekleri: tüm profiller
        var browsers = new (string Name, string UserData)[]
        {
            ("Chrome", Path.Combine(Local, "Google", "Chrome", "User Data")),
            ("Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data")),
        };
        foreach (var (name, userData) in browsers)
        {
            if (!Directory.Exists(userData)) continue;
            var roots = new List<string>();
            foreach (var profile in Directory.EnumerateDirectories(userData))
            {
                var pn = Path.GetFileName(profile);
                if (pn != "Default" && !pn.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                roots.Add(Path.Combine(profile, "Cache"));
                roots.Add(Path.Combine(profile, "Code Cache"));
                roots.Add(Path.Combine(profile, "GPUCache"));
            }
            list.Add(new CleanupCategory
            {
                Id = $"browser-{name.ToLowerInvariant()}", Name = $"{name} önbelleği",
                Description = $"{name} sayfa ve kod önbelleği. Oturumların, şifrelerin ve yer imlerin silinmez. Tarayıcı açıkken kısmen temizlenir.",
                Roots = roots,
            });
        }

        // Firefox: tüm profillerin cache2 klasörü
        var ffProfiles = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
        {
            var roots = Directory.EnumerateDirectories(ffProfiles).Select(p => Path.Combine(p, "cache2")).ToList();
            if (roots.Count > 0)
                list.Add(new CleanupCategory
                {
                    Id = "browser-firefox", Name = "Firefox önbelleği",
                    Description = "Firefox sayfa önbelleği. Oturumların, şifrelerin ve yer imlerin silinmez.",
                    Roots = roots,
                });
        }

        // Oyun başlatıcıları: tarayıcı önbelleği, çökme kayıtları, eski günlükler. Oyun dosyalarına ve shader önbelleğine dokunulmaz.
        var steam = SteamRoots().Where(Directory.Exists).ToList();
        if (steam.Count > 0)
        {
            var roots = new List<string>();
            foreach (var s in steam) { roots.Add(Path.Combine(s, "htmlcache")); roots.Add(Path.Combine(s, "dumps")); roots.Add(Path.Combine(s, "logs")); }
            roots.Add(Path.Combine(Local, "Steam", "htmlcache"));
            list.Add(new CleanupCategory
            {
                Id = "app-steam", Name = "Steam önbelleği ve günlükleri",
                Description = "Steam'in tarayıcı önbelleği, çökme kayıtları ve 7 günden eski günlükleri. Oyunların ve oyun shader önbelleği silinmez.",
                Roots = roots, MinAgeDays = 7, SelectedByDefault = false,
            });
        }
        var epicSaved = Path.Combine(Local, "EpicGamesLauncher", "Saved");
        if (Directory.Exists(epicSaved))
        {
            var roots = Directory.EnumerateDirectories(epicSaved, "webcache*").ToList();
            roots.Add(Path.Combine(epicSaved, "Logs"));
            list.Add(new CleanupCategory
            {
                Id = "app-epic", Name = "Epic Games Launcher önbelleği",
                Description = "Epic'in tarayıcı önbelleği ve günlükleri. Oyunların silinmez.",
                Roots = roots, MinAgeDays = 2, SelectedByDefault = false,
            });
        }

        // NVIDIA sürücü kurulum artıkları: kurulum bitince gerekmez; gerekirse yeniden iner. Yine de silinmeden önce karantinada beklerler.
        var nvidiaRoots = new[] { @"C:\NVIDIA", @"C:\ProgramData\NVIDIA Corporation\Downloader" }.Where(Directory.Exists).ToList();
        if (nvidiaRoots.Count > 0)
            list.Add(new CleanupCategory
            {
                Id = "nvidia-installers", Name = "NVIDIA sürücü kurulum artıkları",
                Description = "Sürücü kurulumundan kalan açılmış kurulum dosyaları. Kurulum bittikten sonra gerekmez. 7 gün karantinada tutulur.",
                Roots = nvidiaRoots, NeedsAdmin = true, Safety = CleanupSafety.Quarantine, SelectedByDefault = false,
            });

        // Uygulama önbellekleri: yalnızca bilgisayarda gerçekten olanlar
        var apps = new (string Name, string Path)[]
        {
            ("Discord", Path.Combine(Roaming, "discord", "Cache")),
            ("Discord kod", Path.Combine(Roaming, "discord", "Code Cache")),
            ("Spotify", Path.Combine(Local, "Spotify", "Storage")),
            ("Slack", Path.Combine(Roaming, "Slack", "Cache")),
            ("Microsoft Teams", Path.Combine(Local, "Microsoft", "Teams", "Cache")),
        };
        foreach (var (name, path) in apps)
            if (Directory.Exists(path))
                list.Add(new CleanupCategory { Id = $"app-{name.ToLowerInvariant().Replace(' ', '-')}", Name = $"{name} önbelleği", Description = $"{name} geçici verileri.", Roots = [path], SelectedByDefault = false });

        return list;
    }

    /// <summary>Steam kurulum klasörleri (kayıt defterinden ve varsayılan yol).</summary>
    private static IEnumerable<string> SteamRoots()
    {
        var paths = new List<string>();
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string sp && sp.Length > 3) paths.Add(sp.Replace('/', '\\'));
        }
        catch { }
        paths.Add(@"C:\Program Files (x86)\Steam");
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}