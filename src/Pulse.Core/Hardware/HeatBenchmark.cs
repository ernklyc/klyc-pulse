using Pulse.Core.Monitoring;
using Pulse.Core.Platform;

namespace Pulse.Core.Hardware;

/// <summary>Bir hız sınırı aşamasının ölçümü: sınır (null = sınırsız), yük altı ortanca hız, son saniyelerin ortalama ve en yüksek sıcaklığı.</summary>
public sealed record HeatBenchmarkPhase(int? CapMhz, double MedianMhz, double? AvgTempC, double? MaxTempC);

/// <summary>
/// "İşlemci hız sınırı sıcaklığı gerçekten düşürüyor mu?" ölçümü. Her aşamada önce işlemcinin dinlenmesini bekler (aynı sıcaklıktan başlansın),
/// sonra tüm çekirdekleri yorar ve son saniyelerin sıcaklık ve hız ortalamasını alır. Ayarları sonunda geri koyar.
/// Sıcaklık okumak için yönetici yetkisi gerekir (yoksa sıcaklık null döner, hız yine ölçülür).
/// </summary>
public static class HeatBenchmark
{
    public static List<HeatBenchmarkPhase> Run(IReadOnlyList<int?> caps, int restSec = 40, int loadSec = 80, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var scheme = Powercfg.ActiveScheme() ?? throw new InvalidOperationException("Etkin güç planı okunamadı.");
        string[] keys = [Powercfg.BoostMode, Powercfg.MaxProcessorState, Powercfg.MaxFrequency, Powercfg.MaxFrequencyClass1];
        var saved = keys.ToDictionary(k => k, k => (Ac: Powercfg.GetAc(scheme, Powercfg.SubProcessor, k), Dc: Powercfg.GetDc(scheme, Powercfg.SubProcessor, k)));
        using var hub = new SensorHub();
        var result = new List<HeatBenchmarkPhase>();

        try
        {
            Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.BoostMode, 2); Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.BoostMode, 2);
            Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, 100); Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, 100);

            foreach (var cap in caps)
            {
                if (ct.IsCancellationRequested) break;
                var label = cap is null ? "sınırsız" : $"{cap} MHz sınırı";
                Powercfg.SetFrequencyCap(scheme, cap ?? 0);
                Powercfg.SetActive(scheme);

                progress?.Report($"{label}: dinleniyor ({restSec} sn)…");
                Wait(restSec, ct);

                progress?.Report($"{label}: tam yük ({loadSec} sn)…");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var workers = Enumerable.Range(0, Environment.ProcessorCount)
                    .Select(_ => Task.Run(() => { double x = 1; while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1.0001) * 1.0000001; return x; })).ToArray();
                var temps = new List<double>(); var mhz = new List<double>();
                var tail = Math.Min(20, Math.Max(3, loadSec / 4));      // son saniyeler: ısı dengeye yaklaşmış olur
                for (var i = 0; i < loadSec && !ct.IsCancellationRequested; i++)
                {
                    Thread.Sleep(1000);
                    if (i < loadSec - tail) continue;
                    var s = hub.Read(cpuTemp: true);
                    if (s.CpuTempC is { } t) temps.Add(t);
                    if (s.CpuMhz is { } f) mhz.Add(f);
                }
                cts.Cancel();
                Task.WaitAll(workers);

                mhz.Sort();
                result.Add(new HeatBenchmarkPhase(cap, mhz.Count == 0 ? 0 : mhz[mhz.Count / 2],
                    temps.Count == 0 ? null : Math.Round(temps.Average(), 1), temps.Count == 0 ? null : temps.Max()));
            }
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                if (v.Ac is { } a) Powercfg.SetAc(scheme, Powercfg.SubProcessor, k, a);
                if (v.Dc is { } d) Powercfg.SetDc(scheme, Powercfg.SubProcessor, k, d);
            }
            Powercfg.SetActive(scheme);
        }
        return result;
    }

    private static void Wait(int sec, CancellationToken ct)
    {
        for (var i = 0; i < sec && !ct.IsCancellationRequested; i++) Thread.Sleep(1000);
    }

    /// <summary>Sonucu sade dille özetler: sınır sıcaklığı kaç derece düşürdü, hız ne kadar kaybetti.</summary>
    public static string Summarize(IReadOnlyList<HeatBenchmarkPhase> phases)
    {
        if (phases.Count == 0) return "Ölçüm yapılamadı.";
        var baseline = phases[0];
        var parts = phases.Select(p =>
        {
            var name = p.CapMhz is null ? "Sınırsız" : $"{p.CapMhz} MHz sınırı";
            var temp = p.AvgTempC is { } t ? $"{t:0} °C (en yüksek {p.MaxTempC:0})" : "sıcaklık okunamadı";
            return $"{name}: hız {p.MedianMhz:0} MHz, ısı {temp}";
        }).ToList();
        var lines = new List<string> { string.Join(" | ", parts) };
        foreach (var p in phases.Skip(1))
        {
            if (baseline.AvgTempC is { } b && p.AvgTempC is { } t && baseline.MedianMhz > 0)
            {
                var speed = (1 - p.MedianMhz / baseline.MedianMhz) * 100;
                lines.Add($"{p.CapMhz} MHz sınırı: ısı {b - t:0.#} °C {(b - t >= 0 ? "düştü" : "arttı")}, hız %{speed:0} azaldı.");
            }
        }
        return string.Join(" ", lines);
    }
}
