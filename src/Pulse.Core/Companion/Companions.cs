using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Companion;

public sealed record CompanionResult(bool Ok, string Message);

/// <summary>
/// ThrottleStop'u arka plandan sürer (undervolt, güç sınırı ve BD PROCHOT ayarları ThrottleStop'ta kalır, Pulse onu başlatır
/// ve moda göre profilini seçer). Profil değişimi ThrottleStop penceresinin kendi profil düğmelerine tıklanarak yapılır ve
/// profil adı pencereden geri okunarak doğrulanır. Yönetici gerekir (ThrottleStop de yönetici ister).
/// </summary>
public static class ThrottleStopControl
{
    private const int FirstProfileButton = 1088;   // 1088..1091 = profil 1..4
    private const int ProfileLabel = 1103;

    public static string? FindExe()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, @"Microsoft\WinGet\Links\ThrottleStop.exe"),
            @"C:\Program Files\ThrottleStop\ThrottleStop.exe",
            @"C:\ThrottleStop\ThrottleStop.exe",
        ];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        return Process.GetProcessesByName("ThrottleStop").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(x => x is not null);
    }

    public static bool IsInstalled => FindExe() is not null;
    public static bool IsRunning => Process.GetProcessesByName("ThrottleStop").Length > 0;

    /// <summary>ThrottleStop.ini'deki profil adları (1-4). Okunamazsa varsayılanlar.</summary>
    public static IReadOnlyList<string> ProfileNames()
    {
        var names = new[] { "Performance", "Game", "Internet", "Battery" };
        try
        {
            var exe = FindExe();
            var dir = exe is null ? null : Path.GetDirectoryName(File.ResolveLinkTarget(exe, true)?.FullName ?? exe);
            var ini = dir is null ? null : Path.Combine(dir, "ThrottleStop.ini");
            if (ini is not null && File.Exists(ini))
                foreach (var line in File.ReadLines(ini))
                    for (var i = 1; i <= 4; i++)
                        if (line.StartsWith($"ProfileName{i}=", StringComparison.Ordinal)) names[i - 1] = line[$"ProfileName{i}=".Length..];
        }
        catch { }
        return names;
    }

    public static CompanionResult Launch(bool minimized = true)
    {
        if (IsRunning) return new(true, "ThrottleStop zaten çalışıyor.");
        var exe = FindExe();
        if (exe is null) return new(false, "ThrottleStop bulunamadı.");
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(File.ResolveLinkTarget(exe, true)?.FullName ?? exe)! });
            if (p is null) return new(false, "ThrottleStop başlatılamadı.");
            var hwnd = WaitForWindow(8000);
            if (hwnd == IntPtr.Zero) return new(false, "ThrottleStop penceresi görünmedi.");
            if (minimized) ShowWindow(hwnd, 6);   // SW_MINIMIZE
            Journal.Write("ThrottleStop başlatıldı (Pulse ile birlikte).");
            return new(true, "ThrottleStop arka planda başlatıldı.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>Profili seçer (1-4) ve pencerenin profil etiketinden doğrular.</summary>
    public static CompanionResult SetProfile(int profile1To4)
    {
        if (profile1To4 is < 1 or > 4) return new(false, "Profil 1-4 arasında olmalı.");
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero) return new(false, "ThrottleStop çalışmıyor.");
        var button = GetDlgItem(hwnd, FirstProfileButton + profile1To4 - 1);
        if (button == IntPtr.Zero) return new(false, "Profil düğmesi bulunamadı (ThrottleStop sürümü farklı olabilir).");

        SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);   // BM_CLICK
        var expected = ProfileNames()[profile1To4 - 1];
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(150);
            if (WindowText(GetDlgItem(hwnd, ProfileLabel)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                Journal.Write($"ThrottleStop profili: {expected}");
                return new(true, $"ThrottleStop profili “{expected}” seçildi ve doğrulandı.");
            }
        }
        return new(false, $"Profil “{expected}” seçilemedi (okunan: {WindowText(GetDlgItem(hwnd, ProfileLabel))}).");
    }

    public static CompanionResult Close()
    {
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero) return new(true, "ThrottleStop zaten kapalı.");
        PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
        for (var i = 0; i < 20 && IsRunning; i++) Thread.Sleep(200);
        return IsRunning ? new(false, "ThrottleStop kapanmadı.") : new(true, "ThrottleStop kapatıldı.");
    }

    // ---- Pencere bulma (gizli/küçültülmüş pencereler dahil) -----------------

    private static IntPtr WaitForWindow(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            var h = FindWindow();
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(250);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindWindow()
    {
        var pids = Process.GetProcessesByName("ThrottleStop").Select(p => (uint)p.Id).ToHashSet();
        var result = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && WindowText(h).StartsWith("ThrottleStop", StringComparison.OrdinalIgnoreCase)) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        return sb.ToString();
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
}

/// <summary>MSI Afterburner'ı arka plandan sürer: kayıtlı profillerden birini (1-5) komut satırıyla uygulatır.</summary>
public static class AfterburnerControl
{
    private const string DefaultExe = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe";

    public static string? FindExe() => File.Exists(DefaultExe) ? DefaultExe : null;
    public static bool IsInstalled => FindExe() is not null;
    public static bool IsRunning => Process.GetProcessesByName("MSIAfterburner").Length > 0;

    /// <summary>Kayıtlı (yazılmış) profil dosyaları; yoksa uygulanacak bir şey de yoktur.</summary>
    public static int SavedProfileCount()
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(DefaultExe)!, "Profiles");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "VEN_*.cfg").Length : 0;
        }
        catch { return 0; }
    }

    public static CompanionResult Launch()
    {
        if (IsRunning) return new(true, "Afterburner zaten çalışıyor.");
        var exe = FindExe();
        if (exe is null) return new(false, "Afterburner bulunamadı.");
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            Journal.Write("Afterburner başlatıldı (Pulse ile birlikte).");
            return new(true, "Afterburner başlatıldı.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>Profili uygulatır. Afterburner profil uygulandığını geri bildirmediği için sonuç "gönderildi" olarak raporlanır.</summary>
    public static CompanionResult ApplyProfile(int profile1To5)
    {
        var exe = FindExe();
        if (exe is null) return new(false, "Afterburner bulunamadı.");
        if (profile1To5 is < 1 or > 5) return new(false, "Profil 1-5 arasında olmalı.");
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"-Profile{profile1To5}") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            Journal.Write($"Afterburner profili {profile1To5} istendi.");
            return new(true, $"Afterburner’a Profil {profile1To5} uygulama komutu gönderildi (Afterburner geri bildirim vermez).");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}

/// <summary>G-Helper'ı gerekirse Pulse ile birlikte başlatır (kapatma/geri alma sihirbazı ayrıdır).</summary>
public static class GHelperControl
{
    public static string? FindExe()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string[] candidates = [Path.Combine(desktop, "GHelper.exe"), @"C:\Program Files\GHelper\GHelper.exe", @"C:\GHelper\GHelper.exe"];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;
        var backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "ghelper_exit.json");
        try
        {
            if (File.Exists(backup) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(backup)).RootElement.TryGetProperty("ProcessPath", out var p) && p.GetString() is { } path && File.Exists(path)) return path;
        }
        catch { }
        return Process.GetProcessesByName("GHelper").Select(pr => { try { return pr.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(x => x is not null);
    }

    public static bool IsInstalled => FindExe() is not null;
    public static bool IsRunning => Process.GetProcessesByName("GHelper").Length > 0;

    public static CompanionResult Launch()
    {
        if (IsRunning) return new(true, "G-Helper zaten çalışıyor.");
        var exe = FindExe();
        if (exe is null) return new(false, "G-Helper bulunamadı.");
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); return new(true, "G-Helper başlatıldı."); }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}
