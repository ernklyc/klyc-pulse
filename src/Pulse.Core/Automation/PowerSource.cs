using System.Runtime.InteropServices;

namespace Pulse.Core.Automation;

public static class PowerSource
{
    /// <summary>true: şarjda, false: pilde, null: bilinmiyor.</summary>
    public static bool? IsOnAc()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        return s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}