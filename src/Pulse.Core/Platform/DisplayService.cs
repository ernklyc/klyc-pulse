using Pulse.Core.Localization;
using System.Runtime.InteropServices;

namespace Pulse.Core.Platform;

/// <summary>Ekran yenileme hızı ve parlaklık. Ek paket gerektirmez (Win32 + WMI/COM).</summary>
public static class DisplayService
{
    // ---- Yenileme hızı --------------------------------------------------
    public static int? GetRefreshRate()
    {
        var dm = NewDevMode();
        return EnumDisplaySettings(null, -1, ref dm) ? dm.dmDisplayFrequency : null;
    }

    public static bool SetRefreshRate(int hz)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettings(null, -1, ref dm)) return false;
        if (dm.dmDisplayFrequency == hz) return true;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = 0x400000; // DM_DISPLAYFREQUENCY
        return ChangeDisplaySettings(ref dm, 1) == 0; // CDS_UPDATEREGISTRY
    }

    /// <summary>Ekranın desteklediği yenileme hızları (geçerli çözünürlükte).</summary>
    public static IReadOnlyList<int> SupportedRefreshRates()
    {
        var cur = NewDevMode();
        if (!EnumDisplaySettings(null, -1, ref cur)) return Array.Empty<int>();
        var set = new SortedSet<int>();
        for (var i = 0; ; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(null, i, ref dm)) break;
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmBitsPerPel == cur.dmBitsPerPel)
                set.Add(dm.dmDisplayFrequency);
        }
        return set.ToList();
    }

    // ---- Parlaklık (WMI, COM late binding) -----------------------------
    /// <summary>Yenileme hızı değişince monitör kaydı kısa süre kaybolur; bu yüzden birkaç kez dener.</summary>
    public static int? GetBrightnessWithRetry(int attempts = 10, int delayMs = 300)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (GetBrightness() is { } b) return b;
            Thread.Sleep(delayMs);
        }
        return null;
    }

    /// <summary>Son hata (tanı için).</summary>
    public static string? LastError { get; private set; }

    // Not: SWbem COM yolu yazma sonrası okumada "Belirtilmemiş hata" veriyor (sağlayıcı kilitleniyor).
    // CIM (MI) yolu güvenilir çalışıyor; parlaklık nadir değiştiği için süreç başlatma bedeli önemsiz.
    public static int? GetBrightness()
    {
        var o = RunPowerShell("(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness | Select-Object -First 1).CurrentBrightness");
        if (int.TryParse(o.Trim(), out var v)) return v;
        LastError = Loc.T("Parlaklık okunamadı: ") + o.Trim();
        return null;
    }

    public static bool SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var o = RunPowerShell(
            "Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightnessMethods | " +
            $"Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=[uint32]1;Brightness=[byte]{percent}}} | Out-Null; 'ok'");
        if (o.Contains("ok")) return true;
        LastError = Loc.T("Parlaklık yazılamadı: ") + o.Trim();
        return false;
    }

    private static string RunPowerShell(string script)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---- Win32 ----------------------------------------------------------
    private static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE { dmDeviceName = "", dmFormName = "" };
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return dm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
}
