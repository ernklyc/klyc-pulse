using Pulse.Core.Localization;
using System.Text.RegularExpressions;

namespace Pulse.Core.Apps;

public enum BloatLevel { None, Optional, Recommended }

public sealed record BloatHint(BloatLevel Level, string Reason);

/// <summary>
/// "Gereksiz olabilir" ipuçları. Bilinçli olarak temkinli: otomatik silmez, yalnızca sebebini açıklayıp öneri sunar.
/// </summary>
public static class BloatCatalog
{
    private static readonly (Regex Pattern, BloatLevel Level, string Reason)[] Rules =
    [
        (R("McAfee|Norton|Avast|AVG |Kaspersky|Bitdefender Trial|ESET.*Trial"), BloatLevel.Recommended,
            Loc.T("Ek antivirüs, Windows Defender ile birlikte çalışınca sistemi yavaşlatır ve çakışır. Biri yeterli.")),
        (R("WildTangent|Booking\\.com|Dropbox Promotion|ExpressVPN.*Trial|Candy Crush|Bubble Witch|Farm Heroes"), BloatLevel.Recommended,
            Loc.T("Bilgisayarla birlikte gelen tanıtım yazılımı.")),
        (R("Toolbar|SearchProtect|Ask Toolbar|Conduit|BrowserSafeguard"), BloatLevel.Recommended,
            Loc.T("Tarayıcı araç çubuğu ya da arama yönlendirici, genellikle istenmeden kurulur.")),
        (R("Armoury Crate|ASUS Software Manager|ASUS System Analysis|ASUS System Diagnosis|ASUS Optimization"), BloatLevel.Optional,
            Loc.T("ASUS arka plan servisleri. KLYC-Pulse kullanıyorsan gerekmez, ama BIOS tuşları (ör. Fn kısayolları) buna bağlı olabilir.")),
        (R("GAMEPOWER|Nahimic|Sonic Studio|SonicRadar"), BloatLevel.Optional,
            Loc.T("Ses efekti yazılımı. Kullanmıyorsan arka planda kaynak harcar.")),
        (R("Java\\s*(8|7)|Adobe Flash|Silverlight"), BloatLevel.Optional,
            Loc.T("Eski, güvenlik açığı riski taşıyan bileşen. Hiçbir şey ona bağlı değilse kaldırılabilir.")),
        (R("Wolfteam|Metin2|Knight Online"), BloatLevel.Optional,
            Loc.T("Oyun istemcisi. Oynamıyorsan yer kaplıyor, istediğin zaman yeniden kurulur.")),
    ];

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static BloatHint? Evaluate(InstalledApp app)
    {
        foreach (var (pattern, level, reason) in Rules)
            if (pattern.IsMatch(app.Name) || pattern.IsMatch(app.Publisher))
                return new BloatHint(level, reason);
        return null;
    }
}