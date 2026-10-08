using Pulse.Core.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core.Apps;
using Pulse.Core.Automation;
using Pulse.Core.Cleanup;
using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;

namespace Pulse.Core.Optimize;

public sealed record OptStep(string Name, bool Ok, string Detail);
public sealed record OptSnapshot(long FreeRamBytes, long FreeDiskBytes, int StartupEnabled, string? ModeKey);

public sealed record OptimizeReport(OptSnapshot Before, OptSnapshot After, IReadOnlyList<OptStep> Steps, long CleanedBytes)
{
    public long RamGained => After.FreeRamBytes - Before.FreeRamBytes;
    public bool AllOk => Steps.All(s => s.Ok);
}

/// <summary>
/// Tek tuşla "Hızlandır": zararsız temizlik, bellek rahatlatma, mod ayarlarını doğrulama, Windows oyun ayarı
/// denetimi ve açılış taraması. Her adım ilerleme bildirir; önce/sonra ölçümü gerçektir.
/// </summary>
public sealed class Optimizer
{
    public const int StepCount = 7;

    private readonly ModeController? _modes;
    private readonly CleanupEngine _engine = new();

    public Optimizer(ModeController? modes = null) => _modes = modes;

    public async Task<OptimizeReport> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<OptStep>();

        progress?.Report(Loc.T("Önce ölçüm alınıyor…"));
        var before = await Task.Run(Snapshot, ct);

        // 1) Zararsız temizlik
        progress?.Report(Loc.T("Zararsız geçici dosyalar temizleniyor…"));
        long cleaned = 0;
        try
        {
            var categories = CleanupCatalog.Build().Where(c => c.AutoSafe).ToList();
            var results = await _engine.CleanAsync(categories, ct: ct);
            cleaned = results.Sum(r => r.FreedBytes);
            var skipped = results.Count(r => r.Note is not null);
            steps.Add(new(Loc.T("Geçici dosyalar"), true,
                cleaned > 0 ? $"{Format(cleaned)} temizlendi" : Loc.T("Temizlenecek bir şey yoktu") + (skipped > 0 ? Loc.F(" ({0} kategori yönetici izni gerektirdiği için atlandı)", skipped) : "")));
        }
        catch (Exception ex) { steps.Add(new(Loc.T("Geçici dosyalar"), false, ex.Message)); }

        // 2) Bellek rahatlatma
        progress?.Report(Loc.T("Arka plandaki uygulamaların belleği rahatlatılıyor…"));
        steps.Add(await Task.Run(TrimMemory, ct));

        // 3) Mod ayarlarını doğrula (başka bir araç bozmuş olabilir)
        progress?.Report(Loc.T("Güç ve mod ayarları doğrulanıyor…"));
        steps.Add(await VerifyModeAsync());

        // 4) Windows oyun ayarları
        progress?.Report(Loc.T("Windows oyun ayarları denetleniyor…"));
        steps.Add(await Task.Run(() =>
        {
            var items = WindowsGameSettings.Read();
            var bad = items.Where(i => !i.IsGood).ToList();
            return bad.Count == 0
                ? new OptStep(Loc.T("Windows oyun ayarları"), true, "Hepsi uygun: " + string.Join(", ", items.Select(i => i.Name)))
                : new OptStep(Loc.T("Windows oyun ayarları"), false, Loc.T("Düzeltilebilir: ") + string.Join(", ", bad.Select(i => i.Name)));
        }, ct));

        // 5) Açılış öğeleri
        progress?.Report(Loc.T("Açılışta başlayan uygulamalar taranıyor…"));
        steps.Add(await Task.Run(() =>
        {
            var enabled = StartupManager.List().Where(s => s.Enabled).ToList();
            return new OptStep(Loc.T("Açılış öğeleri"), true,
                Loc.F("{0} uygulama Windows ile başlıyor. Gereksiz olanları Uygulamalar > Başlangıç'tan kapatabilirsin.", enabled.Count));
        }, ct));

        progress?.Report(Loc.T("Sonra ölçüm alınıyor…"));
        await Task.Delay(400, ct);
        var after = await Task.Run(Snapshot, ct);
        Journal.Write($"Hızlandır: temizlenen {cleaned} bayt, RAM farkı {after.FreeRamBytes - before.FreeRamBytes} bayt.");
        return new OptimizeReport(before, after, steps, cleaned);
    }

    private async Task<OptStep> VerifyModeAsync()
    {
        if (_modes is null || _modes.CurrentKey is not { } key) return new(Loc.T("Mod ayarları"), true, Loc.T("Henüz bir mod seçilmemiş, dokunulmadı."));
        var result = await _modes.ApplyAsync(key);
        if (result is null) return new(Loc.T("Mod ayarları"), true, Loc.T("Başka bir işlem sürüyordu, atlandı."));
        var bad = result.Steps.Count(s => s.Status == StepStatus.Failed);
        return new(Loc.T("Mod ayarları"), bad == 0, bad == 0 ? Loc.F("{0} modu yeniden uygulandı ve doğrulandı.", result.Mode.Title) : Loc.F("{0} ayar doğrulanamadı.", bad));
    }

    public static OptSnapshot Snapshot()
    {
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace; } catch { }
        var startup = 0;
        try { startup = StartupManager.List().Count(s => s.Enabled); } catch { }
        return new OptSnapshot(AvailableRam(), free, startup, null);
    }

    // ---- Bellek ------------------------------------------------------------

    private static readonly HashSet<string> NeverTrim = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "audiodg", "MsMpEng", "SecurityHealthService", "vgc", "vgtray", "KLYC-Pulse",
    };

    /// <summary>Etkin pencere, sistem ve oyun dışındaki süreçlerin çalışma kümesini küçültür. Bellek sayfalanır, hiçbir şey kapanmaz.</summary>
    private static OptStep TrimMemory()
    {
        var before = AvailableRam();
        var fg = ForegroundProcessId();
        var game = GameDetector.FindRunningGame();
        var trimmed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == fg || p.Id == Environment.ProcessId || p.SessionId == 0) continue;
                if (NeverTrim.Contains(p.ProcessName)) continue;
                if (game is not null && p.ProcessName.Equals(game, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.WorkingSet64 < 50L * 1024 * 1024) continue;
                if (EmptyWorkingSet(p.Handle)) trimmed++;
            }
            catch { /* erişilemeyen süreç: atla */ }
            finally { p.Dispose(); }
        }
        Thread.Sleep(700);
        var gained = AvailableRam() - before;
        return new OptStep("Bellek", true,
            trimmed == 0 ? Loc.T("Rahatlatılacak büyük arka plan uygulaması yok.")
            : gained >= 100L * 1024 * 1024 ? Loc.F("{0} uygulamanın belleği rahatlatıldı, {1} boşaldı (geçici: uygulamalar kullandıkça geri alır, hiçbiri kapanmadı).", trimmed, Format(gained))
            : Loc.F("{0} uygulama denendi, fark küçük ({1}). Windows belleği zaten iyi yönetiyor.", trimmed, Format(Math.Max(gained, 0))));
    }

    // ---- Yardımcılar -------------------------------------------------------

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N0} MB" : $"{bytes / 1024.0:N0} KB";

    public static long AvailableRamBytes() => AvailableRam();

    public static long TotalRamBytes()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? (long)m.TotalPhys : 0;
    }

    private static long AvailableRam()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : 0;
    }

    private static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
