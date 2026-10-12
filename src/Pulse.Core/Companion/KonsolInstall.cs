using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Companion;

/// <summary>
/// KLYC Konsol kurulu mu, çalışıyor mu, nasıl (yetkisiz) başlatılır? Pulse yönetici olarak çalışır; Konsol ise standart kullanıcı uygulamasıdır.
/// Yönetici bir süreçten doğrudan başlatılırsa Konsol da yönetici olur (oradan açılan oyunlar da yönetici çalışır): bu yüzden başlatma
/// masaüstü kabuğunun (explorer) yetkisiz anahtarıyla yapılır.
/// </summary>
public static class KonsolInstall
{
    public const string ProcessName = "KlycKonsol";
    public const string ReleasesUrl = "https://github.com/ernklyc/klyc-konsol/releases/latest";
    public const string RepoUrl = "https://github.com/ernklyc/klyc-konsol";

    /// <summary>Aranan yerler (sırayla). <c>KLYC_KONSOL_EXE</c> testlerde/özel kurulumda yolu verir.</summary>
    public static IReadOnlyList<string> Candidates(string? localAppData = null, string? envExe = null)
    {
        var la = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(envExe ?? Environment.GetEnvironmentVariable("KLYC_KONSOL_EXE"))) list.Add((envExe ?? Environment.GetEnvironmentVariable("KLYC_KONSOL_EXE"))!);
        list.Add(Path.Combine(la, "KLYC-Konsol", "current", "KlycKonsol.exe"));            // Setup.exe (Velopack)
        list.Add(Path.Combine(la, "Programs", "KLYC-Konsol", "KlycKonsol.exe"));           // Kur.ps1
        return list;
    }

    /// <summary>Kurulu Konsol'un exe yolu; bulunamazsa null.</summary>
    public static string? FindExe(IEnumerable<string>? candidates = null) => (candidates ?? Candidates()).FirstOrDefault(File.Exists);

    public static bool IsRunning()
    {
        try { var p = Process.GetProcessesByName(ProcessName); var any = p.Length > 0; foreach (var x in p) x.Dispose(); return any; }
        catch { return false; }
    }

    /// <summary>Kart durum yazısı için: (kuruldu, çalışıyor).</summary>
    public static (bool Installed, bool Running) State(IEnumerable<string>? candidates = null) => (FindExe(candidates) is not null, IsRunning());

    /// <summary>Konsol'u YETKİSİZ başlatır (yönetici Pulse'ın yetkisini devralmasın). Başarıda true.</summary>
    public static bool Launch(string exe, string? args = null)
    {
        try
        {
            // Pulse zaten yetkisiz (standart) çalışıyorsa doğrudan başlatmak yeterli: çocuk süreç aynı yetkisiz belirteci alır
            if (!Cleanup.CleanupEngine.IsAdmin)
            {
                if (!File.Exists(exe)) return false;
                using var p = Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                Journal.Write("KLYC Konsol başlatıldı (Pulse yetkisiz çalışıyor).");
                return p is not null;
            }
            if (StartAsShellUser(exe, args)) { Journal.Write("KLYC Konsol yetkisiz başlatıldı (masaüstü kabuğunun belirteciyle)."); return true; }
            Journal.Write("KLYC Konsol yetkisiz başlatılamadı (masaüstü kabuğu bulunamadı): güvenlik gereği yönetici yetkisiyle başlatılmadı.");
            return false;
        }
        catch (Exception ex) { Journal.Write("KLYC Konsol başlatılamadı: " + ex.Message); return false; }
    }

    // ---- Yetkisiz başlatma: masaüstü kabuğunun (explorer) belirtecini kopyala ----------------

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO { public int cb; public IntPtr lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impersonation, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? app, string? cmd, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    private static bool StartAsShellUser(string exe, string? args)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return false;
        GetWindowThreadProcessId(shell, out var pid);
        if (pid == 0) return false;
        IntPtr hProc = IntPtr.Zero, hTok = IntPtr.Zero, hDup = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (hProc == IntPtr.Zero) return false;
            if (!OpenProcessToken(hProc, 0x0002 /* TOKEN_DUPLICATE */, out hTok)) return false;
            if (!DuplicateTokenEx(hTok, 0x000F01FF /* TOKEN_ALL_ACCESS */, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out hDup)) return false;
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = $"\"{exe}\"" + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
            if (!CreateProcessWithTokenW(hDup, 0, null, cmd, 0, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi)) throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (hDup != IntPtr.Zero) CloseHandle(hDup);
            if (hTok != IntPtr.Zero) CloseHandle(hTok);
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }
}
