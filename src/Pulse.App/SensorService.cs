using System.Runtime.InteropServices;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Canlı sensörleri saniyede bir okuyup abonelere yayar (İzleme sayfası, oyun üstü gösterge). Abone yokken hiç çalışmaz.
/// İşlemci sıcaklığı (WMI) pahalı olduğundan 3 saniyede bir okunur, aradaki değerler korunur.
/// Abone varken ve yönetici yetkisi varsa kare hızı (ETW) ölçümü de açılır.
/// </summary>
public sealed class SensorService : IDisposable
{
    // Sensör nesnesi (performans sayaçları, NVML, ASUS sürücüsü) oluşturması saniyeler sürebilir; arayüzü bekletmesin diye arka planda kurulur.
    private volatile SensorHub? _hub;
    private Task? _init;
    private int _ticking;
    private readonly object _gate = new();
    private Timer? _timer;
    private FpsMonitor? _fps;
    private int _users;
    private int _fpsUsers;
    private int _tick;
    private double? _lastTemp;

    public SensorSnapshot? Latest { get; private set; }
    public FpsReading? LatestFps { get; private set; }
    /// <summary>Kare hızı ölçümü çalışmıyorsa nedeni (yönetici yetkisi yok vb.).</summary>
    public string? FpsError { get; private set; }
    public double BaseMhz => _hub?.BaseMhz ?? 2500;
    public bool HasGpu => _hub?.HasGpu ?? true;

    /// <summary>Zamanlayıcı iş parçacığında tetiklenir; arayüze geçmek aboneye aittir.</summary>
    public event Action<SensorSnapshot>? Updated;

    /// <summary>wantFps true ise kare hızı (ETW) ölçümü de açılır; arka planda sürekli çalışan abonelerde kapalı tutulur.</summary>
    public IDisposable Subscribe(bool wantFps = true)
    {
        lock (_gate)
        {
            if (++_users == 1)
            {
                _init ??= Task.Run(() => { try { _hub = new SensorHub(); } catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Sensörler kurulamadı: " + ex.Message); } });
                _timer = new Timer(_ => Tick(), null, 500, 1000);
            }
            if (wantFps && ++_fpsUsers == 1)
            {
                _fps = new FpsMonitor();
                _fps.Start();
                FpsError = _fps.Error;
            }
        }
        return new Release(this, wantFps);
    }

    private void Unsubscribe(bool hadFps)
    {
        lock (_gate)
        {
            if (hadFps && --_fpsUsers == 0)
            {
                _fps?.Dispose();
                _fps = null;
                LatestFps = null;
            }
            if (--_users > 0) return;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        var hub = _hub;
        if (hub is null) return;                                          // henüz kurulmadı
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;           // önceki okuma bitmedi: üst üste binme
        try
        {
            var readTemp = _tick++ % 3 == 0;
            var s = hub.Read(readTemp);
            if (readTemp) _lastTemp = s.CpuTempC; else s = s with { CpuTempC = _lastTemp };
            Latest = s;
            LatestFps = _fps is { IsRunning: true } fps ? fps.Read(ForegroundProcessId()) : null;
            Updated?.Invoke(s);
        }
        catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Sensör okuma hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; _fps?.Dispose(); _fps = null; }
        var hub = _hub;
        _hub = null;                                                    // yeni okuma başlamasın
        for (var i = 0; i < 20 && Volatile.Read(ref _ticking) == 1; i++) Thread.Sleep(50);   // süren okuma bitsin
        hub?.Dispose();
    }

    private sealed class Release(SensorService owner, bool hadFps) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Unsubscribe(hadFps); }
    }
}
