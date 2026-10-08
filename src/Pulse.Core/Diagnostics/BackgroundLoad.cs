using Pulse.Core.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Core.Diagnostics;

/// <summary>Tek bir sürecin o anki sayaçları (CPU süresi, disk okuma/yazma toplamı, bellek).</summary>
public sealed record ProcSample(int Pid, long StartTicks, string Name, double CpuSeconds, long IoBytes, long WorkingSet, string? Path = null);

/// <summary>Oyun sırasında arka planda çalışan bir programın (aynı adlı tüm süreçlerin toplamı) özeti.</summary>
public sealed class BackgroundItem
{
    public string Name { get; init; } = "";
    public string Display { get; init; } = "";

    /// <summary>"app" = kapatılabilir kullanıcı programı, "launcher" = oyun başlatıcısı (açıkken kapatılmaz), "system" = Windows/sürücü işi, "other" = bilinmiyor.</summary>
    public string Kind { get; init; } = "other";

    /// <summary>Toplam işlemcinin yüzdesi olarak oturum ortalaması (tüm çekirdekler = %100).</summary>
    public double CpuAvgPercent { get; init; }
    public double CpuPeakPercent { get; init; }

    /// <summary>Örneklerin yüzde kaçında işlemcinin %8'inden fazlasını kullandı.</summary>
    public int ActivePercent { get; init; }
    public double DiskAvgMBps { get; init; }
    public double RamPeakMB { get; init; }
}

public sealed class BackgroundSummary
{
    public List<BackgroundItem> Items { get; init; } = new();

    /// <summary>Oyun dışındaki tüm programların toplam işlemci ortalaması (%).</summary>
    public double TotalCpuAvgPercent { get; init; }
}

/// <summary>
/// Oyun süresince arka plandaki programların işlemci, disk ve bellek yükünü ölçer (oyunun kendisi ve Pulse hariç).
/// Hiçbir programı kapatmaz; yalnızca ölçüp raporlar. Saf mantık: süreç listesini dışarıdan alır, bu yüzden sahte verilerle sınanır.
/// </summary>
public sealed class BackgroundLoadTracker
{
    private sealed class Acc
    {
        public double CpuSum, CpuPeak, IoSum, RamPeak;
        public int Active;
    }

    private const double ActiveThreshold = 8;       // bir aralıkta toplam işlemcinin %8'i = "etkin"

    // Oyun yükü sayılmayan ya da değerlendirilmeyen sistem süreçleri
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "system", "registry", "memory compression", "secure system", "dwm", "csrss", "audiodg", "fontdrvhost", "wininit",
        "winlogon", "services", "lsass", "smss", "conhost", "klyc-pulse", "pulse", "pulse.app", "pulse.cli", "system idle process",
    };

    private readonly int _cores;
    private readonly string _game;
    private readonly Dictionary<(int, long), (double Cpu, long Io)> _prev = new();
    private readonly Dictionary<string, Acc> _acc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _last;
    private string? _gameDir;
    private int _intervals;
    private double _totalCpuSum;

    public BackgroundLoadTracker(int logicalCores, string game)
    {
        _cores = Math.Max(1, logicalCores);
        _game = game;
    }

    public int Intervals => _intervals;

    public void Add(IReadOnlyList<ProcSample> procs, DateTime now)
    {
        // Oyunun klasörü: oyunun yan süreçleri (anti-hile, yardımcılar) oyun yükü sayılır, arka plan sayılmaz
        if (_gameDir is null)
        {
            var g = procs.FirstOrDefault(p => string.Equals(p.Name, _game, StringComparison.OrdinalIgnoreCase) && p.Path is not null);
            if (g?.Path is not null) _gameDir = System.IO.Path.GetDirectoryName(g.Path);
        }

        var dt = _last is null ? 0 : (now - _last.Value).TotalSeconds;
        var cpuBy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ioBy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var ramBy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(int, long)>();

        foreach (var p in procs)
        {
            var key = (p.Pid, p.StartTicks);
            seen.Add(key);
            if (IsExcluded(p)) { _prev[key] = (p.CpuSeconds, p.IoBytes); continue; }
            if (dt > 0 && _prev.TryGetValue(key, out var old))
            {
                var pct = Math.Max(0, p.CpuSeconds - old.Cpu) / (dt * _cores) * 100;
                var mbps = Math.Max(0, p.IoBytes - old.Io) / dt / 1048576.0;
                cpuBy[p.Name] = cpuBy.GetValueOrDefault(p.Name) + pct;
                ioBy[p.Name] = ioBy.GetValueOrDefault(p.Name) + mbps;
            }
            ramBy[p.Name] = ramBy.GetValueOrDefault(p.Name) + p.WorkingSet / 1048576.0;
            _prev[key] = (p.CpuSeconds, p.IoBytes);
        }

        // Kapanmış süreçleri unut
        if (_prev.Count > seen.Count + 64)
            foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);

        _last = now;
        if (dt <= 0) return;

        _intervals++;
        _totalCpuSum += cpuBy.Values.Sum();
        foreach (var name in cpuBy.Keys.Union(ramBy.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!_acc.TryGetValue(name, out var a)) _acc[name] = a = new Acc();
            var c = cpuBy.GetValueOrDefault(name);
            a.CpuSum += c;
            a.CpuPeak = Math.Max(a.CpuPeak, c);
            if (c >= ActiveThreshold) a.Active++;
            a.IoSum += ioBy.GetValueOrDefault(name);
            a.RamPeak = Math.Max(a.RamPeak, ramBy.GetValueOrDefault(name));
        }
    }

    private bool IsExcluded(ProcSample p)
    {
        if (Ignored.Contains(p.Name) || string.Equals(p.Name, _game, StringComparison.OrdinalIgnoreCase)) return true;
        if (_gameDir is not null && p.Path is not null && p.Path.StartsWith(_gameDir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>En çok yük bindiren programlar (yük sırasına göre). Yeterli ölçüm yoksa boş.</summary>
    public BackgroundSummary Summarize(int max = 6)
    {
        if (_intervals < 6) return new BackgroundSummary();
        var items = _acc.Select(kv =>
        {
            var (display, kind) = Describe(kv.Key);
            return new BackgroundItem
            {
                Name = kv.Key, Display = display, Kind = kind,
                CpuAvgPercent = Math.Round(kv.Value.CpuSum / _intervals, 1),
                CpuPeakPercent = Math.Round(kv.Value.CpuPeak, 0),
                ActivePercent = (int)Math.Round(100.0 * kv.Value.Active / _intervals),
                DiskAvgMBps = Math.Round(kv.Value.IoSum / _intervals, 1),
                RamPeakMB = Math.Round(kv.Value.RamPeak, 0),
            };
        })
        .Where(i => i.CpuAvgPercent >= 0.5 || i.DiskAvgMBps >= 1 || i.RamPeakMB >= 800)
        .OrderByDescending(i => i.CpuAvgPercent + i.DiskAvgMBps / 10 + i.RamPeakMB / 2000)
        .Take(max)
        .ToList();
        return new BackgroundSummary { Items = items, TotalCpuAvgPercent = Math.Round(_totalCpuSum / _intervals, 1) };
    }

    private static readonly Dictionary<string, (string Display, string Kind)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ("Google Chrome", "app"), ["msedge"] = ("Microsoft Edge", "app"), ["firefox"] = ("Firefox", "app"),
        ["brave"] = ("Brave", "app"), ["opera"] = ("Opera", "app"), ["vivaldi"] = ("Vivaldi", "app"),
        ["discord"] = ("Discord", "app"), ["spotify"] = ("Spotify", "app"), ["teams"] = ("Microsoft Teams", "app"), ["ms-teams"] = ("Microsoft Teams", "app"),
        ["slack"] = ("Slack", "app"), ["whatsapp"] = ("WhatsApp", "app"), ["telegram"] = ("Telegram", "app"), ["zoom"] = ("Zoom", "app"),
        ["onedrive"] = ("OneDrive", "app"), ["dropbox"] = ("Dropbox", "app"), ["googledrivefs"] = ("Google Drive", "app"),
        ["code"] = ("Visual Studio Code", "app"), ["cursor"] = ("Cursor", "app"), ["claude"] = ("Claude", "app"), ["codex"] = ("Codex", "app"),
        ["steamwebhelper"] = ("Steam (arayüz)", "launcher"), ["steam"] = ("Steam", "launcher"), ["epicgameslauncher"] = ("Epic Games Launcher", "launcher"),
        ["eadesktop"] = ("EA uygulaması", "launcher"), ["origin"] = ("Origin", "launcher"), ["battle.net"] = ("Battle.net", "launcher"),
        ["galaxyclient"] = ("GOG Galaxy", "launcher"), ["ubisoftconnect"] = ("Ubisoft Connect", "launcher"), ["upc"] = ("Ubisoft Connect", "launcher"),
        ["obs64"] = ("OBS", "app"), ["wallpaper32"] = ("Wallpaper Engine", "app"), ["wallpaper64"] = ("Wallpaper Engine", "app"),
        ["msmpeng"] = ("Windows Defender taraması", "system"), ["mpdefendercoreservice"] = ("Windows Defender", "system"),
        ["searchindexer"] = ("Windows arama dizinleyicisi", "system"), ["tiworker"] = ("Windows Update", "system"),
        ["trustedinstaller"] = ("Windows Update", "system"), ["wuauclt"] = ("Windows Update", "system"), ["musnotification"] = ("Windows Update", "system"),
        ["compattelrunner"] = ("Windows veri toplama", "system"), ["svchost"] = ("Windows hizmetleri", "system"), ["wmiprvse"] = ("Windows yönetim hizmeti", "system"),
        ["explorer"] = ("Windows Gezgini", "system"), ["searchhost"] = ("Windows arama", "system"), ["runtimebroker"] = ("Windows", "system"),
        ["nvcontainer"] = ("NVIDIA hizmeti", "system"), ["nvidia overlay"] = ("NVIDIA kaplaması", "system"),
    };

    private static (string Display, string Kind) Describe(string name)
    {
        if (Known.TryGetValue(name, out var k)) return (Loc.T(k.Display), k.Kind);
        if (name.StartsWith("asus", StringComparison.OrdinalIgnoreCase) || name.StartsWith("armoury", StringComparison.OrdinalIgnoreCase)) return (name, "system");
        return (name, "other");
    }
}

/// <summary>Çalışan süreçlerin sayaçlarını okur (Windows). Erişilemeyen (korumalı) süreçler sessizce atlanır.</summary>
public static class ProcessSampler
{
    public static List<ProcSample> Take()
    {
        var list = new List<ProcSample>(256);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id is 0 or 4) continue;
                var h = OpenProcess(0x1400 /* QUERY_INFORMATION | QUERY_LIMITED_INFORMATION */, false, p.Id);
                var hasIo = h != IntPtr.Zero;
                if (!hasIo) h = OpenProcess(0x1000, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!GetProcessTimes(h, out var created, out _, out var kernel, out var user)) continue;
                    long io = 0;
                    if (hasIo && GetProcessIoCounters(h, out var c)) io = (long)(c.ReadTransferCount + c.WriteTransferCount);
                    string? path = null;
                    var sb = new StringBuilder(1024);
                    var size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                    list.Add(new ProcSample(p.Id, created, p.ProcessName, (kernel + user) / 1e7, io, p.WorkingSet64, path));
                }
                finally { CloseHandle(h); }
            }
            catch { /* süreç kapanmış olabilir */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
}
