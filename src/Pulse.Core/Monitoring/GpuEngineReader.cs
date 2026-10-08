using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pulse.Core.Monitoring;

/// <summary>
/// Ekran kartı 3B kullanımı (%), Windows'un kendi "GPU Engine" sayaçlarından: NVIDIA, AMD ve Intel'de aynı şekilde çalışır
/// (Görev Yöneticisi'nin 3B grafiğiyle aynı kaynak). NVML yokken yedek olarak kullanılır. Birden çok ekran kartı varsa
/// en yoğun olanı verir. Hiçbir ayarı değiştirmez.
/// </summary>
public sealed class GpuEngineReader : IDisposable
{
    private static readonly Regex AdapterRx = new(@"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+)_phys_(\d+)", RegexOptions.Compiled);
    private readonly Dictionary<string, PerformanceCounter> _counters = new();
    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>En yoğun ekran kartının 3B kullanımı (0-100). Sayaç yoksa null.</summary>
    public double? ReadMaxAdapterPercent()
    {
        try
        {
            if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(8)) Refresh();
            if (_counters.Count == 0) return null;

            var perAdapter = new Dictionary<string, double>();
            List<string>? dead = null;
            foreach (var (name, c) in _counters)
            {
                try
                {
                    var m = AdapterRx.Match(name);
                    var key = m.Success ? m.Value : "?";
                    perAdapter[key] = perAdapter.GetValueOrDefault(key) + c.NextValue();
                }
                catch { (dead ??= new()).Add(name); }       // süreç kapanınca örnek kaybolur
            }
            if (dead is not null) foreach (var d in dead) { _counters[d].Dispose(); _counters.Remove(d); }
            return perAdapter.Count == 0 ? null : Math.Min(100, Math.Round(perAdapter.Values.Max(), 0));
        }
        catch { return null; }
    }

    private void Refresh()
    {
        _lastRefresh = DateTime.UtcNow;
        var cat = new PerformanceCounterCategory("GPU Engine");
        var names = cat.GetInstanceNames().Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToHashSet();
        foreach (var gone in _counters.Keys.Where(k => !names.Contains(k)).ToList()) { _counters[gone].Dispose(); _counters.Remove(gone); }
        foreach (var n in names.Where(n => !_counters.ContainsKey(n)))
        {
            try
            {
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true);
                c.NextValue();                                         // ilk okuma 0 döner; sonrakiler gerçek değer
                _counters[n] = c;
            }
            catch { /* örnek bu arada kapanmış */ }
        }
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}
