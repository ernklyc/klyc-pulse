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

    private sealed record Template(System.Text.RegularExpressions.Regex Rx, int[] Order, string En);
    private static List<Template>? _templates;

    /// <summary>
    /// Türkçe kaydedilmiş bir metni (ör. önceki bir oyun raporunun bulgusu: sayılar zaten doldurulmuş) şimdiki dilde göstermek için:
    /// metin sözlükteki bir biçim şablonuyla (<c>"... {0} ... {1}"</c>) eşleşirse sayılar ayıklanıp İngilizce şablona yeniden doldurulur.
    /// Dil Türkçeyse, ya da eşleşme yoksa metin olduğu gibi döner.
    /// </summary>
    public static string Retranslate(string stored)
    {
        if (!IsEnglish || string.IsNullOrEmpty(stored)) return stored;
        if (Map().TryGetValue(stored, out var direct)) return direct;
        var whole = RetranslateOne(stored) ?? RetranslateAtCommas(stored);
        if (whole is not null) return whole;
        // Bulgular birkaç cümlenin art arda eklenmesiyle oluşur: cümle cümle dene
        var parts = System.Text.RegularExpressions.Regex.Split(stored, @"(?<=[.!?])(\s+)");
        if (parts.Length < 2) return stored;
        var changed = false;
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0 || string.IsNullOrWhiteSpace(part)) { sb.Append(part); continue; }
            var r = Map().TryGetValue(part, out var d1) ? d1 : Map().TryGetValue(" " + part, out var d2) ? d2.TrimStart() : RetranslateOne(part) ?? RetranslateAtCommas(part);
            if (r is not null) { changed = true; sb.Append(r); } else sb.Append(part);
        }
        return changed ? sb.ToString() : stored;
    }

    /// <summary>Cümle birden çok parçadan eklenmiş olabilir ("Ortalama 216 FPS" + ", en kötü ... {0} FPS."): virgül sınırlarında iki parçaya bölüp her ikisi de çevrilebiliyorsa birleştirir.</summary>
    private static string? RetranslateAtCommas(string text)
    {
        // Sınırlar: virgül (sağ parça virgülle başlar) ve ": " (sol parça ": " ile biter: "Otomatik ayar: " + metin)
        for (var i = 0; i < text.Length; i++)
        {
            int cut;
            if (text[i] == ',') cut = i;
            else if (text[i] == ':' && i + 1 < text.Length && text[i + 1] == ' ') cut = i + 2;
            else continue;
            if (cut <= 0 || cut >= text.Length) continue;
            var left = text[..cut]; var right = text[cut..];
            var l = Map().TryGetValue(left, out var d1) ? d1 : RetranslateOne(left);
            var r = Map().TryGetValue(right, out var d2) ? d2 : RetranslateOne(right);
            if (l is not null && r is not null) return l + r;
        }
        return null;
    }

    /// <summary>Tek bir cümleyi/şablonu çevirir; eşleşme yoksa null.</summary>
    private static string? RetranslateOne(string stored)
    {
        foreach (var t in Templates())
        {
            var m = t.Rx.Match(stored);
            if (!m.Success) continue;
            var args = new object?[t.Order.Max() + 1];
            for (var i = 0; i < t.Order.Length; i++)
            {
                var g = m.Groups[i + 1].Value;
                args[t.Order[i]] = double.TryParse(g, NumberStyles.Float, CultureInfo.GetCultureInfo("tr-TR"), out var d)
                    ? (g.Contains(',') ? d : (object)(long)d) : Retranslate(g);   // metin parçaları (ör. program adı + iç içe öneri cümlesi) de çevrilir
            }
            try { return string.Format(CultureInfo.CurrentCulture, t.En, args); } catch { return null; }
        }
        return null;
    }

    private static List<Template> Templates()
    {
        if (_templates is not null) return _templates;
        var list = new List<Template>();
        var ph = new System.Text.RegularExpressions.Regex(@"\{(\d+)(?::[^}]*)?\}");
        foreach (var (tr, en) in Map())
        {
            var ms = ph.Matches(tr);
            if (ms.Count == 0) continue;
            if (ph.Replace(tr, "").Trim().Length < 8) continue;   // neredeyse yalnızca yer tutucu olan şablonlar her metinle eşleşirdi
            var sb = new System.Text.StringBuilder("^");
            var order = new List<int>();
            var pos = 0;
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                sb.Append(System.Text.RegularExpressions.Regex.Escape(tr[pos..m.Index])).Append("(.*?)");
                order.Add(int.Parse(m.Groups[1].Value));
                pos = m.Index + m.Length;
            }
            sb.Append(System.Text.RegularExpressions.Regex.Escape(tr[pos..])).Append('$');
            try { list.Add(new Template(new System.Text.RegularExpressions.Regex(sb.ToString(), System.Text.RegularExpressions.RegexOptions.Singleline), order.ToArray(), en)); } catch { }
        }
        // Önce en çok sabit metin içeren şablon (en özgül): "{0}" gibi her şeyi yutanlar en sona
        _templates = list.OrderByDescending(t => t.Rx.ToString().Length).ToList();
        return _templates;
    }

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
