using System.Globalization;
using Microsoft.Win32;

namespace Pulse.Core.Apps;

/// <summary>Kurulu masaüstü uygulamaları (Programlar ve Özellikler kayıtları).</summary>
public static class InstalledAppsReader
{
    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] Sources =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    public static IReadOnlyList<InstalledApp> Read()
    {
        var result = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, path) in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    var display = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") is not null) continue;                       // güncelleme kayıtları
                    if (display.StartsWith("KB", StringComparison.Ordinal) && display.Length < 12) continue;

                    var sizeKb = k.GetValue("EstimatedSize") is int sz ? sz : 0;
                    var app = new InstalledApp(
                        display.Trim(),
                        (k.GetValue("Publisher") as string)?.Trim() ?? "",
                        (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                        ParseDate(k.GetValue("InstallDate") as string),
                        sizeKb * 1024L,
                        (k.GetValue("InstallLocation") as string)?.Trim().Trim('"'),
                        k.GetValue("UninstallString") as string,
                        k.GetValue("QuietUninstallString") as string,
                        $@"{hive}\{path}\{name}");
                    result.TryAdd(app.Name + "|" + app.Version, app);
                }
            }
            catch { /* kayıt defteri bölümü okunamadı */ }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static DateTime? ParseDate(string? s) =>
        s is { Length: 8 } && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}