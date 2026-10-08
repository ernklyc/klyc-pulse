using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Core.Automation;

/// <summary>Çalışan bir oyun var mı? Oyun kütüphanesi klasörlerindeki çalışan süreçlere bakar.</summary>
public static class GameDetector
{
    private static readonly string[] LibraryMarkers =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\EA Games\", @"\Origin Games\",
        @"\Ubisoft Game Launcher\games\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\XboxGames\", @"\Rockstar Games\",
        @"\Games\", @"\SteamLibrary\", @"\Battle.net\Games\", @"\Blizzard\",
    ];

    // Oyun klasöründe olup oyun olmayanlar (yükleyici, hata raporlayıcı, anti-cheat, başlatıcı)
    private static readonly string[] IgnoreNames =
    [
        "crash", "report", "setup", "install", "redist", "vc_", "dxsetup", "unins", "launcher", "easyanticheat",
        "beservice", "battleye", "vgc", "vgtray", "epicwebhelper", "eosoverlay", "ue4prereq", "dotnet", "updater",
    ];

    private static bool AllowTemp => Environment.GetEnvironmentVariable("KLYC_PULSE_TEST") == "1";

    public sealed record DetectedGame(string Name, string Path, int Pid = 0);

    public static string? FindRunningGame() => FindRunningGameInfo()?.Name;

    /// <param name="isKnownGame">Kullanıcının elle eklediği oyunları tanımak için: (tam yol, küçük harfli ad) → bilinen oyun mu? Klasör kuralına uymasa da algılanır.</param>
    public static DetectedGame? FindRunningGameInfo(Func<string, string, bool>? isKnownGame = null)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = GetImagePath(p.Id);
                if (path is null) continue;
                // Geçici klasördeki programlar oyun sayılmaz (testlerin sahte oyunları gerçek profil listesine karışmasın).
                // Testler KLYC_PULSE_TEST ortam değişkeniyle bunu açar.
                if (!AllowTemp && path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) continue;
                var file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                var known = isKnownGame?.Invoke(path, file) == true;      // elle eklenen oyun (pencere/bellek kuralı aşağıda yine uygulanır)
                if (!known && !LibraryMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                if (IgnoreNames.Any(file.Contains)) continue;
                if (p.MainWindowHandle == IntPtr.Zero && p.WorkingSet64 < 400L * 1024 * 1024) continue; // pencere ya da büyük bellek yoksa oyun değildir
                return new DetectedGame(Path.GetFileNameWithoutExtension(path), path, p.Id);
            }
            catch { /* süreç kapanmış olabilir */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    private static string? GetImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}