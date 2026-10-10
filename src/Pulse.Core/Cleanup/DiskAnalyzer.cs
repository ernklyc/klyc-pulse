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

    // Bulut yer tutucuları (OneDrive "yalnızca çevrimiçi" dosyalar) diskte yer kaplamaz ama Length tam boyutu söyler; sayılmamalı
    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | FileAttributes.Offline;

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Verilen köklerin her alt klasörünün boyutunu ölçer. Başka bir kökü içeren klasör atlanır (ör. profil içindeki AppData, kendi köküyle ayrıca taranıyor),
    /// böylece aynı klasör iki kez sayılıp listelenmez. Klasörler birkaç iş parçacığıyla paralel taranır.
    /// </summary>
    public static Task<IReadOnlyList<FolderSize>> AnalyzeAsync(IEnumerable<string> roots, long minBytes = 300L * 1024 * 1024, int top = 40, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<FolderSize>>(() =>
        {
            var rootList = roots.Select(r => r.TrimEnd('\\')).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dirs = new List<string>();
            foreach (var root in rootList)
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        if (rootList.Any(o => !o.Equals(root, StringComparison.OrdinalIgnoreCase) && IsUnder(o, dir))) continue;   // başka bir kökü kapsıyor
                        if (dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
                        dirs.Add(dir);
                    }
                }
                catch { /* kök okunamadı */ }
            }

            var found = new System.Collections.Concurrent.ConcurrentBag<FolderSize>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = ct };
            Parallel.ForEach(dirs, options, dir =>
            {
                progress?.Report(dir);
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
                    long bytes = 0;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Walk))
                    {
                        if ((f.Attributes & CloudOnly) != 0) continue;
                        bytes += f.Length;
                    }
                    if (bytes >= minBytes) found.Add(new FolderSize(dir, bytes, Directory.GetLastWriteTime(dir)));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* erişilemeyen klasör */ }
            });
            return found.OrderByDescending(f => f.Bytes).Take(top).ToList();
        }, ct);
}
