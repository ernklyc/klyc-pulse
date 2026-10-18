using Pulse.Core.Automation;
using Pulse.Core.Diagnostics;
using Pulse.Core.Localization;
using Pulse.Core.Settings;

namespace Pulse.Core.Companion;

/// <summary>Konsol'un "oyunda kapatılacak programlar" satırı: seçilebilir adaylar ve seçililer (özette `bgClose`).</summary>
public sealed record PulseFeedBgClose(List<string> Selected, List<string> Candidates);

/// <summary>Konsol → Pulse "bgclose" komutunun saf mantığı: listeye ekle/çıkar (korumalı programlar eklenemez) ve aday listesi.</summary>
public static class KonsolBgClose
{
    public const int MaxSelected = 12;

    /// <summary>mode: "add" | "remove". Ayar değiştirilir (kaydetme çağırana ait). Dönüş: kullanıcıya gösterilecek sonuç.</summary>
    public static (bool Ok, string Message) Apply(AppSettings settings, string mode, string name)
    {
        name = (name ?? "").Trim();
        var existing = settings.GameCloseApps.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (mode == "remove")
        {
            if (existing is null) return (false, Loc.F("{0} listede yok.", name));
            settings.GameCloseApps.Remove(existing);
            return (true, Loc.F("{0}: oyun sırasında artık kapatılmayacak.", name));
        }
        if (existing is not null) return (true, Loc.F("{0} zaten listede.", name));
        var (ok, why) = GameHelperCloser.Validate(name);
        if (!ok) return (false, Loc.F("{0} eklenemedi: {1}", name, Loc.T(why ?? "Korumalı program.")));
        if (settings.GameCloseApps.Count >= MaxSelected) return (false, Loc.T("Liste dolu (en çok 12 program)."));
        settings.GameCloseApps.Add(name);
        return (true, Loc.F("{0}: oyun açılınca kapatılacak, oyun bitince geri açılacak.", name));
    }

    /// <summary>Son raporlarda arka planı yoran, kapatılabilir görünen programlar (Windows bileşenleri ve başlatıcılar hariç) + zaten seçililer.</summary>
    public static PulseFeedBgClose Feed(AppSettings settings, IReadOnlyList<GameSessionReport> history)
    {
        var games = history.Select(r => new string((r.Game ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).ToHashSet();
        var cands = history.TakeLast(10).SelectMany(r => r.Background)
            .Where(b => b.Kind is not ("system" or "launcher") && b.Name.Length > 0 && GameHelperCloser.Validate(b.Name).Ok
                        && !games.Contains(new string(b.Name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()))
            .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(b => b.CpuAvgPercent)).Take(8).Select(g => g.Key)
            .Where(n => !settings.GameCloseApps.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        return new PulseFeedBgClose(settings.GameCloseApps.ToList(), cands);
    }
}
