using Pulse.Core.Hardware;

namespace Pulse.Core.Modes;

/// <summary>Bir kullanım modunun tüm ayarları. Hepsi geri okunarak doğrulanır.</summary>
public sealed record ModeDefinition(
    string Key,
    string Title,
    string Subtitle,
    AsusPerformanceMode Asus,
    int Boost,          // İşlemci ek hızı (turbo): 0 kapalı, 2 agresif
    int MaxState,       // İşlemci üst sınırı (%)
    int Epp,            // Enerji/performans tercihi (düşük = atak)
    int RefreshHz,
    int Brightness,     // %
    bool IdlePower,     // ekran 1 dk'da kapanır, uyku kapalı
    int? GpuCapMhz = null);   // ekran kartı çekirdek saat sınırı; null = sınırsız

public static class Modes
{
    public const string Game = "oyun";
    public const string Daily = "gunluk";
    public const string Quiet = "sessiz";
    public const string Idle = "bosta";

    public static IReadOnlyList<ModeDefinition> All { get; } =
    [
        new(Game,  "Oyun",   "Tam güç. Oyuna girmeden önce seç.",     AsusPerformanceMode.Turbo,    2, 100, 20, 144, 80, false),
        new(Daily, "Günlük", "Dengeli. İnternet, ofis ve yazılım için.", AsusPerformanceMode.Balanced, 2, 100, 33, 144, 60, false),
        new(Quiet, "Sessiz", "Serin ve sessiz. Hafif işler için.",    AsusPerformanceMode.Silent,   0, 100, 60,  60, 40, false, 1350),
        new(Idle,  "Boşta",  "İndirme, yedekleme, uzakta bırakma.",   AsusPerformanceMode.Silent,   0,  70, 80,  60, 15, true, 900),
    ];

    public static ModeDefinition? Get(string key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
}
