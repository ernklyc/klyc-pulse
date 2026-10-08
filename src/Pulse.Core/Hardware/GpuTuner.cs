using Pulse.Core.Localization;
using System.Diagnostics;
using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;

namespace Pulse.Core.Hardware;

public sealed record TuneStep(int Core, int Mem, double Fps, double CoreMhz, double MemMhz, double MaxTempC, double PowerW, bool Stable, string Note);
public sealed record TuneResult(IReadOnlyList<TuneStep> Steps, int BestCore, int BestMem, string Summary);

/// <summary>
/// Ekran kartı hızlandırmanı kendisi arayarak bulur: bir GPU yükü (Edge'de ağır bir WebGL sayfası) çalıştırır, ofseti kademe kademe
/// artırır; her kademede kare hızını, saatleri, sıcaklığı ve sürücü hatalarını ölçer. Kare hızı artmayı bırakınca, sıcaklık sınırı
/// aşılınca ya da sürücü hata verince durur ve en iyi kararlı kademeyi seçer. Bittiğinde ekran kartı fabrika hızına döner.
/// Görüntü bozulması (artifact) görülemez; bu yüzden tavan düşük tutulur ve sonuç yalnızca bir öneridir.
/// </summary>
public static class GpuTuner
{
    private static readonly (int Core, int Mem)[] Ladder = [(0, 0), (50, 250), (100, 500), (150, 700)];
    private const double HotLimitC = 84;
    private const double AbortC = 87;
    private const double MinGainPercent = 1.5;

    public static async Task<TuneResult> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<TuneStep>();
        var nvml = Nvml.TryOpen();
        if (nvml is null) return new(steps, 0, 0, Loc.T("NVIDIA ekran kartı bulunamadı."));
        if (GpuOverclock.Read() is not { Editable: true }) return new(steps, 0, 0, Loc.T("Bu ekran kartında hız ayarı kapalı."));

        GpuLoad? load = null;
        var tuneStart = DateTime.Now;
        try
        {
            load = GpuLoad.Start();
            if (load is null) return new(steps, 0, 0, Loc.T("Yük üretmek için Microsoft Edge bulunamadı."));
            progress?.Report(Loc.T("Yük sayfası açılıyor…"));
            await Task.Delay(9000, ct);   // pencere + sayfanın yükü kendi ayarlaması (ısınma)

            double bestFps = 0;
            var best = Ladder[0];
            foreach (var (core, mem) in Ladder)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(core == 0 ? Loc.T("Fabrika hızı ölçülüyor…") : Loc.F("Deneniyor: çekirdek +{0}, bellek +{1} MHz…", core, mem));
                var apply = GpuOverclock.Apply(core, mem);
                if (!apply.Ok || !apply.Verified) { steps.Add(new(core, mem, 0, 0, 0, 0, 0, false, Loc.T("Sürücü ofseti kabul etmedi"))); break; }
                await Task.Delay(4000, ct);

                var step = await Measure(nvml, core, mem, tuneStart, ct);
                steps.Add(step);
                Journal.Write($"GPU ayar denemesi {core}/{mem}: fps {step.Fps:0.0}, çekirdek {step.CoreMhz:0}, bellek {step.MemMhz:0}, ısı {step.MaxTempC:0}, kararlı {step.Stable} ({step.Note})");
                if (!step.Stable) break;

                var gain = bestFps > 0 ? (step.Fps - bestFps) / bestFps * 100 : 0;
                if (core == 0) { bestFps = step.Fps; continue; }
                if (gain < MinGainPercent) { steps[^1] = step with { Note = Loc.F("Kazanç %{0:0.0}, artık değmiyor", gain) }; break; }
                bestFps = step.Fps;
                best = (core, mem);
            }

            var summary = best == (0, 0)
                ? Loc.T("Hız aşırtma kayda değer kazanç vermedi (güç ya da ısı sınırı). Fabrika hızı en iyisi.")
                : Loc.F("En iyi kararlı ayar: çekirdek +{0}, bellek +{1} MHz. Fabrika hızına göre kare hızı %{2:0.0} daha yüksek.", best.Core, best.Mem, (bestFps / Math.Max(steps[0].Fps, 0.1) - 1) * 100);
            return new(steps, best.Core, best.Mem, summary);
        }
        finally
        {
            GpuOverclock.Reset();
            load?.Dispose();
        }
    }

    private static async Task<TuneStep> Measure(Nvml nvml, int core, int mem, DateTime since, CancellationToken ct)
    {
        var fps = new List<double>();
        double coreSum = 0, memSum = 0, powerSum = 0, maxTemp = 0;
        var n = 0;
        var hotSamples = 0;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, ct);
            var r = nvml.Read();
            n++;
            coreSum += r.CoreMhz ?? 0; memSum += r.MemMhz ?? 0; powerSum += r.PowerW ?? 0;
            maxTemp = Math.Max(maxTemp, r.TempC ?? 0);
            if ((r.ThrottleReasons & (0x20 | 0x40 | 0x8 | 0x80)) != 0) hotSamples++;
            if (GpuLoad.ReadFps() is { } f) fps.Add(f);
            if (maxTemp >= AbortC) break;
        }

        var note = "";
        var stable = true;
        if (HadDriverReset(since)) { stable = false; note = Loc.T("Ekran sürücüsü hata verdi (TDR)"); }
        else if (GpuLoad.Lost()) { stable = false; note = Loc.T("Grafik kartı yanıt vermedi"); }
        else if (fps.Count < 5) { stable = false; note = Loc.T("Kare hızı ölçülemedi"); }
        else if (maxTemp >= HotLimitC) { stable = false; note = Loc.F("Sıcaklık sınırı aşıldı ({0:0} °C)", maxTemp); }
        else if (hotSamples > n * 0.3) { stable = false; note = Loc.T("Isı ya da donanım kısıtlaması sürekli devrede"); }

        var avgFps = fps.Count > 0 ? fps.Skip(fps.Count / 4).Average() : 0;   // ilk çeyrek ısınma, atılır
        return new(core, mem, avgFps, coreSum / Math.Max(n, 1), memSum / Math.Max(n, 1), maxTemp, powerSum / Math.Max(n, 1), stable, note);
    }

    // ---- Yük sayfası (kare hızını pencere başlığına yazar) -------------------

    private static bool HadDriverReset(DateTime since)
    {
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[(EventID=4101) and TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000Z}']]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            return reader.ReadEvent() is not null;
        }
        catch { return false; }
    }
}
