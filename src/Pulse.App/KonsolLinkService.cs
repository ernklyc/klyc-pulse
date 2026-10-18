using System.IO;
using Pulse.Core.Automation;
using Pulse.Core.Companion;
using Pulse.Core.Diagnostics;
using Pulse.Core.Localization;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;
using Pulse.Core.Optimize;
using Pulse.Core.Settings;

namespace Pulse.App;

/// <summary>
/// KLYC Konsol ile çalışma (kardeş proje). Konsol'un `status.json` dosyasını izler (olay tabanlı, yoklama yok):
/// oyun başlatılırken profili erken uygular, Konsol'un söylediği oyun klasörünü algılamaya yardımcı olarak verir ve Konsol güç planını
/// yönetirken mod koruyucuyu bekletir. Konsol'a `konsol-feed.json` özeti yazar (mod, son oyun raporu, "Pulse çalışıyor" işareti).
/// <para>
/// Her şey isteğe bağlıdır: Konsol yoksa hiçbir dosya yazılmaz, hiçbir şey değişmez. Oyun sırasında Konsol kapalıdır ve Pulse de
/// hiçbir şey yazmaz (yalnızca oyun bitince rapor özeti). Ayrıntı: docs/MIMARI.md ve KLYC-Konsol/docs/PULSE-ENTEGRASYON.md.
/// </para>
/// </summary>
public sealed class KonsolLinkService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(8);     // Konsol 20 sn'den eski özeti "Pulse yok" sayar
    private const string KonsolProcessName = "KlycKonsol";
    private static readonly TimeSpan GameStatusMaxAge = TimeSpan.FromHours(12);

    private readonly SettingsStore _settings;
    private readonly AutoModeService _auto;
    private readonly ModeController _modes;
    private readonly GameReportService _report;
    private readonly Func<IDisposable> _holdKeeper;
    private readonly object _lock = new();
    private readonly string _version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private FileSystemWatcher? _watcher, _cmdWatcher;
    private Timer? _debounce, _heartbeat, _dirProbe, _holdExpire, _cmdDebounce;
    private KonsolStatus? _status;
    private IDisposable? _hold;
    private bool _started, _konsolSeen;
    private readonly Func<SensorService>? _sensors;
    private IDisposable? _sensorSub;
    private readonly KonsolCommandRunner _commands;
    private PulseFeedCommand? _lastCommand;

    public KonsolLinkService(SettingsStore settings, AutoModeService auto, ModeController modes, GameReportService report, Func<IDisposable> holdKeeper, Func<SensorService>? sensors = null)
    {
        _settings = settings; _auto = auto; _modes = modes; _report = report; _holdKeeper = holdKeeper; _sensors = sensors;
        _commands = new KonsolCommandRunner(modes, BoostAsync,
            allowed: () => _settings.Current.LinkWithKonsol && _settings.Current.AllowKonsolCommands,
            konsolOpen: () => { KonsolStatus? st; lock (_lock) st = _status; return st is { IsOpen: true } && KonsolLink.IsProcessAlive(st.Pid, KonsolProcessName); },
            extra: new KonsolExtraActions
            {
                Clean = async () => Loc.F("Temizlik tamam: {0} temizlendi.", Optimizer.Format(await new Optimizer(_modes).CleanSafeAsync())),
                AutoGame = on => { _settings.Current.AutoGameMode = on; _settings.Save(); return on ? Loc.T("Oyun açılınca otomatik mod: açık.") : Loc.T("Oyun açılınca otomatik mod: kapalı."); },
                Fan = async on => (await _modes.SetFanBoostAsync(on)) is { } r ? (on ? Loc.T("Fan desteği açıldı.") : Loc.T("Fan desteği kapatıldı.")) : Loc.T("Fan desteği şu an değiştirilemedi (başka bir işlem sürüyor ya da mod seçilmemiş)."),
                BgClose = (mode, name) => { var r = KonsolBgClose.Apply(_settings.Current, mode, name); if (r.Ok) _settings.Save(); return r; },
                GameMode = (title, mode) => { var r = KonsolGameProfiles.Apply(_settings.Current, title, mode); if (r.Ok) _settings.Save(); return r; },
                Heat = t => { int? target = t == 0 ? null : t; _settings.Current.HeatTarget = target; _settings.Save(); AppServices.Heat.Configure(target); return target is null ? Loc.T("Isı hedefi kapatıldı.") : Loc.F("Isı hedefi {0} °C.", target); },
            });
    }

    /// <summary>Konsol'dan gelen "Hızlandır": arayüzdeki ile aynı işlem (onay penceresi yok; kullanıcı Konsol'da bilerek seçti).</summary>
    private async Task<string> BoostAsync()
    {
        var r = await new Optimizer(_modes).RunAsync();
        return Loc.F("Hızlandır tamam: {0} temizlendi, {1} bellek rahatladı.", Optimizer.Format(r.CleanedBytes), Optimizer.Format(Math.Max(0, r.RamGained)));
    }

    /// <summary>Ayarlar sayfasındaki durum satırı.</summary>
    public string StatusText
    {
        get
        {
            if (!_settings.Current.LinkWithKonsol) return Loc.T("Kapalı.");
            KonsolStatus? st; lock (_lock) st = _status;
            if (st is null) return Loc.T("KLYC Konsol bulunamadı.");
            var age = KonsolLink.UnixNow() - st.UpdatedAt;
            var seen = age > 86400 ? Loc.T("uzun süredir çalışmadı") : st.InGame ? Loc.F("oyun: {0}", st.Game?.Title ?? "?") : Loc.T("açık");
            return Loc.F("Konsol bulundu · v{0} · {1}", st.Version ?? "?", seen);
        }
    }

    public event Action? Changed;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        _modes.Applied += OnApplied;
        _modes.Busy += OnBusy;
        _modes.Failed += OnFailed;
        _report.Ready += OnReady;
        Attach();
    }

    private void OnApplied(ModeResult _) => WriteFeed(duringGame: false);
    private void OnBusy(string _) => WriteFeed(duringGame: false);
    private void OnFailed(string _, string __) => WriteFeed(duringGame: false);
    private void OnReady(GameSessionReport _) => WriteFeed(duringGame: true);      // oyun bitti: Konsol açılınca rapor hazır olsun

    /// <summary>Ayar değişince çağrılır: açıksa dosyayı yeniden okur, kapalıysa ipuçlarını bırakır.</summary>
    public void OnSettingChanged()
    {
        if (_settings.Current.LinkWithKonsol) { Attach(); WriteFeed(duringGame: false); }   // komut kabul bilgisi (AllowKonsolCommands) hemen Konsol'a yazılsın
        else
        {
            lock (_lock)
            {
                ReleaseHold(); StopHeartbeat();
                _auto.NotifyKonsolIdle();
                _status = null;
            }
            if (_konsolSeen) KonsolLink.WriteFeed(Feed(running: false));
            Changed?.Invoke();
        }
    }

    // ---- İzleme -------------------------------------------------------------

    private void Attach()
    {
        if (!_settings.Current.LinkWithKonsol) return;
        var dir = KonsolLink.KonsolDir;
        if (!Directory.Exists(dir))
        {
            // Konsol henüz kurulmamış: klasör oluşana kadar nadiren bak (izlenecek bir şey yok, yoklama maliyeti ihmal edilebilir)
            _dirProbe ??= new Timer(_ => { if (Directory.Exists(KonsolLink.KonsolDir)) Attach(); }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
            return;
        }
        _dirProbe?.Dispose(); _dirProbe = null;
        if (_watcher is null)
        {
            try
            {
                _watcher = new FileSystemWatcher(dir, "status.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
                _watcher.Changed += (_, _) => Schedule();
                _watcher.Created += (_, _) => Schedule();
                _watcher.Renamed += (_, _) => Schedule();
                _watcher.Deleted += (_, _) => Schedule();
            }
            catch (Exception ex) { Journal.Write("Konsol dosya izleyici kurulamadı: " + ex.Message); }
        }
        if (_cmdWatcher is null)
        {
            try
            {
                _cmdWatcher = new FileSystemWatcher(dir, Path.GetFileName(KonsolCommands.CommandPath)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
                _cmdWatcher.Created += (_, _) => ScheduleCommand();
                _cmdWatcher.Changed += (_, _) => ScheduleCommand();
                _cmdWatcher.Renamed += (_, _) => ScheduleCommand();
            }
            catch (Exception ex) { Journal.Write("Konsol komut izleyicisi kurulamadı: " + ex.Message); }
        }
        Schedule();
    }

    /// <summary>Komut dosyası yazımı bitsin diye kısa bekle; sonra komutu işle (ayar kapalıysa komut reddedilir ve Konsol'a nedeni bildirilir).</summary>
    private void ScheduleCommand()
    {
        lock (_lock) { (_cmdDebounce ??= new Timer(_ => _ = RunCommandAsync())).Change(TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan); }
    }

    private async Task RunCommandAsync()
    {
        try
        {
            if (!_settings.Current.LinkWithKonsol) { try { File.Delete(KonsolCommands.CommandPath); } catch { } return; }
            var result = await _commands.ProcessAsync();
            if (result is null) return;
            lock (_lock) _lastCommand = result;
            WriteFeed(duringGame: false);   // sonuç Konsol'a hemen ulaşsın
            Changed?.Invoke();
        }
        catch (Exception ex) { Journal.Write("Konsol komutu işlenemedi: " + ex.Message); }
    }

    /// <summary>Yazım bitsin diye kısa bekleyip (art arda olayları birleştirerek) oku.</summary>
    private void Schedule()
    {
        lock (_lock) { (_debounce ??= new Timer(_ => Refresh())).Change(Debounce, Timeout.InfiniteTimeSpan); }
    }

    private void Refresh()
    {
        try
        {
            lock (_lock)
            {
                if (!_settings.Current.LinkWithKonsol) return;
                var st = KonsolLink.ReadStatus();
                var previous = _status;
                _status = st;
                if (st is not null) _konsolSeen = true;
                if (!Same(previous, st)) Apply(st);
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { Journal.Write("Konsol durumu işlenemedi: " + ex.Message); }
    }

    private static bool Same(KonsolStatus? a, KonsolStatus? b) =>
        a is null && b is null || a is not null && b is not null && a.State == b.State && a.UpdatedAt == b.UpdatedAt && a.Game?.Title == b.Game?.Title && a.PowerPlanManagedBy == b.PowerPlanManagedBy;

    private void Apply(KonsolStatus? st)
    {
        Journal.Write(st is null ? "KLYC Konsol durumu: dosya yok/okunamadı (Konsol kapalı ya da kurulu değil)"
            : $"KLYC Konsol durumu: {st.State}" + (st.Game is null ? "" : $" ({st.Game.Title}, klasör {(string.IsNullOrEmpty(st.Game.InstallDir) ? "yok" : "var")})") + $", güç planı: {st.PowerPlanManagedBy ?? "bilinmiyor"}, sürüm {st.Version ?? "?"}");
        // Oyun durumu eskiyse (ör. Konsol oyun sırasında çöktü, kimse güncellemedi) güvenilmez: ipucu verme, mod koruyucuyu bırak
        var hint = st is not null && !KonsolLink.IsStale(st, GameStatusMaxAge) ? st.ToHint() : null;
        if (hint is not null)
        {
            if (st!.State == "launching") _ = _auto.NotifyGameLaunching(hint);
            else _auto.NotifyGamePlaying(hint);
            StopHeartbeat();                                  // oyun sırasında Konsol kapalı: Pulse de bir şey yazmaz
            if (st.KonsolOwnsPowerPlan) Hold(); else ReleaseHold();
            return;
        }
        _auto.NotifyKonsolIdle();
        ReleaseHold();
        // Konsol açıksa (süreci yaşıyorsa) kalp atışı sürer: Konsol saatlerce boşta kalıp dosyası eskise bile Pulse "var" sayılsın.
        // Süreç ölüyse Beat kendini durdurur.
        if (st is { IsOpen: true } && KonsolLink.IsProcessAlive(st.Pid, KonsolProcessName))
        {
            StartHeartbeat();
            KonsolLink.WriteFeed(Feed(running: true));        // Konsol yeni açıldı: "Pulse var" hemen anlaşılsın
        }
        else StopHeartbeat();
    }

    // ---- Mod koruyucu: Konsol güç planını yönetirken savaşma -------------------------

    private void Hold()
    {
        if (_hold is not null) return;
        _hold = _holdKeeper();
        Journal.Write("KLYC Konsol güç planını yönetiyor: oyun bitene kadar mod koruyucu bekletildi.");
        // Güvenlik: Konsol'dan oyun bitişi hiç gelmezse koruyucu sonsuza dek susmasın
        _holdExpire?.Dispose();
        _holdExpire = new Timer(_ => { lock (_lock) ReleaseHold(); }, null, GameStatusMaxAge, Timeout.InfiniteTimeSpan);
    }

    private void ReleaseHold()
    {
        _holdExpire?.Dispose(); _holdExpire = null;
        if (_hold is null) return;
        try { _hold.Dispose(); } catch { }
        _hold = null;
        Journal.Write("Mod koruyucu yeniden çalışıyor.");
    }

    // ---- Özet yazma ----------------------------------------------------------

    private void StartHeartbeat() { _heartbeat ??= new Timer(_ => Beat(), null, Heartbeat, Heartbeat); SyncLive(); }

    private void StopHeartbeat() { _heartbeat?.Dispose(); _heartbeat = null; SyncLive(); }

    /// <summary>Canlı değerler (ısı/yük) yalnızca Konsol açıkken ve Konsol isterken (`wantsLive`) okunur; aksi halde sensör aboneliği yoktur (sıfır yük).</summary>
    private void SyncLive()
    {
        bool want;
        lock (_lock) want = _heartbeat is not null && _sensors is not null && _settings.Current.LinkWithKonsol && _status is { WantsLive: true, IsOpen: true };
        if (want && _sensorSub is null) { try { _sensorSub = _sensors!().Subscribe(wantFps: false); } catch (Exception ex) { Journal.Write("Konsol için sensör açılamadı: " + ex.Message); } }
        else if (!want && _sensorSub is not null) { try { _sensorSub.Dispose(); } catch { } _sensorSub = null; }
    }

    private PulseFeedLive? Live()
    {
        try
        {
            if (_sensorSub is null || _sensors is null) return null;
            var s = _sensors().Latest;
            if (s is null || DateTime.Now - s.Time > TimeSpan.FromSeconds(15)) return null;
            return new PulseFeedLive(s.CpuTempC, s.Gpu?.TempC, s.CpuPercent, s.Gpu?.UtilPercent ?? s.GpuEnginePercent, KonsolLink.UnixNow());
        }
        catch { return null; }
    }

    private void Beat()
    {
        KonsolStatus? st; lock (_lock) st = _status;
        // Konsol'un kendi süreci yoksa (kapatıldı/çöktü) kalp atışı gereksiz; özet bayatlar ve Konsol zaten açık değil
        if (st is not { IsOpen: true } || !KonsolLink.IsProcessAlive(st.Pid, KonsolProcessName)) { lock (_lock) StopHeartbeat(); return; }
        KonsolLink.WriteFeed(Feed(running: true));
    }

    private List<PulseFeedAudit>? _audit;
    private DateTime _auditAt = DateTime.MinValue;

    /// <summary>Sistem denetimi sonucu, en çok 10 dakikada bir yenilenir (WMI/kayıt okuması her kalp atışında yapılmasın). Hata olursa eski sonuç ya da boş kalır.</summary>
    private List<PulseFeedAudit>? CachedAudit()
    {
        try
        {
            if (_audit is null || DateTime.UtcNow - _auditAt > TimeSpan.FromMinutes(10))
            {
                _audit = Pulse.Core.Optimize.SystemAudit.Run().Select(f => new PulseFeedAudit(f.Id, f.Severity, f.Title, f.Detail)).ToList();
                _auditAt = DateTime.UtcNow;
            }
        }
        catch (Exception ex) { Journal.Write("Sistem denetimi (Konsol özeti) okunamadı: " + ex.Message); _auditAt = DateTime.UtcNow; }
        return _audit;
    }

    private PulseFeed Feed(bool running)
    {
        PulseFeedCommand? cmd; lock (_lock) cmd = _lastCommand;
        if (cmd is not null && KonsolLink.UnixNow() - cmd.At > 600) cmd = null;   // eski sonuç Konsol'da tekrar tekrar görünmesin
        var accepts = _settings.Current.LinkWithKonsol && _settings.Current.AllowKonsolCommands;
        return KonsolLink.BuildFeed(_version, running, _modes.CurrentKey, _modes.IsBusy, _settings.Current.AutoGameMode, _report.Last ?? LastStoredReport(),
            acceptsCommands: accepts, live: running ? Live() : null, lastCommand: cmd, heatTarget: _settings.Current.HeatTarget, gameProfiles: accepts ? KonsolGameProfiles.ToFeed(_settings.Current.GameProfiles) : null, audit: accepts ? CachedAudit() : null, bgClose: accepts ? KonsolBgClose.Feed(_settings.Current, GameReportStore.LoadHistory()) : null);
    }

    private static GameSessionReport? LastStoredReport()
    {
        try { return GameReportStore.LoadHistory().LastOrDefault(); } catch { return null; }
    }

    /// <summary>Konsol hiç görülmediyse hiçbir şey yazılmaz. Oyun sırasında yazılmaz (rapor hariç).</summary>
    private void WriteFeed(bool duringGame)
    {
        try
        {
            KonsolStatus? st;
            lock (_lock)
            {
                if (!_settings.Current.LinkWithKonsol || !_konsolSeen) return;
                st = _status;
            }
            if (st is { InGame: true } && !duringGame) return;
            KonsolLink.WriteFeed(Feed(running: true));
        }
        catch (Exception ex) { Journal.Write("Konsol özeti hazırlanamadı: " + ex.Message); }
    }

    public void Dispose()
    {
        _modes.Applied -= OnApplied; _modes.Busy -= OnBusy; _modes.Failed -= OnFailed; _report.Ready -= OnReady;
        lock (_lock)
        {
            ReleaseHold(); StopHeartbeat();
            _debounce?.Dispose(); _dirProbe?.Dispose();
            _watcher?.Dispose(); _watcher = null;
            _cmdWatcher?.Dispose(); _cmdWatcher = null; _cmdDebounce?.Dispose();
            try { _sensorSub?.Dispose(); } catch { } _sensorSub = null;
        }
        try { if (_konsolSeen && _settings.Current.LinkWithKonsol) KonsolLink.WriteFeed(Feed(running: false)); } catch { }
    }
}
