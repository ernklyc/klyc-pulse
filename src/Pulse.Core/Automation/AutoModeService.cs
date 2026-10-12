using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.Core.Automation;

/// <summary>KLYC Konsol'un bildirdiği oyun: kesin ad, kurulum klasörü ve mağaza. Süreç görünmeden önce de bilinir.</summary>
public sealed record KonsolGameHint(string Title, string? InstallDir, string? Store);

/// <summary>
/// Oyun açılınca ona ayarlı profili uygular (yoksa Oyun modu), kapanınca eski moda döner.
/// Görülen her oyunu profil listesine kendiliğinden ekler; kullanıcı sonra ona özel mod/ekran ayarı seçebilir.
/// İsteğe bağlı: prizden çıkınca Sessiz mod. Kullanıcının elle seçtiği moda saygı duyar:
/// yalnızca kendi yaptığı geçişi geri alır.
/// <para>
/// KLYC Konsol bağlıysa (bkz. <see cref="Companion.KonsolLink"/>) üç şey eklenir: (1) Konsol'un söylediği kurulum klasöründeki süreç, klasör kuralına
/// uymasa bile oyun sayılır; (2) "başlatılıyor" ipucunda profil, oyun penceresi açılmadan uygulanır; (3) süreç hiç gelmezse
/// (<see cref="LaunchGrace"/> sonra) uygulanan mod geri alınır. Bu ipuçları yalnızca ipucudur: oyunu bulma ve bitirme kararı yine süreç denetimindedir.
/// </para>
/// </summary>
public sealed class AutoModeService : IDisposable
{
    /// <summary>"Başlatılıyor" ipucundan sonra oyun sürecinin görünmesi için beklenen en uzun süre.</summary>
    public static readonly TimeSpan LaunchGrace = TimeSpan.FromSeconds(180);

    private readonly IModeSwitch _controller;
    private readonly SettingsStore _settings;
    private readonly Timer _timer;
    private readonly Func<Func<string, string, bool>?, GameDetector.DetectedGame?> _find;
    private readonly Func<DateTime> _now;
    private readonly bool _tune;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _restoreTo;       // oyun başlamadan önceki mod
    private bool _autoApplied;        // oyun profilini biz mi uyguladık?
    private bool? _lastAc;

    private readonly object _hintLock = new();
    private KonsolGameHint? _hint;          // Konsol'un bildirdiği oyun (algılamaya yardım eder)
    private DateTime? _launchDeadline;      // "başlatılıyor" ipucu için sürecin görünmesini bekleme sınırı
    private bool _launchSeen;               // ipucundan sonra süreç göründü mü?

    public string? DetectedGame { get; private set; }

    /// <summary>Oyun algılanınca (ad). Mod geçişi ayarından bağımsız tetiklenir.</summary>
    public event Action<string>? GameStarted;

    /// <summary>Oyun kapanınca (ad).</summary>
    public event Action<string>? GameStopped;

    public AutoModeService(IModeSwitch controller, SettingsStore settings, TimeSpan? period = null,
        Func<Func<string, string, bool>?, GameDetector.DetectedGame?>? find = null, Func<DateTime>? now = null, bool tune = true)
    {
        _tune = tune;
        _controller = controller;
        _settings = settings;
        _find = find ?? (known => GameDetector.FindRunningGameInfo(known));
        _now = now ?? (() => DateTime.UtcNow);
        var p = period ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Tick(), null, p, p);
    }

    // ---- KLYC Konsol ipuçları -------------------------------------------------

    /// <summary>
    /// Konsol "oyunu başlatıyorum" dedi. Otomatik oyun modu açıksa profil hemen uygulanır (oyun penceresi açılmadan);
    /// süreç <see cref="LaunchGrace"/> içinde görünmezse mod geri alınır. Otomatik mod kapalıysa yalnızca algılamaya yardım eder.
    /// </summary>
    public async Task NotifyGameLaunching(KonsolGameHint hint)
    {
        lock (_hintLock) { _hint = hint; _launchDeadline = _now() + LaunchGrace; _launchSeen = false; }
        await _gate.WaitAsync();
        try
        {
            if (!_settings.Current.AutoGameMode || _autoApplied) return;
            var profile = FindProfileForHint(hint);
            if (profile is { Enabled: false }) return;
            await ApplyGameModeAsync(hint.Title + " (Konsol)", profile);
        }
        catch (Exception ex) { Journal.Write("Konsol oyun ipucu işlenemedi: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Konsol oyunun sürdüğünü söylüyor (Pulse sonradan açılmış olabilir): yalnızca algılamaya yardım eder, mod uygulamaz.</summary>
    public void NotifyGamePlaying(KonsolGameHint hint)
    {
        lock (_hintLock) { _hint = hint; _launchDeadline = null; _launchSeen = true; }
    }

    /// <summary>Konsol oyun dışında bir durumda (kütüphane, masaüstü, arka plan): ipucu geçersiz.</summary>
    public void NotifyKonsolIdle()
    {
        lock (_hintLock) { _hint = null; _launchDeadline = null; _launchSeen = false; }
    }

    // ---- Döngü ---------------------------------------------------------------

    private void Tick()
    {
        if (!_gate.Wait(0)) return;
        try { TickCore().GetAwaiter().GetResult(); }
        catch (Exception ex) { Journal.Write("Otomatik mod hatası: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Bir denetim turu (zamanlayıcı dışında, testler için).</summary>
    public async Task TickOnceAsync()
    {
        await _gate.WaitAsync();
        try { await TickCore(); }
        finally { _gate.Release(); }
    }

    private async Task TickCore()
    {
        var s = _settings.Current;

        // Oyun algılama her zaman çalışır (listeyi öğrenmek için); mod geçişi yalnızca ayar açıksa.
        var game = _find(IsKnownGame);
        var previous = DetectedGame;
        DetectedGame = game?.Name;
        try
        {
            if (previous is null && game is not null) GameStarted?.Invoke(game.Name);
            else if (previous is not null && game is null) GameStopped?.Invoke(previous);
        }
        catch (Exception ex) { Journal.Write("Oyun olayı işlenemedi: " + ex.Message); }
        if (game is not null)
        {
            lock (_hintLock) { if (_launchDeadline is not null) _launchSeen = true; }
            _settings.EnsureProfile(game.Name, null, game.Path);
            if (_tune) TuneGame(game, _settings.FindProfile(game.Name));
        }

        if (s.AutoGameMode)
        {
            if (game is not null && !_autoApplied)
            {
                var profile = _settings.FindProfile(game.Name);
                if (profile is { Enabled: false })
                {
                    // Kullanıcı bu oyun için otomatik geçişi kapatmış: hiçbir şey yapma.
                }
                else await ApplyGameModeAsync(game.Name, profile);
            }
            else if (game is null && _autoApplied)
            {
                if (WaitingForLaunchedGame()) return;   // Konsol başlattı, süreç henüz görünmedi: bekle
                _autoApplied = false;
                lock (_hintLock) { _launchDeadline = null; _launchSeen = false; }
                var back = _restoreTo ?? Modes.Modes.Daily;
                Journal.Write($"Oyun kapandı: {back} moduna dönülüyor.");
                await _controller.ApplyAsync(back, null);
            }
        }

        if (s.AutoQuietOnBattery && !_autoApplied)
        {
            var ac = PowerSource.IsOnAc();
            if (ac is not null && ac != _lastAc)
            {
                if (ac == false) { Journal.Write("Pile geçildi: Sessiz mod."); await _controller.ApplyAsync(Modes.Modes.Quiet, null); }
                _lastAc = ac;
            }
        }
    }

    /// <summary>Konsol oyunu başlattığını söyledi, süreç henüz görünmedi ve bekleme süresi dolmadı mı?</summary>
    private bool WaitingForLaunchedGame()
    {
        lock (_hintLock)
        {
            if (_launchDeadline is not { } deadline || _launchSeen) return false;
            if (_now() < deadline) return true;
            Journal.Write("Konsol oyunu başlattı ama oyun süreci görünmedi: uygulanan mod geri alınıyor.");
            return false;
        }
    }

    /// <summary>Profili (yoksa Oyun modunu) uygular; oyun bitince dönülecek modu not eder. Uygulandıysa/zaten istenen moddaysa true.</summary>
    private async Task<bool> ApplyGameModeAsync(string label, GameProfile? profile)
    {
        var key = profile?.ModeKey ?? Modes.Modes.Game;
        var overrides = profile is null ? null : new ModeOverrides(profile.RefreshHz == 144 ? Modes.Modes.MaxHz : profile.RefreshHz, profile.Brightness, profile.CpuMaxMhz);
        var hasOverrides = overrides is { RefreshHz: not null } or { Brightness: not null } or { CpuMaxMhz: not null };
        // Oyun bitince masaüstünde Turbo/yüksek fanla kalmamak için Oyun modundan Günlük'e dönülür.
        _restoreTo = _controller.CurrentKey is null or Modes.Modes.Game ? Modes.Modes.Daily : _controller.CurrentKey;
        Journal.Write($"Oyun algılandı ({label}): '{key}' profili uygulanıyor, önceki mod {_restoreTo}.");

        if (_controller.CurrentKey == key && !hasOverrides) _autoApplied = true;       // zaten istenen modda
        else if (await _controller.ApplyAsync(key, overrides) is not null) _autoApplied = true;
        return _autoApplied;
    }

    /// <summary>Elle eklenmiş (ya da daha önce öğrenilmiş) bir oyun profiliyle ya da Konsol'un bildirdiği klasörle eşleşiyor mu?</summary>
    private bool IsKnownGame(string path, string fileLower) => IsKnownProfile(path, fileLower) || UnderHintDir(path);

    private bool IsKnownProfile(string path, string fileLower) =>
        _settings.Current.GameProfiles.Any(p => p.Enabled &&
            (!string.IsNullOrEmpty(p.ExePath) && string.Equals(p.ExePath, path, StringComparison.OrdinalIgnoreCase)
             || string.IsNullOrEmpty(p.ExePath) && string.Equals(p.ExeName, fileLower, StringComparison.OrdinalIgnoreCase)));

    private bool UnderHintDir(string path)
    {
        KonsolGameHint? h;
        lock (_hintLock) h = _hint;
        return h?.InstallDir is { Length: > 0 } dir && IsUnder(path, dir);
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Konsol oyununa karşılık gelen profil: yolu bildirilen klasörün altındaysa ya da adı eşleşiyorsa (harf/rakam dışı yok sayılır).</summary>
    private GameProfile? FindProfileForHint(KonsolGameHint hint)
    {
        var title = Normalize(hint.Title);
        return _settings.Current.GameProfiles.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(hint.InstallDir) && !string.IsNullOrEmpty(p.ExePath) && IsUnder(p.ExePath, hint.InstallDir))
            || (title.Length > 0 && (Normalize(p.DisplayName) == title || Normalize(p.ExeName) == title)));
    }

    private static string Normalize(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private readonly HashSet<string> _gpuDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _priorityDone = new();

    /// <summary>Oyun başına güvenli ayarlar: ekran kartı tercihi (yüksek performans) ve süreç önceliği.</summary>
    private void TuneGame(GameDetector.DetectedGame game, GameProfile? profile)
    {
        if (profile is { Enabled: false }) return;

        // Ekran kartı tercihi bir sonraki açılışta etkili olur; oyun listeye eklendiği anda bir kez yazılır.
        if (_gpuDone.Add(game.Path) && Optimize.GameTuning.GetGpuPreference(game.Path) != 2)
            Optimize.GameTuning.SetHighPerformanceGpu(game.Path);

        // Öncelik yalnızca otomatik oyun modu açıksa ve profil izin veriyorsa.
        if (_settings.Current.AutoGameMode && (profile?.HighPriority ?? true) && game.Pid > 0 && _priorityDone.Add(game.Pid))
            Optimize.GameTuning.SetHighPriority(game.Pid);
    }

    public void Dispose() => _timer.Dispose();
}
