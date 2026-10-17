using Pulse.Core.Localization;
using Pulse.Core.Settings;

namespace Pulse.Core.Companion;

/// <summary>
/// Konsol'dan "bu oyun için Pulse modu" (oyun başına profil) — saf mantık. Pulse'un oyun profilleri oyun ilk oynandığında kendiliğinden
/// oluşur (exe adıyla); Konsol yalnızca var olan profilin modunu değiştirir ya da bu oyunda otomatik geçişi kapatır. Profil yoksa YENİSİ
/// UYDURULMAZ: Konsol'un yalnızca oyun adını bilir, yanlış bir exe adı otomatik algılamayı bozardı.
/// </summary>
public static class KonsolGameProfiles
{
    public const int MaxInFeed = 60;

    /// <summary>Harf ve rakam dışı karakterler ve büyük/küçük harf yok sayılır ("The Witcher 3" = "thewitcher3").</summary>
    public static string Normalize(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static GameProfile? Find(IEnumerable<GameProfile> profiles, string title)
    {
        var n = Normalize(title);
        if (n.Length == 0) return null;
        return profiles.FirstOrDefault(p => Normalize(p.DisplayName) == n || Normalize(p.ExeName) == n);
    }

    /// <summary>mode: mod anahtarı ya da "off". Dönüş: kullanıcıya gösterilecek sonuç; Ok=false ise hiçbir şey değişmedi.</summary>
    public static (bool Ok, string Message) Apply(AppSettings settings, string title, string mode)
    {
        var p = Find(settings.GameProfiles, title);
        if (p is null) return (false, Loc.T("Pulse bu oyunu henüz tanımıyor: oyunu bir kez oynayınca profili kendiliğinden oluşur."));
        if (mode == "off")
        {
            p.Enabled = false;
            return (true, Loc.F("{0}: bu oyunda otomatik mod geçişi kapatıldı.", p.DisplayName));
        }
        var def = Pulse.Core.Modes.Modes.Get(mode);
        if (def is null) return (false, Loc.T("Bilinmeyen mod."));
        p.ModeKey = def.Key; p.Enabled = true;
        return (true, Loc.F("{0}: bu oyun için {1} modu seçildi.", p.DisplayName, Loc.T(def.Title)));
    }

    /// <summary>Feed için profil özeti (adı olanlar; en çok <see cref="MaxInFeed"/>).</summary>
    public static List<PulseFeedGameProfile> ToFeed(IEnumerable<GameProfile> profiles) =>
        profiles.Where(p => !string.IsNullOrWhiteSpace(p.DisplayName)).Take(MaxInFeed)
            .Select(p => new PulseFeedGameProfile(p.DisplayName, p.ModeKey, p.Enabled)).ToList();
}
