using Pulse.Core.Localization;
using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Apps;

public sealed record StartupItem(string Name, string Command, string Location, bool Machine, bool Enabled, string Publisher, string ApprovedSubKey, string ApprovedValueName);

/// <summary>Açılışta başlayan programlar. Etkinleştirme/devre dışı bırakma, Görev Yöneticisi ile aynı mekanizmayı kullanır (silmez).</summary>
public static class StartupManager
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        ReadRun(items, RegistryHive.CurrentUser, RegistryView.Default, "Run", machine: false, Loc.T("Kullanıcı (Run)"));
        ReadRun(items, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", machine: true, "Sistem (Run)");
        ReadRun(items, RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", machine: true, "Sistem 32-bit (Run)");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), machine: false, Loc.T("Başlangıç klasörü (kullanıcı)"));
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), machine: true, Loc.T("Başlangıç klasörü (ortak)"));
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(item.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
            using var key = hive.CreateSubKey($@"{ApprovedPath}\{item.ApprovedSubKey}", writable: true);
            var data = new byte[12];
            if (enabled) data[0] = 2;
            else { data[0] = 3; BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); }
            key.SetValue(item.ApprovedValueName, data, RegistryValueKind.Binary);
            Journal.Write($"Başlangıç {(enabled ? "etkinleştirildi" : "devre dışı bırakıldı")}: {item.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Journal.Write($"Başlangıç ayarı değiştirilemedi ({item.Name}): {ex.Message}");
            return false;
        }
    }

    private static void ReadRun(List<StartupItem> items, RegistryHive hive, RegistryView view, string approvedSub, bool machine, string location)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var run = baseKey.OpenSubKey(view == RegistryView.Registry32 ? RunPath.Replace("SOFTWARE", @"SOFTWARE\WOW6432Node") : RunPath);
            if (run is null) return;
            foreach (var name in run.GetValueNames())
            {
                var cmd = run.GetValue(name) as string ?? "";
                items.Add(new StartupItem(name, cmd, location, machine, IsApproved(machine, approvedSub, name), Publisher(cmd), approvedSub, name));
            }
        }
        catch { }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, bool machine, string location)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new StartupItem(Path.GetFileNameWithoutExtension(f), f, location, machine, IsApproved(machine, "StartupFolder", name), "", "StartupFolder", name));
            }
        }
        catch { }
    }

    private static bool IsApproved(bool machine, string subKey, string valueName)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Default);
            using var key = hive.OpenSubKey($@"{ApprovedPath}\{subKey}");
            if (key?.GetValue(valueName) is byte[] { Length: > 0 } b) return (b[0] & 1) == 0; // çift sayı = etkin, tek = devre dışı (2/6 etkin, 3 kapalı)
        }
        catch { }
        return true; // kayıt yoksa etkin
    }

    private static string Publisher(string command)
    {
        try
        {
            var path = command.Trim();
            if (path.StartsWith('"')) path = path[1..path.IndexOf('"', 1)];
            else { var idx = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (idx > 0) path = path[..(idx + 4)]; }
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName ?? "" : "";
        }
        catch { return ""; }
    }
}