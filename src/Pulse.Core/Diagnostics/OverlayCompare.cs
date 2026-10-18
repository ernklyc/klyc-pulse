using Pulse.Core.Localization;

namespace Pulse.Core.Diagnostics;

/// <summary>
/// Oyun üstü göstergenin (overlay) gerçek maliyetini ölçmek için: aynı oyunun göstergeli ve göstergesiz oturumlarının FPS'ini yan yana koyar.
/// Tek oturumluk fark kesin kanıt değildir (sahne, ısı değişir); bu yüzden en az 8 dakikalık oturumlar ve karşı durumdan son 3 oturumun ortalaması kullanılır,
/// fark %3'ün altındaysa "belirgin fark yok" denir. Hiçbir şeyi kendisi açıp kapatmaz.
/// </summary>
public static class OverlayCompare
{
    public const int MinMinutes = 8;
    public const double NoticeablePercent = 3;

    public static string? Describe(GameSessionReport current, IReadOnlyList<GameSessionReport> history)
    {
        if (current.Minutes < MinMinutes || current.AvgFps is not { } curFps) return null;
        var others = history.Where(r => string.Equals(r.Game, current.Game, StringComparison.OrdinalIgnoreCase) && r.OverlayOn != current.OverlayOn
                                     && r.Minutes >= MinMinutes && r.AvgFps is not null && r.Start != current.Start).TakeLast(3).ToList();
        if (others.Count == 0) return null;
        var otherFps = others.Average(r => r.AvgFps!.Value);
        var otherLow = others.Where(r => r.AvgLowFps is not null).Select(r => r.AvgLowFps!.Value).DefaultIfEmpty(double.NaN).Average();
        var (on, off, onLow, offLow) = current.OverlayOn ? (curFps, otherFps, current.AvgLowFps, double.IsNaN(otherLow) ? (double?)null : otherLow)
                                                         : (otherFps, curFps, double.IsNaN(otherLow) ? (double?)null : otherLow, current.AvgLowFps);
        var diff = off > 0 ? (off - on) / off * 100 : 0;
        var lows = onLow is { } a && offLow is { } b ? Loc.F(" (%1 en kötü kareler: {0:0} / {1:0})", a, b) : "";
        if (Math.Abs(diff) < NoticeablePercent)
            return Loc.F("Gösterge (overlay) karşılaştırması: açıkken ort. {0:0} FPS, kapalıyken {1:0} FPS{2}. Belirgin bir fark görülmedi ({3} oturumla).", on, off, lows, others.Count + 1);
        return diff > 0
            ? Loc.F("Gösterge (overlay) karşılaştırması: açıkken ort. {0:0} FPS, kapalıyken {1:0} FPS{2}: yaklaşık %{3:0} fark. Göstergeyi kapatmak bu oyunda FPS kazandırabilir (tek oturum kesin kanıt değildir).", on, off, lows, diff)
            : Loc.F("Gösterge (overlay) karşılaştırması: açıkken ort. {0:0} FPS, kapalıyken {1:0} FPS{2}. Gösterge FPS'i düşürmüyor gibi görünüyor (fark başka etkenlerden olabilir).", on, off, lows);
    }
}
