using System.IO;
using Pulse.Core.Automation;
using Pulse.Core.Companion;
using Pulse.Core.Diagnostics;
using Pulse.Core.Localization;
using Pulse.Core.Modes;
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

    private FileSystemWatcher? _watcher;
    private Timer? _debounce, _heartbeat, _dirProbe, _holdExpire;
    private KonsolStatus? _status;
    private IDisposable? _hold;
    private bool _started, _konsolSeen;

    public KonsolLinkService(SettingsStore settings, AutoModeService auto, ModeController modes, GameReportService report, Func<IDisposable> holdKeeper)
    {
        _settings = settings; _auto = auto; _modes = modes; _report = report; _holdKeeper = holdKeeper;
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
        if (_settings.Current.LinkWithKonsol) Attach();
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
        Schedule();
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

    private void StartHeartbeat() => _heartbeat ??= new Timer(_ => Beat(), null, Heartbeat, Heartbeat);

    private void StopHeartbeat() { _heartbeat?.Dispose(); _heartbeat = null; }

    private void Beat()
    {
        KonsolStatus? st; lock (_lock) st = _status;
        // Konsol'un kendi süreci yoksa (kapatıldı/çöktü) kalp atışı gereksiz; özet bayatlar ve Konsol zaten açık değil
        if (st is not { IsOpen: true } || !KonsolLink.IsProcessAlive(st.Pid, KonsolProcessName)) { lock (_lock) StopHeartbeat(); return; }
        KonsolLink.WriteFeed(Feed(running: true));
    }

    private PulseFeed Feed(bool running) =>
        KonsolLink.BuildFeed(_version, running, _modes.CurrentKey, _modes.IsBusy, _settings.Current.AutoGameMode, _report.Last ?? LastStoredReport());

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
        }
        try { if (_konsolSeen && _settings.Current.LinkWithKonsol) KonsolLink.WriteFeed(Feed(running: false)); } catch { }
    }
}
