using Pulse.Core.Diagnostics;

namespace Pulse.Core.Automation;

/// <summary>Otomatik ayarın kararı. <see cref="Changed"/> true ise <see cref="CapMhz"/> profile yazılır.</summary>
public sealed record TuneDecision(bool Changed, int? CapMhz, bool Locked, string Note);

/// <summary>
/// Oyuna özel kendi kendine ayar (saf karar mantığı, test edilebilir). Her oyun oturumundan sonra raporu inceler:
/// ısı yüksek ve oyunu ekran kartı/kare sınırı belirliyorsa işlemci hız sınırını bir kademe düşürür; sonraki oturumda
/// ısı ve FPS'i önceki oturumla karşılaştırır. FPS belirgin düştüyse (ya da ekran kartı boşa çıktıysa) ayarı geri alır ve
/// kilitler; yararlıysa ve ısı hâlâ yüksekse bir kademe daha iner. Kısa oturumlarda karar vermez.
/// </summary>
public static class GameAutoTuner
{
    public const double MinMinutes = 8;
    public const double MaxFpsLoss = 0.07;        // %7'den fazla FPS kaybı zararlı sayılır
    public const double MaxGpuUtilDrop = 8;       // FPS ölçülemiyorsa: ekran kartı kullanımı 8 puandan fazla düştüyse işlemci darboğaz olmuş demektir

    public static bool IsHot(GameSessionReport r) => r.CpuAbove90Percent >= 40 || r.CpuAbove95Percent >= 10;

    /// <param name="ladder">Bu bilgisayarın kademeleri (<see cref="Hardware.CpuLadder.Build"/>): [null, en gevşek, ..., en sıkı].</param>
    public static TuneDecision Decide(int? currentCap, bool locked, GameSessionReport cur, GameSessionReport? prev, int?[] ladder)
    {
        if (ladder.Length < 2) return Same(currentCap, true, "Bu işlemcide hızı sınırlamaya yetecek turbo payı yok; otomatik ayar yapılmadı.");
        if (locked) return Same(currentCap, true, "Bu oyun için ayar öğrenildi ve kilitli (Otomatik ayarı kapatıp açarsan yeniden öğrenir).");
        if (cur.Minutes < MinMinutes) return Same(currentCap, false, $"Oturum kısa ({cur.Minutes:0} dk); karar için en az {MinMinutes:0} dakika gerekir.");

        var hot = IsHot(cur);
        var idx = Array.IndexOf(ladder, currentCap);
        if (idx < 0) return Same(currentCap, false, "Elle seçilmiş bir sınır var; otomatik ayar dokunmadı.");

        // 1) Önceki oturumdan beri sınır değiştiyse: etkisini ölç
        if (prev is not null && string.Equals(prev.Game, cur.Game, StringComparison.OrdinalIgnoreCase) && prev.CpuCapMhz != cur.CpuCapMhz)
        {
            double? fpsLoss = prev.AvgFps is > 0 && cur.AvgFps is > 0 ? (prev.AvgFps - cur.AvgFps) / prev.AvgFps : null;
            double? gpuDrop = prev.GpuUtilAvg is { } pg && cur.GpuUtilAvg is { } cg ? pg - cg : null;
            var tempGain = prev.CpuTempAvg is { } pt && cur.CpuTempAvg is { } ct ? pt - ct : (double?)null;

            var harmful = fpsLoss is { } fl ? fl > MaxFpsLoss : gpuDrop is { } gd && gd >= MaxGpuUtilDrop;
            if (harmful)
            {
                var why = fpsLoss is { } l ? $"FPS %{l * 100:0} düştü" : $"ekran kartı kullanımı {gpuDrop:0} puan düştü";
                return new TuneDecision(prev.CpuCapMhz != currentCap, prev.CpuCapMhz, true,
                    $"Sınır {Describe(currentCap)} iken {why}; önceki ayara ({Describe(prev.CpuCapMhz)}) dönüldü. Bu oyun işlemciye bağlı, ayar kilitlendi.");
            }

            if (fpsLoss is null && gpuDrop is null)
                return Same(currentCap, true, "FPS ve ekran kartı verisi alınamadı; etkisi ölçülemediği için daha ileri gidilmedi.");

            var gain = tempGain is { } g ? $" Isı {g:0.#} °C {(g >= 0 ? "düştü" : "arttı")}, FPS kaybı {(fpsLoss is { } f ? Math.Max(0, f * 100).ToString("0") + "%" : "ölçülemedi")}." : "";
            if (hot && idx < ladder.Length - 1)
            {
                var next = ladder[idx + 1];
                return new TuneDecision(true, next, false, $"Ayar zararsız ama ısı hâlâ yüksek;{gain} işlemci bir kademe daha, {Describe(next)}'e indirildi.");
            }
            return Same(currentCap, true, hot
                ? $"En düşük kademeye ulaşıldı ama ısı hâlâ yüksek;{gain} bundan sonrası için donanım soğutması (temizlik, macun, altlık) gerekir."
                : $"Ayar işe yaradı, ısı normale döndü;{gain} ayar kilitlendi.");
        }

        // 2) Değişiklik yok / ilk oturum: ısıya bak
        if (!hot) return Same(currentCap, false, "Isı normal; ayar gerekmedi.");

        return cur.Bottleneck switch
        {
            "gpu" or "other" => idx + 1 < ladder.Length ? StepDown(currentCap, idx, ladder, cur) : Same(currentCap, true, "En düşük kademede ve ısı yüksek; donanım soğutması gerekir."),
            "cpu" => Same(currentCap, false, "Isı yüksek ama oyunu işlemci sınırlıyor; sınırlamak FPS kaybettirir, otomatik ayar yapılmadı. Soğutma (temizlik, macun, altlık) önerilir."),
            _ => Same(currentCap, false, "Isı yüksek ama oyunu neyin sınırladığı belirlenemedi; otomatik ayar yapılmadı."),
        };
    }

    private static TuneDecision StepDown(int? currentCap, int idx, int?[] ladder, GameSessionReport cur)
    {
        // Sınırsızdan en hafif anlamlı kademeyle (~%92 hız) başla: bu bilgisayarda ölçüldü, %8 hız kaybıyla ısı ~12 °C düştü; ikinci kademe fazladan ısı kazandırmadı
        var next = ladder[idx + 1];
        return new TuneDecision(true, next, false,
            $"Isı yüksekti (sürenin %{cur.CpuAbove90Percent}'ında 90 °C üstü) ve oyunu {(cur.Bottleneck == "gpu" ? "ekran kartı" : "kare sınırı")} belirliyor: işlemci en çok {Describe(next)}'e ayarlandı. " +
            "Sonraki oyunda ısı ve FPS karşılaştırılır; FPS düşerse geri alınır.");
    }

    private static TuneDecision Same(int? cap, bool locked, string note) => new(false, cap, locked, note);

    public static string Describe(int? cap) => cap is null ? "sınırsız" : $"{cap / 1000.0:0.0} GHz";
}
