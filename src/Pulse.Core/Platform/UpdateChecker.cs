using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pulse.Core.Platform;

public sealed record UpdateInfo(string Tag, Version Version, string Url, string Notes, DateTime? Published);

/// <summary>Denetim sonucu: Latest dolu ve yeni ise güncelleme var; Error doluysa denetim yapılamadı (çevrimdışı, gizli depo, hız sınırı...).</summary>
public sealed record UpdateCheckResult(UpdateInfo? Latest, bool IsNewer, string? Error);

/// <summary>
/// GitHub'daki en son kararlı sürümü sorar (günde en fazla bir kez, ayarlardan kapatılabilir). Hiçbir şey indirmez ya da kurmaz;
/// yalnızca sürüm numarasını ve sürüm sayfasının adresini alır. İstek yalnızca standart bir HTTP isteğidir (kullanıcı kimliği, bilgisayar bilgisi gönderilmez).
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "ernklyc/klyc-pulse";
    public static string Endpoint => $"https://api.github.com/repos/{Repo}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public static async Task<UpdateCheckResult> CheckAsync(Version current, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(8);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KLYC-Pulse", current.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await http.GetAsync(Endpoint, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(null, false, "Sürüm bilgisi alınamadı (depo gizli ya da henüz sürüm yok).");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(null, false, "GitHub şu an çok istek aldığı için sürüm sorgusunu reddetti; daha sonra tekrar denenecek.");
            if (!resp.IsSuccessStatusCode)
                return new(null, false, $"Sürüm sorgusu başarısız (HTTP {(int)resp.StatusCode}).");

            var latest = Parse(await resp.Content.ReadAsStringAsync(ct));
            if (latest is null) return new(null, false, "Sürüm bilgisi okunamadı.");
            return new(latest, IsNewer(latest.Version, current), null);
        }
        catch (OperationCanceledException) { return new(null, false, "Sürüm sorgusu zaman aşımına uğradı."); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new(null, false, "İnternete bağlanılamadı; sürüm denetlenemedi."); }
    }

    /// <summary>GitHub "releases/latest" cevabından sürüm bilgisi. Taslak/ön sürüm ya da anlaşılmaz etiket varsa null.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTag(tag, out var v)) return null;
            var url = root.TryGetProperty("html_url", out var u) && u.GetString() is { Length: > 0 } hu ? hu : ReleasesPage;
            // Yalnızca github.com adreslerini kabul et (başka bir yere yönlendirmeyi engeller)
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            DateTime? published = root.TryGetProperty("published_at", out var pa) && pa.TryGetDateTime(out var dt) ? dt : null;
            return new UpdateInfo(tag, v, url, notes, published);
        }
        catch { return null; }
    }

    /// <summary>"v1.2.0", "1.2", "V1.10.3-beta" → sürüm. Anlaşılmazsa false.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s, out var v) || v.Major < 0) return false;
        version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return true;
    }

    public static bool IsNewer(Version latest, Version current) =>
        latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
