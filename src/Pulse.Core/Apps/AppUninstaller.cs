using System.Diagnostics;
using Pulse.Core.Cleanup;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Apps;

public sealed record Leftover(string Path, long Bytes);

/// <summary>Uygulamayı kendi kaldırıcısıyla kaldırır, sonra geride kalan klasörleri bulur. Silme Geri Dönüşüm Kutusu'na gider.</summary>
public static class AppUninstaller
{
    public static Task<int> UninstallAsync(InstalledApp app, bool preferQuiet) =>
        Task.Run(() =>
        {
            var cmd = preferQuiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString! : app.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd)) return -1;
            cmd = NormalizeCommand(cmd);
            Journal.Write($"Kaldırılıyor: {app.Name}");
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                // Steam gibi istemci tabanlı kaldırıcılar açık kalan bir süreç başlatır: sonsuza kadar bekleme.
                // Gerçek sonucu çağıran, kayıt defterinden izler.
                if (cmd.Contains("steam://", StringComparison.OrdinalIgnoreCase))
                {
                    p.WaitForExit(15000);
                    Journal.Write($"Kaldırma komutu Steam'e iletildi ({app.Name}). Onay Steam penceresinde.");
                    return 0;
                }
                p.WaitForExit();
                Journal.Write($"Kaldırma bitti ({app.Name}): kod {p.ExitCode}");
                return p.ExitCode;
            }
            catch (Exception ex) { Journal.Write($"Kaldırılamadı ({app.Name}): {ex.Message}"); return -1; }
        });

    /// <summary>
    /// Kayıt defterindeki kaldırma komutu tırnaksız olabilir ("C:\Program Files (x86)\X\Uninstall X.exe"). cmd bunu ilk boşlukta
    /// keser ve komut hiç çalışmaz. Dosya yolunu bulup tırnak içine alır, kalan kısmı bağımsız değişken olarak bırakır.
    /// </summary>
    public static string NormalizeCommand(string cmd, Func<string, bool>? fileExists = null)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"') || cmd.Contains("://")) return cmd;
        fileExists ??= File.Exists;
        // En uzun ".exe" önekini dene (yol içinde boşluk olabilir): başında ya da bağımsız değişkenlerden önce.
        var idx = 0;
        string? exe = null;
        while ((idx = cmd.IndexOf(".exe", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var candidate = cmd[..(idx + 4)];
            if (fileExists(candidate)) { exe = candidate; break; }
            idx += 4;
        }
        if (exe is null || !exe.Contains(' ')) return cmd;
        return $"\"{exe}\"{cmd[exe.Length..]}";
    }

    /// <summary>Kaldırma sonrası kalan olası klasörler. Yalnızca uygulamanın kendi adı/yayıncısıyla birebir eşleşenler.</summary>
    public static IReadOnlyList<Leftover> FindLeftovers(InstalledApp app)
    {
        var names = new List<string> { app.Name, Normalize(app.Name) };
        if (app.Publisher.Length >= 4 && !IsGeneric(app.Publisher)) names.Add(app.Publisher);
        names = names.Where(n => n.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"C:\Program Files", @"C:\Program Files (x86)",
        };

        var result = new List<Leftover>();
        void Add(string dir)
        {
            if (!Directory.Exists(dir) || result.Any(r => r.Path.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
            long bytes = 0;
            try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })) bytes += f.Length; } catch { }
            result.Add(new Leftover(dir, bytes));
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation)) Add(app.InstallLocation!);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var n in names)
            {
                Add(Path.Combine(root, n));
                Add(Path.Combine(root, Normalize(n)));
            }
        }

        // Sistem klasörlerine ve çok genel adlara asla dokunma
        return result.Where(r => !IsProtected(r.Path)).ToList();
    }

    public static bool RemoveLeftover(Leftover l) => !IsProtected(l.Path) && RecycleBin.SendToRecycleBin(l.Path);

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsGeneric(string publisher) =>
        publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || publisher.Contains("Google", StringComparison.OrdinalIgnoreCase)
        || publisher.Contains("Intel", StringComparison.OrdinalIgnoreCase) || publisher.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || publisher.Contains("Python", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtected(string path)
    {
        var p = path.TrimEnd('\\');
        var depth = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3) return true;
        string[] blocked = [@"\Windows", @"\Microsoft", @"\Common Files", @"\WindowsApps", @"\Packages", @"\Temp", @"\Programs", @"\Windows Defender"];
        var leaf = "\\" + Path.GetFileName(p);
        return blocked.Any(b => leaf.Equals(b, StringComparison.OrdinalIgnoreCase));
    }
}