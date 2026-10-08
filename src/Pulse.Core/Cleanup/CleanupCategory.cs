namespace Pulse.Core.Cleanup;

public enum CleanupSafety
{
    /// <summary>Yeniden oluşan önbellek/geçici dosya: doğrudan silinir.</summary>
    Regenerable,
    /// <summary>Kullanıcının dosyası olabilir: silinmez, karantinaya alınır (geri alınabilir).</summary>
    Quarantine,
}

/// <summary>Bir temizlik kategorisi: nerede aranır, hangi dosyalar uygundur.</summary>
public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    public CleanupSafety Safety { get; init; } = CleanupSafety.Regenerable;
    public bool SelectedByDefault { get; init; } = true;
    public bool NeedsAdmin { get; init; }

    /// <summary>Tamamen zararsız: otomatik haftalık temizlikte onaysız çalışabilir (kullanıcı özelliği açtıysa).</summary>
    public bool AutoSafe { get; init; }

    /// <summary>Bu günden yeni dosyalara dokunulmaz (0 = hepsi).</summary>
    public int MinAgeDays { get; init; }

    /// <summary>Dosya filtresi (null = hepsi). Yol küçük harfe çevrilmiş olarak verilir.</summary>
    public Func<FileInfo, bool>? Filter { get; init; }

    /// <summary>Bu yol parçalarını içeren dosyalar atlanır.</summary>
    public IReadOnlyList<string> SkipPathContains { get; init; } = [];

    /// <summary>Temizlemeden önce durdurulup sonra başlatılacak servisler.</summary>
    public IReadOnlyList<string> StopServices { get; init; } = [];

    /// <summary>Özel işlem (geri dönüşüm kutusu, DISM).</summary>
    public string? Special { get; init; }
}

public sealed record CategoryScan(CleanupCategory Category, long Bytes, int Files, string? Note = null)
{
    public bool Available => Note is null;
}