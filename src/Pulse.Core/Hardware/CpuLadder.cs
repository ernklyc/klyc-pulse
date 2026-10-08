using Pulse.Core.Localization;
namespace Pulse.Core.Hardware;

/// <summary>
/// İşlemci hız sınırı kademeleri, bu bilgisayarın gerçek hızından türetilir (sabit "3,5 GHz" her işlemciye uymaz:
/// bir dizüstü 4,0 GHz'de, bir masaüstü 5,5 GHz'de çalışabilir). Kademeler yük altındaki en yüksek hızın yüzdesidir,
/// taban hızın altına inmez.
/// </summary>
public static class CpuLadder
{
    /// <summary>Oyun profili kademeleri (otomatik ayar ve elle seçim).</summary>
    public static readonly double[] GameFactors = [0.92, 0.85, 0.78, 0.70];

    /// <summary>Sıcaklık sınırı kademeleri (daha ince).</summary>
    public static readonly double[] HeatFactors = [0.92, 0.86, 0.80, 0.74, 0.68, 0.62];     // ilk adım ~%8: bu bilgisayarda ölçüldü, ısının büyük kısmı burada düşer

    /// <summary>Bilinen yük altı tepe hız yoksa taban hızın bir katı kullanılır (turbo'lu işlemcilerde tipik oran).</summary>
    public static double EffectivePeak(double learnedPeakMhz, double baseMhz) =>
        learnedPeakMhz > baseMhz * 1.1 ? learnedPeakMhz : baseMhz * 1.6;

    /// <summary>
    /// Kademe listesi: [null (sınırsız), en gevşek sınır, ..., en sıkı sınır] (MHz, 100'e yuvarlı, azalan).
    /// İşlemcinin turbo payı yoksa (tepe, tabana çok yakınsa) yalnızca [null] döner: sınırlayacak yer yoktur.
    /// </summary>
    public static int?[] Build(double peakMhz, double baseMhz, double[]? factors = null)
    {
        factors ??= GameFactors;
        var floor = (int)(Math.Ceiling((baseMhz + 100) / 100) * 100);                 // tabanın biraz üstü
        var ceiling = (int)(Math.Floor((peakMhz - 200) / 100) * 100);                // tepeden en az 200 MHz aşağı
        var steps = new List<int?> { null };
        var last = int.MaxValue;
        foreach (var f in factors)
        {
            var v = (int)(Math.Round(peakMhz * f / 100.0) * 100);
            v = Math.Max(v, floor);
            if (v > ceiling || v >= last) continue;                                   // anlamsız ya da tekrar
            steps.Add(v);
            last = v;
        }
        return steps.ToArray();
    }

    /// <summary>Kademe adı (arayüz için): "En çok 3,5 GHz (daha serin)".</summary>
    public static string Label(int index, int count, int mhz) =>
        Loc.F("En çok {0:0.0} GHz", mhz / 1000.0) + (index == 1 ? " (hafif)" : index == count - 1 && count > 2 ? " (en serin)" : index >= 2 ? " (daha serin)" : "");
}
