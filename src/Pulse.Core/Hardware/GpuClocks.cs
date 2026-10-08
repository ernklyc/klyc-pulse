using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;

namespace Pulse.Core.Hardware;

public sealed record GpuCapResult(bool Ok, bool Verified, string Message);

/// <summary>
/// Ekran kartı hız sınırı (G-Helper'ın "GPU saat sınırı"nın karşılığı). NVIDIA'nın resmi NVML arayüzüyle çekirdek
/// saatini bir üst sınıra kilitler: daha az ısı, fan sesi ve güç, daha uzun ömür. Sınırı kaldırmak sürücü varsayılanına döner.
/// Sınır doğrulaması: önce kısa bir "sabit saat" sınama kilidiyle saatin gerçekten o değere gittiği okunur, sonra sınırlı aralığa geçilir.
/// </summary>
public static class GpuClocks
{
    public static GpuCapResult Cap(int maxMhz)
    {
        var nvml = Nvml.TryOpen();
        if (nvml is null) return new(false, false, "NVIDIA sürücüsü bulunamadı.");
        var supported = nvml.SupportedCoreClocks();
        if (supported.Count == 0) return new(false, false, "Desteklenen saat listesi okunamadı.");

        var floor = supported.Min();
        var cap = supported.Where(c => c <= maxMhz).DefaultIfEmpty(supported.Min()).Max();   // desteklenen en yakın alt değer

        // Sınama: min=max kilidi saatin gerçekten bu değere gittiğini gösterir.
        if (!nvml.LockCoreClocks(cap, cap)) return new(false, false, "Sürücü saat kilidini kabul etmedi (yönetici gerekir ya da bu kart desteklemiyor).");
        Thread.Sleep(800);
        var probe = nvml.Read().CoreMhz;
        var follows = probe is { } p && Math.Abs(p - cap) <= Math.Max(30, cap * 0.05);

        // Kalıcı sınır: boşta düşük saate inebilsin diye alt sınır en düşük destekli değer.
        if (!nvml.LockCoreClocks(floor, cap)) return new(false, false, "Sınır aralığı yazılamadı.");
        Journal.Write($"Ekran kartı hız sınırı {cap} MHz (istenen {maxMhz}); sınama saati {probe}.");
        return follows
            ? new(true, true, $"Saat sınırı {cap} MHz. Doğrulandı: sınama kilidinde saat {probe} MHz'e gitti.")
            : new(true, false, $"Saat sınırı {cap} MHz kabul edildi ama sınama saati {probe} MHz okundu; boşta olduğu için doğrulanamadı olabilir.");
    }

    public static GpuCapResult Release()
    {
        var nvml = Nvml.TryOpen();
        if (nvml is null) return new(false, false, "NVIDIA sürücüsü bulunamadı.");
        // Kilit yokken NVML "sıfırlama" hata dönebiliyor (gerçek donanımda görüldü). Sonuç her durumda "kilitsiz" olsun:
        // sıfırlama olmadıysa önce geçerli bir aralık kilitleyip sonra sıfırla; ikinci sıfırlama başarılıysa kart kilitsizdir.
        var ok = nvml.ResetCoreClocks();
        if (!ok)
        {
            var clocks = nvml.SupportedCoreClocks();
            if (clocks.Count > 0 && nvml.LockCoreClocks(clocks.Min(), clocks.Max())) ok = nvml.ResetCoreClocks();
        }
        Journal.Write("Ekran kartı hız sınırı kaldırıldı: " + ok);
        return ok ? new(true, true, "Saat sınırı kaldırıldı (sürücü varsayılanı).") : new(false, false, "Saat sınırı kaldırılamadı (yönetici gerekir).");
    }
}