using Pulse.Core.Localization;
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
        if (ladder.Length < 2) return Same(currentCap, true, Loc.T("Bu işlemcide hızı sınırlamaya yetecek turbo payı yok; otomatik ayar yapılmadı."));
        if (locked) return Same(currentCap, true, Loc.T("Bu oyun için ayar öğrenildi ve kilitli (Otomatik ayarı kapatıp açarsan yeniden öğrenir)."));
        if (cur.Minutes < MinMinutes) return Same(currentCap, false, Loc.F("Oturum kısa ({0:0} dk); karar için en az {1:0} dakika gerekir.", cur.Minutes, MinMinutes));

        var hot = IsHot(cur);
        var idx = Array.IndexOf(ladder, currentCap);
        if (idx < 0) return Same(currentCap, false, Loc.T("Elle seçilmiş bir sınır var; otomatik ayar dokunmadı."));

        // 1) Önceki oturumdan beri sınır değiştiyse: etkisini ölç
        if (prev is not null && string.Equals(prev.Game, cur.Game, StringComparison.OrdinalIgnoreCase) && prev.CpuCapMhz != cur.CpuCapMhz)
        {
            double? fpsLoss = prev.AvgFps is > 0 && cur.AvgFps is > 0 ? (prev.AvgFps - cur.AvgFps) / prev.AvgFps : null;
            double? gpuDrop = prev.GpuUtilAvg is { } pg && cur.GpuUtilAvg is { } cg ? pg - cg : null;
            var tempGain = prev.CpuTempAvg is { } pt && cur.CpuTempAvg is { } ct ? pt - ct : (double?)null;

            var harmful = fpsLoss is { } fl ? fl > MaxFpsLoss : gpuDrop is { } gd && gd >= MaxGpuUtilDrop;
            if (harmful)
            {
                var why = fpsLoss is { } l ? Loc.F("FPS %{0:0} düştü", l * 100) : Loc.F("ekran kartı kullanımı {0:0} puan düştü", gpuDrop);
                return new TuneDecision(prev.CpuCapMhz != currentCap, prev.CpuCapMhz, true,
                    Loc.F("Sınır {0} iken {1}; önceki ayara ({2}) dönüldü. Bu oyun işlemciye bağlı, ayar kilitlendi.", Describe(currentCap), why, Describe(prev.CpuCapMhz)));
            }

            if (fpsLoss is null && gpuDrop is null)
                return Same(currentCap, true, Loc.T("FPS ve ekran kartı verisi alınamadı; etkisi ölçülemediği için daha ileri gidilmedi."));

            var gain = tempGain is { } g ? Loc.F(" Isı {0:0.#} °C {1}, FPS kaybı {2}.", g, Loc.T(g >= 0 ? "düştü" : "arttı"), fpsLoss is { } f ? Math.Max(0, f * 100).ToString("0") + "%" : Loc.T("ölçülemedi")) : "";
            if (hot && idx < ladder.Length - 1)
            {
                var next = ladder[idx + 1];
                return new TuneDecision(true, next, false, Loc.F("Ayar zararsız ama ısı hâlâ yüksek;{0} işlemci bir kademe daha, {1}'e indirildi.", gain, Describe(next)));
            }
            return Same(currentCap, true, hot
                ? Loc.F("En düşük kademeye ulaşıldı ama ısı hâlâ yüksek;{0} bundan sonrası için donanım soğutması (temizlik, macun, altlık) gerekir.", gain)
                : Loc.F("Ayar işe yaradı, ısı normale döndü;{0} ayar kilitlendi.", gain));
        }

        // 2) Değişiklik yok / ilk oturum: ısıya bak
        if (!hot) return Same(currentCap, false, Loc.T("Isı normal; ayar gerekmedi."));

        return cur.Bottleneck switch
        {
            "gpu" or "other" => idx + 1 < ladder.Length ? StepDown(currentCap, idx, ladder, cur) : Same(currentCap, true, Loc.T("En düşük kademede ve ısı yüksek; donanım soğutması gerekir.")),
            "cpu" => Same(currentCap, false, Loc.T("Isı yüksek ama oyunu işlemci sınırlıyor; sınırlamak FPS kaybettirir, otomatik ayar yapılmadı. Soğutma (temizlik, macun, altlık) önerilir.")),
            _ => Same(currentCap, false, Loc.T("Isı yüksek ama oyunu neyin sınırladığı belirlenemedi; otomatik ayar yapılmadı.")),
        };
    }

    private static TuneDecision StepDown(int? currentCap, int idx, int?[] ladder, GameSessionReport cur)
    {
        // Sınırsızdan en hafif anlamlı kademeyle (~%92 hız) başla: bu bilgisayarda ölçüldü, %8 hız kaybıyla ısı ~12 °C düştü; ikinci kademe fazladan ısı kazandırmadı
        var next = ladder[idx + 1];
        return new TuneDecision(true, next, false,
            Loc.F("Isı yüksekti (sürenin %{0}'ında 90 °C üstü) ve oyunu {1} belirliyor: işlemci en çok {2}'e ayarlandı. ", cur.CpuAbove90Percent, Loc.T(cur.Bottleneck == "gpu" ? "ekran kartı" : "kare sınırı"), Describe(next)) +
            Loc.T("Sonraki oyunda ısı ve FPS karşılaştırılır; FPS düşerse geri alınır."));
    }

    private static TuneDecision Same(int? cap, bool locked, string note) => new(false, cap, locked, note);

    public static string Describe(int? cap) => cap is null ? Loc.T("sınırsız") : $"{cap / 1000.0:0.0} GHz";
}
