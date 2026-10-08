using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Monitoring;

public sealed record FpsReading(int ProcessId, string ProcessName, double Fps, double FrameMs, double LowFps);

/// <summary>
/// Kare hızı (FPS) ölçümü: Windows'un kendi ETW olayları (DXGI Present), PresentMon'ın yöntemiyle.
/// Oyuna hiçbir şey enjekte etmez, oyunun belleğine dokunmaz. Yönetici yetkisi gerekir. DirectX 10/11/12 oyunları ölçer;
/// Vulkan/OpenGL oyunlarında olay gelmez ve "ölçülemiyor" denir.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int PresentStart = 42;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<int, FrameBuffer> _frames = new();
    private readonly string _sessionName = "KLYC-Pulse-Fps-" + Environment.ProcessId;
    private TraceEventSession? _session;
    private Thread? _thread;

    /// <summary>Başlatılamazsa nedeni (örn. yönetici yetkisi yok).</summary>
    public string? Error { get; private set; }
    public bool IsRunning => _session is not null && Error is null;

    public bool Start()
    {
        if (_session is not null) return true;
        try
        {
            if (!TraceEventSession.IsElevated() ?? true) { Error = "Kare hızı ölçümü için KLYC-Pulse'ın yönetici olarak çalışması gerekir."; return false; }
            _session = new TraceEventSession(_sessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Journal.Write("FPS oturumu bitti: " + ex.Message); }
            }) { IsBackground = true, Name = "FpsEtw" };
            _thread.Start();
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = "Kare hızı ölçümü başlatılamadı: " + ex.Message;
            Journal.Write(Error);
            _session?.Dispose();
            _session = null;
            return false;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        if ((int)e.ID != PresentStart) return;
        var buffer = _frames.GetOrAdd(e.ProcessID, _ => new FrameBuffer());
        buffer.Add(e.TimeStampRelativeMSec);
    }

    // Masaüstünün kendisi ve Pulse oyun sayılmaz.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "dwm", "explorer", "KLYC-Pulse", "csrss", "ShellExperienceHost", "SearchHost" };

    /// <summary>Son birkaç saniyede (verilen ya da en çok kare üreten) oyun benzeri sürecin ölçümü. Yoksa null.</summary>
    public FpsReading? Read(int? preferredPid = null)
    {
        FpsReading? best = null;
        foreach (var (pid, buffer) in _frames)
        {
            var stats = buffer.Stats();
            if (stats is null) continue;
            var (fps, ms, low) = stats.Value;
            string name;
            try { name = Process.GetProcessById(pid).ProcessName; } catch { continue; }
            if (NotGames.Contains(name)) continue;
            var reading = new FpsReading(pid, name, fps, ms, low);
            if (preferredPid == pid) return reading;
            if (best is null || reading.Fps > best.Fps) best = reading;
        }
        return best;
    }

    /// <summary>Tüm süreçlerin son ölçümleri (sınama ve ayrıntı için).</summary>
    public IReadOnlyList<FpsReading> ReadAll()
    {
        var list = new List<FpsReading>();
        foreach (var (pid, buffer) in _frames)
        {
            if (buffer.Stats() is not { } s) continue;
            try { list.Add(new FpsReading(pid, Process.GetProcessById(pid).ProcessName, s.Fps, s.FrameMs, s.LowFps)); } catch { }
        }
        return list.OrderByDescending(r => r.Fps).ToList();
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    /// <summary>Bir sürecin son 5 saniyedeki kare zamanları.</summary>
    private sealed class FrameBuffer
    {
        private readonly object _gate = new();
        private readonly Queue<double> _times = new();
        private double _lastArrivalMs;

        public void Add(double ms)
        {
            lock (_gate)
            {
                _lastArrivalMs = ClockMs();
                _times.Enqueue(ms);
                while (_times.Count > 0 && ms - _times.Peek() > Window.TotalMilliseconds) _times.Dequeue();
            }
        }

        /// <summary>(ortalama FPS, ortalama kare süresi ms, %1 düşük FPS). Yeterli veri yoksa null.</summary>
        public (double Fps, double FrameMs, double LowFps)? Stats()
        {
            double[] t; double arrival;
            lock (_gate) { t = _times.ToArray(); arrival = _lastArrivalMs; }
            if (t.Length < 10) return null;
            // Son kare 1.5 sn'den eskiyse oyun artık kare üretmiyor
            if (ClockMs() - arrival > 1500) return null;
            var intervals = new double[t.Length - 1];
            for (var i = 1; i < t.Length; i++) intervals[i - 1] = t[i] - t[i - 1];
            Array.Sort(intervals);
            var avg = (t[^1] - t[0]) / intervals.Length;
            var worst = intervals[Math.Min(intervals.Length - 1, (int)Math.Ceiling(intervals.Length * 0.99) - 1)];
            return (1000.0 / avg, avg, 1000.0 / worst);
        }
    }

    // Karelerin geliş zamanı yerel saatle tutulur (ETW zamanı oturum başlangıcına göredir, ikisi karıştırılmaz).
    private static double ClockMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
