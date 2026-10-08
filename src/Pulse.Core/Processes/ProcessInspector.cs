using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Processes;

/// <summary>Aynı programın tüm süreçlerini (örn. 14 Chrome süreci) tek satırda toplar.</summary>
public sealed record ProcGroup(
    string Name,
    string? Path,
    string Title,
    int Count,
    double CpuPercent,
    long MemoryBytes,
    IReadOnlyList<int> Pids,
    ProcessPriorityClass? Priority,
    bool EcoMode,
    bool Protected,
    string? ProtectedReason,
    bool HasWindow);

public enum ProcResult { Ok, Partial, Failed, Protected }

/// <summary>
/// Süreç yöneticisi: kaynak kullanımını gruplayarak gösterir, kapatır, önceliği ve Verimlilik Modu'nu (EcoQoS) ayarlar.
/// Sistem ve güvenlik süreçleri, Pulse'ın kendisi ve oyun anti-hile süreçleri korumalıdır; hiçbir yolla kapatılmaz.
/// Kapatma önce nazik (pencereyi kapat), zorla sonlandırma ayrı bir kararla yapılır.
/// </summary>
public sealed class ProcessInspector
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
        "svchost", "dwm", "fontdrvhost", "explorer", "sihost", "ctfmon", "RuntimeBroker", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchIndexer", "TextInputHost", "audiodg", "WUDFHost", "spoolsv", "taskhostw",
        "conhost", "dllhost", "wlanext", "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "MpDefenderCoreService",
        "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "KLYC-Pulse", "NVDisplay.Container", "nvcontainer",
        "ApplicationFrameHost", "LockApp", "backgroundTaskHost", "smartscreen", "WmiPrvSE", "msdtc", "Secure System",
    };

    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _last = new();
    // MainModule okumak pahalı; bir sürecin yolu ömrü boyunca değişmez, bu yüzden (pid, başlangıç zamanı) ile önbelleğe alınır.
    private readonly Dictionary<(int, long), string?> _paths = new();
    private readonly int _cores = Environment.ProcessorCount;

    public IReadOnlyList<ProcGroup> Sample()
    {
        var now = DateTime.UtcNow;
        var perProc = new List<(Process P, string Name, string? Path, double Cpu, long Mem, string Title, bool Window)>();
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                seen.Add(p.Id);
                double cpu = 0;
                try
                {
                    var total = p.TotalProcessorTime;
                    if (_last.TryGetValue(p.Id, out var prev))
                    {
                        var dt = (now - prev.At).TotalSeconds;
                        if (dt > 0.2) cpu = Math.Clamp((total - prev.Cpu).TotalSeconds / dt / _cores * 100.0, 0, 100);
                    }
                    _last[p.Id] = (total, now);
                }
                catch { /* korumalı süreç: CPU okunamaz */ }

                string? path = null;
                try
                {
                    var key = (p.Id, p.StartTime.Ticks);
                    if (!_paths.TryGetValue(key, out path)) { try { path = p.MainModule?.FileName; } catch { path = null; } _paths[key] = path; }
                }
                catch { }
                var title = "";
                try { title = p.MainWindowTitle; } catch { }
                perProc.Add((p, p.ProcessName, path, cpu, p.WorkingSet64, title, p.MainWindowHandle != IntPtr.Zero));
            }
            catch { p.Dispose(); }
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        foreach (var gone in _paths.Keys.Where(k => !seen.Contains(k.Item1)).ToList()) _paths.Remove(gone);

        var groups = perProc
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.OrderByDescending(x => x.Window).ThenByDescending(x => x.Mem).First();
                var (prot, reason) = Protection(first.Name, first.Path, g.Any(x => x.Window), g.Any(x => x.P.SessionId == 0));
                ProcessPriorityClass? prio = null;
                try { prio = first.P.PriorityClass; } catch { }
                return new ProcGroup(
                    first.Name, first.Path, g.Select(x => x.Title).FirstOrDefault(t => t.Length > 0) ?? "",
                    g.Count(), Math.Round(g.Sum(x => x.Cpu), 1), g.Sum(x => x.Mem),
                    g.Select(x => x.P.Id).ToList(), prio, IsEco(first.P.Id), prot, reason, g.Any(x => x.Window));
            })
            .ToList();

        foreach (var x in perProc) x.P.Dispose();
        return groups;
    }

    /// <summary>Bu grup korumalı mı? Korumalıysa nedeni de döner.</summary>
    public static (bool, string?) Protection(string name, string? path, bool hasWindow, bool inSession0)
    {
        if (ProtectedNames.Contains(name))
        {
            var why = name.ToLowerInvariant() switch
            {
                "klyc-pulse" => "KLYC-Pulse'ın kendisi",
                "vgc" or "vgtray" => "Riot Vanguard (Valorant anti-hile)",
                "easyanticheat" or "easyanticheat_eos" or "beservice" => "Oyun anti-hile koruması",
                "msmpeng" or "nissrv" or "securityhealthservice" or "securityhealthsystray" or "mpdefendercoreservice" => "Windows güvenliği",
                "nvdisplay.container" or "nvcontainer" => "NVIDIA sürücü hizmeti",
                _ => "Windows için gerekli",
            };
            return (true, why);
        }
        if (inSession0) return (true, "Arka plan hizmeti (Arka plan sekmesinden yönet)");
        if (path is not null)
        {
            var p = path.ToLowerInvariant();
            var isWindows = p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant() + "\\");
            if (isWindows && !hasWindow) return (true, "Windows bileşeni");
        }
        return (false, null);
    }

    // ---- Eylemler ----------------------------------------------------------

    /// <summary>Nazikçe kapatır: ana pencereye kapat komutu gönderir ve bekler. Kapanmayanlar sayılır, zorla sonlandırma yapılmaz.</summary>
    public (ProcResult Result, int Closed, int Remaining) Close(ProcGroup group, int waitMs = 4000)
    {
        if (group.Protected) return (ProcResult.Protected, 0, group.Pids.Count);
        var procs = Alive(group);
        foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
        var end = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < end && procs.Any(p => !Exited(p))) Thread.Sleep(150);
        var remaining = procs.Count(p => !Exited(p));
        foreach (var p in procs) p.Dispose();
        var closed = procs.Count - remaining;
        return (remaining == 0 ? ProcResult.Ok : closed > 0 ? ProcResult.Partial : ProcResult.Failed, closed, remaining);
    }

    /// <summary>Zorla sonlandırır. Kaydedilmemiş veri kaybolur; yalnızca kullanıcı onayıyla çağrılmalı.</summary>
    public (ProcResult Result, int Killed) ForceClose(ProcGroup group)
    {
        if (group.Protected) return (ProcResult.Protected, 0);
        var killed = 0;
        foreach (var p in Alive(group))
        {
            try { p.Kill(); p.WaitForExit(2000); if (Exited(p)) killed++; } catch { }
            p.Dispose();
        }
        return (killed == group.Pids.Count ? ProcResult.Ok : killed > 0 ? ProcResult.Partial : ProcResult.Failed, killed);
    }

    /// <summary>Önceliği ayarlar (Gerçek zamanlı yasak) ve geri okuyarak doğrular.</summary>
    public (ProcResult Result, int Changed) SetPriority(ProcGroup group, ProcessPriorityClass priority)
    {
        if (group.Protected) return (ProcResult.Protected, 0);
        if (priority == ProcessPriorityClass.RealTime) return (ProcResult.Failed, 0);
        var changed = 0;
        foreach (var p in Alive(group))
        {
            try { p.PriorityClass = priority; p.Refresh(); if (p.PriorityClass == priority) changed++; } catch { }
            p.Dispose();
        }
        return (changed == group.Pids.Count ? ProcResult.Ok : changed > 0 ? ProcResult.Partial : ProcResult.Failed, changed);
    }

    /// <summary>Verimlilik Modu (EcoQoS): süreç düşük güçlü çekirdeklerde/düşük frekansta çalışır, ısı ve fan sesi azalır.</summary>
    public (ProcResult Result, int Changed) SetEcoMode(ProcGroup group, bool on)
    {
        if (group.Protected) return (ProcResult.Protected, 0);
        var changed = 0;
        foreach (var pid in group.Pids)
        {
            if (SetEco(pid, on) && IsEco(pid) == on) changed++;
        }
        return (changed == group.Pids.Count ? ProcResult.Ok : changed > 0 ? ProcResult.Partial : ProcResult.Failed, changed);
    }

    private static List<Process> Alive(ProcGroup g)
    {
        var list = new List<Process>();
        foreach (var pid in g.Pids) { try { list.Add(Process.GetProcessById(pid)); } catch { } }
        return list;
    }

    private static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }

    // ---- EcoQoS (kernel32) -------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    private const int ProcessPowerThrottling = 4;
    private const uint ThrottlingExecutionSpeed = 1;

    private static bool SetEco(int pid, bool on)
    {
        var h = OpenProcess(0x0200 | 0x1000, false, pid);          // SET_INFORMATION | QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return false;
        try
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = ThrottlingExecutionSpeed, StateMask = on ? ThrottlingExecutionSpeed : 0 };
            return SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottlingState>());
        }
        finally { CloseHandle(h); }
    }

    public static bool IsEco(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var s = new PowerThrottlingState { Version = 1 };
            return GetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf<PowerThrottlingState>())
                && (s.StateMask & ThrottlingExecutionSpeed) != 0 && (s.ControlMask & ThrottlingExecutionSpeed) != 0;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(IntPtr h, int cls, ref PowerThrottlingState info, int size);
}
