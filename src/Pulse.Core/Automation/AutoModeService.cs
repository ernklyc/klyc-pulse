using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.Core.Automation;

/// <summary>
/// Oyun açılınca ona ayarlı profili uygular (yoksa Oyun modu), kapanınca eski moda döner.
/// Görülen her oyunu profil listesine kendiliğinden ekler; kullanıcı sonra ona özel mod/ekran ayarı seçebilir.
/// İsteğe bağlı: prizden çıkınca Sessiz mod. Kullanıcının elle seçtiği moda saygı duyar:
/// yalnızca kendi yaptığı geçişi geri alır.
/// </summary>
public sealed class AutoModeService : IDisposable
{
    private readonly ModeController _controller;
    private readonly SettingsStore _settings;
    private readonly Timer _timer;
    private string? _restoreTo;       // oyun başlamadan önceki mod
    private bool _autoApplied;        // oyun profilini biz mi uyguladık?
    private bool? _lastAc;
    private int _running;

    public string? DetectedGame { get; private set; }

    /// <summary>Oyun algılanınca (ad). Mod geçişi ayarından bağımsız tetiklenir.</summary>
    public event Action<string>? GameStarted;

    /// <summary>Oyun kapanınca (ad).</summary>
    public event Action<string>? GameStopped;

    public AutoModeService(ModeController controller, SettingsStore settings, TimeSpan? period = null)
    {
        _controller = controller;
        _settings = settings;
        var p = period ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Tick(), null, p, p);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try { TickCore().GetAwaiter().GetResult(); }
        catch (Exception ex) { Journal.Write("Otomatik mod hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private async Task TickCore()
    {
        var s = _settings.Current;

        // Oyun algılama her zaman çalışır (listeyi öğrenmek için); mod geçişi yalnızca ayar açıksa.
        var game = GameDetector.FindRunningGameInfo();
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
            _settings.EnsureProfile(game.Name, null, game.Path);
            TuneGame(game, _settings.FindProfile(game.Name));
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
                else
                {
                    var key = profile?.ModeKey ?? Modes.Modes.Game;
                    var overrides = profile is null ? null : new ModeOverrides(profile.RefreshHz == 144 ? Modes.Modes.MaxHz : profile.RefreshHz, profile.Brightness, profile.CpuMaxMhz);
                    var hasOverrides = overrides is { RefreshHz: not null } or { Brightness: not null } or { CpuMaxMhz: not null };
                    // Oyun bitince masaüstünde Turbo/yüksek fanla kalmamak için Oyun modundan Günlük'e dönülür.
                    _restoreTo = _controller.CurrentKey is null or Modes.Modes.Game ? Modes.Modes.Daily : _controller.CurrentKey;
                    Journal.Write($"Oyun algılandı ({game.Name}): '{key}' profili uygulanıyor, önceki mod {_restoreTo}.");

                    if (_controller.CurrentKey == key && !hasOverrides) _autoApplied = true;       // zaten istenen modda
                    else if (await _controller.ApplyAsync(key, overrides) is not null) _autoApplied = true;
                }
            }
            else if (game is null && _autoApplied)
            {
                _autoApplied = false;
                var back = _restoreTo ?? Modes.Modes.Daily;
                Journal.Write($"Oyun kapandı: {back} moduna dönülüyor.");
                await _controller.ApplyAsync(back);
            }
        }

        if (s.AutoQuietOnBattery && !_autoApplied)
        {
            var ac = PowerSource.IsOnAc();
            if (ac is not null && ac != _lastAc)
            {
                if (ac == false) { Journal.Write("Pile geçildi: Sessiz mod."); await _controller.ApplyAsync(Modes.Modes.Quiet); }
                _lastAc = ac;
            }
        }
    }

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
