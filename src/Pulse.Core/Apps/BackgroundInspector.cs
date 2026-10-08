using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Pulse.Core.Diagnostics;
using Pulse.Core.Platform;

namespace Pulse.Core.Apps;

public enum BgAdvice { Keep, Optional, Unknown }

/// <summary>Windows ile birlikte başlayan üçüncü taraf bir servis ya da zamanlanmış görev.</summary>
public sealed record BgItem(string Kind, string Name, string Display, string Path, bool StartsAtBoot, bool Running, BgAdvice Advice, string Reason)
{
    public bool IsService => Kind == "Servis";
    public string Key => $"{Kind}:{Name}";
}

/// <summary>
/// Üçüncü taraf servisleri ve oturum açılışında çalışan görevleri listeler, bilinenleri sınıflandırır
/// ("dokunma", "kapatılabilir", "bilinmiyor"). Kapatma silme değildir: servis "elle başlat"a alınır, görev devre dışı
/// bırakılır; eski hali kaydedilir ve tek tıkla geri gelir. Windows'un kendi servislerine hiç bakılmaz.
/// </summary>
public sealed class BackgroundInspector
{
    private sealed record Rule(string Pattern, BgAdvice Advice, string Reason);

    // Sıra önemli: ilk eşleşen kural geçerli. Eşleşme, ad + görünen ad + yol üzerinde (küçük harf) aranır.
    private static readonly Rule[] Rules =
    [
        new("nvidia", BgAdvice.Keep, "NVIDIA sürücü hizmeti. Ekran kartı ayarları ve oyun profilleri buna bağlı."),
        new("nvcontainer", BgAdvice.Keep, "NVIDIA sürücü hizmeti."),
        new("vgc", BgAdvice.Keep, "Riot Vanguard (Valorant anti-hile). Kapatılırsa Valorant açılmaz."),
        new("vgk", BgAdvice.Keep, "Riot Vanguard (Valorant anti-hile)."),
        new("riot", BgAdvice.Keep, "Riot oyunları için gerekli."),
        new("easyanticheat", BgAdvice.Keep, "Oyun anti-hile hizmeti."),
        new("beservice", BgAdvice.Keep, "Oyun anti-hile hizmeti (BattlEye)."),
        new("sunshine", BgAdvice.Keep, "Sunshine oyun akışı hizmeti."),
        new("steam", BgAdvice.Keep, "Steam istemci hizmeti."),
        new("rtkaud", BgAdvice.Keep, "Ses sürücüsü (Realtek). Sesi bozabilir."),
        new("realtek", BgAdvice.Keep, "Ses sürücüsü (Realtek)."),
        new("asusoptimization", BgAdvice.Keep, "ASUS profil ve donanım sürücüsü. Fn tuşlarını etkileyebilir."),
        new("asusswitch", BgAdvice.Keep, "ASUS donanım geçişleri. Fn tuşlarını etkileyebilir."),
        new("ghelper", BgAdvice.Keep, "G-Helper'ın pil şarj limitini her açılışta yazan görevi. Pil limitin böyle korunuyor, dokunma."),

        new("dsaservice", BgAdvice.Optional, "Intel Driver & Support Assistant. Sürücü güncellemesi için; sürekli arka planda çalışması gerekmez."),
        new("dsaupdate", BgAdvice.Optional, "Intel Driver & Support Assistant güncelleyicisi."),
        new("driver and support assistant", BgAdvice.Optional, "Intel Driver & Support Assistant."),
        new("googleupdat", BgAdvice.Optional, "Google güncelleyicisi. Tarayıcı kendi kendine de güncellenir."),
        new("gupdate", BgAdvice.Optional, "Google güncelleyicisi."),
        new("edgeupdate", BgAdvice.Optional, "Edge güncelleyicisi."),
        new("adobe", BgAdvice.Optional, "Adobe güncelleme/lisans hizmeti."),
        new("armourycrate", BgAdvice.Optional, "Armoury Crate arka planı. 'Dizüstü' sayfasındaki sihirbazla birlikte yönetilir."),
        new("asusappservice", BgAdvice.Optional, "Armoury Crate arka planı."),
        new("asussoftwaremanager", BgAdvice.Optional, "ASUS yazılım güncelleyicisi."),
        new("asussystemanalysis", BgAdvice.Optional, "ASUS sistem analizi (telemetri)."),
        new("asussystemdiagnosis", BgAdvice.Optional, "ASUS sistem tanılama (telemetri)."),
        new("ccleaner", BgAdvice.Optional, "CCleaner arka plan izleyicisi."),
        new("epic", BgAdvice.Optional, "Epic Games hizmeti. Oyunlar başlatıcıyla açılırken kendiliğinden devreye girer."),
    ];

    private readonly string _changesPath;

    public BackgroundInspector(string? changesPath = null) =>
        _changesPath = changesPath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "background_changes.json");

    public IReadOnlyList<BgItem> List()
    {
        var items = new List<BgItem>();
        items.AddRange(ReadServices());
        items.AddRange(ReadTasks());
        // Kapattıklarımız "başlamıyor" görünür ama listede kalsın ki geri açılabilsin.
        foreach (var key in LoadChanges().Keys)
            if (!items.Any(i => i.Key == key)) { var restored = ReadSingle(key); if (restored is not null) items.Add(restored); }
        return items.OrderBy(i => i.Advice).ThenBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- Servisler ---------------------------------------------------------

    private IEnumerable<BgItem> ReadServices()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) yield break;
        foreach (var name in root.GetSubKeyNames())
        {
            var item = ReadService(root, name, includeManual: false);
            if (item is not null) yield return item;
        }
    }

    private BgItem? ReadService(RegistryKey servicesRoot, string name, bool includeManual)
    {
        using var k = servicesRoot.OpenSubKey(name);
        if (k is null) return null;
        var start = k.GetValue("Start") as int?;
        var type = k.GetValue("Type") as int?;
        var image = (k.GetValue("ImagePath") as string ?? "").Trim('"');
        if (type is null || (type & 0x30) == 0) return null;                 // yalnızca süreç olan servisler (sürücü değil)
        if (start != 2 && !includeManual) return null;                       // otomatik başlayanlar
        if (IsWindowsOwned(image)) return null;

        var display = k.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@')) display = name;
        var (advice, reason) = Classify(name, display, image);
        var running = ServiceControl.State(name) == 4;
        return new BgItem("Servis", name, display, image, start == 2, running, advice, reason);
    }

    private static bool IsWindowsOwned(string image)
    {
        var p = image.ToLowerInvariant();
        return p.Contains(@"\windows\") || p.Contains("svchost") || p.Contains(@"\windows defender\") || p.Contains(@"\windowsapps\microsoft.")
            || p.Contains(@"\microsoft shared\") || p.Contains("msmpeng") || p.Contains(@"\system32\");
    }

    // ---- Görevler ----------------------------------------------------------

    private static string TasksRoot => System.IO.Path.Combine(Environment.SystemDirectory, "Tasks");

    private IEnumerable<BgItem> ReadTasks()
    {
        if (!Directory.Exists(TasksRoot)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(TasksRoot, "*", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }

        foreach (var file in files)
        {
            var rel = file[TasksRoot.Length..];
            if (rel.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            var item = ReadTaskFile(file, rel);
            if (item is { StartsAtBoot: true }) yield return item;
        }
    }

    private BgItem? ReadTaskFile(string file, string rel)
    {
        try
        {
            var doc = XDocument.Load(file);
            var enabledSetting = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value;
            var enabled = !string.Equals(enabledSetting, "false", StringComparison.OrdinalIgnoreCase);
            var atLogon = doc.Descendants().Any(e => e.Name.LocalName is "LogonTrigger" or "BootTrigger");
            if (!atLogon) return null;
            var command = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value ?? "";
            var (advice, reason) = Classify(rel, rel.TrimStart('\\'), command);
            return new BgItem("Görev", rel, rel.TrimStart('\\'), command.Trim('"'), enabled, false, advice, reason);
        }
        catch { return null; }
    }

    private static (BgAdvice, string) Classify(string name, string display, string path)
    {
        var hay = $"{name} {display} {path}".ToLowerInvariant();
        foreach (var r in Rules)
            if (hay.Contains(r.Pattern)) return (r.Advice, r.Reason);
        return (BgAdvice.Unknown, "Bilinmiyor. Ne işe yaradığından emin olmadığımız için dokunulmaz.");
    }

    private BgItem? ReadSingle(string key)
    {
        var colon = key.IndexOf(':');
        var kind = key[..colon];
        var name = key[(colon + 1)..];
        if (kind == "Servis")
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            return root is null ? null : ReadService(root, name, includeManual: true);
        }
        var file = TasksRoot + name;
        return File.Exists(file) ? ReadTaskFile(file, name) : null;
    }

    // ---- Değiştirme --------------------------------------------------------

    /// <summary>
    /// startAtBoot false: servis "elle başlat"a alınır / görev devre dışı bırakılır (eski hali kaydedilir).
    /// true: eski haline döner. Her adım geri okunarak doğrulanır. Yönetici gerekir.
    /// </summary>
    public (bool Ok, string Message) SetStartAtBoot(BgItem item, bool startAtBoot)
    {
        if (item.Advice == BgAdvice.Keep) return (false, "Bu öğe sistem veya oyunlar için gerekli, dokunulmaz.");
        if (item.Advice == BgAdvice.Unknown && !startAtBoot) return (false, "Ne işe yaradığı bilinmiyor, bu yüzden kapatılmaz.");

        var changes = LoadChanges();
        try
        {
            if (item.IsService)
            {
                if (!startAtBoot)
                {
                    var original = CurrentServiceStartType(item.Name);
                    if (original is null) return (false, "Servis okunamadı.");
                    if (!changes.ContainsKey(item.Key)) changes[item.Key] = original;
                    Exec("sc.exe", "config", item.Name, "start=", "demand");
                    var now = CurrentServiceStartType(item.Name);
                    if (now != "demand") return (false, "Servis ayarı doğrulanamadı (yönetici gerekebilir).");
                }
                else
                {
                    var original = changes.TryGetValue(item.Key, out var o) ? o : "auto";
                    Exec("sc.exe", "config", item.Name, "start=", original);
                    if (CurrentServiceStartType(item.Name) != original) return (false, "Eski ayar geri yüklenemedi.");
                    changes.Remove(item.Key);
                }
            }
            else
            {
                Exec("schtasks.exe", "/Change", "/TN", item.Name, startAtBoot ? "/ENABLE" : "/DISABLE");
                var file = TasksRoot + item.Name;
                var after = File.Exists(file) ? ReadTaskFile(file, item.Name) : null;
                if (after is null || after.StartsAtBoot != startAtBoot) return (false, "Görev ayarı doğrulanamadı (yönetici gerekebilir).");
                if (!startAtBoot) changes[item.Key] = "enabled"; else changes.Remove(item.Key);
            }
            SaveChanges(changes);
            Journal.Write($"Arka plan: {item.Key} açılışta {(startAtBoot ? "başlayacak" : "başlamayacak")}.");
            return (true, startAtBoot ? "Eski haline döndü." : "Açılışta başlamayacak (silinmedi, istediğin an geri açılır).");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string? CurrentServiceStartType(string service)
    {
        var o = Exec("sc.exe", "qc", service);
        var m = Regex.Match(o, @"START_TYPE\s*:\s*(\d+)");
        if (!m.Success) return null;
        return m.Groups[1].Value switch
        {
            "2" => o.Contains("DELAYED", StringComparison.OrdinalIgnoreCase) ? "delayed-auto" : "auto",
            "3" => "demand",
            "4" => "disabled",
            _ => null,
        };
    }

    private Dictionary<string, string> LoadChanges()
    {
        try { return File.Exists(_changesPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_changesPath)) ?? new() : new(); }
        catch { return new(); }
    }

    private void SaveChanges(Dictionary<string, string> changes)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_changesPath)!);
        if (changes.Count == 0) { try { File.Delete(_changesPath); } catch { } return; }
        File.WriteAllText(_changesPath, JsonSerializer.Serialize(changes, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Exec(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return o;
    }
}
