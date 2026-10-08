using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.Core.Cleanup;

/// <summary>
/// Haftada bir, yalnızca "tamamen zararsız" işaretli kategorileri (eski geçici dosyalar, hata raporları) kendiliğinden temizler.
/// Kullanıcı ayarlardan özelliği açtıysa çalışır. Oyun modundayken ve yönetici yetkisi yokken çalışmaz.
/// Kullanıcının dosyalarına, uygulama verilerine, indirilenlere, geri dönüşüm kutusuna dokunmaz.
/// </summary>
public sealed class AutoCleanService : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    private readonly SettingsStore _settings;
    private readonly ModeController _controller;
    private readonly CleanupEngine _engine = new();
    private readonly Timer _timer;
    private int _running;

    /// <summary>Temizlik bittiğinde: kazanılan bayt.</summary>
    public event Action<long>? Completed;

    public AutoCleanService(SettingsStore settings, ModeController controller)
    {
        _settings = settings;
        _controller = controller;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    /// <summary>Şimdi çalışması gerekir mi? (Test edilebilir saf mantık.)</summary>
    public static bool IsDue(AppSettings s, DateTime now, bool isAdmin, string? currentMode) =>
        s.AutoClean && isAdmin && currentMode != Modes.Modes.Game
        && (s.LastAutoClean is null || now - s.LastAutoClean.Value >= Interval);

    private void Tick()
    {
        if (!IsDue(_settings.Current, DateTime.Now, CleanupEngine.IsAdmin, _controller.CurrentKey)) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var categories = CleanupCatalog.Build().Where(c => c.AutoSafe).ToList();
            var results = _engine.CleanAsync(categories).GetAwaiter().GetResult();
            var freed = results.Sum(r => r.FreedBytes);
            _settings.Current.LastAutoClean = DateTime.Now;
            _settings.Current.LastAutoCleanBytes = freed;
            _settings.Save();
            Journal.Write($"Otomatik temizlik tamam: {freed} bayt ({string.Join(", ", categories.Select(c => c.Name))}).");
            Completed?.Invoke(freed);
        }
        catch (Exception ex) { Journal.Write("Otomatik temizlik hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
