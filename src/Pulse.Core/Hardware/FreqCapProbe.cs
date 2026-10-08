using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;
using Pulse.Core.Platform;

namespace Pulse.Core.Hardware;

/// <summary>
/// Frekans sınırı denemesinin sonucu. <see cref="Supported"/>: true = Windows sınırı uyguluyor, false = yok sayıyor,
/// null = karar verilemedi (işlemci denemede turbo hızına çıkmadı, ör. pilde / güç sınırında).
/// </summary>
public sealed record FreqCapProbeResult(bool? Supported, double UncappedMhz, double CappedMhz, int CapMhz, string Note);

/// <summary>
/// Bu bilgisayarda Windows'un "işlemci en yüksek frekansı" ayarı gerçekten işe yarıyor mu? Tüm çekirdekleri birkaç saniye yorup
/// sınırsız ve sınırlı gerçek hızı ölçer (Ryzen ve bazı HWP sistemlerinde Windows bu ayarı yok sayabilir). Ayarları sonunda geri koyar.
/// Yaklaşık 15-20 saniye sürer ve işlemciyi tam yükler.
/// </summary>
public static class FreqCapProbe
{
    public static FreqCapProbeResult Run(CancellationToken ct = default)
    {
        var scheme = Powercfg.ActiveScheme();
        if (scheme is null) return new(null, 0, 0, 0, "Etkin güç planı okunamadı.");

        string[] keys = [Powercfg.BoostMode, Powercfg.MaxProcessorState, Powercfg.MaxFrequency, Powercfg.MaxFrequencyClass1];
        var saved = keys.ToDictionary(k => k, k => (Ac: Powercfg.GetAc(scheme, Powercfg.SubProcessor, k), Dc: Powercfg.GetDc(scheme, Powercfg.SubProcessor, k)));
        using var hub = new SensorHub();
        void SetBoth(string key, int v) { Powercfg.SetAc(scheme, Powercfg.SubProcessor, key, v); Powercfg.SetDc(scheme, Powercfg.SubProcessor, key, v); }

        double Measure(int capMhz)
        {
            Powercfg.SetFrequencyCap(scheme, capMhz);
            Powercfg.SetActive(scheme);
            Thread.Sleep(600);
            using var cts = new CancellationTokenSource();
            var workers = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(_ => Task.Run(() => { double x = 1; while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1.0001) * 1.0000001; return x; })).ToArray();
            Thread.Sleep(2500);
            var samples = new List<double>();
            for (var i = 0; i < 10 && !ct.IsCancellationRequested; i++) { if (hub.Read(cpuTemp: false).CpuMhz is { } f) samples.Add(f); Thread.Sleep(500); }
            cts.Cancel();
            Task.WaitAll(workers);
            samples.Sort();
            return samples.Count == 0 ? 0 : samples[samples.Count / 2];
        }

        try
        {
            // Turbo açık ve üst sınır %100 olsun ki sınırsız hız gerçekten yüksek çıksın (kullanıcının modundan bağımsız)
            SetBoth(Powercfg.BoostMode, 2);
            SetBoth(Powercfg.MaxProcessorState, 100);
            var uncapped = Measure(0);
            var baseMhz = hub.BaseMhz;
            if (uncapped < baseMhz * 1.1)
                return new(null, uncapped, 0, 0, $"İşlemci denemede taban hızın ({baseMhz:0} MHz) üstüne çıkmadı ({uncapped:0} MHz); güç/ısı sınırı ya da pil olabilir. Sınır denenemedi.");

            Thread.Sleep(3000);
            var cap = (int)(Math.Max(baseMhz + 100, Math.Round(uncapped * 0.75 / 100) * 100));
            var capped = Measure(cap);
            var supported = capped <= cap * 1.1 && capped <= uncapped * 0.92;
            var note = supported
                ? $"Windows frekans sınırını uyguluyor: sınırsız {uncapped:0} MHz → {cap} MHz sınırıyla {capped:0} MHz."
                : $"Windows frekans sınırını uygulamıyor: sınırsız {uncapped:0} MHz, {cap} MHz sınırıyla {capped:0} MHz. Bu bilgisayarda (Ryzen/HWP ya da üretici yazılımı) sınır çalışmıyor.";
            Journal.Write("Frekans sınırı denemesi: " + note);
            return new(supported, uncapped, capped, cap, note);
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
    }
}
