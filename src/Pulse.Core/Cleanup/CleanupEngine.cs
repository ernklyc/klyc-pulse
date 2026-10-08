using Pulse.Core.Localization;
using System.Diagnostics;
using System.Security.Principal;
using Pulse.Core.Diagnostics;
using Pulse.Core.Platform;

namespace Pulse.Core.Cleanup;

public sealed record CategoryClean(CleanupCategory Category, long FreedBytes, int Files, int Skipped, string? Note = null);

public sealed class CleanupEngine
{
    private readonly QuarantineStore _quarantine;
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // sembolik bağlantıları asla izleme
    };

    public CleanupEngine(QuarantineStore? quarantine = null) => _quarantine = quarantine ?? new QuarantineStore();

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---- Tarama ------------------------------------------------------------
    public Task<IReadOnlyList<CategoryScan>> ScanAsync(IEnumerable<CleanupCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CategoryScan>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // fanları ve diski zorlamasın
            var result = new List<CategoryScan>();
            foreach (var c in categories)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(c.Name);
                result.Add(Scan(c));
            }
            return result;
        }, ct);

    public CategoryScan Scan(CleanupCategory c)
    {
        if (c.NeedsAdmin && !IsAdmin) return new CategoryScan(c, 0, 0, Loc.T("Yönetici izni gerekir"));
        if (c.Special == "recycle") { var (b, n) = RecycleBin.Query(); return new CategoryScan(c, b, (int)n); }
        if (c.Special == "dism") return new CategoryScan(c, 0, 0, null);

        long bytes = 0; var files = 0;
        foreach (var f in Eligible(c)) { bytes += f.Length; files++; }
        return new CategoryScan(c, bytes, files);
    }

    // ---- Temizlik ----------------------------------------------------------
    public Task<IReadOnlyList<CategoryClean>> CleanAsync(IEnumerable<CleanupCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<CategoryClean>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var result = new List<CategoryClean>();
            foreach (var c in categories)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(c.Name);
                var r = Clean(c);
                result.Add(r);
                Journal.Write($"Temizlik: {c.Name}: {r.FreedBytes} bayt, {r.Files} dosya, {r.Skipped} atlandı. {r.Note}");
            }
            return result;
        }, ct);

    public CategoryClean Clean(CleanupCategory c)
    {
        if (c.NeedsAdmin && !IsAdmin) return new CategoryClean(c, 0, 0, 0, Loc.T("Yönetici izni gerekir"));

        if (c.Special == "recycle")
        {
            var (before, n) = RecycleBin.Query();
            return RecycleBin.Empty() ? new CategoryClean(c, before, (int)n, 0) : new CategoryClean(c, 0, 0, 0, Loc.T("Boşaltılamadı"));
        }
        if (c.Special == "dism") return RunDism(c);

        var stopped = new List<string>();
        try
        {
            foreach (var name in c.StopServices)
            {
                try
                {
                    if (ServiceControl.IsRunning(name) && ServiceControl.Stop(name)) stopped.Add(name);
                }
                catch (Exception ex) { Journal.Write($"Servis durdurulamadı ({name}): {ex.Message}"); }
            }

            long freed = 0; int files = 0, skipped = 0;
            foreach (var f in Eligible(c).ToList())
            {
                try
                {
                    var size = f.Length;
                    if (c.Safety == CleanupSafety.Quarantine)
                    {
                        if (_quarantine.Quarantine(f.FullName, c.Id) is null) { skipped++; continue; }
                    }
                    else
                    {
                        if (f.IsReadOnly) f.IsReadOnly = false;
                        f.Delete();
                    }
                    freed += size; files++;
                }
                catch { skipped++; } // kullanımda ya da izin yok: dokunma
            }

            if (c.Safety == CleanupSafety.Regenerable) RemoveEmptyDirectories(c);
            return new CategoryClean(c, freed, files, skipped, skipped > 0 ? Loc.F("{0} dosya kullanımda, atlandı", skipped) : null);
        }
        finally
        {
            foreach (var name in stopped)
            {
                try { ServiceControl.Start(name); } catch { /* servis kendiliğinden tetiklenir */ }
            }
        }
    }

    // ---- Yardımcılar -------------------------------------------------------
    /// <summary>Kategorinin kurallarına uyan dosyalar. Katalog dışına hiç çıkmaz.</summary>
    public IEnumerable<FileInfo> Eligible(CleanupCategory c)
    {
        var cutoff = c.MinAgeDays > 0 ? DateTime.Now.AddDays(-c.MinAgeDays) : DateTime.MaxValue;
        foreach (var root in c.Roots)
        {
            if (!IsSafeRoot(root) || !Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", Walk))
            {
                FileInfo f;
                try { f = new FileInfo(path); } catch { continue; }
                if (c.SkipPathContains.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (c.MinAgeDays > 0 && f.LastWriteTime >= cutoff) continue;
                if (c.Filter is not null && !c.Filter(f)) continue;
                yield return f;
            }
        }
    }

    /// <summary>Sürücü kökü ve kullanıcı profili kökü gibi tehlikeli yerlere izin verilmez.</summary>
    private static bool IsSafeRoot(string root)
    {
        try
        {
            var full = Path.GetFullPath(root).TrimEnd('\\');
            var depth = full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth < 3) return false;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            return !string.Equals(full, profile, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void RemoveEmptyDirectories(CleanupCategory c)
    {
        foreach (var root in c.Roots)
        {
            if (!IsSafeRoot(root) || !Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root, "*", Walk).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }
    }

    private static CategoryClean RunDism(CleanupCategory c)
    {
        try
        {
            var psi = new ProcessStartInfo("Dism.exe", "/Online /Cleanup-Image /StartComponentCleanup")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15 * 60 * 1000);
            return new CategoryClean(c, 0, 0, 0, p.ExitCode == 0 ? Loc.T("Bileşen deposu temizlendi (alan kazancı sistem tarafından hesaplanır)") : $"DISM hata kodu {p.ExitCode}");
        }
        catch (Exception ex) { return new CategoryClean(c, 0, 0, 0, ex.Message); }
    }
}