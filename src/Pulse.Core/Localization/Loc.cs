using System.Globalization;
using System.Reflection;

namespace Pulse.Core.Localization;

/// <summary>
/// Basit çeviri: kaynak dil Türkçe. <see cref="T"/> Türkçe metni verir, dil İngilizceyse sözlükteki karşılığını döner;
/// karşılığı yoksa Türkçesini aynen bırakır (yarım çeviri bile çalışır, hiçbir şey kırılmaz).
/// Sözlük, derleme içine gömülü <c>Localization/en*.tsv</c> dosyalarıdır (her satır: Türkçe &lt;TAB&gt; İngilizce; satır sonu için \n).
/// </summary>
public static class Loc
{
    private static Dictionary<string, string>? _en;
    private static HashSet<string>? _values;
    private static readonly object Gate = new();

    /// <summary>"tr" ya da "en".</summary>
    public static string Language { get; private set; } = "tr";
    public static bool IsEnglish => Language == "en";

    /// <summary>"tr", "en" ya da "auto" (Windows Türkçeyse Türkçe, değilse İngilizce).</summary>
    public static void Configure(string? setting, CultureInfo? ui = null)
    {
        var s = (setting ?? "auto").Trim().ToLowerInvariant();
        if (s is not ("tr" or "en")) s = (ui ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        Language = s;
        if (s == "en")
        {
            // Tarih, ay adı ve ondalık ayracı da İngilizce biçimde gelsin
            var c = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentCulture = c;
            CultureInfo.DefaultThreadCurrentUICulture = c;
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = c;
        }
    }

    /// <summary>
    /// Dili, ayar dosyasını (ve KLYC_PULSE_LANG ortam değişkenini) servisleri kurmadan okuyarak en başta belirler;
    /// böylece statik metin dizileri ve ilk açılan pencereler doğru dilde oluşur.
    /// </summary>
    public static void ConfigureEarly()
    {
        var setting = Environment.GetEnvironmentVariable("KLYC_PULSE_LANG");
        if (setting is null)
        {
            try
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "settings.json");
                if (File.Exists(file))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("Language", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String) setting = l.GetString();
                }
            }
            catch { /* ayar dosyası okunamazsa otomatik dil */ }
        }
        Configure(setting);
    }

    /// <summary>Metnin İngilizce karşılığı (dil İngilizceyse ve sözlükte varsa); yoksa metin olduğu gibi.</summary>
    public static string T(string tr)
    {
        if (!IsEnglish || string.IsNullOrEmpty(tr)) return tr;
        return Map().TryGetValue(tr, out var en) ? en : tr;
    }

    /// <summary>Biçim şablonunu çevirip doldurur: <c>Loc.F("Ortalama {0} FPS", 60)</c>.</summary>
    public static string F(string trFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(trFormat), args);

    /// <summary>Sözlükte karşılığı var mı (testler için).</summary>
    public static bool Has(string tr) => Map().ContainsKey(tr);

    public static int Count => Map().Count;

    /// <summary>Metin sözlükteki bir İngilizce çeviriyle birebir aynı mı? (sınama: "çevrilmiş mi, hiç tanınmıyor mu" ayrımı için)</summary>
    public static bool IsTranslatedText(string text) { Map(); return _values!.Contains(text); }

    private static Dictionary<string, string> Map()
    {
        if (_en is not null) return _en;
        lock (Gate)
        {
            if (_en is not null) return _en;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            var asm = typeof(Loc).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Localization.en", StringComparison.Ordinal) && n.EndsWith(".tsv", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
            {
                using var st = asm.GetManifestResourceStream(name);
                if (st is null) continue;
                using var rd = new StreamReader(st, System.Text.Encoding.UTF8);
                string? line;
                while ((line = rd.ReadLine()) is not null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    var key = Unescape(line[..tab]);
                    var val = Unescape(line[(tab + 1)..]);
                    if (val.Length > 0) d[key] = val;
                }
            }
            _en = d;
            _values = new HashSet<string>(d.Values, StringComparer.Ordinal);
            return d;
        }
    }

    private static string Unescape(string s) => s.Replace("\n", "\n").Replace("\t", "\t");
}
