using System.Security.Cryptography;

namespace Pulse.Core.Cleanup;

public sealed record DupFile(string Path, DateTime Modified, long Bytes);

/// <summary>Birebir aynı içerikli dosyalar. <see cref="Keeper"/> korunacak asıl kopyadır; diğerleri fazlalıktır.</summary>
public sealed class DuplicateGroup
{
    public required IReadOnlyList<DupFile> Files { get; init; }
    public long Size => Files[0].Bytes;

    /// <summary>Korunacak kopya: en eski değiştirilen, eşitse yolu en kısa olan (genellikle asıl dosya).</summary>
    public DupFile Keeper => Files[0];
    public long WastedBytes => Size * (Files.Count - 1);
}

/// <summary>
/// Kopya dosya bulucu. Ucuzdan pahalıya: boyut → baştan/sondan kısmi özet → tam özet. Yalnızca verilen kullanıcı klasörlerine bakar;
/// sistem, gizli, bağlantı (junction) ve buluttan indirilmemiş (OneDrive) dosyalara dokunmaz (okumak indirmeyi tetiklerdi).
/// Hiçbir şey silmez; silme Geri Dönüşüm Kutusu'na gider, korunan kopya asla silinmez.
/// </summary>
public static class DuplicateFinder
{
    public const long DefaultMinBytes = 1L * 1024 * 1024;
    private const int PartialBytes = 64 * 1024;

    // FileAttributes.RecallOnOpen (0x40000) ve RecallOnDataAccess (0x400000): OneDrive "yalnızca bulutta" dosyaları
    private const FileAttributes CloudOnly = (FileAttributes)0x40000 | (FileAttributes)0x400000 | FileAttributes.Offline;

    private static readonly string[] SkipFolders =
    [
        @"\AppData\", @"\node_modules\", @"\.git\", @"\Windows\", @"\Program Files", @"\$Recycle.Bin\", @"\ProgramData\", @"\.gradle\", @"\.nuget\",
    ];

    public static IEnumerable<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            System.IO.Path.Combine(profile, "Downloads"),
        };
        return roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<DuplicateGroup> Find(IEnumerable<string> roots, long minBytes = DefaultMinBytes, IProgress<string>? progress = null, CancellationToken ct = default, int maxGroups = 100)
    {
        // 1) Boyuta göre grupla (yalnızca dosya bilgisi, okuma yok)
        progress?.Report("Dosyalar listeleniyor…");
        var bySize = new Dictionary<long, List<FileInfo>>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint | FileAttributes.Temporary | CloudOnly,
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                if (ct.IsCancellationRequested) return [];
                if (f.Length < minBytes) continue;
                // Atlama kuralları seçilen klasöre göre göreli yola bakar (kök kendisi AppData altında olsa bile çalışır).
                var rel = "\\" + System.IO.Path.GetRelativePath(root, f.FullName);
                if (SkipFolders.Any(s => rel.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (!bySize.TryGetValue(f.Length, out var list)) bySize[f.Length] = list = new List<FileInfo>();
                list.Add(f);
            }
        }

        // aynı yola iki kez girmesin (kökler iç içe olabilir)
        var candidates = bySize.Values.Select(l => l.DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList()).Where(l => l.Count > 1).ToList();

        // 2) Baştan/sondan kısmi özet, 3) tam özet
        var groups = new List<DuplicateGroup>();
        var done = 0;
        foreach (var sizeGroup in candidates)
        {
            if (ct.IsCancellationRequested) return [];
            progress?.Report($"Karşılaştırılıyor ({++done}/{candidates.Count})…");
            foreach (var partial in GroupBy(sizeGroup, PartialHash, ct))
            {
                foreach (var full in GroupBy(partial, FullHash, ct))
                {
                    var ordered = full.OrderBy(f => f.LastWriteTime).ThenBy(f => f.FullName.Length).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                        .Select(f => new DupFile(f.FullName, f.LastWriteTime, f.Length)).ToList();
                    groups.Add(new DuplicateGroup { Files = ordered });
                }
            }
        }
        return groups.OrderByDescending(g => g.WastedBytes).Take(maxGroups).ToList();
    }

    private static IEnumerable<List<FileInfo>> GroupBy(List<FileInfo> files, Func<FileInfo, string?> hash, CancellationToken ct)
    {
        var map = new Dictionary<string, List<FileInfo>>();
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) yield break;
            var h = hash(f);
            if (h is null) continue;                          // okunamadı (kilitli, silindi): grupta sayma
            if (!map.TryGetValue(h, out var l)) map[h] = l = new List<FileInfo>();
            l.Add(f);
        }
        foreach (var l in map.Values) if (l.Count > 1) yield return l;
    }

    private static string? PartialHash(FileInfo f)
    {
        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buf = new byte[PartialBytes];
            var n = ReadFully(fs, buf);
            sha.TransformBlock(buf, 0, n, null, 0);
            if (f.Length > PartialBytes * 2)
            {
                fs.Seek(-PartialBytes, SeekOrigin.End);
                n = ReadFully(fs, buf);
                sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch { return null; }
    }

    private static string? FullHash(FileInfo f)
    {
        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { return null; }
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        var total = 0;
        while (total < buf.Length) { var n = s.Read(buf, total, buf.Length - total); if (n == 0) break; total += n; }
        return total;
    }

    /// <summary>
    /// Fazlalık kopyayı Geri Dönüşüm Kutusu'na gönderir. Korunan kopya asla silinmez; korunan kopya yoksa ya da dosya değiştiyse reddedilir.
    /// </summary>
    public static bool Remove(DuplicateGroup group, DupFile file, Func<string, bool>? sendToRecycleBin = null)
    {
        sendToRecycleBin ??= RecycleBin.SendToRecycleBin;
        if (string.Equals(file.Path, group.Keeper.Path, StringComparison.OrdinalIgnoreCase)) return false;      // asıl kopya
        if (!group.Files.Any(f => f.Path == file.Path)) return false;                                         // bu gruba ait değil
        try
        {
            var keeper = new FileInfo(group.Keeper.Path);
            var target = new FileInfo(file.Path);
            if (!keeper.Exists || !target.Exists) return false;                                                // son kopyayı kaybetme
            if (keeper.Length != group.Size || target.Length != group.Size) return false;                    // dosya sonradan değişmiş
            return sendToRecycleBin(file.Path);
        }
        catch { return false; }
    }
}
