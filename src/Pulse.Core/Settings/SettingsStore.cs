using System.Text.Json;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Settings;

/// <summary>Kullanıcı ayarları (%LOCALAPPDATA%\Pulse\settings.json). Değişince kendiliğinden kaydedilir.</summary>
public sealed class AppSettings
{
    public bool ChangeBrightness { get; set; } = true;
    public bool AutoGameMode { get; set; } = false;
    /// <summary>Oyunlara göre kendini ayarla: oyun raporlarına bakıp işlemci hız sınırını dener, ölçer, zararlıysa geri alır.</summary>
    public bool AutoTuneGames { get; set; } = true;

    /// <summary>Günde en fazla bir kez GitHub'dan en son sürümü sorar (indirme/kurma yapmaz). Kapatılabilir.</summary>
    public bool CheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? DismissedUpdate { get; set; }

    /// <summary>Bu bilgisayarda Windows işlemci frekans sınırını uyguluyor mu? null = henüz denenmedi.</summary>
    public bool? FreqCapSupported { get; set; }
    public string? FreqCapNote { get; set; }

    /// <summary>Öğrenilen yük altı en yüksek işlemci hızı (MHz). Sınır kademeleri buna göre bu bilgisayara uyarlanır. 0 = bilinmiyor.</summary>
    public double CpuPeakMhz { get; set; }
    public bool AutoQuietOnBattery { get; set; } = false;
    public bool CloseConflictingApps { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool OnboardingDone { get; set; } = false;
    public bool AutoClean { get; set; } = true;
    public DateTime? LastAutoClean { get; set; }
    public long LastAutoCleanBytes { get; set; }
    public List<GameProfile> GameProfiles { get; set; } = new();
    public int? BatteryLimit { get; set; }       // kullanıcının seçtiği şarj limiti (%); null = dokunma
    public bool Hotkeys { get; set; } = true;    // Ctrl+Alt kısayolları
    public bool ThermalGuard { get; set; } = true;   // ısı bekçisi
    public bool KeepMode { get; set; } = true;       // başka bir program güç ayarını bozarsa geri düzelt
    public Dictionary<string, string>? HotkeyBindings { get; set; }   // eylem → "Ctrl+Alt+3"; yoksa varsayılanlar
    public int? GpuOcCore { get; set; }              // Oyun modunda uygulanacak ekran kartı çekirdek ofseti (MHz); null = fabrika
    public int? GpuOcMem { get; set; }               // ... bellek ofseti (MHz)
    public int? HeatTarget { get; set; }             // ısı hedefi (°C); null = kapalı
    public ToolLink ThrottleStopLink { get; set; } = new();
    public ToolLink AfterburnerLink { get; set; } = new();
    public ToolLink GHelperLink { get; set; } = new();
    public int OverlayCorner { get; set; }       // oyun üstü gösterge köşesi: 0 sol üst, 1 sağ üst, 2 sol alt, 3 sağ alt
    public KeyboardRgb? KeyboardColor { get; set; }  // klavye RGB seçimi; null = dokunma
    public int? KeyboardLevel { get; set; }     // kullanıcının seçtiği klavye ışığı (0-3); null = dokunma
}

/// <summary>Harici bir aracın (ThrottleStop, Afterburner, G-Helper) Pulse ile birlikte çalışma ayarı.</summary>
public sealed class ToolLink
{
    public bool StartWithPulse { get; set; }
    /// <summary>Mod anahtarı → araç profili (0 = dokunma; ThrottleStop 1-4, Afterburner 1-5).</summary>
    public Dictionary<string, int> ModeProfiles { get; set; } = new();
}

/// <summary>Klavye RGB seçimi: mod (0 sabit, 1 nefes, 2 renk döngüsü, 3 gökkuşağı), renk ve hız (0-2).</summary>
public sealed class KeyboardRgb
{
    public int Mode { get; set; }
    public byte R { get; set; } = 255;
    public byte G { get; set; }
    public byte B { get; set; }
    public int Speed { get; set; } = 1;
}

/// <summary>Bir oyuna özel ayar: hangi mod, istenirse ekran yenileme/parlaklık.</summary>
public sealed class GameProfile
{
    public string ExeName { get; set; } = "";          // uzantısız, örn. "cs2"
    public string DisplayName { get; set; } = "";
    public string ModeKey { get; set; } = "oyun";
    public int? RefreshHz { get; set; }                 // null = modun varsayılanı
    public int? Brightness { get; set; }                // null = modun varsayılanı
    public bool Enabled { get; set; } = true;           // false = bu oyunda otomatik geçiş yapma
    public string? ExePath { get; set; }                // oyunun tam yolu (biliniyorsa); ekran kartı tercihi için
    public bool HighPriority { get; set; } = true;      // oyun açılınca süreç önceliği "Yüksek"
    public int? CpuMaxMhz { get; set; }                 // oyun açıkken işlemcinin en yüksek hızı (MHz); null = sınırsız
    public bool AutoTune { get; set; } = true;          // bu oyun için kendi kendine ayar (raporlara bakıp işlemci sınırını dener/geri alır)
    public bool AutoTuneLocked { get; set; }            // ayar öğrenildi: artık değiştirilmez
    public string? AutoTuneNote { get; set; }           // son otomatik kararın açıklaması
}

public sealed class SettingsStore
{
    private static readonly string DefaultPath = Environment.GetEnvironmentVariable("KLYC_PULSE_SETTINGS") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string _filePath = DefaultPath;
    private readonly object _saveGate = new();

    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    /// <summary>Geçici klasörden (%TEMP%) gelen oyun profillerini siler (test/yükleyici artıkları gerçek oyun değildir). Silinen sayısını döner.</summary>
    public int RemoveTempProfiles()
    {
        var temp = Path.GetTempPath();
        var n = Current.GameProfiles.RemoveAll(p => !string.IsNullOrEmpty(p.ExePath) && p.ExePath.StartsWith(temp, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save();
        return n;
    }

    /// <summary>Ayarları yükler. path verilirse (testler için) o dosya kullanılır, gerçek ayarlara dokunulmaz.</summary>
    public static SettingsStore Load(string? path = null)
    {
        var store = new SettingsStore { _filePath = path ?? DefaultPath };
        try
        {
            if (File.Exists(store._filePath))
                store.Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store._filePath)) ?? new();
        }
        catch (Exception ex) { Journal.Write("Ayarlar okunamadı, varsayılanlar kullanılıyor: " + ex.Message); }
        return store;
    }

    public GameProfile? FindProfile(string exeName) =>
        Current.GameProfiles.FirstOrDefault(p => string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Oyun listede yoksa varsayılanlarla ekler (Oyun modu). Eklediyse true.</summary>
    public bool EnsureProfile(string exeName, string? displayName = null, string? exePath = null)
    {
        lock (Current)
        {
            if (FindProfile(exeName) is { } existing)
            {
                if (exePath is not null && existing.ExePath is null) { existing.ExePath = exePath; Save(); }
                return false;
            }
            Current.GameProfiles.Add(new GameProfile { ExeName = exeName, DisplayName = displayName ?? exeName, ExePath = exePath });
        }
        Save();
        return true;
    }

    public void Save()
    {
        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                // Önce geçici dosyaya yaz, sonra değiştir: yarım kalmış (bozuk) ayar dosyası oluşmaz.
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, _filePath, true);
            }
            catch (Exception ex) { Journal.Write("Ayarlar kaydedilemedi: " + ex.Message); }
        }
        Changed?.Invoke();
    }
}