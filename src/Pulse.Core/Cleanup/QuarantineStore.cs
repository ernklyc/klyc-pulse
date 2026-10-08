using Pulse.Core.Localization;
using System.Text.Json;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Cleanup;

public sealed record QuarantineItem(string Id, string OriginalPath, string StoredPath, long Bytes, DateTime Date, string Category);

/// <summary>Silinmesi riskli dosyalar için 7 günlük geri alınabilir karantina.</summary>
public sealed class QuarantineStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly string _root;
    private readonly string _index;
    private readonly object _gate = new();

    public QuarantineStore(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "Quarantine");
        _index = Path.Combine(_root, "index.json");
    }

    public string Root => _root;

    public QuarantineItem? Quarantine(string path, string category)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            var id = Guid.NewGuid().ToString("N")[..12];
            var dir = Path.Combine(_root, DateTime.Now.ToString("yyyyMMdd-HHmmss"), category);
            Directory.CreateDirectory(dir);
            var stored = Path.Combine(dir, id + "_" + info.Name);
            var bytes = info.Length;
            File.Move(path, stored);
            var item = new QuarantineItem(id, path, stored, bytes, DateTime.Now, category);
            lock (_gate) { var all = Load(); all.Add(item); Save(all); }
            return item;
        }
        catch (Exception ex)
        {
            Journal.Write($"Karantinaya alınamadı: {path}: {ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<QuarantineItem> List() { lock (_gate) return Load(); }

    /// <summary>Dosyayı özgün yerine geri koyar. Orada aynı adda dosya varsa kendi adının sonuna ekler.</summary>
    public bool Restore(string id)
    {
        lock (_gate)
        {
            var all = Load();
            var item = all.FirstOrDefault(i => i.Id == id);
            if (item is null || !File.Exists(item.StoredPath)) return false;
            try
            {
                var target = item.OriginalPath;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                    target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + Loc.T(" (geri yüklendi)") + Path.GetExtension(target));
                File.Move(item.StoredPath, target);
                all.Remove(item);
                Save(all);
                return true;
            }
            catch (Exception ex) { Journal.Write("Geri yüklenemedi: " + ex.Message); return false; }
        }
    }

    /// <summary>7 günü geçen karantina kayıtlarını kalıcı siler. Boşalan alanı döndürür.</summary>
    public long PurgeExpired(DateTime? now = null)
    {
        long freed = 0;
        var limit = (now ?? DateTime.Now) - Retention;
        lock (_gate)
        {
            var all = Load();
            foreach (var item in all.Where(i => i.Date < limit).ToList())
            {
                try { if (File.Exists(item.StoredPath)) File.Delete(item.StoredPath); freed += item.Bytes; all.Remove(item); }
                catch (Exception ex) { Journal.Write("Karantina süresi dolan dosya silinemedi: " + ex.Message); }
            }
            Save(all);
        }
        return freed;
    }

    private List<QuarantineItem> Load()
    {
        try { return JsonSerializer.Deserialize<List<QuarantineItem>>(File.ReadAllText(_index)) ?? []; }
        catch { return []; }
    }

    private void Save(List<QuarantineItem> items)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_index, JsonSerializer.Serialize(items));
    }
}