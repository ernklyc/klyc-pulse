using System.Text.Json;
using Pulse.Core.Automation;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Companion;

/// <summary>Konsol'un `status.json` içinde bildirdiği oyun (şema 2); şema 1'de yalnızca ad ve kimlik vardır.</summary>
public sealed record KonsolGameInfo(string? Id, string Title, string? Store, string? InstallDir, string? Target, long? StartedAt);

/// <summary>KLYC Konsol'un durumu (`%AppData%\KLYC-Konsol\status.json`). Eksik alan = null; bilinmeyen alanlar yok sayılır.</summary>
public sealed record KonsolStatus(int Schema, string State, long UpdatedAt, string? Version, int? Pid, KonsolGameInfo? Game, string? PowerPlanManagedBy, bool WantsLive = false)
{
    /// <summary>Konsol bir oyunu başlatıyor ya da oyun sürüyor (bu sırada Konsol kapalı olabilir; durum "son bilinen"dir).</summary>
    public bool InGame => State is "launching" or "playing";

    /// <summary>Konsol açık ve ekranda ya da gizli (kütüphane, masaüstü modu, arka plan).</summary>
    public bool IsOpen => State is "launcher" or "background" or "desktop";

    /// <summary>Oyun durumundaysa Pulse'ın oyun algılamasına yardım edecek ipucu; değilse null.</summary>
    public KonsolGameHint? ToHint() =>
        InGame && Game is { Title.Length: > 0 } g ? new KonsolGameHint(g.Title, g.InstallDir, g.Store) : null;

    /// <summary>Konsol güç planını kendisi yönetiyor ve oyun sürüyor: mod koruyucu bu sırada Konsol'un planıyla savaşmamalı.</summary>
    public bool KonsolOwnsPowerPlan => InGame && string.Equals(PowerPlanManagedBy, "konsol", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pulse'ın Konsol'a verdiği özet (`%LocalAppData%\Pulse\konsol-feed.json`).</summary>
public sealed record PulseFeedReport(string Game, long StartedAt, double Minutes, double? AvgFps, double? CpuTempMax, int? GpuTempMax,
    string Bottleneck, int Severity, List<string> Findings);

public sealed record PulseFeedMode(string? Key, bool Applying);

/// <summary>Konsol üst çubuğundaki chip için canlı değerler (yalnızca Konsol `wantsLive` isterse ve açıkken doldurulur).</summary>
public sealed record PulseFeedLive(double? CpuTemp, double? GpuTemp, double? CpuLoad, double? GpuLoad, long At);

/// <summary>Konsol'da gösterilecek mod listesi (yalnızca komutlar kabul ediliyorsa).</summary>
public sealed record PulseFeedModeInfo(string Key, string Title);

/// <summary>Pulse'ın bildiği bir oyun profili (Konsol "bu oyun için Pulse modu" satırında gösterir): Mode = mod anahtarı, Enabled=false ise bu oyunda otomatik geçiş yok.</summary>
public sealed record PulseFeedGameProfile(string Name, string Mode, bool Enabled);

public sealed record PulseFeed(int Schema, long UpdatedAt, string PulseVersion, bool Running, PulseFeedMode Mode, bool AutoGameMode, bool AcceptsCommands, PulseFeedReport? LastReport,
    string? ModeTitle = null, List<PulseFeedModeInfo>? Modes = null, PulseFeedLive? Live = null, PulseFeedCommand? LastCommand = null, int? HeatTarget = null, List<PulseFeedGameProfile>? GameProfiles = null);

/// <summary>
/// KLYC Konsol ile dosya tabanlı haberleşme: saf mantık (okuma, yazma, yollar). Dosyalar kullanıcı profilindedir, yoklama yoktur.
/// Testler gerçek veriye dokunmasın diye klasörler `KLYC_KONSOL_DIR` / `KLYC_PULSE_DIR` ortam değişkenleriyle değiştirilebilir.
/// Her şey isteğe bağlıdır: dosya yoksa, bozuksa ya da bayatsa "Konsol yok" sayılır, hata verilmez.
/// </summary>
public static class KonsolLink
{
    public const int FeedSchema = 1;

    /// <summary>Pulse'ın canlı sayılması için Konsol'un kabul edeceği en uzun yaş (feed kalp atışı bundan sık yazılır).</summary>
    public static readonly TimeSpan FeedFreshness = TimeSpan.FromSeconds(20);

    public static string KonsolDir =>
        Environment.GetEnvironmentVariable("KLYC_KONSOL_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KLYC-Konsol");

    public static string PulseDir =>
        Environment.GetEnvironmentVariable("KLYC_PULSE_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse");

    public static string StatusPath => Path.Combine(KonsolDir, "status.json");
    public static string FeedPath => Path.Combine(PulseDir, "konsol-feed.json");

    public static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- Konsol durumunu okuma -------------------------------------------------

    /// <summary>`status.json` okur (şema 1 ve 2). Dosya yok/bozuk/yarım ise null.</summary>
    public static KonsolStatus? ReadStatus(string? path = null)
    {
        path ??= StatusPath;
        try
        {
            if (!File.Exists(path)) return null;
            var json = ReadShared(path);
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
            return ParseStatus(json, mtime);
        }
        catch (Exception ex) { Journal.Write("Konsol durumu okunamadı: " + ex.Message); return null; }
    }

    /// <summary>Konsol yazarken de okuyabilmek için paylaşımlı açar.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <param name="fileTime">`updatedAt` yoksa (şema 1) yedek olarak dosyanın değişme zamanı (unix saniye).</param>
    public static KonsolStatus? ParseStatus(string json, long fileTime = 0)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var schema = Int(root, "schema") ?? 1;
            var state = Str(root, "state")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(state)) return null;
            var updated = Long(root, "updatedAt") ?? fileTime;

            KonsolGameInfo? game = null;
            if (root.TryGetProperty("game", out var g))
            {
                if (g.ValueKind == JsonValueKind.Object)
                {
                    var title = Str(g, "title");
                    if (!string.IsNullOrWhiteSpace(title))
                        game = new KonsolGameInfo(Str(g, "id"), title!, Str(g, "store"), Str(g, "installDir"), Str(g, "target"), Long(g, "startedAt"));
                }
                else if (g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
                {
                    // şema 1: oyun yalnızca metin, kimlik ayrı alanda, başlangıç `since`
                    game = new KonsolGameInfo(Str(root, "gameId"), g.GetString()!, null, null, null, Long(root, "since"));
                }
            }
            var wantsLive = root.TryGetProperty("wantsLive", out var wl) && wl.ValueKind == JsonValueKind.True;
            return new KonsolStatus(schema, state!, updated, Str(root, "version"), Int(root, "pid"), game, Str(root, "powerPlanManagedBy"), wantsLive);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Durum, verilen süreden daha eskiyse (ya da gelecekten, saat farkı) güvenilmez.</summary>
    public static bool IsStale(KonsolStatus s, TimeSpan maxAge, long? now = null)
    {
        var age = (now ?? UnixNow()) - s.UpdatedAt;
        return age > maxAge.TotalSeconds || age < -300;
    }

    /// <summary>Konsol'un kendi süreci (durumu en son yazan) hâlâ yaşıyor mu? Oyun sırasında Konsol kapalıdır, o durumda doğrulama için kullanılmaz.</summary>
    /// <param name="expectedName">Verilirse süreç adı da denetlenir (kimlik başka bir programa geçmiş olabilir; uzun süre önce yazılmış bayat dosyada yanlış "canlı" çıkmasın).</param>
    public static bool IsProcessAlive(int? pid, string? expectedName = null)
    {
        if (pid is not > 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !p.HasExited && (expectedName is null || string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ---- Konsol'a özet yazma ---------------------------------------------------

    public static PulseFeedReport? ToFeedReport(GameSessionReport? r) => r is null ? null : new PulseFeedReport(
        r.Game, new DateTimeOffset(r.Start.ToUniversalTime()).ToUnixTimeSeconds(), Math.Round(r.Minutes, 1), r.AvgFps is { } f ? Math.Round(f) : null,
        r.CpuTempMax is { } c ? Math.Round(c) : null, r.GpuTempMax, r.Bottleneck, r.Severity, r.Findings.Take(3).ToList());

    public static PulseFeed BuildFeed(string pulseVersion, bool running, string? modeKey, bool applying, bool autoGameMode, GameSessionReport? last, long? now = null,
        bool acceptsCommands = false, PulseFeedLive? live = null, PulseFeedCommand? lastCommand = null, int? heatTarget = null, List<PulseFeedGameProfile>? gameProfiles = null) =>
        new(FeedSchema, now ?? UnixNow(), pulseVersion, running, new PulseFeedMode(modeKey, applying), autoGameMode, acceptsCommands, ToFeedReport(last),
            modeKey is null ? null : Pulse.Core.Modes.Modes.Get(modeKey) is { } d ? Pulse.Core.Localization.Loc.T(d.Title) : modeKey,
            acceptsCommands ? Pulse.Core.Modes.Modes.All.Select(m => new PulseFeedModeInfo(m.Key, Pulse.Core.Localization.Loc.T(m.Title))).ToList() : null,
            live, lastCommand, heatTarget, acceptsCommands ? gameProfiles : null);

    private static readonly JsonSerializerOptions FeedJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string Serialize(PulseFeed feed) => JsonSerializer.Serialize(feed, FeedJson);

    /// <summary>
    /// Atomik yazım: aynı klasörde geçici dosyaya yaz, sonra üzerine taşı; okuyucu yarım dosya görmez.
    /// Başarısızlık sessizdir (Konsol yoksa ya da klasör yazılamıyorsa Pulse'ın işi aksamaz). Başarılıysa true.
    /// </summary>
    public static bool WriteFeed(PulseFeed feed, string? path = null)
    {
        path ??= FeedPath;
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllText(tmp, Serialize(feed), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) { Journal.Write("Konsol özeti yazılamadı: " + ex.Message); return false; }
    }

    // ---- JSON yardımcıları (tür uyuşmazlığı hata değil, "yok" sayılır) ----------

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static int? Int(JsonElement e, string name) => Long(e, name) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
}
