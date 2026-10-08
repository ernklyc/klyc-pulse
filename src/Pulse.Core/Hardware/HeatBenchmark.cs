using Pulse.Core.Monitoring;
using Pulse.Core.Platform;

namespace Pulse.Core.Hardware;

/// <summary>
/// Bir hız sınırı aşamasının ölçümü: sınır (null = sınırsız), yük altı ortanca hız, son saniyelerin ortalama ve en yüksek sıcaklığı.
/// <paramref name="WithGpu"/> = aşamada ekran kartı da çalıştırıldı (oyundaki gibi); o zaman ekran kartının ısı ve hızı da ölçülür.
/// </summary>
public sealed record HeatBenchmarkPhase(int? CapMhz, double MedianMhz, double? AvgTempC, double? MaxTempC, bool WithGpu = false, double? GpuAvgTempC = null, double? GpuMedianMhz = null);

/// <summary>
/// "İşlemci hız sınırı sıcaklığı gerçekten düşürüyor mu?" ölçümü. Her aşamada önce işlemcinin dinlenmesini bekler (aynı sıcaklıktan başlansın),
/// sonra tüm çekirdekleri yorar ve son saniyelerin sıcaklık ve hız ortalamasını alır. Ayarları sonunda geri koyar.
/// Sıcaklık okumak için yönetici yetkisi gerekir (yoksa sıcaklık null döner, hız yine ölçülür).
/// </summary>
public static class HeatBenchmark
{
    public static List<HeatBenchmarkPhase> Run(IReadOnlyList<int?> caps, int restSec = 40, int loadSec = 80, IProgress<string>? progress = null, CancellationToken ct = default) =>
        RunPlan(caps.Select(c => (c, false)).ToList(), restSec, loadSec, progress, ct);

    /// <summary>Aşama planı: (sınır, ekran kartı da çalışsın mı). Ekran kartı yükü Edge'deki WebGL sayfasıdır; Edge yoksa o aşamalar atlanır.</summary>
    public static List<HeatBenchmarkPhase> RunPlan(IReadOnlyList<(int? Cap, bool Gpu)> plan, int restSec = 40, int loadSec = 80, IProgress<string>? progress = null, CancellationToken ct = default)
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

            var prevGpu = false;
            foreach (var (cap, gpu) in plan)
            {
                if (ct.IsCancellationRequested) break;
                var label = (cap is null ? "sınırsız" : $"{cap} MHz sınırı") + (gpu ? " + ekran kartı" : "");
                Powercfg.SetFrequencyCap(scheme, cap ?? 0);
                Powercfg.SetActive(scheme);

                // Ekran kartı çalıştıktan sonra kasa daha uzun sıcak kalır: sonraki aşama aynı sıcaklıktan başlasın diye daha uzun dinlenilir
                var rest = prevGpu ? restSec + 30 : restSec;
                progress?.Report($"{label}: dinleniyor ({rest} sn)…");
                Wait(rest, ct);

                progress?.Report($"{label}: tam yük ({loadSec} sn)…");
                GpuLoad? gpuLoad = null;
                if (gpu)
                {
                    try { gpuLoad = GpuLoad.Start(); } catch { gpuLoad = null; }
                    if (gpuLoad is null) { progress?.Report($"{label}: ekran kartı yükü başlatılamadı (Edge yok?), aşama atlandı."); continue; }
                }
                prevGpu = gpu;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var workers = Enumerable.Range(0, Environment.ProcessorCount)
                    .Select(_ => Task.Run(() => { double x = 1; while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1.0001) * 1.0000001; return x; })).ToArray();
                var temps = new List<double>(); var mhz = new List<double>(); var gTemps = new List<double>(); var gMhz = new List<double>();
                var tail = Math.Min(20, Math.Max(3, loadSec / 4));      // son saniyeler: ısı dengeye yaklaşmış olur
                for (var i = 0; i < loadSec && !ct.IsCancellationRequested; i++)
                {
                    Thread.Sleep(1000);
                    if (i < loadSec - tail) continue;
                    var s = hub.Read(cpuTemp: true);
                    if (s.CpuTempC is { } t) temps.Add(t);
                    if (s.CpuMhz is { } f) mhz.Add(f);
                    if (gpu && s.Gpu is { } g)
                    {
                        if (g.TempC is { } gt) gTemps.Add(gt);
                        if (g.CoreMhz is { } gf) gMhz.Add(gf);
                    }
                }
                cts.Cancel();
                Task.WaitAll(workers);
                gpuLoad?.Dispose();

                mhz.Sort(); gMhz.Sort();
                result.Add(new HeatBenchmarkPhase(cap, mhz.Count == 0 ? 0 : mhz[mhz.Count / 2],
                    temps.Count == 0 ? null : Math.Round(temps.Average(), 1), temps.Count == 0 ? null : temps.Max(),
                    gpu, gTemps.Count == 0 ? null : Math.Round(gTemps.Average(), 1), gMhz.Count == 0 ? null : gMhz[gMhz.Count / 2]));
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

    /// <summary>Sonucu sade dille özetler: sınır sıcaklığı kaç derece düşürdü, hız ne kadar kaybetti. Ekran kartı aşamaları ayrı anlatılır.</summary>
    public static string Summarize(IReadOnlyList<HeatBenchmarkPhase> phases)
    {
        if (phases.Count == 0) return "Ölçüm yapılamadı.";
        var cpuOnly = phases.Where(p => !p.WithGpu).ToList();
        var withGpu = phases.Where(p => p.WithGpu).ToList();
        var lines = new List<string>();
        if (cpuOnly.Count > 0) lines.Add(SummarizeGroup(cpuOnly, withGpu.Count > 0 ? "Yalnız işlemci yükü: " : ""));
        if (withGpu.Count > 0)
        {
            lines.Add(SummarizeGroup(withGpu, "İşlemci + ekran kartı (oyundaki gibi): "));
            var g0 = withGpu[0];
            if (g0.GpuAvgTempC is { } gt) lines.Add($"Ekran kartı {gt:0} °C" + (g0.GpuMedianMhz is { } gm ? $", {gm:0} MHz" : "") + " çalıştı.");
            if (cpuOnly.Count > 0 && cpuOnly[0].AvgTempC is { } a && g0.AvgTempC is { } b)
                lines.Add($"Ekran kartı da çalışınca işlemci sınırsızken {(b - a >= 0 ? $"{b - a:0.#} °C daha sıcak" : $"{a - b:0.#} °C daha serin")} ({a:0} → {b:0} °C); oyunda gerçek ısı bu ikinci değere yakındır.");
        }
        return string.Join(" ", lines);
    }

    private static string SummarizeGroup(IReadOnlyList<HeatBenchmarkPhase> phases, string prefix)
    {
        var baseline = phases[0];
        var parts = phases.Select(p =>
        {
            var name = p.CapMhz is null ? "Sınırsız" : $"{p.CapMhz} MHz sınırı";
            var temp = p.AvgTempC is { } t ? $"{t:0} °C (en yüksek {p.MaxTempC:0})" : "sıcaklık okunamadı";
            return $"{name}: hız {p.MedianMhz:0} MHz, ısı {temp}";
        }).ToList();
        var lines = new List<string> { prefix + string.Join(" | ", parts) + "." };
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
