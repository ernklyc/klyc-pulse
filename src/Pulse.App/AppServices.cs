using Pulse.Core.Automation;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.App;

/// <summary>Uygulama genelinde paylaşılan servisler (ana ekran, tepsi ve otomatik mod aynı denetleyiciyi kullanır).</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; } = SettingsStore.Load();
    public static ModeController Modes { get; } = new(Settings);
    public static AutoModeService Auto { get; } = new(Modes, Settings);
    public static Pulse.Core.Cleanup.AutoCleanService AutoClean { get; } = new(Settings, Modes);

    private static SensorService? _sensors;
    public static SensorService Sensors => _sensors ??= new SensorService();
    public static OverlayService Overlay { get; } = new();

    public static ThermalGuardService Guard { get; } = new();
    public static HeatTargetService Heat { get; } = new();
    private static ModeKeeperService? _keeper;
    public static ModeKeeperService Keeper => _keeper ??= new ModeKeeperService();
    public static CompanionService Companion { get; } = new(Settings);

    private static HotkeyService? _hotkeys;

    /// <summary>Genel kısayollar; ilk erişimde (UI iş parçacığında) oluşturulur.</summary>
    public static HotkeyService Hotkeys => _hotkeys ??= new HotkeyService();

    public static void Shutdown()
    {
        _keeper?.Dispose();
        // Güvenlik: Pulse kapanırken ekran kartı hızlandırması fabrika hızına döner (kalıcı kalmasın).
        try { if (Pulse.Core.Hardware.GpuOverclock.Read() is { } oc && (oc.CoreMhz != 0 || oc.MemMhz != 0)) Pulse.Core.Hardware.GpuOverclock.Reset(); } catch { }
        Heat.Dispose();
        Guard.Dispose();
        Overlay.Set(false);
        _hotkeys?.Dispose();
        _sensors?.Dispose();
        Auto.Dispose();
        AutoClean.Dispose();
        Modes.Dispose();
    }
}