using Pulse.Core.Localization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Platform;

public sealed record ExitStep(string Name, bool Ok, string Detail);

public sealed class ServiceBackup
{
    public string Name { get; set; } = "";
    public string StartType { get; set; } = "auto";   // auto | delayed-auto | demand | disabled
    public bool WasRunning { get; set; }
}

public sealed class ExitBackup
{
    public DateTime At { get; set; } = DateTime.Now;
    public bool TaskExisted { get; set; }
    public bool TaskWasEnabled { get; set; }
    public bool ProcessWasRunning { get; set; }
    public string? ProcessPath { get; set; }
    public List<ServiceBackup> Services { get; set; } = new();
}

/// <summary>
/// "G-Helper'ı kapat" sihirbazının çekirdeği: G-Helper'ın açılış görevini ve Armoury Crate'in gereksiz arka plan
/// servislerini kapatır. Hiçbir şey silinmez; her değişiklik yedeklenir ve "Geri al" ile eski haline döner.
/// </summary>
public sealed class GHelperExit
{
    // Donanım tuşlarını ve profil sürücüsünü yöneten ASUSOptimization / ASUSSwitch bilerek listede yok.
    public static readonly string[] DefaultServices =
    [
        "ArmouryCrateControlInterface", "AsusAppService", "ASUSSoftwareManager", "ASUSSystemAnalysis", "ASUSSystemDiagnosis",
    ];

    private readonly string _taskName;
    private readonly string _processName;
    private readonly string[] _services;
    private readonly string _backupPath;

    public GHelperExit(string taskName = "GHelper", string processName = "GHelper", string[]? services = null, string? backupPath = null)
    {
        _taskName = taskName;
        _processName = processName;
        _services = services ?? DefaultServices;
        _backupPath = backupPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "ghelper_exit.json");
    }

    public bool HasBackup => File.Exists(_backupPath);

    /// <summary>Şu anki durumun özeti (hiçbir şeyi değiştirmez). Ok = "bırakılmış" demek.</summary>
    public IReadOnlyList<ExitStep> Probe()
    {
        var steps = new List<ExitStep>();
        var running = Process.GetProcessesByName(_processName).Length > 0;
        steps.Add(new(Loc.F("{0} çalışıyor mu", _processName), !running, running ? Loc.T("Evet, çalışıyor") : Loc.T("Hayır")));
        var task = TaskEnabled();
        steps.Add(new(Loc.F("{0} açılış görevi", _processName), task != true, task switch { null => Loc.T("Yok"), true => Loc.T("Açık"), false => Loc.T("Kapalı") }));
        foreach (var s in _services)
        {
            var st = StartType(s);
            if (st is null) { steps.Add(new(s, true, Loc.T("Kurulu değil"))); continue; }
            steps.Add(new(s, st != "auto" && st != "delayed-auto", Loc.F("Başlangıç: {0}", st)));
        }
        return steps;
    }

    /// <summary>Uygular. Yönetici gerekir. Her adım yazıldıktan sonra yeniden okunarak doğrulanır.</summary>
    public IReadOnlyList<ExitStep> Apply()
    {
        var steps = new List<ExitStep>();

        if (!File.Exists(_backupPath))
        {
            // Yedek yalnızca ilk uygulamada alınır; tekrar basılırsa asıl "önceki durum" ezilmez.
            var backup = new ExitBackup();
            var tEnabled = TaskEnabled();
            backup.TaskExisted = tEnabled is not null;
            backup.TaskWasEnabled = tEnabled == true;
            var procs = Process.GetProcessesByName(_processName);
            backup.ProcessWasRunning = procs.Length > 0;
            backup.ProcessPath = procs.Select(p => { try { return p.MainModule?.FileName; } catch { return null; } }).FirstOrDefault(x => x is not null);
            foreach (var s in _services)
            {
                var st = StartType(s);
                if (st is null) continue;
                backup.Services.Add(new ServiceBackup { Name = s, StartType = st, WasRunning = ServiceControl.State(s) == 4 });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
            File.WriteAllText(_backupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            steps.Add(new(Loc.T("Yedek alındı"), File.Exists(_backupPath), _backupPath));
        }

        // 1) G-Helper'ı kapat
        foreach (var p in Process.GetProcessesByName(_processName))
        {
            try { if (!p.CloseMainWindow() || !p.WaitForExit(2000)) p.Kill(); p.WaitForExit(3000); }
            catch (Exception ex) { Journal.Write($"{_processName} kapatılamadı: {ex.Message}"); }
        }
        var stillRunning = Process.GetProcessesByName(_processName).Length > 0;
        steps.Add(new(Loc.F("{0} kapatıldı", _processName), !stillRunning, stillRunning ? Loc.T("Hâlâ çalışıyor") : Loc.T("Kapalı")));

        // 2) Açılış görevini kapat (silmeden)
        if (TaskEnabled() is not null)
        {
            Schtasks("/Change", "/TN", _taskName, "/DISABLE");
            var t = TaskEnabled();
            steps.Add(new(Loc.F("{0} açılışta başlamasın", _processName), t == false, t == false ? Loc.T("Görev kapatıldı") : Loc.T("Görev kapatılamadı")));
        }
        else steps.Add(new(Loc.F("{0} açılış görevi", _processName), true, "Zaten yok"));

        // 3) Armoury Crate servislerini "elle başlat"a al ve durdur
        foreach (var s in _services)
        {
            if (StartType(s) is null) { steps.Add(new(s, true, Loc.T("Kurulu değil, atlandı"))); continue; }
            Sc("config", s, "start=", "demand");
            ServiceControl.Stop(s, 10);
            var st = StartType(s);
            var running = ServiceControl.State(s) == 4;
            steps.Add(new(Loc.F("{0} elle başlatmaya alındı", s), st == "demand" && !running, Loc.F("Başlangıç: {0}, {1}", st, Loc.T(running ? "çalışıyor" : "durdu"))));
        }

        Journal.Write("G-Helper'ı kapat uygulandı: " + string.Join("; ", steps.Select(s => $"{s.Name}={(s.Ok ? "ok" : "HATA")}")));
        return steps;
    }

    /// <summary>Yedeğe bakarak her şeyi eski haline döndürür.</summary>
    public IReadOnlyList<ExitStep> Revert()
    {
        var steps = new List<ExitStep>();
        var backup = ReadBackup();
        if (backup is null) return [new(Loc.T("Yedek"), false, Loc.T("Geri alınacak yedek bulunamadı."))];

        foreach (var s in backup.Services)
        {
            Sc("config", s.Name, "start=", s.StartType);
            if (s.WasRunning) ServiceControl.Start(s.Name);
            var st = StartType(s.Name);
            steps.Add(new(Loc.F("{0} eski haline döndü", s.Name), st == s.StartType, Loc.F("Başlangıç: {0}", st)));
        }

        if (backup.TaskExisted)
        {
            Schtasks("/Change", "/TN", _taskName, backup.TaskWasEnabled ? "/ENABLE" : "/DISABLE");
            var t = TaskEnabled();
            steps.Add(new(Loc.F("{0} açılış görevi eski haline döndü", _processName), t == backup.TaskWasEnabled, t == true ? Loc.T("Açık") : Loc.T("Kapalı")));
        }

        if (backup.ProcessWasRunning && backup.ProcessPath is { } path && File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); steps.Add(new(Loc.F("{0} yeniden başlatıldı", _processName), true, Loc.T("Başlatıldı"))); }
            catch (Exception ex) { steps.Add(new(Loc.F("{0} yeniden başlatıldı", _processName), false, ex.Message)); }
        }

        if (steps.All(s => s.Ok)) { try { File.Delete(_backupPath); } catch { } }
        Journal.Write("G-Helper'ı kapat geri alındı: " + string.Join("; ", steps.Select(s => $"{s.Name}={(s.Ok ? "ok" : "HATA")}")));
        return steps;
    }

    private ExitBackup? ReadBackup()
    {
        try { return JsonSerializer.Deserialize<ExitBackup>(File.ReadAllText(_backupPath)); }
        catch { return null; }
    }

    /// <summary>null: görev yok, true: açık, false: kapalı.</summary>
    private bool? TaskEnabled()
    {
        var r = Schtasks("/Query", "/TN", _taskName, "/XML");
        if (r.Code != 0) return null;
        try
        {
            var xml = r.Output[r.Output.IndexOf('<')..];
            var doc = XDocument.Parse(xml);
            var settings = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
            return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    /// <summary>auto | delayed-auto | demand | disabled; servis yoksa null.</summary>
    private static string? StartType(string service)
    {
        var o = Sc("qc", service).Output;
        var m = Regex.Match(o, @"START_TYPE\s*:\s*(\d+)");
        if (!m.Success) return null;
        return m.Groups[1].Value switch
        {
            "2" => o.Contains("DELAYED", StringComparison.OrdinalIgnoreCase) ? "delayed-auto" : "auto",
            "3" => "demand",
            "4" => "disabled",
            "1" => "system",
            "0" => "boot",
            _ => null,
        };
    }

    private static (int Code, string Output) Sc(params string[] args) => Exec("sc.exe", args);
    private static (int Code, string Output) Schtasks(params string[] args) => Exec("schtasks.exe", args);

    private static (int Code, string Output) Exec(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o + e);
    }
}
