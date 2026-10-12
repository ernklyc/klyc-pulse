using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Pulse.Core.Diagnostics;
using Pulse.Core.Localization;
using Pulse.Core.Modes;

namespace Pulse.Core.Companion;

/// <summary>KLYC Konsol'un Pulse'a gönderdiği tek komut (`%AppData%\KLYC-Konsol\pulse-command.json`).</summary>
/// <param name="Action">"mode" (Mode alanındaki modu uygula) ya da "boost" (Hızlandır)</param>
public sealed record KonsolCommand(string Id, string Action, string? Mode, long At);

public sealed record KonsolCommandCheck(KonsolCommand? Command, string? Rejected)
{
    public bool Ok => Command is not null;
}

/// <summary>Konsol'a geri bildirilen komut sonucu (feed içinde `lastCommand`).</summary>
public sealed record PulseFeedCommand(string Id, string Action, bool Ok, string Message, long At);

/// <summary>
/// Konsol → Pulse komut kanalı: saf doğrulama mantığı. Kanal bilerek DAR: yalnızca bilinen modlardan birini uygula ya da Hızlandır
/// (ikisi de kullanıcının Pulse'ta zaten elle yapabildiği, geri alınabilir işlemler). Dosya kullanıcı profilindedir; yetkili Pulse'ın
/// başka bir programın bıraktığı dosyaya uymaması için her komut: boyut sınırı, sahiplik, bağlantı noktası (reparse) yok, yaş sınırı,
/// kimlik tekrarı yok, hız sınırı ve izin listesi denetimlerinden geçer. Varsayılan KAPALI (Pulse ayarı: AllowKonsolCommands).
/// </summary>
public static class KonsolCommands
{
    public const int MaxBytes = 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(20);
    public static readonly string[] Actions = { "mode", "boost" };

    public static string CommandPath => Path.Combine(KonsolLink.KonsolDir, "pulse-command.json");

    /// <summary>JSON'u çözer ve izin listesine göre doğrular. <paramref name="seen"/>: daha önce işlenen kimlikler (tekrar saldırısı).</summary>
    public static KonsolCommandCheck Parse(string json, long now, IReadOnlyCollection<string>? seen = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return Reject("boyut");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Reject("biçim");
            var schema = r.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv) ? sv : 1;
            if (schema != 1) return Reject("şema");
            var id = Str(r, "id");
            if (id is null || id.Length is < 8 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return Reject("kimlik");
            if (seen?.Contains(id) == true) return Reject("tekrar");
            var action = Str(r, "action")?.Trim().ToLowerInvariant();
            if (action is null || Array.IndexOf(Actions, action) < 0) return Reject("eylem");
            var at = r.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var av) ? av : 0;
            var age = now - at;
            if (age > MaxAge.TotalSeconds || age < -5) return Reject("yaş");
            string? mode = null;
            if (action == "mode")
            {
                mode = Str(r, "mode")?.Trim().ToLowerInvariant();
                if (mode is null || Modes.Modes.Get(mode) is null) return Reject("mod");
            }
            return new KonsolCommandCheck(new KonsolCommand(id, action, mode, at), null);
        }
        catch (JsonException) { return Reject("json"); }

        static KonsolCommandCheck Reject(string why) => new(null, why);
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Dosya güvenli mi: yoksa/yönlendirme/çok büyük/başkasına ait ise gerekçe döner; uygunsa null.</summary>
    public static string? CheckFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "yok";
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "bağlantı noktası";
            if (info.Length > MaxBytes) return "boyut";
            var parent = info.Directory;
            if (parent is not null && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "klasör bağlantı noktası";
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return IsTrustedOwner(owner) ? null : "sahip";
        }
        catch (Exception ex) { Journal.Write("Konsol komut dosyası denetlenemedi: " + ex.Message); return "okunamadı"; }
    }

    /// <summary>Dosyayı sahibi bu kullanıcı (ya da Yöneticiler / SYSTEM) ise güvenilir sayar.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner)
    {
        if (owner is null) return false;
        var me = WindowsIdentity.GetCurrent().User;
        if (me is not null && owner.Equals(me)) return true;
        return owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid);
    }

    /// <summary>Komut dosyasını atomik yazar (testler ve Konsol'un kendi yazımı için örnek biçim).</summary>
    public static string Serialize(KonsolCommand c) => JsonSerializer.Serialize(new { schema = 1, id = c.Id, action = c.Action, mode = c.Mode, at = c.At });
}

/// <summary>
/// Komutu alıp uygular. Donanıma doğrudan dokunmaz: modu <see cref="IModeSwitch"/>, Hızlandır'ı verilen temsilci yapar (testlerde sahteleri verilir).
/// Her komut sonunda dosya silinir; sonuç `PulseFeedCommand` olarak döner (feed'e yazılıp Konsol'a bildirilir).
/// </summary>
public sealed class KonsolCommandRunner
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    private readonly IModeSwitch _modes;
    private readonly Func<Task<string>> _boost;
    private readonly Func<bool> _allowed;
    private readonly Func<bool> _konsolOpen;
    private readonly Queue<string> _seen = new();
    private DateTime _lastRun = DateTime.MinValue;
    private int _busy;

    public KonsolCommandRunner(IModeSwitch modes, Func<Task<string>> boost, Func<bool> allowed, Func<bool> konsolOpen)
    {
        _modes = modes; _boost = boost; _allowed = allowed; _konsolOpen = konsolOpen;
    }

    /// <summary>Komut dosyasını işler; dosya yoksa null. Dosya her durumda silinir.</summary>
    public async Task<PulseFeedCommand?> ProcessAsync(string? path = null, long? now = null)
    {
        path ??= KonsolCommands.CommandPath;
        var t = now ?? KonsolLink.UnixNow();
        if (!File.Exists(path)) return null;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;   // bir komut sürerken ikincisi (aynı dosya olayı) yok sayılır
        string? json = null;
        try
        {
            var bad = KonsolCommands.CheckFile(path);
            if (bad is null) { try { json = File.ReadAllText(path); } catch { bad = "okunamadı"; } }
            TryDelete(path);
            if (bad is not null) return Fail("?", "?", Loc.F("Komut reddedildi ({0}).", bad), t, bad);

            var check = KonsolCommands.Parse(json!, t, _seen);
            if (!check.Ok) return Fail("?", "?", Loc.F("Komut reddedildi ({0}).", check.Rejected), t, check.Rejected!);
            var c = check.Command!;
            Remember(c.Id);

            if (!_allowed()) return Fail(c.Id, c.Action, Loc.T("Pulse'ta \"Konsol kumandası\" kapalı: Pulse → Ayarlar'dan açılabilir."), t, "kapalı");
            if (!_konsolOpen()) return Fail(c.Id, c.Action, Loc.T("Konsol açık görünmüyor."), t, "konsol yok");
            if (DateTime.UtcNow - _lastRun < MinGap) return Fail(c.Id, c.Action, Loc.T("Çok sık komut: biraz bekle."), t, "hız sınırı");
            _lastRun = DateTime.UtcNow;

            try
            {
                if (c.Action == "mode")
                {
                    var def = Modes.Modes.Get(c.Mode!)!;
                    var result = await _modes.ApplyAsync(def.Key, null);
                    if (result is null) return Fail(c.Id, c.Action, Loc.T("Başka bir işlem sürüyor, sonra tekrar dene."), t, "meşgul");
                    var failed = result.Steps.Count(s => s.Status == StepStatus.Failed);
                    Journal.Write($"Konsol komutu: {def.Key} modu uygulandı ({failed} doğrulanamayan adım).");
                    return new PulseFeedCommand(c.Id, c.Action, true, failed == 0 ? Loc.F("{0} modu uygulandı.", Loc.T(def.Title)) : Loc.F("{0} modu uygulandı ({1} ayar doğrulanamadı).", Loc.T(def.Title), failed), KonsolLink.UnixNow());
                }
                var summary = await _boost();
                Journal.Write("Konsol komutu: Hızlandır çalıştı: " + summary);
                return new PulseFeedCommand(c.Id, c.Action, true, summary, KonsolLink.UnixNow());
            }
            catch (Exception ex)
            {
                Journal.Write("Konsol komutu başarısız: " + ex.Message);
                return Fail(c.Id, c.Action, Loc.T("Komut uygulanamadı."), t, "hata");
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static PulseFeedCommand Fail(string id, string action, string message, long at, string why)
    {
        Journal.Write($"Konsol komutu yapılmadı ({why}).");
        return new PulseFeedCommand(id, action, false, message, at);
    }

    private void Remember(string id) { _seen.Enqueue(id); while (_seen.Count > 64) _seen.Dequeue(); }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
