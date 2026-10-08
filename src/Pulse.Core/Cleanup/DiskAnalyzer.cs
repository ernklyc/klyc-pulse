namespace Pulse.Core.Cleanup;

public sealed record FolderSize(string Path, long Bytes, DateTime LastWrite);

/// <summary>Disk neyle dolu? Büyük klasörleri listeler. Hiçbir şey silmez, karar kullanıcıya ait.</summary>
public static class DiskAnalyzer
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static IReadOnlyList<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
            @"C:\Program Files (x86)\Steam\steamapps\common", @"C:\Program Files\Epic Games", @"C:\Riot Games",
        };
        return roots.Where(Directory.Exists).ToList();
    }

    public static Task<IReadOnlyList<FolderSize>> AnalyzeAsync(IEnumerable<string> roots, long minBytes = 300L * 1024 * 1024, int top = 40, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<FolderSize>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var found = new List<FolderSize>();
            foreach (var root in roots)
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(dir);
                    try
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                        long bytes = 0;
                        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Walk)) bytes += f.Length;
                        if (bytes >= minBytes) found.Add(new FolderSize(dir, bytes, Directory.GetLastWriteTime(dir)));
                    }
                    catch { /* erişilemeyen klasör */ }
                }
            }
            return found.OrderByDescending(f => f.Bytes).Take(top).ToList();
        }, ct);
}