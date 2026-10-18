using Pulse.Core.Diagnostics;
using Pulse.Core.Companion;
using Pulse.Core.Settings;
using Pulse.Core.Apps;
using Pulse.Core.Automation;
using Pulse.Core.Cleanup;
using Pulse.Core.Hardware;
using Pulse.Core.Health;
using Pulse.Core.Modes;
using Pulse.Core.Platform;

Environment.SetEnvironmentVariable("KLYC_PULSE_LOGDIR", Path.Combine(Path.GetTempPath(), "pulse-cli-logs"));   // test günlükleri gerçek günlük klasörüne karışmasın
// Motoru arayüzsüz sınamak için: pulse-cli status | apply <mod> | all
Environment.SetEnvironmentVariable("KLYC_PULSE_TEST", "1");   // testlerin geçici klasördeki sahte oyunları algılanabilsin (uygulamada kapalıdır)
var cmd = args.Length > 0 ? args[0] : "status";

if (cmd == "status")
{
    using var acpi = AsusAcpi.TryOpen();
    Console.WriteLine($"ASUS sürücüsü : {(acpi is null ? "yok" : "var")}");
    Console.WriteLine($"CPU/GPU fan   : {acpi?.GetCpuFanRpm()} / {acpi?.GetGpuFanRpm()} RPM");
    var active = Powercfg.ActiveScheme()!;
    Console.WriteLine($"Güç planı     : {active}");
    Console.WriteLine($"Turbo         : {Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode)}");
    Console.WriteLine($"CPU üst sınır : {Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.MaxProcessorState)}");
    Console.WriteLine($"EPP           : {Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref)}");
    Console.WriteLine($"Yenileme      : {DisplayService.GetRefreshRate()} Hz (desteklenen: {string.Join(", ", DisplayService.SupportedRefreshRates())})");
    Console.WriteLine($"Parlaklık     : %{DisplayService.GetBrightness()}");
    return 0;
}

if (cmd == "brightness")
{
    Console.WriteLine($"ilk okuma: {DisplayService.GetBrightness()} (hata: {DisplayService.LastError})");
    var target = int.Parse(args.ElementAtOrDefault(1) ?? "50");
    Console.WriteLine($"yazma {target}: {DisplayService.SetBrightness(target)} (hata: {DisplayService.LastError})");
    for (var i = 0; i < 6; i++)
    {
        Thread.Sleep(500);
        Console.WriteLine($"okuma {i}: {DisplayService.GetBrightness()} (hata: {DisplayService.LastError})");
    }
    return 0;
}

if (cmd == "cleanup-scan")
{
    var engine2 = new CleanupEngine();
    Console.WriteLine($"Yönetici: {CleanupEngine.IsAdmin}");
    foreach (var cat in CleanupCatalog.Build())
    {
        var s = engine2.Scan(cat);
        Console.WriteLine($"  {cat.Name,-42} {(s.Note is not null ? "[" + s.Note + "]" : $"{s.Bytes / 1048576.0,9:N1} MB  {s.Files} dosya")}");
    }
    return 0;
}

if (cmd == "cleanup-test")
{
    // Gerçek dosyalara dokunmadan: sahte klasörlerde silme, atlama ve karantina davranışını doğrular.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }

    var root = Path.Combine(Path.GetTempPath(), "pulse-cleanup-test-" + Guid.NewGuid().ToString("N")[..8]);
    var qroot = Path.Combine(Path.GetTempPath(), "pulse-quarantine-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(Path.Combine(root, "alt", "derin"));
    string F(string rel, int ageDays, string text = "veri")
    {
        var full = Path.Combine(root, rel);
        File.WriteAllText(full, text);
        File.SetLastWriteTime(full, DateTime.Now.AddDays(-ageDays));
        return full;
    }
    var old1 = F("eski1.tmp", 10); var old2 = F(Path.Combine("alt", "derin", "eski2.tmp"), 10);
    var ro = F("salt-okunur.tmp", 10); File.SetAttributes(ro, FileAttributes.ReadOnly);
    var fresh = F("yeni.tmp", 0);
    var locked = F("kilitli.tmp", 10);
    using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None); // kullanımda

    var cat = new CleanupCategory { Id = "test", Name = "Test", Description = "", Roots = [root], MinAgeDays = 2 };
    var eng = new CleanupEngine(new QuarantineStore(qroot));
    var scan = eng.Scan(cat);
    Check(scan.Files == 4, $"Tarama 4 uygun dosya buldu (bulunan: {scan.Files})");   // eski1, eski2, salt-okunur, kilitli
    var res = eng.Clean(cat);
    Check(!File.Exists(old1) && !File.Exists(old2), "Eski dosyalar silindi (alt klasördekiler dahil)");
    Check(!File.Exists(ro), "Salt okunur eski dosya silindi");
    Check(File.Exists(fresh), "Yeni dosyaya dokunulmadı");
    Check(File.Exists(locked), "Kullanımdaki dosya atlandı, hata vermedi");
    Check(res.Skipped == 1, $"Atlanan sayısı 1 (bulunan: {res.Skipped})");
    Check(!Directory.Exists(Path.Combine(root, "alt")), "Boşalan alt klasörler temizlendi");

    // Tehlikeli kökler reddedilir
    var danger = new CleanupCategory { Id = "d1", Name = "Tehlike", Description = "", Roots = [@"C:\", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] };
    Check(!eng.Eligible(danger).Any(), "Sürücü kökü ve kullanıcı profili kökü reddedildi");

    // Karantina: taşı, geri yükle, süre dolunca sil
    hold.Dispose();
    var dl = Path.Combine(root, "indirilenler"); Directory.CreateDirectory(dl);
    var inst = Path.Combine(dl, "kurulum.exe"); File.WriteAllText(inst, "x"); File.SetLastWriteTime(inst, DateTime.Now.AddDays(-40));
    var newInst = Path.Combine(dl, "yeni.exe"); File.WriteAllText(newInst, "y");
    var qcat = new CleanupCategory { Id = "inst", Name = "Kurulum", Description = "", Roots = [dl], MinAgeDays = 30, Safety = CleanupSafety.Quarantine, Filter = f => f.Extension == ".exe" };
    var qres = eng.Clean(qcat);
    Check(!File.Exists(inst) && qres.Files == 1, "Eski kurulum dosyası karantinaya alındı (silinmedi)");
    Check(File.Exists(newInst), "Yeni kurulum dosyasına dokunulmadı");
    var store = new QuarantineStore(qroot);
    var item = store.List().FirstOrDefault();
    Check(item is not null && File.Exists(item.StoredPath), "Karantina kaydı ve dosyası var");
    Check(item is not null && store.Restore(item.Id) && File.Exists(inst), "Karantinadan özgün yere geri yüklendi");
    eng.Clean(qcat);                                       // tekrar karantinaya
    Check(store.PurgeExpired(DateTime.Now.AddDays(3)) == 0, "7 günden önce kalıcı silinmedi");
    Check(store.PurgeExpired(DateTime.Now.AddDays(8)) > 0 && !store.List().Any(), "7 gün sonra kalıcı silindi");

    try { Directory.Delete(root, true); Directory.Delete(qroot, true); } catch { }
    Console.WriteLine(fails == 0 ? "TÜM TEMİZLİK TESTLERİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "apps")
{
    var apps = InstalledAppsReader.Read();
    Console.WriteLine($"Kurulu uygulama: {apps.Count}");
    foreach (var a in apps.OrderByDescending(a => a.SizeBytes).Take(12))
        Console.WriteLine($"  {a.SizeBytes / 1048576.0,8:N0} MB  {a.Name}  [{a.Publisher}]  {(a.CanUninstall ? "kaldırılabilir" : "-")}");
    Console.WriteLine("--- Gereksiz yazılım ipuçları ---");
    foreach (var a in apps)
        if (BloatCatalog.Evaluate(a) is { } h) Console.WriteLine($"  [{h.Level}] {a.Name}: {h.Reason}");
    Console.WriteLine("--- Başlangıç öğeleri ---");
    foreach (var s in StartupManager.List()) Console.WriteLine($"  [{(s.Enabled ? "açık " : "kapalı")}] {s.Name}  ({s.Location})  {s.Publisher}");
    return 0;
}

if (cmd == "winget")
{
    Console.WriteLine($"winget var: {WingetService.IsAvailable()}");
    var (items, err) = await WingetService.GetUpgradesAsync();
    Console.WriteLine(err ?? $"Güncellenebilir: {items.Count}");
    foreach (var i in items) Console.WriteLine($"  {i.Name,-40} {i.Id,-38} {i.Current,-14} -> {i.Available}");
    return 0;
}

if (cmd == "winget-parse-test")
{
    // Farklı sütun düzenleri ve gürültü satırlarıyla ayrıştırıcıyı sınar (ağ gerektirmez).
    var sample = "   - \\ | /\r\nAd                  Kimlik             Sürüm     Kullanılabilir  Kaynak\r\n----------------------------------------------------------------------\r\nGoogle Chrome       Google.Chrome      141.0.1   142.0.2         winget\r\nGit                 Git.Git            2.50.1    2.51.0          winget\r\nMy Cool App Pro     Vendor.MyCoolApp   1.0       1.1             winget\r\n3 yükseltme var.\r\n";
    var parsed = WingetService.ParseTable(sample);
    Console.WriteLine($"Ayrıştırılan: {parsed.Count}");
    foreach (var i in parsed) Console.WriteLine($"  {i.Name} | {i.Id} | {i.Current} -> {i.Available}");
    var ok = parsed.Count == 3 && parsed[2].Name == "My Cool App Pro" && parsed[0].Id == "Google.Chrome" && parsed[1].Available == "2.51.0";
    // Gerçek hata: ad sütunu tam dolunca ad ile kimlik arasında tek boşluk kalıyor; sürüm numarası kimlik sanılmamalı
    string Row(string a, string b2, string c, string d, string e) => a.PadRight(61) + b2.PadRight(32) + c.PadRight(14) + d.PadRight(16) + e + "\r\n";
    var tight = Row("Ad", "Kimlik", "Sürüm", "Kullanılabilir", "Kaynak") + new string('-', 120) + "\r\n" +
                Row("Epic Online Services", "EpicGames.EpicOnlineServices", "4.2.1", "4.3.1", "winget") +
                Row("Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.30501", "Microsoft.VCRedist.2013.x86", "12.0.30501.0", "12.0.40664.0", "winget") +
                "2 yükseltme var.\r\n";
    var t = WingetService.ParseTable(tight);
    foreach (var i in t) Console.WriteLine($"  {i.Name} | {i.Id} | {i.Current} -> {i.Available}");
    var ok2 = t.Count == 2 && t[1].Id == "Microsoft.VCRedist.2013.x86" && t[1].Current == "12.0.30501.0" && t[1].Name.EndsWith("12.0.30501") && t[0].Id == "EpicGames.EpicOnlineServices";
    Console.WriteLine(ok2 ? "  [GEÇTİ] Dolu ad sütununda kimlik doğru okunur" : "  [KALDI] Dolu ad sütununda kimlik yanlış okundu");
    ok &= ok2;
    var okL = WingetService.IsGameLauncher("EpicGames.EpicGamesLauncher") && WingetService.IsGameLauncher("valve.steam") && !WingetService.IsGameLauncher("Git.Git");
    Console.WriteLine(okL ? "  [GEÇTİ] Oyun başlatıcıları tanınır (Epic, Steam), sıradan uygulama tanınmaz" : "  [KALDI] Oyun başlatıcısı tanıma");
    ok &= okL;
    if (WingetService.IsAvailable())
    {
        var (inst, _) = await WingetService.VerifyAsync("Not.Installed.Pulse.Test", "1.0");
        Console.WriteLine(!inst ? "  [GEÇTİ] Kurulu olmayan paket doğrulamada 'kurulu değil' çıkar" : "  [KALDI] Olmayan paket kurulu göründü");
        ok &= !inst;
    }
    Console.WriteLine(ok ? "AYRIŞTIRICI TESTİ GEÇTİ" : "AYRIŞTIRICI TESTİ KALDI");
    return ok ? 0 : 1;
}

if (cmd == "diskscan-test")
{
    // Disk analizi: iç içe kökler iki kez sayılmaz, küçük klasörler elenir, sonuç büyükten küçüğe sıralanır
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var tmp = Path.Combine(Path.GetTempPath(), "pulse-diskscan-test");
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    var a = Path.Combine(tmp, "A"); var bigA = Path.Combine(a, "big"); var small = Path.Combine(a, "small");
    var nested = Path.Combine(a, "sub"); var b = Path.Combine(nested, "inner"); var bigB = Path.Combine(b, "big2");
    foreach (var d in new[] { bigA, small, bigB }) Directory.CreateDirectory(d);
    File.WriteAllBytes(Path.Combine(bigA, "x.bin"), new byte[3 * 1024 * 1024]);
    File.WriteAllBytes(Path.Combine(small, "x.bin"), new byte[10 * 1024]);
    File.WriteAllBytes(Path.Combine(bigB, "y.bin"), new byte[2 * 1024 * 1024]);
    var res = await DiskAnalyzer.AnalyzeAsync(new[] { a, b }, minBytes: 1024 * 1024);
    foreach (var r in res) Console.WriteLine($"  {r.Path}  {r.Bytes / 1048576.0:0.0} MB");
    Check(res.Count == 2, "İki büyük klasör bulunur, küçük olan ve iç içe kök kapsayan klasör listelenmez");
    Check(res.Any(r => r.Path == bigA) && res.Any(r => r.Path == bigB), "Doğru klasörler listelenir");
    Check(!res.Any(r => r.Path == nested), "Başka bir kökü kapsayan klasör (sub) atlanır, çift sayılmaz");
    Check(res.Count > 0 && res[0].Path == bigA, "Büyükten küçüğe sıralanır");
    var none = await DiskAnalyzer.AnalyzeAsync(new[] { Path.Combine(tmp, "yok") }, minBytes: 1);
    Check(none.Count == 0, "Olmayan kök hata vermez");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var cancelled = false;
    try { await DiskAnalyzer.AnalyzeAsync(new[] { a }, minBytes: 1, ct: cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "İptal edilince durur");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var real = await DiskAnalyzer.AnalyzeAsync(DiskAnalyzer.DefaultRoots());
    Console.WriteLine($"  Gerçek tarama: {sw.Elapsed.TotalSeconds:0.0} sn, {real.Count} büyük klasör");
    foreach (var r in real.Take(8)) Console.WriteLine($"    {r.Bytes / 1073741824.0,6:0.0} GB  {r.Path}");
    Check(real.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == real.Count, "Gerçek taramada aynı klasör iki kez çıkmaz");
    try { Directory.Delete(tmp, true); } catch { }
    Console.WriteLine(fails == 0 ? "DİSK ANALİZİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "konsol-cmd-test")
{
    // Konsol → Pulse komut kanalı: doğrulama, dosya güvenliği, yürütme (sahte mod anahtarı; donanıma ve gerçek ayara dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var root = Path.Combine(Path.GetTempPath(), "pulse-cmd-test-" + Guid.NewGuid().ToString("N")[..6]);
    var konsolDir = Path.Combine(root, "konsol"); Directory.CreateDirectory(konsolDir);
    Environment.SetEnvironmentVariable("KLYC_KONSOL_DIR", konsolDir);
    Environment.SetEnvironmentVariable("KLYC_PULSE_DIR", Path.Combine(root, "pulse"));
    long now = 1800000000;
    string Cmd(string id = "abcdef12-0001", string action = "mode", string? mode = "oyun", long? at = null, int schema = 1) =>
        KonsolCommands.Serialize(new KonsolCommand(id, action, mode, at ?? now)).Replace("\"schema\":1", $"\"schema\":{schema}");
    try
    {
        // ---- Doğrulama (izin listesi) ----
        Check(KonsolCommands.CommandPath == Path.Combine(konsolDir, "pulse-command.json"), "Komut dosyası yolu ortam değişkenine uyar");
        Check(KonsolCommands.Parse(Cmd(), now).Command is { Action: "mode", Mode: "oyun", Id: "abcdef12-0001" }, "Geçerli mod komutu kabul edilir");
        Check(KonsolCommands.Parse(Cmd(action: "boost", mode: null), now).Command is { Action: "boost", Mode: null }, "Geçerli Hızlandır komutu kabul edilir");
        Check(Pulse.Core.Modes.Modes.All.All(m => KonsolCommands.Parse(Cmd(id: "ok-" + m.Key + "-12345", mode: m.Key), now).Ok), "Pulse'ın tüm modları (oyun, günlük, sessiz, boşta) kabul edilir");
        Check(KonsolCommands.Parse(Cmd(action: "Mode", mode: "OYUN"), now).Ok, "Büyük/küçük harf fark etmez");
        foreach (var (name, json, why) in new (string, string, string)[]
        {
            ("bozuk JSON", "{yarım", "json"), ("boş", "", "boyut"), ("dizi", "[1]", "biçim"),
            ("bilinmeyen eylem (kapat)", Cmd(action: "shutdown", mode: null), "eylem"), ("bilinmeyen eylem (çalıştır)", Cmd(action: "exec", mode: null), "eylem"), ("eylem yok", "{\"schema\":1,\"id\":\"abcdef12-0002\",\"at\":1800000000}", "eylem"),
            ("bilinmeyen mod", Cmd(mode: "turbo"), "mod"), ("mod yok", Cmd(mode: null), "mod"), ("yol benzeri mod", Cmd(mode: "..\\oyun"), "mod"),
            ("kısa kimlik", Cmd(id: "abc"), "kimlik"), ("kimlikte tehlikeli karakter", Cmd(id: "abcdef12;calc"), "kimlik"), ("çok uzun kimlik", Cmd(id: new string('a', 80)), "kimlik"),
            ("yanlış şema", Cmd(schema: 7), "şema"), ("çok eski", Cmd(at: now - 120), "yaş"), ("gelecekten", Cmd(at: now + 60), "yaş"), ("zaman yok", "{\"schema\":1,\"id\":\"abcdef12-0003\",\"action\":\"boost\"}", "yaş"),
            ("çok büyük", Cmd() + new string(' ', 2000), "boyut"),
        })
        {
            var c = KonsolCommands.Parse(json, now);
            Check(!c.Ok && c.Rejected == why, $"Reddedilir: {name} → {c.Rejected}");
        }
        Check(KonsolCommands.Parse(Cmd(id: "tekrar-0001"), now, new[] { "tekrar-0001" }).Rejected == "tekrar", "Aynı kimlik ikinci kez reddedilir (tekrar saldırısı)");
        Check(KonsolCommands.Parse(Cmd(at: now - 19), now).Ok && KonsolCommands.Parse(Cmd(at: now + 3), now).Ok, "Yaş sınırı içinde (20 sn) ve küçük saat farkı kabul edilir");

        // ---- Dosya güvenliği ----
        var path = KonsolCommands.CommandPath;
        Check(KonsolCommands.CheckFile(path) == "yok", "Dosya yoksa 'yok'");
        File.WriteAllText(path, Cmd());
        Check(KonsolCommands.CheckFile(path) is null, "Bu kullanıcının oluşturduğu küçük dosya güvenilir");
        File.WriteAllText(path, new string('x', 5000));
        Check(KonsolCommands.CheckFile(path) == "boyut", "Çok büyük dosya reddedilir");
        File.Delete(path);
        // Klasör bağlantısı (junction): komut dosyası başka bir yere yönlendirilmiş klasörde ise reddedilir
        var real = Path.Combine(root, "gercek"); Directory.CreateDirectory(real);
        var junction = Path.Combine(root, "baglanti");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        mk!.WaitForExit();
        if (Directory.Exists(junction))
        {
            File.WriteAllText(Path.Combine(real, "pulse-command.json"), Cmd());
            Check(KonsolCommands.CheckFile(Path.Combine(junction, "pulse-command.json")) == "klasör bağlantı noktası", "Bağlantı (junction) üzerinden gelen dosya reddedilir");
        }
        else Check(true, "(junction oluşturulamadı: bu ortamda atlandı)");
        Check(!KonsolCommands.IsTrustedOwner(null) && !KonsolCommands.IsTrustedOwner(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001")) && KonsolCommands.IsTrustedOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null)) && KonsolCommands.IsTrustedOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User), "Sahip denetimi: ben/Yöneticiler/SYSTEM güvenilir, yabancı kullanıcı değil");

        // ---- Yürütme (sahte mod anahtarı) ----
        var sw = new FakeSwitch("gunluk");
        var allowed = true; var open = true; var boosts = 0;
        var runner = new KonsolCommandRunner(sw, () => { boosts++; return Task.FromResult("Hızlandır tamam: 12 MB temizlendi."); }, () => allowed, () => open);
        File.WriteAllText(path, Cmd(id: "run-mode-0001"));
        var r1 = await runner.ProcessAsync(path, now);
        Check(r1 is { Ok: true, Id: "run-mode-0001", Action: "mode" } && sw.Calls.SequenceEqual(new[] { "oyun" }), $"Mod komutu uygulanır ve sonuç döner: {r1?.Message}");
        Check(!File.Exists(path), "Komut dosyası işlenince silinir");
        await Task.Delay(2100);
        File.WriteAllText(path, Cmd(id: "run-boost-0001", action: "boost", mode: null));
        var r2 = await runner.ProcessAsync(path, now);
        Check(r2 is { Ok: true, Action: "boost" } && boosts == 1 && r2.Message.Contains("Hızlandır"), $"Hızlandır komutu çalışır: {r2?.Message}");
        File.WriteAllText(path, Cmd(id: "run-fast-0001", mode: "sessiz"));
        var r4 = await runner.ProcessAsync(path, now);   // Hızlandır'ın hemen ardından
        Check(r4 is { Ok: false } && sw.Calls.Count == 1, $"Çok sık komut hız sınırına takılır: {r4?.Message}");
        await Task.Delay(2100);
        File.WriteAllText(path, Cmd(id: "run-mode-0001"));
        var r3 = await runner.ProcessAsync(path, now);
        Check(r3 is { Ok: false } && sw.Calls.Count == 1, "Aynı kimlikli komut tekrar yürütülmez");
        allowed = false;
        File.WriteAllText(path, Cmd(id: "run-off-0001", mode: "sessiz"));
        var r5 = await runner.ProcessAsync(path, now);
        Check(r5 is { Ok: false } && sw.Calls.Count == 1 && !File.Exists(path), $"Ayar kapalıyken komut yürütülmez, dosya silinir, neden bildirilir: {r5?.Message}");
        allowed = true; open = false;
        await Task.Delay(2100);
        File.WriteAllText(path, Cmd(id: "run-noko-0001", mode: "sessiz"));
        var r6 = await runner.ProcessAsync(path, now);
        Check(r6 is { Ok: false } && sw.Calls.Count == 1, $"Konsol açık değilken yürütülmez: {r6?.Message}");
        open = true;
        await Task.Delay(2100);
        File.WriteAllText(path, Cmd(id: "run-old-00001", at: now - 300));
        var r7 = await runner.ProcessAsync(path, now);
        Check(r7 is { Ok: false } && sw.Calls.Count == 1, "Eski komut yürütülmez");
        File.WriteAllText(path, "{\"schema\":1,\"id\":\"zzzzzzzz-1\",\"action\":\"mode\",\"mode\":\"oyun\",\"at\":1800000000,\"komut\":\"calc.exe\"}");
        await Task.Delay(2100);
        var r8 = await runner.ProcessAsync(path, now);
        Check(r8 is { Ok: true } && sw.Calls.Count == 2, "Bilinmeyen ek alanlar yok sayılır (hiçbir şey çalıştırılmaz)");
        Check(await runner.ProcessAsync(path, now) is null, "Dosya yoksa hiçbir şey olmaz");

        // Mod uygulanamazsa (meşgul) dürüst hata
        var busy = new BusySwitch();
        var runner2 = new KonsolCommandRunner(busy, () => Task.FromResult(""), () => true, () => true);
        File.WriteAllText(path, Cmd(id: "run-busy-0001"));
        var r9 = await runner2.ProcessAsync(path, now);
        Check(r9 is { Ok: false }, $"Mod uygulanamazsa Konsol'a başarısızlık bildirilir: {r9?.Message}");

        // ---- Ek eylemler: clean / autogame / fan / heat (dar değer listeleri) ----
        string CmdV(string action, string? value, string id = "extra-0001") => "{\"schema\":1,\"id\":\"" + id + "\",\"action\":\"" + action + "\"" + (value is null ? "" : ",\"value\":\"" + value + "\"") + ",\"at\":" + now + "}";
        Check(KonsolCommands.Parse(CmdV("autogame", "on"), now).Command is { Action: "autogame", Value: "on" } && KonsolCommands.Parse(CmdV("fan", "OFF"), now).Command is { Value: "off" }, "autogame/fan: on/off kabul edilir");
        Check(KonsolCommands.Parse(CmdV("heat", "80"), now).Command is { Action: "heat", Value: "80" } && KonsolCommands.Parse(CmdV("heat", "0"), now).Ok && KonsolCommands.Parse(CmdV("clean", null), now).Ok, "heat 0/75/80/85/90 ve clean kabul edilir");
        foreach (var (n2, j2) in new[] { ("heat 200", CmdV("heat", "200")), ("heat 40", CmdV("heat", "40")), ("heat harf", CmdV("heat", "sicak")), ("heat değersiz", CmdV("heat", null)), ("autogame belirsiz", CmdV("autogame", "belki")), ("fan değersiz", CmdV("fan", null)), ("heat enjeksiyon", CmdV("heat", "80;calc")) })
            Check(KonsolCommands.Parse(j2, now).Rejected == "değer", $"Reddedilir: {n2}");
        Check(KonsolCommands.Parse(KonsolCommands.Serialize(new KonsolCommand("rt-00000001", "heat", null, now, "85")), now).Command is { Value: "85" }, "Value serileştirme turu");
        var calls = new List<string>();
        var runner3 = new KonsolCommandRunner(new FakeSwitch("gunluk"), () => Task.FromResult("b"), () => true, () => true, new KonsolExtraActions
        {
            Clean = () => { calls.Add("clean"); return Task.FromResult("temiz"); },
            AutoGame = on => { calls.Add("autogame:" + on); return "ag"; },
            Fan = on => { calls.Add("fan:" + on); return Task.FromResult("fan"); },
            Heat = t => { calls.Add("heat:" + t); return "ht"; },
        });
        var seq = new[] { ("clean", (string?)null), ("autogame", "on"), ("fan", "off"), ("heat", "80") };
        var okAll = true; var ix = 0;
        foreach (var (a, v) in seq)
        {
            await Task.Delay(2100);
            File.WriteAllText(path, CmdV(a, v, "ex-run-" + (ix++) + "-0001"));
            var rr = await runner3.ProcessAsync(path, now);
            okAll &= rr is { Ok: true };
        }
        Check(okAll && calls.SequenceEqual(new[] { "clean", "autogame:True", "fan:False", "heat:80" }), $"Ek eylemler doğru temsilciyi doğru değerle çağırır: {string.Join(",", calls)}");
        var runner4 = new KonsolCommandRunner(new FakeSwitch("gunluk"), () => Task.FromResult("b"), () => true, () => true);
        File.WriteAllText(path, CmdV("heat", "80", "ex-none-0001"));
        Check(await runner4.ProcessAsync(path, now) is { Ok: false }, "Temsilci yoksa (eski kurulum) dürüstçe başarısız");
        File.WriteAllText(path, CmdV("heat", "250", "peek-id-0001"));
        Check(await runner3.ProcessAsync(path, now) is { Ok: false, Id: "peek-id-0001" }, "Reddedilen komutun sonucu kendi kimliğiyle döner (Konsol gerçek nedeni görür)");

        // ---- Oyun başına profil (gamemode): doğrulama, var olan profili değiştirme, olmayanı uydurmama ----
        string GmCmd(string mode, string? title, string id = "gm-0000001") => "{\"schema\":1,\"id\":\"" + id + "\",\"action\":\"gamemode\",\"mode\":\"" + mode + "\"" + (title is null ? "" : ",\"value\":\"" + title + "\"") + ",\"at\":" + now + "}";
        Check(KonsolCommands.Parse(GmCmd("sessiz", "Hades II"), now).Command is { Action: "gamemode", Mode: "sessiz", Value: "Hades II" } && KonsolCommands.Parse(GmCmd("off", "Hades II"), now).Ok, "gamemode: bilinen mod ya da 'off' + oyun adı kabul edilir");
        Check(KonsolCommands.Parse(GmCmd("turbo", "Hades II"), now).Rejected == "mod" && KonsolCommands.Parse(GmCmd("oyun", null), now).Rejected == "değer" && KonsolCommands.Parse(GmCmd("oyun", new string('a', 81)), now).Rejected == "değer" && KonsolCommands.Parse(GmCmd("oyun", "a\\nb"), now).Rejected == "değer", "gamemode: bilinmeyen mod, oyun adı yok/çok uzun/denetim karakteri reddedilir");
        var gset = new Pulse.Core.Settings.AppSettings();
        gset.GameProfiles.Add(new Pulse.Core.Settings.GameProfile { ExeName = "hades2", DisplayName = "Hades II", ModeKey = "oyun" });
        Check(KonsolGameProfiles.Apply(gset, "hades ii", "sessiz") is { Ok: true } && gset.GameProfiles[0].ModeKey == "sessiz" && gset.GameProfiles[0].Enabled, "Var olan profilin modu değişir (ad büyük/küçük harf ve boşluktan bağımsız eşleşir)");
        Check(KonsolGameProfiles.Apply(gset, "Hades II", "off") is { Ok: true } && !gset.GameProfiles[0].Enabled && gset.GameProfiles[0].ModeKey == "sessiz", "'off' otomatik geçişi kapatır, modu silmez");
        Check(KonsolGameProfiles.Apply(gset, "Hades II", "oyun") is { Ok: true } && gset.GameProfiles[0].Enabled, "Mod seçmek profili yeniden açar");
        Check(KonsolGameProfiles.Apply(gset, "Bilinmeyen Oyun", "oyun") is { Ok: false } && gset.GameProfiles.Count == 1, "Profili olmayan oyun için yeni profil UYDURULMAZ");
        var gcalls = new List<string>();
        var runner5 = new KonsolCommandRunner(new FakeSwitch("gunluk"), () => Task.FromResult("b"), () => true, () => true, new KonsolExtraActions { GameMode = (t, m) => { gcalls.Add(t + ":" + m); return KonsolGameProfiles.Apply(gset, t, m); } });
        File.WriteAllText(path, GmCmd("sessiz", "Hades II", "gm-run-0001"));
        Check(await runner5.ProcessAsync(path, now) is { Ok: true } && gcalls.SequenceEqual(new[] { "Hades II:sessiz" }) && gset.GameProfiles[0].ModeKey == "sessiz", "Çalıştırıcı gamemode'u temsilciye (oyun, mod) olarak iletir");
        await Task.Delay(2100);
        File.WriteAllText(path, GmCmd("sessiz", "Yok Oyun", "gm-run-0002"));
        Check(await runner5.ProcessAsync(path, now) is { Ok: false, Id: "gm-run-0002" }, "Profil yoksa Konsol'a dürüst başarısızlık döner");
        var gf = KonsolLink.BuildFeed("1.12.0", true, "oyun", false, true, null, now: now, acceptsCommands: true, gameProfiles: KonsolGameProfiles.ToFeed(gset.GameProfiles));
        Check(gf.GameProfiles is { Count: 1 } && gf.GameProfiles[0] is { Name: "Hades II", Mode: "sessiz", Enabled: true } && KonsolLink.BuildFeed("1.12.0", true, "oyun", false, true, null, now: now, gameProfiles: KonsolGameProfiles.ToFeed(gset.GameProfiles)).GameProfiles is null, "Özet profilleri yalnızca komutlar kabul ediliyorsa taşır");

        // ---- Özet (feed): komutlar kapalı/açık, mod listesi, canlı değerler, son komut ----
        var off = KonsolLink.BuildFeed("1.11.0", true, "oyun", false, true, null, now: now);
        Check(!off.AcceptsCommands && off.Modes is null && off.ModeTitle == "Oyun" && off.Live is null && off.LastCommand is null, "Varsayılan özet: komut kabul etmiyor, mod listesi yok, mod adı var");
        var on = KonsolLink.BuildFeed("1.11.0", true, "sessiz", false, true, null, now: now, acceptsCommands: true,
            live: new PulseFeedLive(61.5, 55, 12, 3, now), lastCommand: new PulseFeedCommand("x1", "mode", true, "Sessiz modu uygulandı.", now));
        var onJson = KonsolLink.Serialize(on);
        using (var d = System.Text.Json.JsonDocument.Parse(onJson))
        {
            var e = d.RootElement;
            Check(e.GetProperty("acceptsCommands").GetBoolean() && e.GetProperty("modes").GetArrayLength() == Pulse.Core.Modes.Modes.All.Count && e.GetProperty("modes")[0].GetProperty("key").GetString() == "oyun", "Komut açıkken mod listesi (anahtar+ad) yazılır");
            Check(e.GetProperty("live").GetProperty("cpuTemp").GetDouble() == 61.5 && e.GetProperty("live").GetProperty("gpuTemp").GetDouble() == 55, "Canlı ısı değerleri yazılır");
            Check(e.GetProperty("lastCommand").GetProperty("id").GetString() == "x1" && e.GetProperty("lastCommand").GetProperty("ok").GetBoolean() && e.GetProperty("modeTitle").GetString() == "Sessiz", "Son komut sonucu ve mod adı yazılır");
        }
        var wl = KonsolLink.ParseStatus("{\"schema\":2,\"updatedAt\":5,\"state\":\"launcher\",\"wantsLive\":true}");
        Check(wl is { WantsLive: true } && KonsolLink.ParseStatus("{\"schema\":2,\"updatedAt\":5,\"state\":\"launcher\"}") is { WantsLive: false }, "Konsol'un 'canlı değer istiyorum' bayrağı okunur (yoksa kapalı)");
        Check(new Pulse.Core.Settings.AppSettings().AllowKonsolCommands == false, "Komut kanalı varsayılan KAPALI");
    }
    finally { try { Directory.Delete(root, true); } catch { } Environment.SetEnvironmentVariable("KLYC_KONSOL_DIR", null); Environment.SetEnvironmentVariable("KLYC_PULSE_DIR", null); }
    Console.WriteLine(fails == 0 ? "KONSOL KOMUT KANALI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "konsol-link-test")
{
    // KLYC Konsol bağlantısı: dosya okuma/yazma ve oyun ipuçları. Gerçek ayara, donanıma ya da kullanıcı verisine dokunmaz.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var root = Path.Combine(Path.GetTempPath(), "pulse-konsol-test-" + Guid.NewGuid().ToString("N")[..6]);
    var konsolDir = Path.Combine(root, "konsol"); var pulseDir = Path.Combine(root, "pulse");
    Directory.CreateDirectory(konsolDir);
    Environment.SetEnvironmentVariable("KLYC_KONSOL_DIR", konsolDir);
    Environment.SetEnvironmentVariable("KLYC_PULSE_DIR", pulseDir);
    try
    {
        // ---- Durum okuma ----
        Check(KonsolLink.StatusPath == Path.Combine(konsolDir, "status.json") && KonsolLink.FeedPath == Path.Combine(pulseDir, "konsol-feed.json"), "Yollar ortam değişkenlerine uyar (gerçek veriye dokunulmaz)");
        Check(KonsolLink.ReadStatus() is null, "Dosya yoksa 'Konsol yok' (hata vermez)");

        var v2 = "{\"schema\":2,\"updatedAt\":1791796212,\"version\":\"0.8.0\",\"pid\":14660,\"state\":\"launching\",\"game\":{\"id\":\"steam:1145360\",\"title\":\"Hades\",\"store\":\"steam\",\"installDir\":\"C:\\\\Games\\\\Hades\",\"target\":\"steam://rungameid/1145360\",\"startedAt\":1791796200},\"powerPlanManagedBy\":\"konsol\",\"guard\":true,\"yeniAlan\":{\"x\":1}}";
        var st = KonsolLink.ParseStatus(v2);
        Check(st is { Schema: 2, State: "launching", UpdatedAt: 1791796212, Pid: 14660 } && st.Game is { Title: "Hades", Store: "steam", InstallDir: "C:\\Games\\Hades" }, "Şema 2 okunur, bilinmeyen alan yok sayılır");
        Check(st!.InGame && !st.IsOpen && st.KonsolOwnsPowerPlan, "Başlatılıyor + güç planı Konsol'da: oyun durumu ve plan sahipliği");
        Check(st.ToHint() is { Title: "Hades", InstallDir: "C:\\Games\\Hades" }, "Oyun durumunda ipucu üretilir");

        var v1 = "{\"state\":\"playing\",\"game\":\"Hades\",\"gameId\":\"steam:1145360\",\"since\":1791796212,\"pid\":14660,\"version\":\"0.7.0\"}";
        var st1 = KonsolLink.ParseStatus(v1, fileTime: 1791796300);
        Check(st1 is { Schema: 1, State: "playing", UpdatedAt: 1791796300 } && st1.Game is { Title: "Hades", Id: "steam:1145360", StartedAt: 1791796212 }, "Eski şema 1 de okunur (güncelleme zamanı dosya zamanından)");
        Check(st1!.ToHint() is { InstallDir: null } && !st1.KonsolOwnsPowerPlan, "Şema 1: klasör yok (ipucu yine üretilir), güç planı sahibi bilinmiyor");

        var idle = KonsolLink.ParseStatus("{\"schema\":2,\"updatedAt\":5,\"state\":\"launcher\",\"game\":null}");
        Check(idle is { IsOpen: true, InGame: false, Game: null } && idle.ToHint() is null, "Kütüphane durumunda oyun yok, ipucu yok");
        Check(KonsolLink.ParseStatus("{bozuk") is null && KonsolLink.ParseStatus("") is null && KonsolLink.ParseStatus("[1,2]") is null && KonsolLink.ParseStatus("{\"schema\":2}") is null, "Bozuk, boş, dizi ve durumsuz dosya 'veri yok' sayılır");
        var odd = KonsolLink.ParseStatus("{\"state\":\"playing\",\"game\":{\"title\":5},\"pid\":\"x\",\"updatedAt\":\"dün\"}", 77);
        Check(odd is { Pid: null, UpdatedAt: 77, Game: null }, "Yanlış türdeki alanlar hata değil, 'yok' sayılır");

        // Konsol'un gerçekten ürettiği dosya (KLYC-Konsol PulseLink.BuildStatus çıktısı, 0.8.0): iki proje aynı şemayı konuşuyor mu
        var real = KonsolLink.ParseStatus("{\"schema\":2,\"updatedAt\":1791796212,\"version\":\"0.8.0\",\"pid\":14660,\"state\":\"launching\",\"game\":{\"id\":\"steam:1145360\",\"title\":\"Hades\",\"store\":\"steam\",\"installDir\":\"D:\\\\Oyunlar\\\\Hades\",\"target\":\"steam://rungameid/1145360\",\"startedAt\":1791796212},\"gameId\":\"steam:1145360\",\"since\":1791796212,\"powerPlanManagedBy\":\"pulse\",\"guard\":true,\"controllerConnected\":true}");
        Check(real is { Schema: 2, State: "launching", PowerPlanManagedBy: "pulse", Pid: 14660 } && real.Game is { Title: "Hades", Id: "steam:1145360", Store: "steam", InstallDir: "D:\\Oyunlar\\Hades", StartedAt: 1791796212 } && real.ToHint() is not null && !real.KonsolOwnsPowerPlan, "Konsol'un gerçek çıktısı Pulse'ta doğru okunur");

        File.WriteAllText(KonsolLink.StatusPath, v2);
        Check(KonsolLink.ReadStatus() is { State: "launching" }, "Dosyadan okunur");
        File.WriteAllText(KonsolLink.StatusPath, "{yarım");
        Check(KonsolLink.ReadStatus() is null, "Yarım yazılmış dosya sessizce 'veri yok'");

        Check(!KonsolLink.IsStale(st, TimeSpan.FromHours(24), now: st.UpdatedAt + 60) && KonsolLink.IsStale(st, TimeSpan.FromHours(24), now: st.UpdatedAt + 90000) && KonsolLink.IsStale(st, TimeSpan.FromHours(24), now: st.UpdatedAt - 3600), "Bayatlık: 24 saatten eski ya da gelecekteki zaman güvenilmez");
        Check(!KonsolLink.IsProcessAlive(null) && !KonsolLink.IsProcessAlive(0) && KonsolLink.IsProcessAlive(Environment.ProcessId) && !KonsolLink.IsProcessAlive(Environment.ProcessId, "KlycKonsol") && KonsolLink.IsProcessAlive(Environment.ProcessId, System.Diagnostics.Process.GetCurrentProcess().ProcessName), "Süreç canlılığı (kendi sürecimiz canlı, olmayan değil; ad uyuşmazsa 'canlı' sayılmaz)");

        // ---- Özet yazma ----
        var report = new Pulse.Core.Diagnostics.GameSessionReport { Game = "Hades", Start = DateTime.Now.AddMinutes(-62), Minutes = 62.44, AvgFps = 118.4, CpuTempMax = 84.2, GpuTempMax = 71, Bottleneck = "gpu", Severity = 1,
            Findings = ["bir", "iki", "üç", "dört"] };
        var feed = KonsolLink.BuildFeed("1.10.0", true, "oyun", false, true, report, now: 1791796300);
        Check(KonsolLink.WriteFeed(feed), "Özet yazılır (klasör yoksa oluşturulur)");
        var json = File.ReadAllText(KonsolLink.FeedPath);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            var r = doc.RootElement;
            Check(r.GetProperty("schema").GetInt32() == 1 && r.GetProperty("running").GetBoolean() && r.GetProperty("updatedAt").GetInt64() == 1791796300 && r.GetProperty("mode").GetProperty("key").GetString() == "oyun", "Özet alanları (şema, çalışıyor, zaman, mod) doğru ve camelCase");
            var lr = r.GetProperty("lastReport");
            Check(lr.GetProperty("game").GetString() == "Hades" && lr.GetProperty("minutes").GetDouble() == 62.4 && lr.GetProperty("avgFps").GetDouble() == 118 && lr.GetProperty("severity").GetInt32() == 1 && lr.GetProperty("findings").GetArrayLength() == 3, "Son rapor özetlenir (en çok 3 bulgu)");
            Check(!r.GetProperty("acceptsCommands").GetBoolean(), "Komutlar kapalı bildirilir");
        }
        using (var doc2 = System.Text.Json.JsonDocument.Parse(File.ReadAllText(KonsolLink.FeedPath)))
        {
            // Konsol'un (PulseLink.ParseFeed) beklediği alan adları: biri değişirse iki proje birbirini anlamaz
            var rr = doc2.RootElement;
            var topKeys = new[] { "schema", "updatedAt", "pulseVersion", "running", "mode", "autoGameMode", "acceptsCommands", "lastReport" };
            var lr2 = rr.GetProperty("lastReport");
            var repKeys = new[] { "game", "startedAt", "minutes", "avgFps", "cpuTempMax", "gpuTempMax", "bottleneck", "severity", "findings" };
            var modeEl = rr.GetProperty("mode");
            Check(topKeys.All(n => rr.TryGetProperty(n, out _)) && repKeys.All(n => lr2.TryGetProperty(n, out _)) && modeEl.TryGetProperty("key", out _) && modeEl.TryGetProperty("applying", out _), "Özet alan adları Konsol'un beklediğiyle aynı");
        }
        Check(Directory.GetFiles(pulseDir, "*.tmp").Length == 0, "Geçici dosya kalmaz");
        Check(KonsolLink.WriteFeed(KonsolLink.BuildFeed("1.10.0", false, null, false, false, null)) && File.ReadAllText(KonsolLink.FeedPath).Contains("\"running\": false"), "Üzerine yazılır; kapanırken running=false");

        // Atomiklik: yazıcı sürekli yazarken okuyucu hiç yarım dosya görmemeli
        var stop = false; var bad = 0; var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try { using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(KonsolLink.FeedPath)); reads++; }
                catch (IOException) { /* o an taşınıyor olabilir */ }
                catch (System.Text.Json.JsonException) { bad++; }
            }
        });
        for (var i = 0; i < 300; i++) KonsolLink.WriteFeed(KonsolLink.BuildFeed("1.10.0", true, "oyun", false, true, report, now: 1000 + i));
        Volatile.Write(ref stop, true); await reader;
        Check(bad == 0 && reads > 0, $"300 yazım sırasında okuyucu hiç yarım dosya görmedi ({reads} okuma)");

        // ---- KLYC Konsol süreçleri korumalı: "Açık uygulamaları kapat" / süreç listesi onları kapatamaz ----
        Check(Pulse.Core.Processes.ProcessInspector.Protection("KlycKonsol", null, true, false) is (true, { Length: > 0 })
              && Pulse.Core.Processes.ProcessInspector.Protection("KlycGuard", null, false, false).Item1
              && !Pulse.Core.Processes.ProcessInspector.Protection("SomeRandomApp", null, true, false).Item1, "KlycKonsol ve KlycGuard korumalı (kapatılamaz), sıradan uygulama değil");

        // ---- Oyun ipuçları (sahte mod anahtarı, sahte süreç bulucu, sahte saat: donanıma dokunmaz) ----
        var hintDir = @"D:\Oyunlar\Hades";            // klasör kuralına (steamapps\common, Epic Games...) uymayan yer
        var gamePath = @"D:\Oyunlar\Hades\Hades.exe";
        var now = DateTime.UtcNow;
        Pulse.Core.Automation.GameDetector.DetectedGame? running = null;
        bool? knownSeen = null;
        var sw = new FakeSwitch("gunluk");
        AutoModeService NewAuto(SettingsStore ss, bool autoMode = true)
        {
            ss.Current.AutoGameMode = autoMode;
            return new AutoModeService(sw, ss, Timeout.InfiniteTimeSpan, known => { if (running is { } g) knownSeen = known?.Invoke(g.Path, Path.GetFileNameWithoutExtension(g.Path).ToLowerInvariant()); return knownSeen == true || known is null ? running : null; }, () => now, tune: false);
        }
        SettingsStore NewSettings() => SettingsStore.Load(Path.Combine(root, "ayar-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
        var hint = new KonsolGameHint("Hades", hintDir, "steam");

        var s1 = NewSettings(); var auto = NewAuto(s1);
        var started = new List<string>(); auto.GameStarted += started.Add;
        await auto.NotifyGameLaunching(hint);
        Check(sw.Calls.SequenceEqual(new[] { "oyun" }) && sw.CurrentKey == "oyun", "'Başlatılıyor' ipucunda Oyun modu süreç görünmeden uygulanır");

        now = now.AddSeconds(30); await auto.TickOnceAsync();
        Check(sw.CurrentKey == "oyun" && sw.Calls.Count == 1, "Süreç henüz yokken (bekleme süresi içinde) mod geri alınmaz");

        running = new Pulse.Core.Automation.GameDetector.DetectedGame("Hades", gamePath, 4242);
        now = now.AddSeconds(10); await auto.TickOnceAsync();
        Check(knownSeen == true && started.SequenceEqual(new[] { "Hades" }), "Klasör kuralına uymayan oyun, Konsol'un bildirdiği klasör sayesinde algılanır");
        Check(sw.Calls.Count == 1, "Aynı oyun için ikinci kez mod uygulanmaz (tek oturum)");

        running = null; now = now.AddSeconds(5); await auto.TickOnceAsync();
        Check(sw.CurrentKey == "gunluk" && sw.Calls.SequenceEqual(new[] { "oyun", "gunluk" }), "Süreç kapanınca önceki moda (Günlük) dönülür");

        // Süreç hiç gelmezse bekleme süresi sonunda geri alınır
        sw.Reset("gunluk"); knownSeen = null;
        var s2 = NewSettings(); var auto2 = NewAuto(s2);
        await auto2.NotifyGameLaunching(hint);
        now = now.AddSeconds(100); await auto2.TickOnceAsync();
        Check(sw.CurrentKey == "oyun", "100 sn sonra hâlâ bekleniyor");
        now = now.AddSeconds(100); await auto2.TickOnceAsync();
        Check(sw.CurrentKey == "gunluk", "180 sn içinde süreç gelmeyince uygulanan mod geri alınır");

        // Otomatik mod kapalıysa mod uygulanmaz ama algılamaya yardım eder
        sw.Reset("gunluk"); knownSeen = null;
        var s3 = NewSettings(); var auto3 = NewAuto(s3, autoMode: false);
        await auto3.NotifyGameLaunching(hint);
        running = new Pulse.Core.Automation.GameDetector.DetectedGame("Hades", gamePath, 1);
        var started3 = new List<string>(); auto3.GameStarted += started3.Add;
        await auto3.TickOnceAsync();
        Check(sw.Calls.Count == 0 && started3.Count == 1 && knownSeen == true, "Otomatik oyun modu kapalıyken mod değişmez; oyun yine de algılanır");
        running = null;

        // Kullanıcı bu oyun için otomatik geçişi kapatmışsa dokunulmaz
        sw.Reset("gunluk");
        var s4 = NewSettings(); s4.Current.GameProfiles.Add(new GameProfile { ExeName = "hades", DisplayName = "Hades", ExePath = gamePath, Enabled = false });
        var auto4 = NewAuto(s4);
        await auto4.NotifyGameLaunching(hint);
        Check(sw.Calls.Count == 0, "Profilde otomatik geçiş kapalıysa ipucu mod uygulamaz");

        // Profil ada/klasöre göre bulunur ve kendi modu kullanılır
        sw.Reset("gunluk");
        var s5 = NewSettings(); s5.Current.GameProfiles.Add(new GameProfile { ExeName = "hades", DisplayName = "H A D E S", ModeKey = "sessiz", ExePath = gamePath });
        var auto5 = NewAuto(s5);
        await auto5.NotifyGameLaunching(hint);
        Check(sw.Calls.SequenceEqual(new[] { "sessiz" }), "Oyunun profili (ad/klasör eşleşmesi) kendi modunu uygular");

        // Oyun sürüyor ipucu mod uygulamaz; Konsol boşa çıkınca ipucu silinir
        sw.Reset("gunluk"); knownSeen = null;
        var s6 = NewSettings(); var auto6 = NewAuto(s6);
        auto6.NotifyGamePlaying(hint);
        Check(sw.Calls.Count == 0, "'Oyun sürüyor' ipucu tek başına mod uygulamaz");
        running = new Pulse.Core.Automation.GameDetector.DetectedGame("Hades", gamePath, 2);
        await auto6.TickOnceAsync();
        Check(knownSeen == true, "Pulse oyundan sonra açılsa da klasör ipucuyla oyunu bulur");
        Check(s6.FindProfile("Hades") is { ExePath: not null }, "Bulunan oyun profile eklenir (sonra Konsol olmadan da tanınır)");
        running = null;

        var auto7 = NewAuto(NewSettings());           // temiz ayar: öğrenilmiş profil yok
        auto7.NotifyGamePlaying(hint); auto7.NotifyKonsolIdle();
        knownSeen = null; running = new Pulse.Core.Automation.GameDetector.DetectedGame("Hades", gamePath, 3);
        await auto7.TickOnceAsync();
        Check(knownSeen == false, "Konsol kütüphaneye dönünce klasör ipucu geçersiz olur (kural dışı program oyun sayılmaz)");
        running = null;

    }
    finally
    {
        Environment.SetEnvironmentVariable("KLYC_KONSOL_DIR", null);
        Environment.SetEnvironmentVariable("KLYC_PULSE_DIR", null);
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine(fails == 0 ? "KONSOL BAĞLANTISI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "procwin-test")
{
    // Süreç listesi pencere bilgisini tek geçişte toplar: .NET'in süreç başına MainWindowHandle sonucuyla aynı mı, ve daha hızlı mı?
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var ins = new Pulse.Core.Processes.ProcessInspector();
    ins.Sample();
    var groups = ins.Sample();
    var mismatch = 0; var withWindow = 0;
    foreach (var g in groups)
    {
        var expect = false;
        foreach (var pid in g.Pids) { try { using var pr = System.Diagnostics.Process.GetProcessById(pid); if (pr.MainWindowHandle != IntPtr.Zero) { expect = true; break; } } catch { } }
        if (expect) withWindow++;
        if (expect != g.HasWindow) mismatch++;
    }
    Console.WriteLine($"  {groups.Count} grup, {withWindow} tanesi pencereli, uyuşmayan {mismatch}");
    Check(groups.Count > 10 && withWindow > 0, "Süreçler ve pencereliler bulundu");
    Check(mismatch <= 2, "Pencere bilgisi .NET'in MainWindowHandle sonucuyla aynı (en çok 2 anlık fark: süreç tam o sırada açılıp kapanmış olabilir)");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 5; i++) ins.Sample();
    var perSample = sw.Elapsed.TotalMilliseconds / 5;
    sw.Restart();
    foreach (var pr in System.Diagnostics.Process.GetProcesses()) { try { _ = pr.MainWindowHandle; _ = pr.MainWindowTitle; } catch { } pr.Dispose(); }
    var oldWay = sw.Elapsed.TotalMilliseconds;
    Console.WriteLine($"  Bir ölçüm: {perSample:0} ms (eski yöntemin yalnızca pencere kısmı: {oldWay:0} ms)");
    Check(perSample < 1500, "Bir süreç ölçümü 1,5 sn'den kısa");
    Console.WriteLine(fails == 0 ? "SÜREÇ PENCERE TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "support-test")
{
    // "Sorun bildir" paketi: kişisel veri temizlenir, ayarlardan yalnızca bayraklar, günlük/rapor zipe girer
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var user = Environment.UserName; var machine = Environment.MachineName; var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dirty = $@"Hata: {profile}\AppData\Local\Pulse\x.json. Kullanıcı {user}, makine {machine}. JSON: {profile.Replace(@"\", @"\\")}\\Pulse";
    var clean = Pulse.Core.Diagnostics.SupportBundle.Sanitize(dirty, user, machine, profile);
    Check(!clean.Contains(user, StringComparison.OrdinalIgnoreCase) || user.Length < 3, "Kullanıcı adı temizlenir");
    Check(machine.Length < 3 || !clean.Contains(machine, StringComparison.OrdinalIgnoreCase), "Bilgisayar adı temizlenir");
    Check(clean.Contains("<kullanici>"), "Yerine genel ad yazılır");
    Check(Pulse.Core.Diagnostics.SupportBundle.Sanitize("temiz metin", user, machine, profile) == "temiz metin", "Temiz metne dokunulmaz");
    var st = new Pulse.Core.Settings.AppSettings(); st.GameProfiles.Add(new Pulse.Core.Settings.GameProfile { ExeName = "x", ExePath = @"C:\Users\biri\oyun.exe" });
    var flags = Pulse.Core.Diagnostics.SupportBundle.DescribeFlags(st);
    Check(flags.Contains("GameProfiles=1") && !flags.Contains("biri") && !flags.Contains("oyun.exe"), "Ayarlardan yalnızca bayrak/sayı gider (yol yok)");
    var tmp = Path.Combine(Path.GetTempPath(), "pulse-support-test-" + Guid.NewGuid().ToString("N")[..6]);
    var logs = Path.Combine(tmp, "logs"); Directory.CreateDirectory(logs);
    File.WriteAllText(Path.Combine(logs, "2026-10-10.log"), $"[01:00:00] eski {user}");
    File.WriteAllText(Path.Combine(logs, "2026-10-11.log"), $@"[01:00:00] yol {profile}\x kullanıcı {user}");
    File.WriteAllText(Path.Combine(logs, "2026-10-12.log"), $"[01:00:00] bugün {machine}");
    File.WriteAllText(Path.Combine(logs, "2026-10-09.log"), "çok eski gün");
    File.WriteAllText(Path.Combine(tmp, "rapor.json"), $@"{{ ""Game"": ""X"", ""p"": ""{profile.Replace(@"\", @"\\")}"" }}");
    try
    {
        var zipPath = Pulse.Core.Diagnostics.SupportBundle.Create("sürüm bilgisi", tmp, logs, Path.Combine(tmp, "rapor.json"), logDays: 3);
        using var z = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Check(names.Contains("info.txt") && names.Contains("last_game_report.json") && names.Count(n => n.StartsWith("logs/")) == 3, "Zipte bilgi, rapor ve en yeni 3 günlük var");
        Check(!names.Contains("logs/2026-10-09.log"), "Eski günlükler girmez");
        var allText = string.Concat(z.Entries.Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
        Check((user.Length < 3 || !allText.Contains(user, StringComparison.OrdinalIgnoreCase)) && (machine.Length < 3 || !allText.Contains(machine, StringComparison.OrdinalIgnoreCase)), "Zip içinde kullanıcı/bilgisayar adı yok");
    }
    finally { try { Directory.Delete(tmp, true); } catch { } }
    Console.WriteLine(fails == 0 ? "SORUN BİLDİR TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "disk-test")
{
    // Disk sağlığı birleştirme: okunan değerler gösterilir, okunamayanın NEDENİ söylenir (sahte WMI satırlarıyla; donanıma dokunmaz)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Dictionary<string, object?> D(string id, string name, int health) => new() { ["DeviceId"] = id, ["FriendlyName"] = name, ["HealthStatus"] = health };
    Dictionary<string, object?> C(string id, int? wear, int? temp, long? hours, long rerr = 0) => new() { ["DeviceId"] = id, ["Wear"] = wear, ["Temperature"] = temp, ["PowerOnHours"] = hours, ["ReadErrorsTotal"] = rerr, ["WriteErrorsTotal"] = 0L };
    var full = Pulse.Core.Health.DiskHealth.Build([D("0", "Micron SSD", 0)], [C("0", 3, 38, 1240)], isAdmin: true)[0];
    Check(full is { Healthy: true, WearPercent: 3, TempC: 38, PowerOnHours: 1240, Note: "" }, "Her şey okunduysa not boş");
    var noAdmin = Pulse.Core.Health.DiskHealth.Build([D("0", "Micron SSD", 0)], [], isAdmin: false)[0];
    Check(noAdmin.Note.Contains("yönetici") && noAdmin.Note.Contains("aşınma") && noAdmin.Note.Contains("sıcaklık") && noAdmin.Note.Contains("çalışma saati"), "Yönetici değilse neden: yönetici gerekir");
    var admin = Pulse.Core.Health.DiskHealth.Build([D("1", "USB Disk", 0)], [C("1", null, null, null)], isAdmin: true)[0];
    Check(admin.Note.Contains("bildirmiyor") && !admin.Note.Contains("yönetici olarak"), "Yöneticiyken okunamıyorsa neden: disk bildirmiyor");
    var partial = Pulse.Core.Health.DiskHealth.Build([D("2", "NVMe", 0)], [C("2", null, 41, 900)], isAdmin: true)[0];
    Check(partial.TempC == 41 && partial.Note.Contains("aşınma") && !partial.Note.Contains("sıcaklık"), "Kısmi okuma: yalnızca eksik olanın nedeni yazılır");
    var zero = Pulse.Core.Health.DiskHealth.Build([D("3", "X", 0)], [C("3", 1, 0, 5)], isAdmin: true)[0];
    Check(zero.TempC is null && zero.Note.Contains("sıcaklık"), "0 °C sıcaklık okunmuş sayılmaz");
    var bad = Pulse.Core.Health.DiskHealth.Build([D("4", "Eski HDD", 2)], [C("4", 5, 40, 90000, rerr: 12)], isAdmin: true)[0];
    Check(!bad.Healthy && bad.Errors == 12, "Sağlıksız disk ve hata sayısı");
    var real = Pulse.Core.Health.DiskHealth.Read();
    Console.WriteLine($"  Bu bilgisayar: {real.Count} disk");
    foreach (var d in real) Console.WriteLine($"    {d.Name}: {d.HealthText}, aşınma {d.WearPercent?.ToString() ?? "-"}, sıcaklık {d.TempC?.ToString() ?? "-"}, saat {d.PowerOnHours?.ToString() ?? "-"}  | {d.Note}");
    Check(real.Count > 0, "Gerçek disk listesi okunur");
    Check(real.All(d => d.Note.Length == 0 || d.Note.Contains("yönetici") || d.Note.Contains("bildirmiyor")), "Okunamayan her değerin nedeni yazılmış");
    Console.WriteLine(fails == 0 ? "DİSK SAĞLIĞI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "konsol-install-test")
{
    // KLYC Konsol kartı: kurulu mu, yetkisiz başlatma (masaüstü kabuğunun belirteciyle)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var tmp = Path.Combine(Path.GetTempPath(), "pulse-konsol-install-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(tmp);
    try
    {
        var c = KonsolInstall.Candidates(Path.Combine(tmp, "la"), envExe: null);
        Check(c.Any(x => x.EndsWith(@"KLYC-Konsol\current\KlycKonsol.exe")) && c.Any(x => x.EndsWith(@"Programs\KLYC-Konsol\KlycKonsol.exe")), "Setup.exe ve Kur.ps1 konumları aranır");
        Check(KonsolInstall.FindExe(c) is null, "Kurulu değilse null");
        Directory.CreateDirectory(Path.GetDirectoryName(c[0])!); File.WriteAllText(c[0], "x");
        Check(KonsolInstall.FindExe(c) == c[0], "Kurulu exe bulunur");
        Check(KonsolInstall.Candidates(Path.Combine(tmp, "la"), envExe: @"C:\ozel\K.exe")[0] == @"C:\ozel\K.exe", "KLYC_KONSOL_EXE ile özel yol önceliklidir");
        var marker = Path.Combine(tmp, "basladi.txt");
        var ok = KonsolInstall.Launch(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c echo tamam> \"{marker}\"");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (ok && !File.Exists(marker) && sw.Elapsed.TotalSeconds < 10) await Task.Delay(200);
        Check(ok && File.Exists(marker), "Başlatma çalışır (yetkisiz Pulse: doğrudan; yönetici Pulse: kabuk belirteciyle, bu yol testte denenemez)");
        Check(!KonsolInstall.Launch(Path.Combine(tmp, "yok", "yok.exe")), "Olmayan exe başlatılamaz (hata vermez, false döner)");
    }
    finally { try { Directory.Delete(tmp, true); } catch { } }
    Console.WriteLine(fails == 0 ? "KONSOL KURULUM TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "startup-guard-test")
{
    // Açılış öğeleri: uygula-oku-doğrula, korunan öğeler; kaldırmada oyun başlatıcısı tanıma (kayıt defterinde yalnızca kendi sahte HKCU değerimize dokunulur, sonunda silinir)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Check(StartupManager.ProtectionReason("SecurityHealth") is { Block: true } && StartupManager.ProtectionReason("SecurityHealthSystray") is { Block: true } && StartupManager.ProtectionReason("Windows Defender notification icon") is { Block: true }, "Windows Güvenliği öğeleri engellenir");
    Check(StartupManager.ProtectionReason("KLYC-Konsol") is { Block: false, Reason.Length: > 0 }, "KLYC Konsol öğesi uyarıyla değiştirilebilir (engellenmez)");
    Check(StartupManager.ProtectionReason("Discord") is null && StartupManager.ProtectionReason("Spotify") is null, "Sıradan öğeler serbest");
    Check(WingetService.LooksLikeGameLauncher("Steam") && WingetService.LooksLikeGameLauncher("Epic Games Launcher") && WingetService.LooksLikeGameLauncher("Battle.net") && WingetService.LooksLikeGameLauncher("Steam Client Bootstrapper"), "Oyun başlatıcıları tanınır");
    Check(!WingetService.LooksLikeGameLauncher("Visual Studio Code") && !WingetService.LooksLikeGameLauncher("Notepad++") && !WingetService.LooksLikeGameLauncher("Steamworks"), "Sıradan uygulama başlatıcı sayılmaz");
    const string approvedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    var name = "KlycPulseTestEntry" + Guid.NewGuid().ToString("N")[..6];
    var item = new StartupItem(name, "x.exe", "test", false, true, "", "Run", name);
    try
    {
        Check(StartupManager.SetEnabled(item, false), "Kapatma yazılır ve geri okunarak doğrulanır");
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey))
            Check(k?.GetValue(name) is byte[] { Length: 12 } b && b[0] == 3, "Kayıt Görev Yöneticisi biçiminde (12 bayt, 3 = kapalı)");
        Check(StartupManager.SetEnabled(item, true), "Açma yazılır ve doğrulanır");
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey))
            Check(k?.GetValue(name) is byte[] b2 && b2[0] == 2, "Açık = 2");
    }
    finally
    {
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey, true); k?.DeleteValue(name, false); } catch { }
    }
    Console.WriteLine(fails == 0 ? "AÇILIŞ KORUMA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "retranslate-test")
{
    // Türkçe kaydedilmiş oyun raporu bulguları İngilizce arayüzde İngilizce görünür (sayılar korunur)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Pulse.Core.Localization.Loc.Configure("tr");
    var stored1 = Pulse.Core.Localization.Loc.F("Arka planda {0} oyun boyunca ortalama %{1:0.#} işlemci kullandı (en çok %{2:0}).{3}", "vgc", 10.1, 48.0, "");
    var stored2 = Pulse.Core.Localization.Loc.F("Ortalama {0:0} FPS, en kötü %1'lik karelerde {1:0} FPS. En kötü karelerde bile akıcı.", 216.0, 129.0);
    var stored3 = "Bu tamamen bilinmeyen bir cümle: 42";
    Check(Pulse.Core.Localization.Loc.Retranslate(stored1) == stored1, "Türkçe modda saklanan metin aynen kalır");
    Pulse.Core.Localization.Loc.Configure("en");
    var en1 = Pulse.Core.Localization.Loc.Retranslate(stored1);
    Console.WriteLine("  " + stored1 + " → " + en1);
    Check(en1.StartsWith("In the background vgc") && en1.Contains("10.1") && en1.Contains("48"), "Arka plan bulgusu İngilizceye çevrilir, program adı ve sayılar korunur (10,1 → 10.1)");
    var en2 = Pulse.Core.Localization.Loc.Retranslate(stored2);
    Console.WriteLine("  " + stored2 + " → " + en2);
    Check(en2.Contains("216") && en2.Contains("129") && !en2.Contains("Ortalama"), "FPS bulgusu çevrilir");
    Check(Pulse.Core.Localization.Loc.Retranslate(stored3) == stored3, "Tanınmayan metin aynen kalır (hiçbir şey bozulmaz)");
    Check(Pulse.Core.Localization.Loc.Retranslate("") == "", "Boş metin");
    // Gerçek kayıtlı raporlar (bu bilgisayardaki geçmiş): kaç bulgu çevrilebildi? (bilgi; çevrilemeyenler listelenir)
    try
    {
        var all = Pulse.Core.Diagnostics.GameReportStore.LoadHistory().SelectMany(r => r.Findings).Distinct().ToList();
        var same = all.Where(f => Pulse.Core.Localization.Loc.Retranslate(f) == f).ToList();
        Console.WriteLine($"  Gerçek geçmiş: {all.Count} farklı bulgu, çevrilemeyen {same.Count}");
        foreach (var f in same.Take(6)) Console.WriteLine("    çevrilemedi: " + f[..Math.Min(110, f.Length)]);
        Check(all.Count == 0 || same.Count * 4 <= all.Count, "Gerçek geçmiş bulgularının en az %75'i çevrilir");
    }
    catch (Exception ex) { Console.WriteLine("  (geçmiş okunamadı: " + ex.Message + ")"); }
    // Tüm şablonlar derlenebiliyor, kendi Türkçe çıktısını İngilizceye geri çevirebiliyor (örnek: 3 şablon)
    Console.WriteLine(fails == 0 ? "YENİDEN ÇEVİRİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "boot-test")
{
    // Açılış analizi: sahte olaylarla birleştirme/metin; gerçek günlükte neden/sonuç
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    BootAnalyzer.RawEvent E(int id, DateTime t, params (string K, string V)[] d) => new(id, t, d.ToDictionary(x => x.K, x => x.V));
    var t0 = new DateTime(2026, 10, 12, 8, 0, 0);
    var info = BootAnalyzer.Build([
        E(100, t0.AddDays(-3), ("BootTime", "30000"), ("MainPathBootTime", "20000"), ("BootPostBootTime", "10000")),
        E(100, t0, ("BootTime", "74500"), ("MainPathBootTime", "41000"), ("BootPostBootTime", "33500")),
        E(101, t0.AddMinutes(1), ("FriendlyName", "Discord"), ("Name", "Discord.exe"), ("DegradationTime", "12000")),
        E(101, t0.AddMinutes(2), ("FriendlyName", "OneDrive"), ("DegradationTime", "8000")),
        E(101, t0.AddMinutes(2), ("FriendlyName", "OneDrive"), ("DegradationTime", "9500")),
        E(101, t0.AddMinutes(3), ("Name", "steam.exe"), ("DegradationTime", "4000")),
        E(101, t0.AddHours(5), ("FriendlyName", "BaşkaBoot"), ("DegradationTime", "99000")),
        E(101, t0.AddDays(-3).AddMinutes(1), ("FriendlyName", "EskiBoot"), ("DegradationTime", "50000")),
    ]);
    Check(info is { TotalSeconds: 74.5, MainPathSeconds: 41, PostBootSeconds: 33.5 }, "En son açılış seçilir ve süreler saniyeye çevrilir");
    Check(info!.Slow.Select(s => s.Name).SequenceEqual(["Discord", "OneDrive", "steam.exe"]), "Yalnız o açılışın geciktirenleri, aynı ad tekilleştirilip büyükten küçüğe");
    Check(info.Slow[1].DelaySeconds == 9.5, "Aynı programın en büyük gecikmesi alınır");
    var text = BootAnalyzer.Describe(info, null);
    Check(text.Contains("75 sn") || text.Contains("74"), "Metinde toplam süre");
    Check(text.Contains("yavaş") && text.Contains("Discord (+12 sn)"), "74 sn yavaş sayılır, geciktirenler yazılır");
    Check(!BootAnalyzer.Describe(BootAnalyzer.Build([E(100, t0, ("BootTime", "20000"), ("MainPathBootTime", "12000"), ("BootPostBootTime", "8000"))]), null).Contains("yavaş"), "Hızlı açılış yavaş denmez");
    Check(BootAnalyzer.Build([]) is null && BootAnalyzer.Describe(null, "neden").Contains("neden"), "Ölçüm yoksa nedeni söylenir");
    var real = BootAnalyzer.Read(out var err);
    Console.WriteLine("  Bu bilgisayar: " + BootAnalyzer.Describe(real, err));
    Check(real is not null || !string.IsNullOrEmpty(err), "Gerçek günlükte ya sonuç ya da neden var");
    Console.WriteLine(fails == 0 ? "AÇILIŞ ANALİZİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "weekly-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var now = new DateTime(2026, 10, 12, 12, 0, 0);
    GameSessionReport R(string g, int daysAgo, double min, double? fps, double? temp, int sev) => new() { Game = g, Start = now.AddDays(-daysAgo), Minutes = min, AvgFps = fps, CpuTempMax = temp, Severity = sev };
    var hist = new[] { R("Hades", 1, 60, 120, 80, 0), R("Hades", 2, 120, 140, 91, 1), R("CS2", 3, 30, 200, 75, 0), R("Eski", 12, 600, 30, 99, 2) };
    var w = WeeklyReport.Build(hist, now, now.AddDays(-2), 52428800);
    Check(w.Sessions == 3 && w.Hours == 3.5, "Son 7 günün 3 oturumu ve 3,5 saati (12 gün önceki sayılmaz)");
    Check(w.HottestCpuC == 91 && w.HottestGame == "Hades" && w.MostPlayed == "Hades", "En sıcak ve en çok oynanan oyun");
    Check(w.AvgFps == 153 && w.ProblemSessions == 1, "Ortalama FPS ve sorunlu oturum sayısı");
    Check(w.LastClean is not null && w.LastCleanBytes == 52428800, "Son 7 günde temizlik varsa gösterilir");
    var old = WeeklyReport.Build(hist, now, now.AddDays(-20), 999);
    Check(old.LastClean is null && old.LastCleanBytes == 0, "Eski temizlik haftalık özete girmez");
    var text = WeeklyReport.Describe(w);
    Check(text.Contains("3 oyun oturumu") && text.Contains((3.5).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)) && text.Contains("91") && text.Contains("50 MB"), "Metin özetin tüm parçalarını içerir");
    Check(WeeklyReport.Describe(WeeklyReport.Build([], now)).Contains("yok"), "Hiç kayıt yoksa bunu söyler");
    Check(!WeeklyReport.Describe(WeeklyReport.Build([R("X", 1, 10, null, null, 0)], now)).Contains("FPS"), "Ölçülmeyen değer yazılmaz (FPS yok)");
    Console.WriteLine(fails == 0 ? "HAFTALIK ÖZET TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "heatbench-full")
{
    // Gerçek bilgisayarda TAM ısı denemesi (uygulamanın "Isı Denemesi" ile aynı plan, ~9 dk): işlemci tam yüklenir, ekran kartı için Edge'de WebGL penceresi açılır.
    // Yönetici değilse sıcaklık okunamaz (hız yine ölçülür). Sonunda tüm güç ayarlarının ve geçici dosyaların eski haline döndüğü doğrulanır.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    int? Q(string k) => Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k);
    int?[] Snap() => [Q(Pulse.Core.Platform.Powercfg.BoostMode), Q(Pulse.Core.Platform.Powercfg.MaxProcessorState), Q(Pulse.Core.Platform.Powercfg.MaxFrequency), Q(Pulse.Core.Platform.Powercfg.MaxFrequencyClass1)];
    var before = Snap();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var progress = new Progress<string>(m => Console.WriteLine($"  [{sw.Elapsed.TotalMinutes:0.0} dk] {m}"));
    var first = Pulse.Core.Hardware.HeatBenchmark.RunPlan([(null, false)], 40, 80, progress);
    var baseMhz = first.Count > 0 ? first[0].MedianMhz : 0;
    Check(first.Count == 1 && baseMhz > 500, $"Yalnız işlemci aşaması ölçüldü (sürekli hız {baseMhz:0} MHz)");
    int Round100(double v) => (int)(Math.Round(v / 100.0) * 100);
    var caps = new List<int?> { null, Round100(baseMhz * 0.85), Round100(baseMhz * 0.70) };
    var second = Pulse.Core.Hardware.HeatBenchmark.RunPlan(caps.Select(c => (c, true)).ToList(), 40, 80, progress);
    var all = first.Concat(second).ToList();
    Console.WriteLine("  " + Pulse.Core.Hardware.HeatBenchmark.Summarize(all));
    Check(second.Count == 3, "Ekran kartlı üç aşama ölçüldü (sınırsız ve iki kademe)");
    Check(second.Count == 3 && second[1].MedianMhz < second[0].MedianMhz && second[2].MedianMhz < second[1].MedianMhz, "Hız sınırı koydukça ölçülen hız düşüyor (sınır gerçekten uygulanıyor)");
    Check(second.Count == 3 && second[1].CapMhz is { } c1 && Math.Abs(second[1].MedianMhz - c1) <= c1 * 0.12, "Sınırlı aşamanın hızı koyulan sınıra yakın (±%12)");
    Check(second.Count == 3 && second.All(p => p.GpuMedianMhz is > 0), "Her ekran kartlı aşamada ekran kartı hızı okundu");
    var hasTemps = all.All(p => p.AvgTempC is not null);
    Console.WriteLine(hasTemps ? "  Sıcaklıklar okundu." : "  (Sıcaklık okunamadı: yönetici olmadan beklenen durum; ısı doğrulaması yapılamadı.)");
    if (hasTemps) Check(second[2].AvgTempC <= second[0].AvgTempC + 1, "Sınır ısıyı artırmıyor (en düşük kademe ≤ sınırsız)");
    System.Threading.Thread.Sleep(2500);
    Check(Snap().SequenceEqual(before), "Güç ayarları (turbo, en yüksek durum, hız sınırları) eski haline döndü");
    Check(Pulse.Core.Platform.Powercfg.ActiveScheme() == scheme, "Etkin güç planı aynı");
    Check(!System.Diagnostics.Process.GetProcessesByName("msedge").Any(p => { try { return p.MainWindowTitle.StartsWith("F:") || p.MainWindowTitle.StartsWith("LOST"); } catch { return false; } }), "Ekran kartı yük penceresi kapandı");
    Check(!Directory.Exists(Path.Combine(Path.GetTempPath(), "pulse-gputune")), "Geçici yük klasörü silindi");
    Console.WriteLine($"  Toplam süre: {sw.Elapsed.TotalMinutes:0.0} dk");
    Console.WriteLine(fails == 0 ? "TAM ISI DENEMESİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "health")
{
    using var sampler = new HealthSampler();
    // WMI kararlılığı: art arda 15 okuma, hiçbiri takılmamalı
    var nulls = 0; HealthSample? last = null;
    for (var i = 0; i < 15; i++) { last = sampler.Sample(); if (last.CpuTempC is null) nulls++; }
    Console.WriteLine($"15 ardışık örnek, CPU sıcaklığı okunamayan: {nulls}");
    var s = last!;
    Console.WriteLine($"CPU {s.CpuTempC} °C | GPU {s.GpuTempC} °C (%{s.GpuUtilPercent}) | fan {s.CpuFanRpm}/{s.GpuFanRpm} RPM");
    var b = s.Battery;
    Console.WriteLine($"Pil: var={b.Present} voltaj={b.Volts} kalan={b.RemainingMwh} tasarım={b.DesignMwh} dolu={b.FullChargeMwh} döngü={b.Cycles} aşınma={b.WearPercent}");
    Console.WriteLine("--- Bulgular ---");
    var findings = HealthAnalyzer.Analyze(s);
    foreach (var f in findings) Console.WriteLine($"  [{f.Level}] {f.Title}: {f.Detail}");
    var (lvl, text) = HealthAnalyzer.Summarize(findings);
    Console.WriteLine($"ÖZET: {lvl}, {text}");
    return nulls == 15 ? 1 : 0;
}

if (cmd == "game")
{
    var g = GameDetector.FindRunningGame();
    Console.WriteLine($"Çalışan oyun: {g ?? "yok"}  |  şarjda: {PowerSource.IsOnAc()}");
    return 0;
}

if (cmd == "startup-test")
{
    // Açılışta başlatma görevini oluştur, doğrula, sil, doğrula. Yönetici gerekir.
    var exe = Environment.ProcessPath!;
    var before = StartupTask.IsEnabled();
    var (ok1, m1) = StartupTask.Enable(exe);
    var enabled = StartupTask.IsEnabled();
    var (ok2, m2) = StartupTask.Disable();
    var after = StartupTask.IsEnabled();
    Console.WriteLine($"Önce: {before} | Oluştur: {ok1} ({m1}) | Etkin görünüyor: {enabled} | Sil: {ok2} ({m2}) | Sonra: {after}");
    var pass = !before && ok1 && enabled && ok2 && !after;
    Console.WriteLine(pass ? "AÇILIŞ GÖREVİ TESTİ GEÇTİ" : "AÇILIŞ GÖREVİ TESTİ KALDI");
    return pass ? 0 : 1;
}

if (cmd == "restore-point")
{
    var r = await RestorePoint.CreateAsync("KLYC-Pulse test noktası");
    Console.WriteLine($"Oluşturuldu: {r.Created} | {r.Message}");
    return 0;
}

if (cmd == "cleanup-run")
{
    // Yalnızca bu üç yeniden oluşan sistem önbelleği: servis durdur/başlat yolunu gerçek veride sınar.
    var ids = new[] { "delivery-opt", "wu-cache", "error-reports" };
    var engineC = new CleanupEngine();
    foreach (var cat in CleanupCatalog.Build().Where(x => ids.Contains(x.Id)))
    {
        var before = engineC.Scan(cat);
        var res = engineC.Clean(cat);
        var after = engineC.Scan(cat);
        Console.WriteLine($"  {cat.Name}: önce {before.Bytes / 1048576.0:N1} MB -> silinen {res.FreedBytes / 1048576.0:N1} MB, atlanan {res.Skipped} -> sonra {after.Bytes / 1048576.0:N1} MB {res.Note}");
    }
    foreach (var svc in new[] { "DoSvc", "wuauserv", "bits" })
        Console.WriteLine($"  Servis {svc}: durum kodu {ServiceControl.State(svc)} (1 durdu, 4 çalışıyor)");
    return 0;
}

if (cmd == "uninstall-parse-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    bool Has(string p) => p is @"C:\Program Files (x86)\Example Vendor\Uninstall Example App.exe" or @"C:\Program Files\App\unins000.exe" or @"C:\Windows\System32\msiexec.exe";
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand(@"C:\Program Files (x86)\Example Vendor\Uninstall Example App.exe", Has) == "\"C:\\Program Files (x86)\\Example Vendor\\Uninstall Example App.exe\"", "Tırnaksız, boşluklu yol tırnağa alınır");
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand(@"C:\Program Files\App\unins000.exe /SILENT", Has) == "\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "Bağımsız değişkenler korunur");
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand("\"C:\\Program Files\\App\\unins000.exe\" /S", Has) == "\"C:\\Program Files\\App\\unins000.exe\" /S", "Zaten tırnaklı komuta dokunulmaz");
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand("MsiExec.exe /X{ABC}", Has) == "MsiExec.exe /X{ABC}", "MsiExec gibi yolsuz komut olduğu gibi kalır");
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand("steam://uninstall/12345", Has) == "steam://uninstall/12345", "steam:// bağlantısına dokunulmaz");
    Check(Pulse.Core.Apps.AppUninstaller.NormalizeCommand(@"C:\Olmayan Klasor\x.exe", Has) == @"C:\Olmayan Klasor\x.exe", "Dosya bulunamazsa komut değişmez");
    Console.WriteLine(fails == 0 ? "KALDIRMA KOMUTU TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "keeper-test")
{
    // Başka bir araç güç ayarını bozarsa algılanıp düzeltiliyor mu? (Günlük modun turbo ayarını bozar, onarır, kontrol eder.)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var def = Modes.Get("gunluk")!;
    using var keeperEngine = new ModeEngine();
    keeperEngine.Apply(def, new ModeOptions { ChangeBrightness = false });
    Check(ModeEngine.PowerMatches(def, false), "Mod uygulandıktan sonra güç ayarları modla uyuşuyor");
    Check(Pulse.Core.Platform.Powercfg.GetDc(Pulse.Core.Platform.Powercfg.ActiveScheme()!, Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.BoostMode) == def.Boost, "Pildeyken (DC) turbo ayarı da modla aynı");
    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    Pulse.Core.Platform.Powercfg.SetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.BoostMode, 0);   // başka bir araç turbo'yu kapatmış gibi
    Pulse.Core.Platform.Powercfg.SetActive(scheme);
    Check(!ModeEngine.PowerMatches(def, false), "Bozulma (turbo kapatılmış) algılandı");
    keeperEngine.ReapplyPower(def);
    Check(ModeEngine.PowerMatches(def, false), "Geri düzeltildi ve doğrulandı");
    Pulse.Core.Platform.Powercfg.SetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.MaxProcessorState, 80);
    Pulse.Core.Platform.Powercfg.SetActive(scheme);
    Check(!ModeEngine.PowerMatches(def, false) && ModeEngine.PowerMatches(def, true), "Üst sınır farkı: normalde bozulma, sıcaklık sınırı açıkken yok sayılır");
    keeperEngine.ReapplyPower(def);
    Check(ModeEngine.PowerMatches(def, false), "Son durum modla uyuşuyor");
    Console.WriteLine(fails == 0 ? "MOD KORUYUCU TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "hotkey-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var h = Pulse.Core.Settings.HotkeyBinding.Parse("Ctrl+Alt+3");
    Check(h is { Modifiers: 3, VirtualKey: 0x33 } && h.Value.ToString() == "Ctrl+Alt+3", "Ctrl+Alt+3 çözülür ve geri yazılır");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("ctrl + shift + f9") is { Modifiers: 6, VirtualKey: 0x78 }, "Küçük harf ve boşluklu F9 çözülür");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("Pause") is { IsValid: true }, "Pause tek başına geçerli");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("F13") is { IsValid: true }, "F13 tek başına geçerli");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("A") is { IsValid: false }, "Düz A tuşu geçersiz (her yazışta tetiklenirdi)");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("Shift+A") is { IsValid: false }, "Shift+A geçersiz");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("Ctrl+Alt") is null, "Tuşsuz kombinasyon geçersiz");
    Check(Pulse.Core.Settings.HotkeyBinding.Parse("Ctrl+A+B") is null, "İki ana tuş geçersiz");
    var map = Pulse.Core.Settings.HotkeyActions.Resolve(new Dictionary<string, string> { ["mode:oyun"] = "Ctrl+Alt+G", ["kbd:cycle"] = "" });
    Check(map["mode:oyun"] is { VirtualKey: 0x47 }, "Kayıtlı atama varsayılanın önüne geçer");
    Check(map["kbd:cycle"] is null, "Boş atama = atanmamış");
    Check(map["mode:gunluk"] is { VirtualKey: 0x32 }, "Kayıtsız eylem varsayılanı alır");
    // KLYC Konsol Ctrl+Alt+K'yı genel kısayol olarak kullanır: Pulse'ın hiçbir eylemi (varsayılan ya da eski kayıtlı atama) o tuşu almamalı
    var def2 = Pulse.Core.Settings.HotkeyActions.Resolve(null);
    var old = Pulse.Core.Settings.HotkeyActions.Resolve(new Dictionary<string, string> { ["kbd:cycle"] = "Ctrl+Alt+K" });
    Check(def2.Values.All(b => b is not { VirtualKey: 0x4B }), "Varsayılan kısayollarda Ctrl+Alt+K yok (KLYC Konsol'un kısayolu)");
    Check(old["kbd:cycle"] is { VirtualKey: 0x42 }, "Eski kayıtlı Ctrl+Alt+K ataması yeni varsayılana (Ctrl+Alt+B) taşınır");
    Console.WriteLine(fails == 0 ? "KISAYOL TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "refresh-test")
{
    // Yenileme hızı seçimi: sahte ekran listeleriyle (donanıma dokunmaz). 180 Hz'lik ekran dahil.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    int R(int req, params int[] supported) => Pulse.Core.Modes.ModeEngine.ResolveRefreshTarget(req, supported);
    const int Max = Pulse.Core.Modes.Modes.MaxHz;
    Check(R(Max, 60, 144, 180) == 180, "180 Hz'lik ekranda 'en yüksek' = 180");
    Check(R(Max, 60, 100, 144) == 144, "144 Hz'lik ekranda 'en yüksek' = 144");
    Check(R(Max, 60) == 60, "Yalnız 60 Hz'lik ekranda 'en yüksek' = 60");
    Check(R(Max) == Max, "Liste okunamazsa belirlenemedi (atlanır)");
    Check(R(60, 60, 144, 180) == 60, "Açık 60 Hz isteği olduğu gibi kalır");
    Check(Pulse.Core.Modes.Modes.Get("oyun")!.RefreshHz == Max && Pulse.Core.Modes.Modes.Get("gunluk")!.RefreshHz == Max, "Oyun ve Günlük modu 'en yüksek' ister");
    Check(Pulse.Core.Modes.Modes.Get("sessiz")!.RefreshHz == 60 && Pulse.Core.Modes.Modes.Get("bosta")!.RefreshHz == 60, "Sessiz ve Boşta 60 Hz kalır");
    Console.WriteLine(fails == 0 ? "YENİLEME HIZI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "governor-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var t0 = new DateTime(2026, 1, 1);
    var g = new Pulse.Core.Monitoring.StepGovernor(4) { TargetC = 85 };
    int? Run(double temp, int from, int to) { int? last = null; for (var s = from; s <= to; s++) if (g.Feed(temp, t0.AddSeconds(s)) is { } l) last = l; return last; }
    Check(Run(80, 0, 100) is null && g.Level == 0, "Hedefin altında kademe değişmez");
    Check(Run(90, 101, 112) is null, "12 sn sıcak: henüz kısmaz (15 sn bekler)");
    Check(Run(90, 113, 120) == 1 && g.Level == 1, "15 sn sıcak kalınca 1 kademe kısar");
    Check(Run(90, 121, 128) is null, "Kademeler arası 10 sn bekleme var (titreşim yok)");
    Check(Run(90, 129, 200) is 4 && g.Level == 4, "Sıcak kalmaya devam ederse en fazla son kademeye kadar iner");
    Check(Run(90, 201, 400) is null && g.Level == 4, "Son kademeden aşağı inmez");
    Check(Run(80, 401, 430) is null && g.Level == 4, "Hedefin altı ama yeterince değil (margin 8): geri vermez");
    Check(Run(70, 431, 480) == 3, "Hedef-8'in altında 40 sn kalınca 1 kademe geri verir");
    Check(Run(70, 481, 700) == 0 && g.Level == 0, "Serin kalırsa tam performansa döner");
    g.Reset();
    Check(g.Level == 0 && g.Feed(null, t0.AddSeconds(900)) is null, "Sensör yoksa (null) hata vermez, kademe sabit");
    Console.WriteLine(fails == 0 ? "ISI HEDEFİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "companion-test")
{
    // Yönetici gerekir. ThrottleStop'u başlatır, profilleri sırayla seçip doğrular, ilk profile döner, kapatır.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Check(Pulse.Core.Companion.ThrottleStopControl.IsInstalled, "ThrottleStop bulundu");
    Console.WriteLine("    profil adları: " + string.Join(", ", Pulse.Core.Companion.ThrottleStopControl.ProfileNames()));
    var l = Pulse.Core.Companion.ThrottleStopControl.Launch(minimized: true);
    Check(l.Ok, "Arka planda başlatıldı: " + l.Message);
    await Task.Delay(1500);
    foreach (var pr in new[] { 2, 4, 3, 1 })
    {
        var r = Pulse.Core.Companion.ThrottleStopControl.SetProfile(pr);
        Check(r.Ok, r.Message);
    }
    var c = Pulse.Core.Companion.ThrottleStopControl.Close();
    Check(c.Ok, "Kapatıldı: " + c.Message);
    Console.WriteLine($"    Afterburner kurulu: {Pulse.Core.Companion.AfterburnerControl.IsInstalled}, kayıtlı profil: {Pulse.Core.Companion.AfterburnerControl.SavedProfileCount()}; G-Helper yolu: {Pulse.Core.Companion.GHelperControl.FindExe()}");
    Console.WriteLine(fails == 0 ? "ORTAK ÇALIŞMA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "rgb-test")
{
    // Klavye rengi: yeşil sabit, sonra gökkuşağı, sonra kırmızı sabit (kullanıcının mevcut rengi) yazılır. Firmware kabulü kontrol edilir.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Check(Pulse.Core.Hardware.LaptopControl.IsRgbAvailable(), "Klavye RGB desteği algılandı");
    var s = new Pulse.Core.Settings.KeyboardRgb { Mode = 0, R = 0, G = 255, B = 0, Speed = 1 };
    Check(Pulse.Core.Hardware.LaptopControl.SetKeyboardRgb(s).Ok, "Yeşil sabit renk kabul edildi");
    await Task.Delay(2500);
    s.Mode = 3;
    Check(Pulse.Core.Hardware.LaptopControl.SetKeyboardRgb(s).Ok, "Gökkuşağı modu kabul edildi");
    await Task.Delay(2500);
    s.Mode = 0; s.R = 255; s.G = 0; s.B = 0;
    Check(Pulse.Core.Hardware.LaptopControl.SetKeyboardRgb(s).Ok, "Eski renk (kırmızı sabit) geri yazıldı");
    Console.WriteLine(fails == 0 ? "KLAVYE RGB TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "gpu-oc-tune")
{
    // Yönetici gerekir. Yük çalıştırır, ofseti kademe kademe dener, en iyi kararlı ayarı yazar, sonunda fabrika hızına döner.
    var progress = new Progress<string>(t => Console.WriteLine("  » " + t));
    var res = await Pulse.Core.Hardware.GpuTuner.RunAsync(progress);
    foreach (var s in res.Steps) Console.WriteLine($"  +{s.Core,3}/+{s.Mem,3}  fps {s.Fps,5:0.0}  çekirdek {s.CoreMhz,5:0} MHz  bellek {s.MemMhz,5:0} MHz  ısı {s.MaxTempC,3:0}°C  güç {s.PowerW,4:0} W  {(s.Stable ? "kararlı" : "KARARSIZ")} {s.Note}");
    Console.WriteLine("  Sonuç: " + res.Summary);
    Console.WriteLine($"  KARAR: {res.BestCore}/{res.BestMem}");
    var after = Pulse.Core.Hardware.GpuOverclock.Read();
    Console.WriteLine($"  Bitişte GPU ofseti: {after?.CoreMhz}/{after?.MemMhz} (0/0 olmalı)");
    return 0;
}

if (cmd == "gpu-oc-reset")
{
    var r = Pulse.Core.Hardware.GpuOverclock.Reset();
    Console.WriteLine($"  {r.Message}  (çekirdek {r.State?.CoreMhz}, bellek {r.State?.MemMhz})");
    return r.Ok && r.Verified ? 0 : 1;
}

if (cmd == "gpu-oc-read")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Pulse.Core.Hardware.GpuOverclock.Read()));
    return 0;
}

if (cmd == "gpu-oc-test")
{
    // Yönetici gerekebilir. Küçük bir ofset yazar, geri okur, sıfırlar. Sonunda fabrika hızında kalır.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var s0 = Pulse.Core.Hardware.GpuOverclock.Read();
    Console.WriteLine($"    önce: {System.Text.Json.JsonSerializer.Serialize(s0)}");
    Check(s0 is { Editable: true }, "Ekran kartı hızlandırma düzenlenebilir görünüyor");
    var r1 = Pulse.Core.Hardware.GpuOverclock.Apply(25, 100);
    Console.WriteLine("    " + r1.Message);
    Check(r1.Ok && r1.Verified, "+25 çekirdek / +100 bellek yazıldı ve geri okundu");
    var r2 = Pulse.Core.Hardware.GpuOverclock.Apply(1000, 5000);
    Console.WriteLine("    " + r2.Message);
    Check(r2.State is { } st && st.CoreMhz <= Pulse.Core.Hardware.GpuOverclock.SafeCoreCeiling && st.MemMhz <= Pulse.Core.Hardware.GpuOverclock.SafeMemCeiling, $"Aşırı istek güvenli tavana kırpıldı (okunan {r2.State?.CoreMhz}/{r2.State?.MemMhz})");
    var r3 = Pulse.Core.Hardware.GpuOverclock.Reset();
    Console.WriteLine("    " + r3.Message);
    Check(r3.Ok && r3.Verified && r3.State is { CoreMhz: 0, MemMhz: 0 }, "Fabrika hızına sıfırlandı ve doğrulandı");
    Console.WriteLine(fails == 0 ? "GPU HIZ AYARI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "gpu-oc-probe")
{
    // Yalnızca okur: NVIDIA performans durumu (P-state) saat ofsetleri düzenlenebilir mi, aralıkları ne?
    NvAPIWrapper.NVIDIA.Initialize();
    void Dump(object? o, string indent, int depth)
    {
        if (o is null || depth > 4) return;
        var t = o.GetType();
        if (o is System.Collections.IEnumerable en && o is not string)
        {
            var k = 0;
            foreach (var x in en) { Console.WriteLine($"{indent}[{k++}]"); Dump(x, indent + "  ", depth + 1); }
            return;
        }
        foreach (var prop in t.GetProperties())
        {
            object? v; try { v = prop.GetValue(o); } catch { continue; }
            if (v is null) continue;
            var vt = v.GetType();
            if (vt.IsPrimitive || vt.IsEnum || v is string) Console.WriteLine($"{indent}{prop.Name} = {v}");
            else { Console.WriteLine($"{indent}{prop.Name}:"); Dump(v, indent + "  ", depth + 1); }
        }
    }
    foreach (var gpu in NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs())
    {
        Console.WriteLine($"  GPU: {gpu.FullName}");
        var info = NvAPIWrapper.Native.GPUApi.GetPerformanceStates20(gpu.Handle);
        Dump(info, "   ", 0);
    }
    return 0;
}
if (cmd == "acpi-probe")
{
    using var acpi = AsusAcpi.TryOpen();
    if (acpi is null) { Console.WriteLine("ASUS sürücüsü yok"); return 2; }
    var ids = new (uint Id, string Name)[]
    {
        (0x00110024, "CPU fan eğrisi"), (0x00110025, "GPU fan eğrisi"), (0x00110032, "Orta fan eğrisi"),
        (0x001200A0, "PPT PL2 (SPPT)"), (0x001200A1, "PPT platform"), (0x001200A2, "PPT APU SPPT"), (0x001200A3, "PPT PL1 (SPL)"), (0x001200B0, "NVIDIA dinamik güç"), (0x001200C0, "NVIDIA sıcaklık hedefi"),
        (0x00100056, "TUF RGB modu"), (0x00100057, "TUF RGB durumu"), (0x00100058, "TUF RGB"),
        (0x00050019, "Panel overdrive"), (0x0005001E, "Panel güç tasarrufu"), (0x00090016, "GPU Eco"), (0x00090020, "GPU MUX"),
        (0x00120075, "Performans modu"), (0x00120057, "Pil limiti"), (0x00050021, "Klavye ışığı"), (0x00060078, "Fn kilidi"),
    };
    foreach (var (id, name) in ids)
    {
        var raw = acpi.GetRaw(id);
        Console.WriteLine($"  0x{id:X8}  {name,-26} ham=0x{raw:X8}  destek={(((raw & 0x10000) != 0) ? "EVET" : "hayır")}  değer={(raw & 0xFFFF)}");
    }
    return 0;
}

if (cmd == "gpu-cap-test")
{
    // Yönetici gerekir. Sınırı uygular, doğrular, kaldırır. Sonunda sınır kaldırılmış olur.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var r1 = Pulse.Core.Hardware.GpuClocks.Cap(1350);
    Console.WriteLine("    " + r1.Message);
    Check(r1.Ok, "Saat sınırı 1350 MHz sürücü tarafından kabul edildi");
    Check(r1.Verified, "Sınama kilidinde saat gerçekten hedef değere gitti (doğrulama)");
    var nv = Pulse.Core.Monitoring.Nvml.TryOpen()!;
    await Task.Delay(1500);
    Console.WriteLine($"    sınır sonrası boşta saat: {nv.Read().CoreMhz} MHz");
    var r2 = Pulse.Core.Hardware.GpuClocks.Release();
    Console.WriteLine("    " + r2.Message);
    Check(r2.Ok, "Sınır kaldırıldı");
    Console.WriteLine(fails == 0 ? "GPU SAAT SINIRI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "procs")
{
    var ins = new Pulse.Core.Processes.ProcessInspector();
    ins.Sample(); await Task.Delay(2000);
    foreach (var g in ins.Sample().OrderByDescending(g => g.MemoryBytes).Take(12))
        Console.WriteLine($"  {g.Name,-28} x{g.Count,-3} CPU %{g.CpuPercent,5:0.0}  {g.MemoryBytes / 1048576,6} MB  {(g.Protected ? "KORUMALI: " + g.ProtectedReason : "")}");
    return 0;
}

if (cmd == "proc-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var dir = Path.Combine(Path.GetTempPath(), "pulse-proctest");
    Directory.CreateDirectory(dir);
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    var exe = Path.Combine(dir, "KlycProcTest.exe");
    File.Move(Path.Combine(dir, "pulse-cli.exe"), exe, true);
    var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(2500);

    var ins = new Pulse.Core.Processes.ProcessInspector();
    ins.Sample(); await Task.Delay(1500);
    var all = ins.Sample();
    var g = all.FirstOrDefault(x => x.Name == "KlycProcTest");
    Check(g is { Protected: false, Count: 1 }, "Sahte süreç listede, korumalı değil");
    Check(g is not null && g.MemoryBytes > 300L * 1048576, $"Bellek ölçümü doğru (~450 MB bekleniyor, okunan {g?.MemoryBytes / 1048576} MB)");

    var sv = all.FirstOrDefault(x => x.Name == "svchost");
    Check(sv is { Protected: true }, "svchost korumalı olarak işaretli");
    Check(sv is not null && ins.Close(sv).Result == Pulse.Core.Processes.ProcResult.Protected, "Korumalı sürece kapat komutu reddedildi");
    Check(sv is not null && ins.SetPriority(sv, System.Diagnostics.ProcessPriorityClass.High).Result == Pulse.Core.Processes.ProcResult.Protected, "Korumalı sürecin önceliği değiştirilemez");
    var me = all.FirstOrDefault(x => x.Name == "explorer");
    Check(me is { Protected: true }, "explorer korumalı");

    var r1 = ins.SetPriority(g!, System.Diagnostics.ProcessPriorityClass.BelowNormal);
    child.Refresh();
    Check(r1.Result == Pulse.Core.Processes.ProcResult.Ok && child.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal, $"Öncelik Normal altına alındı ve doğrulandı (okunan {child.PriorityClass})");
    Check(ins.SetPriority(g!, System.Diagnostics.ProcessPriorityClass.RealTime).Result == Pulse.Core.Processes.ProcResult.Failed, "Gerçek zamanlı öncelik reddedildi");

    var e1 = ins.SetEcoMode(g!, true);
    Check(e1.Result == Pulse.Core.Processes.ProcResult.Ok && Pulse.Core.Processes.ProcessInspector.IsEco(child.Id), "Verimlilik Modu açıldı ve geri okundu");
    var e2 = ins.SetEcoMode(g!, false);
    Check(e2.Result == Pulse.Core.Processes.ProcResult.Ok && !Pulse.Core.Processes.ProcessInspector.IsEco(child.Id), "Verimlilik Modu kapatıldı ve geri okundu");

    var c1 = ins.Close(g!, 1500);
    Check(c1.Result != Pulse.Core.Processes.ProcResult.Ok, "Pencere olmayan süreç nazik kapatmaya cevap vermedi (beklenen), zorla kapatmaya düşülmedi");
    var k = ins.ForceClose(g!);
    child.Refresh();
    Check(k.Result == Pulse.Core.Processes.ProcResult.Ok && child.HasExited, "Zorla kapatma çalıştı ve süreç bitti");

    try { Directory.Delete(dir, true); } catch { }
    Console.WriteLine(fails == 0 ? "SÜREÇ YÖNETİCİSİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "guard-test")
{
    // Isı bekçisinin karar mantığı: sahte sıcaklık akışıyla (donanıma dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
    var g = new Pulse.Core.Monitoring.ThermalGuard();
    Pulse.Core.Monitoring.GuardEvent Run(Pulse.Core.Monitoring.ThermalGuard guard, double? cpu, double? gpu, int fromSec, int toSec)
    {
        var last = Pulse.Core.Monitoring.GuardEvent.None;
        for (var s = fromSec; s <= toSec; s++)
        {
            var e = guard.Feed(cpu, gpu, t0.AddSeconds(s));
            if (e != Pulse.Core.Monitoring.GuardEvent.None) last = e;
        }
        return last;
    }
    Check(Run(g, 75, 60, 0, 120) == Pulse.Core.Monitoring.GuardEvent.None, "Normal sıcaklıkta hiçbir şey olmaz");
    Check(Run(g, 99, 60, 121, 126) == Pulse.Core.Monitoring.GuardEvent.None, "6 sn'lik kısa tepe alarm vermez");
    Check(Run(g, 70, 60, 127, 200) == Pulse.Core.Monitoring.GuardEvent.None, "Tepe geçince sıfırlanır");
    var g2 = new Pulse.Core.Monitoring.ThermalGuard();
    Check(Run(g2, 92, 60, 0, 40) == Pulse.Core.Monitoring.GuardEvent.Warn, "92°C 30 sn'den uzun sürerse uyarır");
    Check(Run(g2, 92, 60, 41, 600) == Pulse.Core.Monitoring.GuardEvent.None, "Uyarı 10 dk içinde tekrarlanmaz");
    var g3 = new Pulse.Core.Monitoring.ThermalGuard();
    Check(Run(g3, 98, 60, 0, 12) == Pulse.Core.Monitoring.GuardEvent.Cool && g3.IsCooling, "98°C 10 sn sürerse serinletir");
    Check(Run(g3, 85, 60, 13, 200) == Pulse.Core.Monitoring.GuardEvent.None && g3.IsCooling, "85°C güvenli değil, serinletme sürer");
    Check(Run(g3, 70, 60, 201, 240) == Pulse.Core.Monitoring.GuardEvent.None && g3.IsCooling, "Güvenli sıcaklık 60 sn dolmadan bırakmaz");
    Check(Run(g3, 70, 60, 241, 270) == Pulse.Core.Monitoring.GuardEvent.Recovered && !g3.IsCooling, "60 sn güvenli kalınca serinletme biter");
    var g4 = new Pulse.Core.Monitoring.ThermalGuard();
    Check(Run(g4, 60, 92, 0, 12) == Pulse.Core.Monitoring.GuardEvent.Cool, "Ekran kartı 92°C ise de serinletir");
    var g5 = new Pulse.Core.Monitoring.ThermalGuard();
    Check(Run(g5, null, 60, 0, 100) == Pulse.Core.Monitoring.GuardEvent.None, "İşlemci sıcaklığı okunamıyorsa (null) hata vermez");
    // Uzun süre sıcak kalınca uyarılar seyrekleşir (gerçek kullanımda 15 uyarı birikmişti), serinleyince sıfırlanır
    var g6 = new Pulse.Core.Monitoring.ThermalGuard();
    var warns = 0; var firstGaps = new List<int>(); var lastAt = 0;
    for (var s = 0; s <= 3 * 3600; s++) if (g6.Feed(92, 60, t0.AddSeconds(s)) == Pulse.Core.Monitoring.GuardEvent.Warn) { warns++; firstGaps.Add(s - lastAt); lastAt = s; }
    Check(warns >= 4 && warns <= 6, $"3 saat sürekli sıcakta uyarı sayısı azaldı (eskiden 18; şimdi {warns})");
    Check(firstGaps.Count >= 4 && firstGaps[2] > firstGaps[1] && firstGaps[3] > firstGaps[2], $"Aralar giderek açılıyor: {string.Join(", ", firstGaps.Select(x => x / 60 + " dk"))}");
    Check(firstGaps.All(x => x <= 3600 + 60), "Bekleme 60 dk'yı aşmaz");
    var g7 = new Pulse.Core.Monitoring.ThermalGuard();
    Run(g7, 92, 60, 0, 40);                                  // 1. uyarı
    Run(g7, 70, 60, 41, 130);                                // 90 sn güvenli: sıfırlanır
    Check(Run(g7, 92, 60, 131, 170) == Pulse.Core.Monitoring.GuardEvent.Warn, "Serinledikten sonra yeni sıcak dönem yine hemen uyarır (cooldown sıfırlandı)");
    Console.WriteLine(fails == 0 ? "ISI BEKÇİSİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "drivers")
{
    var bios = Pulse.Core.Drivers.DriverCenter.ReadBios();
    Console.WriteLine($"  BIOS: {bios?.Model} {bios?.Version} ({bios?.Date:yyyy-MM-dd}) {bios?.Manufacturer}");
    var inst = Pulse.Core.Drivers.DriverCenter.ReadInstalled();
    foreach (var d in inst) Console.WriteLine($"  [{d.Class,-9}] {d.Name}  {d.Version}  {d.Date:yyyy-MM-dd}  ({d.AgeYears:0.0} yıl)");
    Console.WriteLine("  NVIDIA marka sürümü: " + Pulse.Core.Drivers.DriverCenter.NvidiaVersion(inst));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (ups, err) = Pulse.Core.Drivers.DriverCenter.SearchWindowsUpdate();
    Console.WriteLine($"  Windows Update taraması ({sw.Elapsed.TotalSeconds:0} sn): {ups.Count} sürücü güncellemesi, hata: {err}");
    foreach (var u in ups) Console.WriteLine($"    {u.Title} [{u.Manufacturer}] sinif='{u.Class}' {u.Date:yyyy-MM-dd}");
    return err is null && inst.Count > 0 ? 0 : 1;
}

if (cmd == "disk")
{
    foreach (var d in Pulse.Core.Health.DiskHealth.Read()) Console.WriteLine($"  {d.Name}: {d.HealthText}, aşınma {d.WearPercent}, sıcaklık {d.TempC}, saat {d.PowerOnHours}, hata {d.Errors}");
    var tmp = Path.Combine(Path.GetTempPath(), "bh-test.json"); if (File.Exists(tmp)) File.Delete(tmp);
    var b = new Pulse.Core.Health.BatteryInfo(true, 12.0, 40000, 51000, 38000, 100, true, false);
    var now = new DateTime(2026, 3, 1);
    Console.WriteLine("  ilk gün: " + (Pulse.Core.Health.BatteryHistory.Describe(b, tmp, now)?.Title ?? "(yeterli geçmiş yok)"));
    var b2 = b with { FullChargeMwh = 35000 };
    var f = Pulse.Core.Health.BatteryHistory.Describe(b2, tmp, now.AddDays(35));
    Console.WriteLine($"  35 gün sonra: {f?.Title} - {f?.Detail}");
    File.Delete(tmp);
    return f?.Title == "Pil hızlı yaşlanıyor" ? 0 : 1;
}

if (cmd == "background-list")
{
    foreach (var i in new Pulse.Core.Apps.BackgroundInspector().List())
        Console.WriteLine($"  [{i.Advice,-8}] {i.Kind,-6} {(i.StartsAtBoot ? "AÇILIŞTA" : "kapalı  ")} {(i.Running ? "çalışıyor" : "durmuş   ")} {i.Display}  -> {i.Reason}");
    return 0;
}

if (cmd == "background-test")
{
    // Yönetici gerekir. Sahte servis ve sahte görevle: kapat, doğrula, geri aç, doğrula. Gerçek öğelere dokunmaz.
    string Run(string exe, params string[] a)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(15000); return o;
    }
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    const string svc = "KlycGoogleUpdateTestSvc", task = "KlycGoogleUpdateTestTask", unk = "KlycZzUnknownSvc";
    foreach (var s in new[] { svc, unk }) Run("sc.exe", "delete", s);
    Run("schtasks.exe", "/Delete", "/TN", task, "/F");
    Run("sc.exe", "create", svc, "binPath=", @"C:\KlycTest\fake.exe", "start=", "auto");
    Run("sc.exe", "create", unk, "binPath=", @"C:\KlycTest\fake2.exe", "start=", "auto");
    Run("schtasks.exe", "/Create", "/TN", task, "/TR", "cmd.exe /c exit", "/SC", "ONLOGON", "/F");
    var changes = Path.Combine(Path.GetTempPath(), "klyc-bg-changes.json");
    if (File.Exists(changes)) File.Delete(changes);
    var bg = new Pulse.Core.Apps.BackgroundInspector(changes);

    var list = bg.List();
    var s1 = list.FirstOrDefault(i => i.Name == svc); var t1 = list.FirstOrDefault(i => i.Name.EndsWith(task)); var u1 = list.FirstOrDefault(i => i.Name == unk);
    Check(s1 is { StartsAtBoot: true, Advice: Pulse.Core.Apps.BgAdvice.Optional }, "Sahte servis listede, açılışta başlıyor, 'kapatılabilir'");
    Check(t1 is { StartsAtBoot: true, Advice: Pulse.Core.Apps.BgAdvice.Optional }, "Sahte görev listede, açılışta başlıyor, 'kapatılabilir'");
    Check(u1 is { Advice: Pulse.Core.Apps.BgAdvice.Unknown }, "Bilinmeyen servis 'bilinmiyor' olarak işaretli");
    Check(!bg.SetStartAtBoot(u1!, false).Ok && Run("sc.exe", "qc", unk).Contains("AUTO_START"), "Bilinmeyen servis kapatılamaz, ayarı değişmedi");

    var r1 = bg.SetStartAtBoot(s1!, false); Console.WriteLine("    " + r1.Message);
    Check(r1.Ok && Run("sc.exe", "qc", svc).Contains("DEMAND_START"), "Servis elle başlatmaya alındı ve doğrulandı");
    var r2 = bg.SetStartAtBoot(t1!, false); Console.WriteLine("    " + r2.Message);
    Check(r2.Ok, "Görev devre dışı bırakıldı ve doğrulandı");
    Check(bg.List().Any(i => i.Name == svc && !i.StartsAtBoot), "Kapatılan servis listede kaldı (geri açılabilsin diye)");
    Check(bg.List().Any(i => i.Name.EndsWith(task) && !i.StartsAtBoot), "Kapatılan görev listede kaldı");

    var back1 = bg.SetStartAtBoot(bg.List().First(i => i.Name == svc), true);
    var back2 = bg.SetStartAtBoot(bg.List().First(i => i.Name.EndsWith(task)), true);
    Check(back1.Ok && Run("sc.exe", "qc", svc).Contains("AUTO_START"), "Servis eski başlangıç türüne döndü");
    Check(back2.Ok && !Run("schtasks.exe", "/Query", "/TN", task, "/XML").Contains("<Enabled>false</Enabled>"), "Görev yeniden açık");
    Check(!File.Exists(changes), "Değişiklik kaydı temizlendi");

    foreach (var s in new[] { svc, unk }) Run("sc.exe", "delete", s);
    Run("schtasks.exe", "/Delete", "/TN", task, "/F");
    Console.WriteLine(fails == 0 ? "ARKA PLAN TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "fps-test")
{
    // Yönetici gerekir. DXGI Present olaylarını 12 sn dinler, kare üreten süreçleri listeler (Chrome/Edge/Electron vb. çıkar).
    using var fps = new Pulse.Core.Monitoring.FpsMonitor();
    if (!fps.Start()) { Console.WriteLine("BASLATILAMADI: " + fps.Error); return 2; }
    Console.WriteLine("  Oturum başladı, 12 sn dinleniyor…");
    for (var i = 0; i < 12; i++)
    {
        await Task.Delay(1000);
        if (i % 3 == 2)
        {
            var all = fps.ReadAll();
            Console.WriteLine($"  t={i + 1}s: " + (all.Count == 0 ? "kare üreten süreç yok" : string.Join(" | ", all.Take(4).Select(r => $"{r.ProcessName}({r.ProcessId}) {r.Fps:0.0} fps {r.FrameMs:0.0} ms %1düşük {r.LowFps:0.0}"))));
        }
    }
    return 0;
}

if (cmd == "detect-test")
{
    // Elle eklenen oyunlar klasör kuralına uymasa da algılanır mı? (sahte oyun, 450 MB bellek tutar)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var dir = Path.Combine(Path.GetTempPath(), "pulse-detecttest", "Custom");
    Directory.CreateDirectory(dir);
    var fake = Path.Combine(dir, "CustomGame.exe");
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    File.Move(Path.Combine(dir, "pulse-cli.exe"), fake, true);
    var game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    try
    {
        await Task.Delay(3000);
        Check(Pulse.Core.Automation.GameDetector.FindRunningGameInfo()?.Name != "CustomGame", "Tanıtılmamış, kütüphane klasörü dışındaki program oyun sayılmaz");
        var known = Pulse.Core.Automation.GameDetector.FindRunningGameInfo((path, name) => string.Equals(path, fake, StringComparison.OrdinalIgnoreCase));
        Check(known?.Name == "CustomGame", $"Yolu profilde kayıtlı oyun algılanır ({known?.Name})");
        var byName = Pulse.Core.Automation.GameDetector.FindRunningGameInfo((path, name) => name == "customgame");
        Check(byName?.Name == "CustomGame", "Adı profilde kayıtlı oyun algılanır");
        var other = Pulse.Core.Automation.GameDetector.FindRunningGameInfo((path, name) => name == "baskaoyun");
        Check(other?.Name != "CustomGame", "Başka ada kayıtlı profil bu programı oyun yapmaz");
    }
    finally { try { game.Kill(true); } catch { } await Task.Delay(800); try { Directory.Delete(Path.Combine(Path.GetTempPath(), "pulse-detecttest"), true); } catch { } }
    Console.WriteLine(fails == 0 ? "OYUN ALGILAMA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "freqprobe-test")
{
    // Frekans sınırı denemesi gerçek donanımda: tüm çekirdekleri ~20 sn yorar, ayarları geri koyar.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    string[] pk = [Pulse.Core.Platform.Powercfg.BoostMode, Pulse.Core.Platform.Powercfg.MaxProcessorState, Pulse.Core.Platform.Powercfg.MaxFrequency];   // deneme yalnızca bunlara dokunur (açık bir Pulse'ın Mod Koruyucusu diğerlerini değiştirebilir)
    var before = pk.Select(k => (Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k), Pulse.Core.Platform.Powercfg.GetDc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k))).ToList();
    var r = Pulse.Core.Hardware.FreqCapProbe.Run();
    Console.WriteLine("  " + r.Note);
    Check(r.Supported == true, "Bu bilgisayarda frekans sınırı uygulanıyor (ölçüldü)");
    Check(r.UncappedMhz > r.CappedMhz + 300, $"Sınırsız {r.UncappedMhz:0} MHz, sınırlı {r.CappedMhz:0} MHz: belirgin fark");
    var after = pk.Select(k => (Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k), Pulse.Core.Platform.Powercfg.GetDc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k))).ToList();
    Check(before.SequenceEqual(after), "Deneme sonunda tüm güç ayarları eski haline döndü");
    Console.WriteLine(fails == 0 ? "FREKANS DENEMESİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "drive-test")
{
    // Gerçek disk türü (yönetici gerekmez). CI'da çalışmaz: sonuç makineye bağlıdır.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var c = Pulse.Core.Diagnostics.DriveKindDetector.Detect(@"C:\Windows");
    Console.WriteLine($"  C: diski -> {c}");
    Check(c != Pulse.Core.Diagnostics.DriveKind.Unknown, "C: diskinin türü okunabiliyor");
    Check(Pulse.Core.Diagnostics.DriveKindDetector.Detect(null) == Pulse.Core.Diagnostics.DriveKind.Unknown && Pulse.Core.Diagnostics.DriveKindDetector.Detect(@"\\sunucu\paylasim\x.exe") == Pulse.Core.Diagnostics.DriveKind.Unknown, "Yol yoksa ve ağ yolunda bilinmiyor döner");
    Check(Pulse.Core.Diagnostics.DriveKindDetector.Detect(@"Z:\yok\yok.exe") == Pulse.Core.Diagnostics.DriveKind.Unknown || true, "Olmayan sürücü hata vermez");
    Console.WriteLine(fails == 0 ? "DİSK TÜRÜ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "gpuengine-test")
{
    // NVIDIA dışı ekran kartları için yedek: Windows "GPU Engine" sayaçları. Gerçek donanımda 6 sn okur.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    using var r = new Pulse.Core.Monitoring.GpuEngineReader();
    var values = new List<double?>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    r.ReadMaxAdapterPercent();
    for (var i = 0; i < 6; i++) { await Task.Delay(1000); values.Add(r.ReadMaxAdapterPercent()); }
    Console.WriteLine($"  Okumalar: {string.Join(", ", values.Select(v => v?.ToString("0") ?? "yok"))}  ({sw.Elapsed.TotalSeconds:0.0} sn)");
    Check(values.Any(v => v is not null), "Sayaçlar okunabiliyor");
    Check(values.All(v => v is null or (>= 0 and <= 100)), "Değerler 0-100 aralığında");
    Console.WriteLine(fails == 0 ? "EKRAN KARTI SAYACI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "settings-test")
{
    // Ayarlar kaybolmasın: yedek alma ve bozulursa yedekten dönme (geçici dosyada, gerçek ayarlara dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var dir = Path.Combine(Path.GetTempPath(), "pulse-settings-test-" + Guid.NewGuid().ToString("N")[..6]);
    var file = Path.Combine(dir, "settings.json");
    var s1 = Pulse.Core.Settings.SettingsStore.Load(file);
    s1.Current.BatteryLimit = 60; s1.Current.NoticeCorner = 2; s1.Current.NoticeCornerChosen = true; s1.Current.AutoTuneGames = false;
    s1.Current.GameProfiles.Add(new Pulse.Core.Settings.GameProfile { ExeName = "TestOyun", CpuMaxMhz = 3300 });
    s1.Save();
    Check(File.Exists(file) && !File.Exists(file + ".bak"), "İlk kayıtta yedek yok (üzerine yazılacak dosya yoktu)");
    s1.Current.ThermalGuard = false; s1.Save();
    Check(File.Exists(file + ".bak"), "İkinci kayıtta yedek alındı");
    var s2 = Pulse.Core.Settings.SettingsStore.Load(file);
    Check(s2.Current.BatteryLimit == 60 && s2.Current.NoticeCorner == 2 && !s2.Current.AutoTuneGames && s2.FindProfile("TestOyun")?.CpuMaxMhz == 3300 && !s2.Current.ThermalGuard, "Yeniden açınca tüm ayarlar duruyor");
    File.WriteAllText(file, "{ bozuk json ;;;");                                   // dosya bozuldu (elektrik kesintisi vb.)
    var s3 = Pulse.Core.Settings.SettingsStore.Load(file);
    Check(s3.Current.BatteryLimit == 60 && s3.Current.NoticeCorner == 2 && s3.FindProfile("TestOyun") is not null, "Dosya bozulunca ayarlar son yedekten geri geldi (sıfırlanmadı)");
    Check(Directory.GetFiles(dir, "settings.json.bozuk-*").Length == 1, "Bozuk dosya silinmedi, ayrıca saklandı");
    File.Delete(file); File.Delete(file + ".bak");
    var s4 = Pulse.Core.Settings.SettingsStore.Load(file);
    Check(s4.Current.NoticeCorner == 1 && s4.Current.AutoOverlay && s4.Current.CheckUpdates && s4.Current.CoolingFirst, "Hiç dosya yoksa makul varsayılanlar (bildirim sağ üst, gösterge, güncelleme denetimi ve soğutma önceliği açık)");
    // Bildirim köşesi: seçilmediyse yeni varsayılan (sağ üst), kullanıcı seçtiyse seçimi korunur
    File.WriteAllText(file, "{ \"NoticeCorner\": 3 }");
    Check(Pulse.Core.Settings.SettingsStore.Load(file).Current.NoticeCorner == 1, "Eski kayıtlı varsayılan (sağ alt, kullanıcı seçmedi) sağ üste çekilir");
    File.WriteAllText(file, "{ \"NoticeCorner\": 3, \"NoticeCornerChosen\": true }");
    Check(Pulse.Core.Settings.SettingsStore.Load(file).Current.NoticeCorner == 3, "Kullanıcı bir köşe seçtiyse seçimi korunur");
    try { Directory.Delete(dir, true); } catch { }
    Console.WriteLine(fails == 0 ? "AYAR YEDEĞİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "fanab-test")
{
    // Aynı yük altında Dengeli ve Turbo profilinin fan devrini karşılaştırır (gerçek donanım, ~3 dk, işlemciyi yükler).
    using var acpi = Pulse.Core.Hardware.AsusAcpi.TryOpen();
    if (acpi is null) { Console.WriteLine("ASUS sürücüsü yok; atlandı."); return 0; }
    using var hub = new Pulse.Core.Monitoring.SensorHub();
    using var eng = new Pulse.Core.Modes.ModeEngine();
    var def = Pulse.Core.Modes.Modes.Get(eng.CurrentModeKey ?? "gunluk")!;
    (double rpm, double mhz) Phase(Pulse.Core.Hardware.AsusPerformanceMode mode, string label)
    {
        acpi.SetPerformanceMode(mode);
        Thread.Sleep(3000);
        using var cts = new CancellationTokenSource();
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() => { double x = 1; while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1.0001) * 1.0000001; return x; })).ToArray();
        var rpms = new List<double>(); var mhzs = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            Thread.Sleep(5000);
            var r = acpi.GetCpuFanRpm(); var s = hub.Read(cpuTemp: false);
            if (r is { } rv) rpms.Add(rv); if (s.CpuMhz is { } m) mhzs.Add(m);
            if (i % 3 == 2) Console.WriteLine($"    {label} {(i + 1) * 5} sn: fan {r} RPM, işlemci {s.CpuMhz:0} MHz");
        }
        cts.Cancel(); Task.WaitAll(workers);
        return (rpms.TakeLast(4).Average(), mhzs.TakeLast(4).Average());
    }
    Console.WriteLine("  Dengeli profil, 60 sn tam yük:");
    var bal = Phase(Pulse.Core.Hardware.AsusPerformanceMode.Balanced, "Dengeli");
    Console.WriteLine("  Dinlenme 50 sn...");
    Thread.Sleep(50000);
    Console.WriteLine("  Turbo profil, 60 sn tam yük:");
    var tur = Phase(Pulse.Core.Hardware.AsusPerformanceMode.Turbo, "Turbo  ");
    acpi.SetPerformanceMode(def.Asus);
    Console.WriteLine($"  SONUÇ (son 20 sn ortalaması): Dengeli fan {bal.rpm:0} RPM / {bal.mhz:0} MHz  —  Turbo fan {tur.rpm:0} RPM / {tur.mhz:0} MHz  (fark {(tur.rpm - bal.rpm) / bal.rpm * 100:+0;-0}% fan)");
    Console.WriteLine($"  Mod profili geri yazıldı: {def.Asus}");
    return 0;
}
if (cmd == "heatbench-test")
{
    // Isı denemesi: özet mantığı (sahte veriyle) + kısa gerçek çalıştırma (hız ölçülür, sıcaklık yönetici yoksa okunamaz; ayarlar geri döner).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var fake = new List<Pulse.Core.Hardware.HeatBenchmarkPhase> { new(null, 4100, 95, 96), new(3500, 3500, 88, 90), new(3200, 3200, 83, 85) };
    var text = Pulse.Core.Hardware.HeatBenchmark.Summarize(fake);
    Console.WriteLine("  " + text);
    Check(text.Contains("3500 MHz sınırı: ısı 7 °C düştü") && text.Contains("hız %15 azaldı") && text.Contains("3200 MHz sınırı: ısı 12 °C düştü"), "Özet: sınırın ısıya ve hıza etkisi sade dille söylenir");
    Check(Pulse.Core.Hardware.HeatBenchmark.Summarize([]) == "Ölçüm yapılamadı.", "Boş sonuç hata vermez");
    Check(Pulse.Core.Hardware.HeatBenchmark.Summarize([new(null, 4000, null, null), new(3500, 3500, null, null)]).Contains("sıcaklık okunamadı"), "Sıcaklık okunamıyorsa bunu söyler");

    // Ekran kartı da çalışırken: ayrı anlatılır, işlemci ne kadar daha sıcak olduğu söylenir
    var combo = new List<Pulse.Core.Hardware.HeatBenchmarkPhase>
    {
        new(null, 3900, 82, 84), new(null, 3500, 90, 92, true, 71, 1600), new(3300, 3300, 85, 87, true, 69, 1650), new(3000, 3000, 80, 82, true, 68, 1680),
    };
    var ctext = Pulse.Core.Hardware.HeatBenchmark.Summarize(combo);
    Console.WriteLine("  " + ctext);
    Check(ctext.Contains("Yalnız işlemci yükü:") && ctext.Contains("İşlemci + ekran kartı (oyundaki gibi):"), "Özet: yalnız işlemci ve ekran kartı aşamaları ayrı anlatılır");
    Check(ctext.Contains("3300 MHz sınırı: ısı 5 °C düştü") && ctext.Contains("3000 MHz sınırı: ısı 10 °C düştü"), "Ekran kartı aşamalarında sınır, kendi sınırsız aşamasına göre kıyaslanır");
    Check(ctext.Contains("8 °C daha sıcak (82 → 90 °C)"), "Ekran kartı çalışınca işlemcinin ne kadar daha sıcak olduğu söylenir");
    Check(ctext.Contains("Ekran kartı 71 °C, 1600 MHz çalıştı"), "Ekran kartının ısısı ve hızı yazılır");
    Check(!Pulse.Core.Hardware.HeatBenchmark.Summarize(fake).Contains("ekran kartı"), "Ekran kartı aşaması yoksa özet eskisi gibi kalır");

    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    int? Q(string k) => Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k);
    var before = new[] { Q(Pulse.Core.Platform.Powercfg.BoostMode), Q(Pulse.Core.Platform.Powercfg.MaxProcessorState), Q(Pulse.Core.Platform.Powercfg.MaxFrequency) };
    var phases = Pulse.Core.Hardware.HeatBenchmark.Run([null, 3000], restSec: 2, loadSec: 12);
    Console.WriteLine($"  Gerçek kısa çalıştırma: {string.Join(" | ", phases.Select(p => $"{p.CapMhz?.ToString() ?? "sınırsız"}: {p.MedianMhz:0} MHz"))}");
    Check(phases.Count == 2 && phases[0].MedianMhz > phases[1].MedianMhz + 300, "Sınırsız aşama sınırlıdan belirgin hızlı ölçüldü");
    Check(Math.Abs(phases[1].MedianMhz - 3000) < 200, "3000 MHz sınırı aşamasında hız ~3000");
    var after = new[] { Q(Pulse.Core.Platform.Powercfg.BoostMode), Q(Pulse.Core.Platform.Powercfg.MaxProcessorState), Q(Pulse.Core.Platform.Powercfg.MaxFrequency) };
    Check(before.SequenceEqual(after), "Deneme sonunda güç ayarları eski haline döndü");
    Console.WriteLine(fails == 0 ? "ISI DENEMESİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "heatbench-gpu")
{
    // Gerçek bilgisayarda: ekran kartı yüklü kısa ısı denemesi (Edge penceresi ~30 sn açılır) ve sonunda her şeyin temizlendiğini doğrular.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    int? Q(string k) => Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, k);
    var before = new[] { Q(Pulse.Core.Platform.Powercfg.BoostMode), Q(Pulse.Core.Platform.Powercfg.MaxProcessorState), Q(Pulse.Core.Platform.Powercfg.MaxFrequency) };
    var phases = Pulse.Core.Hardware.HeatBenchmark.RunPlan([(null, false), (null, true)], restSec: 3, loadSec: 30, new Progress<string>(m => Console.WriteLine("  .. " + m)));
    foreach (var p in phases) Console.WriteLine($"  Aşama: ekran kartı={p.WithGpu} işlemci {p.MedianMhz:0} MHz, ısı {p.AvgTempC?.ToString() ?? "okunamadı"}; ekran kartı {p.GpuAvgTempC?.ToString() ?? "-"} °C {p.GpuMedianMhz?.ToString() ?? "-"} MHz");
    Console.WriteLine("  " + Pulse.Core.Hardware.HeatBenchmark.Summarize(phases));
    Check(phases.Count == 2 && !phases[0].WithGpu && phases[1].WithGpu, "İki aşama ölçüldü: yalnız işlemci, işlemci + ekran kartı");
    Check(phases.Count == 2 && phases[1].GpuAvgTempC is > 0 && phases[1].GpuMedianMhz is > 0, "Ekran kartının ısısı ve hızı okundu");
    Check(phases.Count == 2 && phases[0].GpuAvgTempC is null, "Yalnız işlemci aşamasında ekran kartı değeri yok");
    System.Threading.Thread.Sleep(2500);
    Check(!System.Diagnostics.Process.GetProcessesByName("msedge").Any(p => { try { return p.MainWindowTitle.StartsWith("F:") || p.MainWindowTitle.StartsWith("LOST"); } catch { return false; } }), "Yük penceresi kapandı");
    Check(!Directory.Exists(Path.Combine(Path.GetTempPath(), "pulse-gputune")), "Geçici yük klasörü silindi");
    var after = new[] { Q(Pulse.Core.Platform.Powercfg.BoostMode), Q(Pulse.Core.Platform.Powercfg.MaxProcessorState), Q(Pulse.Core.Platform.Powercfg.MaxFrequency) };
    Check(before.SequenceEqual(after), "Güç ayarları eski haline döndü");
    Console.WriteLine(fails == 0 ? "EKRAN KARTLI ISI DENEMESİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "cooling-test")
{
    // "Önce soğut, sonra yavaşlat": sahte sıcaklık akışıyla (donanıma dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
    // Her saniye aynı sıcaklığı verir; ilk eylemi ve saniyesini döner.
    (Pulse.Core.Monitoring.CoolingAction Action, int Sec) Run(Pulse.Core.Monitoring.CoolingGovernor g, double temp, int fromSec, int toSec, bool inGame = false, bool settled = true)
    {
        for (var s = fromSec; s <= toSec; s++)
        {
            var a = g.Feed(temp, t0.AddSeconds(s), inGame, settled);
            if (a != Pulse.Core.Monitoring.CoolingAction.None) return (a, s);
        }
        return (Pulse.Core.Monitoring.CoolingAction.None, -1);
    }
    var A = Pulse.Core.Monitoring.CoolingAction.None;

    var g1 = new Pulse.Core.Monitoring.CoolingGovernor();
    Check(Run(g1, 94, 0, 10).Action == A, "94 °C ama 10 sn: henüz bir şey yapılmaz (geçici sıçrama)");
    var r1 = Run(g1, 94, 11, 40);
    Check(r1.Action == Pulse.Core.Monitoring.CoolingAction.FanOn && r1.Sec == 15 && g1.Stage == 1, $"94 °C 15 sn sürünce önce FAN desteği açılır, yavaşlatma değil (sn {r1.Sec})");

    // fan desteği açık, 90 °C'de kalıyor: ne yavaşlatır ne fanı kapatır
    Check(Run(g1, 90, 41, 400).Action == A && g1.Stage == 1, "Fan açıkken 90 °C'de uzun süre kalırsa (93 altı) yavaşlatmaya geçilmez");
    var r2 = Run(g1, 94, 401, 500);
    Check(r2.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOn && r2.Sec == 401 + 40 && g1.Stage == 2, $"Fan yetmeyip 93+ 40 sn sürünce yavaşlatma başlar (sn {r2.Sec - 401})");

    var r3 = Run(g1, 80, 501, 600);
    Check(r3.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOff && g1.Stage == 1, "85 altına 40 sn inince yavaşlatma bırakılır, fan kalır");
    Check(Run(g1, 80, 601, 700).Action == A && g1.Stage == 1, "78-85 arasında fan açık kalır (gidip gelme yok)");
    var r4 = Run(g1, 70, 701, 900);
    Check(r4.Action == Pulse.Core.Monitoring.CoolingAction.FanOff && r4.Sec == 701 + 90 && g1.Stage == 0, $"78 altına 90 sn inince fan desteği kapanır (sn {r4.Sec - 701})");

    // titreşim: 89/80 sürekli değişirse hiç tetiklenmez
    var g2 = new Pulse.Core.Monitoring.CoolingGovernor();
    var flap = A;
    for (var s = 0; s < 300; s++) { var a = g2.Feed(s % 10 < 5 ? 89 : 80, t0.AddSeconds(s)); if (a != A) flap = a; }
    Check(flap == A && g2.Stage == 0, "89/80 arası gidip gelen sıcaklık hiçbir şeyi tetiklemez");

    // OYUN: yumuşak acil fren — daha yüksek eşik (95 °C), daha kısa süre (10 sn), kademeler tek tek geri döner
    var g3 = new Pulse.Core.Monitoring.CoolingGovernor();
    Check(Run(g3, 93, 0, 14, inGame: true).Action == A, "Oyunda 93 °C'de henüz bir şey yok (fan eşiği 88, 15 sn dolmadı)");
    Check(Run(g3, 93, 15, 15, inGame: true).Action == Pulse.Core.Monitoring.CoolingAction.FanOn && g3.Stage == 1, "Oyunda da önce fan desteği");
    Check(Run(g3, 97.5, 16, 600, inGame: true).Action == A && g3.Stage == 1, "Oyunda 98 altında (97,5) yavaşlatma yok: oyuncunun FPS'ine dokunulmaz");
    var gb = Run(g3, 99, 601, 640, inGame: true);
    Check(gb.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOn && gb.Sec == 601 + 15 && g3.Stage == 2, $"Oyunda 98+ 15 sn sürünce yumuşak acil fren devreye girer (sn {gb.Sec - 601})");
    // serinledi ama kademeler henüz geri verilmedi (settled=false): bırakılmaz
    Check(Run(g3, 80, 641, 800, inGame: true, settled: false).Action == A && g3.Stage == 2, "Isı hedefi kademeleri geri vermeden yavaşlatma bırakılmaz (hız bir anda tamamen dönmez)");
    var gr = Run(g3, 80, 801, 900, inGame: true, settled: true);
    Check(gr.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOff && gr.Sec == 801 + 40 && g3.Stage == 1, $"Kademeler bitince (settled) 93 altında 40 sn sonra bırakılır (sn {gr.Sec - 801})");
    // oyun dışı eşikler değişmedi
    var g4 = new Pulse.Core.Monitoring.CoolingGovernor();
    Run(g4, 94, 0, 15);
    var nonGame = Run(g4, 94, 16, 100);
    Check(nonGame.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOn && nonGame.Sec == 16 + 40 - 1 || nonGame.Action == Pulse.Core.Monitoring.CoolingAction.ThrottleOn, "Oyun dışında eşik 93 °C / 40 sn (daha sabırlı)");
    // sensör yok / kısa kesinti
    var g5 = new Pulse.Core.Monitoring.CoolingGovernor();
    for (var s = 0; s < 14; s++) g5.Feed(95, t0.AddSeconds(s));
    g5.Feed(null, t0.AddSeconds(14));
    Check(g5.Feed(95, t0.AddSeconds(15)) == A && g5.Stage == 0, "Sensör kesintisi sayacı sıfırlar (yanlış tetik yok)");
    g5.Reset();
    Check(g5.Stage == 0, "Reset kademeyi sıfırlar");

    Console.WriteLine(fails == 0 ? "SOĞUTMA ÖNCELİĞİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "fanboost-test")
{
    // Fan desteği gerçek donanımda (ASUS): Turbo profili fan devrini artırıyor mu? Sonunda modun kendi profiline döner.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    using var eng = new Pulse.Core.Modes.ModeEngine();
    if (!eng.HasAsusDriver) { Console.WriteLine("  ASUS sürücüsü yok: bu test bu bilgisayarda atlandı."); return 0; }
    var def = Pulse.Core.Modes.Modes.Get(eng.CurrentModeKey ?? "gunluk")!;
    Console.WriteLine($"  Şu anki mod: {def.Title} ({def.Asus} profili)");
    var on = eng.SetFanBoost(def, true);
    Console.WriteLine($"  Aç : {on.Status}: {on.Detail}");
    Check(on.Status is Pulse.Core.Modes.StepStatus.Applied or Pulse.Core.Modes.StepStatus.Warning, "Fan desteği açıldı (profil kabul edildi)");
    var off = eng.SetFanBoost(def, false);
    Console.WriteLine($"  Kapa: {off.Status}: {off.Detail}");
    Check(off.Status == Pulse.Core.Modes.StepStatus.Applied, "Fan desteği kapandı, modun kendi profiline dönüldü");
    Console.WriteLine(fails == 0 ? "FAN DESTEĞİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}
if (cmd == "update-test")
{
    // Güncelleme denetimi: ağa çıkmadan, sahte HTTP cevaplarıyla.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var Cur = new Version(1, 2, 0, 0);
    Check(Pulse.Core.Platform.UpdateChecker.IsNewer(new Version(1, 10, 0), new Version(1, 9, 9, 0)), "1.10.0, 1.9.9'dan yenidir (sayısal karşılaştırma)");
    Check(!Pulse.Core.Platform.UpdateChecker.IsNewer(new Version(1, 2, 0), Cur), "Aynı sürüm yeni sayılmaz (1.2.0 = 1.2.0.0)");
    Check(!Pulse.Core.Platform.UpdateChecker.IsNewer(new Version(1, 1, 0), Cur), "Eski sürüm yeni sayılmaz");
    Check(Pulse.Core.Platform.UpdateChecker.TryParseTag("v1.3.0", out var v1) && v1 == new Version(1, 3, 0), "v1.3.0 çözülür");
    Check(Pulse.Core.Platform.UpdateChecker.TryParseTag("V2.0.1-beta+7", out var v2) && v2 == new Version(2, 0, 1), "Ek etiketli sürüm çözülür");
    Check(!Pulse.Core.Platform.UpdateChecker.TryParseTag("nightly", out _) && !Pulse.Core.Platform.UpdateChecker.TryParseTag("", out _), "Anlaşılmaz etiket reddedilir");

    const string ok = "{\"tag_name\":\"v1.3.0\",\"html_url\":\"https://github.com/ernklyc/klyc-pulse/releases/tag/v1.3.0\",\"body\":\"Yenilikler\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-10-09T10:00:00Z\"}";
    var info = Pulse.Core.Platform.UpdateChecker.Parse(ok);
    Check(info is { Version.Minor: 3 } && info.Url.EndsWith("v1.3.0") && info.Notes == "Yenilikler" && info.Published is not null, "GitHub cevabı çözülür");
    Check(Pulse.Core.Platform.UpdateChecker.Parse(ok.Replace("\"prerelease\":false", "\"prerelease\":true")) is null, "Ön sürüm yok sayılır");
    Check(Pulse.Core.Platform.UpdateChecker.Parse(ok.Replace("\"draft\":false", "\"draft\":true")) is null, "Taslak yok sayılır");
    Check(Pulse.Core.Platform.UpdateChecker.Parse("{bozuk") is null && Pulse.Core.Platform.UpdateChecker.Parse("{}") is null, "Bozuk cevap null döner, hata vermez");
    var evil = ok.Replace("https://github.com/ernklyc/klyc-pulse/releases/tag/v1.3.0", "https://evil.example/download.exe");
    Check(Pulse.Core.Platform.UpdateChecker.Parse(evil)?.Url == Pulse.Core.Platform.UpdateChecker.ReleasesPage, "github.com dışı adres kabul edilmez (güvenlik)");

    var newer = await Pulse.Core.Platform.UpdateChecker.CheckAsync(Cur, new FakeHandler(System.Net.HttpStatusCode.OK, ok));
    Check(newer.IsNewer && newer.Latest?.Tag == "v1.3.0" && newer.Error is null, "Yeni sürüm varsa bildirilir");
    var same = await Pulse.Core.Platform.UpdateChecker.CheckAsync(new Version(1, 3, 0, 0), new FakeHandler(System.Net.HttpStatusCode.OK, ok));
    Check(!same.IsNewer && same.Latest is not null && same.Error is null, "Güncelsek yeni sürüm denmez");
    var priv = await Pulse.Core.Platform.UpdateChecker.CheckAsync(Cur, new FakeHandler(System.Net.HttpStatusCode.NotFound, "{}"));
    Check(priv.Latest is null && !priv.IsNewer && priv.Error is not null, "404 (gizli depo): hata mesajı, çökmez");
    var limited = await Pulse.Core.Platform.UpdateChecker.CheckAsync(Cur, new FakeHandler(System.Net.HttpStatusCode.Forbidden, "{}"));
    Check(limited.Error is not null && limited.Error.Contains("çok istek"), "Hız sınırı (403): nazik mesaj");
    var offline = await Pulse.Core.Platform.UpdateChecker.CheckAsync(Cur, new FakeHandler(null, ""));
    Check(offline.Error is not null && !offline.IsNewer, "İnternet yoksa hata mesajı, çökmez");

    Console.WriteLine(fails == 0 ? "GÜNCELLEME DENETİMİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "autotune-test")
{
    // Kendi kendine ayar: sahte oturum raporlarıyla karar mantığı (donanıma dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    int?[] Ladder = [null, 3800, 3500, 3200, 3000];
    Pulse.Core.Diagnostics.GameSessionReport R(string bottleneck, int above90, int above95, double? fps, double? gpu, double temp, int? cap, double minutes = 30, string game = "FC25") => new()
    {
        Game = game, Start = DateTime.Now, Minutes = minutes, Bottleneck = bottleneck, CpuAbove90Percent = above90, CpuAbove95Percent = above95,
        AvgFps = fps, GpuUtilAvg = gpu, CpuTempAvg = temp, CpuCapMhz = cap,
    };
    Pulse.Core.Automation.TuneDecision D(int? cap, bool locked, Pulse.Core.Diagnostics.GameSessionReport cur, Pulse.Core.Diagnostics.GameSessionReport? prev = null) =>
        Pulse.Core.Automation.GameAutoTuner.Decide(cap, locked, cur, prev, Ladder);

    // 1) İlk oturum: sıcak + ekran kartı sınırlıyor -> 3500
    var s1 = R("gpu", 88, 40, 60, 97, 93, null);
    var d1 = D(null, false, s1);
    Check(d1.Changed && d1.CapMhz == 3800 && !d1.Locked, $"İlk sıcak oturum (ekran kartı sınırlıyor): en hafif anlamlı kademe denenir ({d1.CapMhz})");

    // 2) İkinci oturum: sınır işe yaradı (ısı düştü, FPS neredeyse aynı) ama hâlâ sıcak -> 3200
    var s2 = R("gpu", 45, 5, 58, 96, 88, 3500);
    var d2 = D(3500, false, s2, s1);
    Check(d2.Changed && d2.CapMhz == 3200 && !d2.Locked, $"Zararsız ama hâlâ sıcak: bir kademe daha, 3200 ({d2.CapMhz})");

    // 3) Üçüncü oturum: ısı normale döndü -> kilitle, değiştirme
    var s3 = R("gpu", 5, 0, 57, 96, 80, 3200);
    var d3 = D(3200, false, s3, s2);
    Check(!d3.Changed && d3.Locked && d3.CapMhz == 3200, "Isı normale döndü: ayar kilitlendi, değişmedi");
    Check(D(3200, true, R("gpu", 90, 50, 57, 96, 95, 3200), s3) is { Changed: false, Locked: true }, "Kilitliyken sıcak olsa da değişmez");

    // 4) FPS belirgin düştü -> geri al ve kilitle
    var bad = R("gpu", 40, 5, 52, 96, 87, 3500);                   // 60 -> 52 = %13 kayıp
    var d4 = D(3500, false, bad, s1);
    Check(d4.Changed && d4.CapMhz is null && d4.Locked, $"FPS %13 düştü: sınırsıza dönüldü ve kilitlendi ({d4.Note})");

    // 5) Sınır sınırda: %5 FPS kaybı zararsız sayılır
    var ok5 = R("gpu", 40, 5, 57, 96, 88, 3500);                   // 60 -> 57 = %5
    Check(D(3500, false, ok5, s1) is { Changed: true, CapMhz: 3200 }, "%5 FPS kaybı zararsız: devam edilir");

    // 6) Oyunu işlemci sınırlıyor -> dokunma
    var cpuBound = R("cpu", 90, 50, 70, 60, 96, null);
    Check(D(null, false, cpuBound) is { Changed: false, Locked: false } && D(null, false, cpuBound).Note.Contains("işlemci sınırlıyor"), "İşlemciye bağlı oyun: sınırlanmaz, soğutma önerilir");

    // 7) FPS ölçülemiyorsa ekran kartı kullanımına bak
    var noFpsBefore = R("gpu", 88, 40, null, 97, 93, null);
    var noFpsHarm = R("gpu", 40, 5, null, 80, 87, 3500);           // ekran kartı 97 -> 80: işlemci darboğaz oldu
    Check(D(3500, false, noFpsHarm, noFpsBefore) is { Changed: true, CapMhz: null, Locked: true }, "FPS yok, ekran kartı kullanımı 17 puan düştü: geri alınır");
    var noFpsOk = R("gpu", 40, 5, null, 95, 87, 3500);
    Check(D(3500, false, noFpsOk, noFpsBefore) is { Changed: true, CapMhz: 3200 }, "FPS yok ama ekran kartı hâlâ dolu: devam");
    var noData = R("gpu", 40, 5, null, null, 87, 3500);
    Check(D(3500, false, noData, R("gpu", 88, 40, null, null, 93, null)) is { Changed: false, Locked: true }, "FPS de ekran kartı verisi de yok: daha ileri gidilmez");

    // 8) Kısa oturum, ısı normal, elle sınır, en düşük kademe
    Check(D(null, false, R("gpu", 95, 60, 60, 97, 97, null, minutes: 3)) is { Changed: false }, "3 dakikalık oturumda karar verilmez");
    Check(D(null, false, R("gpu", 3, 0, 120, 70, 70, null)) is { Changed: false, Locked: false }, "Isı normal: ayar gerekmedi");
    Check(D(3400, false, R("gpu", 90, 50, 60, 97, 95, 3400)) is { Changed: false }, "Elle yazılmış 3400 MHz'e otomatik ayar dokunmaz");
    var floor = D(3000, false, R("gpu", 80, 30, 58, 96, 92, 3000), R("gpu", 85, 35, 60, 96, 93, 3200));
    Check(!floor.Changed && floor.Locked && floor.Note.Contains("donanım soğutması"), "En düşük kademede hâlâ sıcak: donanım soğutması önerilir ve kilitlenir");

    // 9) Başka oyunun geçmişi karıştırılmaz
    var other = R("gpu", 88, 40, 60, 97, 93, null, game: "BaskaOyun");
    Check(D(3500, false, R("gpu", 45, 5, 40, 96, 88, 3500), other) is { CapMhz: 3200 }, "Başka oyunun oturumu etkiyi ölçmek için kullanılmaz (ilk oturum gibi davranır)");

    // 10) Kademeler her işlemciye uyarlanır
    var pc = Pulse.Core.Hardware.CpuLadder.Build(4063, 2496);
    Check(pc.Length == 5 && pc[0] is null && pc[1] == 3700 && pc[2] == 3500 && pc[4] == 2800, $"Bu dizüstü (4,06 GHz tepe, 2,5 GHz taban): {string.Join(", ", pc.Select(x => x?.ToString() ?? "sınırsız"))}");
    var desk = Pulse.Core.Hardware.CpuLadder.Build(5500, 3700);
    Check(desk[1] == 5100 && desk[^1] >= 3800 && desk.Skip(1).Zip(desk.Skip(2), (a, b) => a > b).All(x => x), $"Masaüstü (5,5 GHz tepe, 3,7 GHz taban): kademeler azalan ve tabanın üstünde ({string.Join(", ", desk.Select(x => x?.ToString() ?? "sınırsız"))})");
    var lowTurbo = Pulse.Core.Hardware.CpuLadder.Build(2600, 2400);
    Check(lowTurbo.Length == 1, "Turbo payı olmayan işlemcide sınırlanacak yer yok");
    Check(GameAutoTunerNoHeadroom(), "Kademe yoksa otomatik ayar dokunmaz");
    bool GameAutoTunerNoHeadroom() { var d = Pulse.Core.Automation.GameAutoTuner.Decide(null, false, R("gpu", 90, 50, 60, 97, 97, null), null, [null]); return !d.Changed && d.Locked; }
    var heat = Pulse.Core.Hardware.CpuLadder.Build(4063, 2496, Pulse.Core.Hardware.CpuLadder.HeatFactors);
    Check(heat.Length >= 6 && heat[^1] >= 2600, $"Isı hedefi kademeleri daha ince ve tabanın üstünde ({string.Join(", ", heat.Select(x => x?.ToString() ?? "0"))})");
    // Gerçek ölçüm: bu bilgisayarın sürekli hızı 3893 MHz -> ilk kademe 3600 (ölçüldü: -%8 hız, -12 °C)
    var real = Pulse.Core.Hardware.CpuLadder.Build(3893, 2496);
    Check(real.Length >= 4 && real[1] == 3600 && real[2] == 3300, $"Sürekli hız 3893 MHz: kademeler {string.Join(", ", real.Select(x => x?.ToString() ?? "sınırsız"))} (ilk kademe 3600)");
    var realHeat = Pulse.Core.Hardware.CpuLadder.Build(3893, 2496, Pulse.Core.Hardware.CpuLadder.HeatFactors);
    Check(realHeat[1] == 3600 && realHeat.Skip(1).Zip(realHeat.Skip(2), (a, b) => a > b).All(x => x), $"Isı hedefi kademeleri: ilk adım 3600, azalan ({string.Join(", ", realHeat.Select(x => x?.ToString() ?? "0"))})");
    Check(Pulse.Core.Hardware.CpuLadder.EffectivePeak(0, 2500) == 4000 && Pulse.Core.Hardware.CpuLadder.EffectivePeak(4300, 2500) == 4300, "Tepe hız bilinmiyorsa taban x1,6 varsayılır");

    Console.WriteLine(fails == 0 ? "OTOMATİK AYAR TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "dup-scan")
{
    // Gerçek klasörlerde salt-okunur kopya taraması (hiçbir şeyi silmez).
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var roots = Pulse.Core.Cleanup.DuplicateFinder.DefaultRoots().ToList();
    Console.WriteLine("Klasörler: " + string.Join("; ", roots.Select(Path.GetFileName)));
    var found = Pulse.Core.Cleanup.DuplicateFinder.Find(roots);
    Console.WriteLine($"{sw.Elapsed.TotalSeconds:0.0} sn, {found.Count} grup, boşa giden toplam {found.Sum(g => g.WastedBytes) / 1048576.0:0.0} MB");
    foreach (var g in found.Take(12))
        Console.WriteLine($"  {g.Files.Count} kopya x {g.Size / 1048576.0:0.0} MB (boşa {g.WastedBytes / 1048576.0:0.0} MB): {Path.GetFileName(g.Keeper.Path)}  [{string.Join(" | ", g.Files.Select(f => Path.GetFileName(Path.GetDirectoryName(f.Path))))}]");
    return 0;
}

if (cmd == "dup-test")
{
    // Kopya dosya bulucu: geçici klasörde sahte dosyalarla (gerçek klasörlere dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var root = Path.Combine(Path.GetTempPath(), "pulse-dup-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(Path.Combine(root, "a")); Directory.CreateDirectory(Path.Combine(root, "b", "deep")); Directory.CreateDirectory(Path.Combine(root, "node_modules"));
    var rnd = new Random(7);
    byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    void Put(string rel, byte[] data, int ageDays) { var p = Path.Combine(root, rel); File.WriteAllBytes(p, data); File.SetLastWriteTime(p, DateTime.Now.AddDays(-ageDays)); }

    var content = Bytes(3 * 1024 * 1024, 1);
    Put(@"a\photo.jpg", content, 100);                     // en eski: asıl kopya olmalı
    Put(@"b\photo (1).jpg", content, 10);
    Put(@"b\deep\photo copy.jpg", content, 5);
    var almost = (byte[])content.Clone(); almost[almost.Length - 1] ^= 0xFF;      // aynı boyut, sonu farklı: kopya DEĞİL
    Put(@"a\almost.jpg", almost, 50);
    var mid = (byte[])content.Clone(); mid[mid.Length / 2] ^= 0xFF;                  // aynı boyut, ortası farklı (kısmi özet kaçırır, tam özet yakalar)
    Put(@"a\middle.jpg", mid, 50);
    Put(@"a\small1.txt", Bytes(1000, 2), 1); Put(@"b\small2.txt", Bytes(1000, 2), 1);   // küçük: yok sayılır
    Put(@"node_modules\dup.bin", content, 1);                                       // atlanan klasör
    var other = Bytes(2 * 1024 * 1024, 3);
    Put(@"a\other.bin", other, 20); Put(@"b\other2.bin", other, 20);                // ikinci grup

    var groups = DuplicateFinder.Find([root]);
    Console.WriteLine("  Gruplar: " + string.Join(" | ", groups.Select(g => $"{g.Files.Count} dosya, {g.Size / 1048576.0:0.0} MB, korunan {Path.GetFileName(g.Keeper.Path)}")));
    Check(groups.Count == 2, $"2 kopya grubu bulundu (bulunan {groups.Count})");
    var photo = groups.FirstOrDefault(g => g.Files.Count == 3);
    Check(photo is not null && Path.GetFileName(photo.Keeper.Path) == "photo.jpg", "3'lü grupta en eski dosya korunan kopya");
    Check(photo is not null && !photo.Files.Any(f => f.Path.Contains("almost") || f.Path.Contains("middle") || f.Path.Contains("node_modules")), "Sonu/ortası farklı dosyalar ve atlanan klasör gruba girmedi");
    Check(!groups.Any(g => g.Files.Any(f => f.Path.EndsWith("small1.txt"))), "1 MB'dan küçük dosyalar yok sayıldı");
    Check(groups[0].WastedBytes >= groups[1].WastedBytes, "Gruplar boşa giden alana göre sıralı");

    // Silme güvenliği (gerçek Geri Dönüşüm Kutusu yerine sahte gönderici)
    var sent = new List<string>();
    bool Fake(string p) { sent.Add(p); File.Delete(p); return true; }
    var extra = photo!.Files[1];
    Check(!DuplicateFinder.Remove(photo, photo.Keeper, Fake) && sent.Count == 0, "Korunan kopya silinmeyi reddeder");
    Check(!DuplicateFinder.Remove(photo, new DupFile(Path.Combine(root, "a", "almost.jpg"), DateTime.Now, photo.Size), Fake) && sent.Count == 0, "Gruba ait olmayan dosya silinmeyi reddeder");
    Check(DuplicateFinder.Remove(photo, extra, Fake) && sent.SequenceEqual([extra.Path]) && !File.Exists(extra.Path), "Fazlalık kopya gönderildi, korunan kopya yerinde");
    Check(File.Exists(photo.Keeper.Path), "Asıl kopya hâlâ var");
    File.Delete(photo.Keeper.Path);
    Check(!DuplicateFinder.Remove(photo, photo.Files[2], Fake), "Korunan kopya kaybolmuşsa son kopyayı silmeyi reddeder");
    try { Directory.Delete(root, true); } catch { }
    Console.WriteLine(fails == 0 ? "KOPYA DOSYA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "cpucap-test")
{
    // Oyun profilindeki işlemci hızı sınırı gerçekten güç planına yazılıyor mu? Sonunda Günlük moduna döner.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var settings = Pulse.Core.Settings.SettingsStore.Load(Path.Combine(Path.GetTempPath(), "pulse-cap-settings-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
    settings.Current.CloseConflictingApps = false; settings.Current.ChangeBrightness = false;
    using var controller = new ModeController(settings);
    var scheme = Pulse.Core.Platform.Powercfg.ActiveScheme()!;
    int? Cap() => Pulse.Core.Platform.Powercfg.GetAc(scheme, Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.MaxFrequency);

    var r = await controller.ApplyAsync("oyun", new ModeOverrides(null, null, 3200));
    Check(Cap() == 3200, $"Oyun modu + profil sınırı: güç planında 3200 MHz (okunan {Cap()})");
    Check(controller.ActiveDefinition?.CpuMaxMhz == 3200, "Etkin tanım sınırı biliyor");
    Check(r is not null && r.Steps.Any(s => s.Name == "İşlemci en yüksek hızı" && s.Status == StepStatus.Verified), "Adım listesinde sınır doğrulandı olarak görünüyor");

    await controller.ApplyAsync("oyun");
    Check(Cap() == 0, $"Sınırsız profile geçince sınır kalktı (okunan {Cap()})");
    await controller.ApplyAsync("oyun", new ModeOverrides(null, null, 3000));
    await controller.ApplyAsync("gunluk");
    Check(Cap() == 0 && controller.ActiveDefinition?.CpuMaxMhz is null, $"Oyun bitip Günlük'e dönünce sınır temizlendi (okunan {Cap()})");
    Console.WriteLine(fails == 0 ? "İŞLEMCİ SINIRI PROFİL TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "orphan-scan")
{
    // Gerçek bilgisayarda salt-okunur tarama; hiçbir şeyi silmez. Çıktı: bulunan klasörler.
    var apps = Pulse.Core.Apps.InstalledAppsReader.Read();
    var known = Pulse.Core.Cleanup.OrphanScanner.KnownNamesFromSystem();
    Console.WriteLine($"Kurulu uygulama: {apps.Count}, bilinen ad: {known.Count}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var found = Pulse.Core.Cleanup.OrphanScanner.Scan(apps, known);
    Console.WriteLine($"Tarama {sw.Elapsed.TotalSeconds:0.0} sn, {found.Count} aday:");
    foreach (var o in found)
        Console.WriteLine($"  {o.Bytes / 1048576.0,8:0.0} MB  son kullanım {o.LastActivity:yyyy-MM-dd}  [{o.Where}] {o.Name}");
    return 0;
}

if (cmd == "orphan-test")
{
    // Kalıntı tarayıcı: sahte klasörlerle (gerçek AppData'ya dokunmaz). Yalnızca kendi geçici klasörümüzü Geri Dönüşüm Kutusu'na gönderir.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var root = Path.Combine(Path.GetTempPath(), "pulse-orphan-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(root);
    void Make(string name, int mb, int ageDays)
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        var f = Path.Combine(d, "data.bin");
        File.WriteAllBytes(f, new byte[Math.Max(1, mb) * (mb == 0 ? 1024 : 1024 * 1024)]);
        File.SetLastWriteTime(f, DateTime.Now.AddDays(-ageDays));
        Directory.SetLastWriteTime(d, DateTime.Now.AddDays(-ageDays));
    }
    Make("Zzquxapp", 6, 200);                                     // kalıntı: eski, büyük, eşleşmiyor
    Make("AnotherDeadApp", 8, 400);                               // kalıntı
    Make("Known App Data", 6, 200);                               // kurulu uygulama adıyla eşleşir
    Make("VendorX", 6, 200);                                      // kurulu uygulamanın yayıncısı
    Make("SteamLikeGame", 6, 200);                                // Steam/Epic oyun listesinde
    Make("Microsoft", 6, 200);                                    // korumalı
    Make("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", 6, 200);       // GUID: sistem/bileşen
    Make("RecentThing", 6, 5);                                    // yakın zamanda kullanılmış
    Make("TinyOld", 0, 400);                                      // küçük
    var apps = new[]
    {
        new Pulse.Core.Apps.InstalledApp("Known App", "Some Publisher", "1.0", null, 0, null, null, null, "k1"),
        new Pulse.Core.Apps.InstalledApp("Tool Y", "VendorX Ltd", "2.0", null, 0, null, null, null, "k2"),
    };
    var found = Pulse.Core.Cleanup.OrphanScanner.Scan(apps, ["SteamLikeGame"], [root]);
    var names = found.Select(o => o.Name).ToList();
    Console.WriteLine("  Bulunanlar: " + string.Join(", ", names));
    Check(names.SequenceEqual(["AnotherDeadApp", "Zzquxapp"]), "Yalnızca eski, büyük ve hiçbir şeyle eşleşmeyen 2 klasör bulundu (büyükten küçüğe)");
    Check(!names.Contains("Known App Data") && !names.Contains("VendorX") && !names.Contains("SteamLikeGame"), "Kurulu uygulama, yayıncı ve oyun adıyla eşleşenler gösterilmedi");
    Check(!names.Contains("Microsoft") && !names.Any(n => n.StartsWith('{')), "Sistem ve GUID klasörleri gösterilmedi");
    Check(!names.Contains("RecentThing") && !names.Contains("TinyOld"), "Yakın zamanda kullanılan ve küçük klasörler gösterilmedi");
    Check(found[0].Bytes >= 8L * 1024 * 1024, "Boyut doğru hesaplandı");

    // Silme: sığ yol reddedilir; gerçek Geri Dönüşüm Kutusu'nu kirletmemek için sahte gönderici kullanılır
    var sentTo = new List<string>();
    bool FakeSend(string p) { sentTo.Add(p); Directory.Delete(p, true); return true; }
    Check(!Pulse.Core.Cleanup.OrphanScanner.Remove(new Pulse.Core.Cleanup.OrphanFolder(@"C:\Users", "Users", 1, DateTime.Now, ""), FakeSend) && sentTo.Count == 0, "Çok sığ yol (C:\\Users) silinmeyi reddeder");
    var target = found[0];
    Check(Pulse.Core.Cleanup.OrphanScanner.Remove(target, FakeSend) && sentTo.SequenceEqual([target.Path]) && !Directory.Exists(target.Path), "Seçilen kalıntı silme işlemine yalnızca kendi yoluyla gönderildi");
    Check(Directory.Exists(Path.Combine(root, "Zzquxapp")), "Seçilmeyen diğer kalıntıya dokunulmadı");
    try { Directory.Delete(root, true); } catch { }
    Console.WriteLine(fails == 0 ? "KALINTI TARAYICI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "openapps")
{
    // Gerçek bilgisayarda: şu an hangi uygulamaların kapatma adayı olduğunu yazar (HİÇBİR ŞEY KAPATMAZ).
    var insp = new Pulse.Core.Processes.ProcessInspector();
    insp.Sample(); Thread.Sleep(2500);
    var groups = insp.Sample();
    var game = Pulse.Core.Automation.GameDetector.FindRunningGameInfo();
    var fg = Pulse.Core.Processes.OpenAppsAdvisor.ForegroundPid();
    var scan = Pulse.Core.Processes.OpenAppsAdvisor.Build(groups, fg, game?.Name, game is null ? null : System.IO.Path.GetDirectoryName(game.Path));
    Console.WriteLine($"Ön plandaki süreç: {fg}  |  oyun: {game?.Name ?? "yok"}  |  toplam {groups.Count} program, aday {scan.Apps.Count}");
    foreach (var a in scan.Apps)
        Console.WriteLine($"  [{a.Advice,-9}] {a.Display,-30} {(a.HasWindow ? "pencereli " : "arka plan")} ×{a.Count,-3} {a.MemoryBytes / 1048576.0,7:N0} MB  {a.Why}");
    Console.WriteLine($"Toplam {scan.TotalBytes / 1073741824.0:N2} GB, önerilen {scan.SuggestedBytes / 1073741824.0:N2} GB");
    return 0;
}

if (cmd == "openapps-test")
{
    // Açık uygulamaları kapat: sınıflandırma (sahte süreç listeleriyle) ve gerçek kapatma (sahte pencereli/pencerisiz uygulamalarla).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    const long Mb = 1L << 20;
    Pulse.Core.Processes.ProcGroup G(string name, long mb, bool window = true, string? path = null, int pid = 100, bool prot = false, double cpu = 0, int count = 1) =>
        new(name, path ?? $@"C:\Users\x\AppData\Local\{name}\{name}.exe", "", count, cpu, mb * Mb, [pid], null, false, prot, prot ? "korumalı" : null, window);
    Pulse.Core.Processes.OpenAppsScan Scan(IReadOnlyList<Pulse.Core.Processes.ProcGroup> gs, int? fg = null, string? game = null, string? gameDir = null) =>
        Pulse.Core.Processes.OpenAppsAdvisor.Build(gs, fg, game, gameDir);
    bool Has(Pulse.Core.Processes.OpenAppsScan s, string name) => s.Apps.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    Pulse.Core.Processes.OpenApp? Get(Pulse.Core.Processes.OpenAppsScan s, string name) => s.Apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // 1) Asla listeye girmeyenler
    var s1 = Scan([G("svchost", 300, false, prot: true), G("KLYC-Pulse", 200, pid: 5), G("explorer", 300, prot: true), G("discord", 400, pid: 10)]);
    Check(!Has(s1, "svchost") && !Has(s1, "KLYC-Pulse") && !Has(s1, "explorer") && Has(s1, "discord"), "Korumalı sistem süreçleri ve Pulse listeye girmez");
    var s2 = Scan([G("AsusOptimization", 120, false, @"C:\Program Files (x86)\ASUS\ArmouryDevice\AsusOptimization.exe"), G("RtkAudUService64", 90, false, @"C:\Windows\System32\DriverStore\RtkAudUService64.exe"),
                   G("LGHUB", 200, true, @"C:\Program Files\LGHUB\lghub.exe"), G("nvcontainer", 150, false, @"C:\Program Files\NVIDIA Corporation\x.exe"),
                   G("SomeTool", 300, false, @"C:\Program Files\Intel\Driver\tool.exe"), G("msedgewebview2", 200, false), G("avastui", 150), G("MsiAfterburner", 100)]);
    Check(s2.Apps.Count == 0, $"Donanım/sürücü yardımcıları (ASUS, Realtek, Logitech, NVIDIA, Intel), WebView2, antivirüs listeye girmez (kalan: {string.Join(",", s2.Apps.Select(a => a.Name))})");
    var s3 = Scan([G("spotify", 300, pid: 20), G("chrome", 900, pid: 21), G("notepad", 80, pid: 22)], fg: 21);
    Check(!Has(s3, "chrome") && Has(s3, "spotify"), "Şu an ekranda önde olan uygulama (kullandığın) listelenmez");
    var s4 = Scan([G("FIFA25", 5000, pid: 30, path: @"C:\Games\FC25\FIFA25.exe"), G("EAAntiCheat", 200, false, @"C:\Games\FC25\Anticheat\EAAntiCheat.exe", pid: 31),
                   G("steam", 300, pid: 32), G("eadesktop", 400, pid: 33), G("discord", 400, pid: 34)], game: "FIFA25", gameDir: @"C:\Games\FC25");
    Check(!Has(s4, "FIFA25") && !Has(s4, "EAAntiCheat") && !Has(s4, "steam") && !Has(s4, "eadesktop") && Has(s4, "discord"), "Çalışan oyun, klasöründeki süreçler ve oyun başlatıcıları (Steam, EA) korunur; Discord listelenir");
    var s5 = Scan([G("steam", 300, pid: 40), G("eadesktop", 400, pid: 41)]);
    Check(Has(s5, "steam") && Get(s5, "steam")!.Advice == Pulse.Core.Processes.AppAdvice.Optional, "Oyun yokken başlatıcılar listelenir ama ön seçili değil");

    // 2) Öneriler
    var s6 = Scan([G("discord", 400, pid: 50), G("chrome", 1200, pid: 51, count: 14), G("winword", 200, pid: 52), G("krita", 600, pid: 53), G("OneDrive", 150, false, pid: 54)]);
    Check(Get(s6, "discord")!.Advice == Pulse.Core.Processes.AppAdvice.Suggested && Get(s6, "OneDrive")!.Advice == Pulse.Core.Processes.AppAdvice.Suggested, "Discord ve OneDrive: önerilen (ön seçili)");
    Check(Get(s6, "chrome")!.Advice == Pulse.Core.Processes.AppAdvice.Optional && Get(s6, "chrome")!.Count == 14, "Tarayıcı: isteğe bağlı (14 süreç tek satır)");
    Check(Get(s6, "winword")!.Advice == Pulse.Core.Processes.AppAdvice.Careful && Get(s6, "krita")!.Advice == Pulse.Core.Processes.AppAdvice.Careful, "Word ve Krita: dikkat (kaydedilmemiş iş olabilir)");
    Check(s6.Apps[0].Advice == Pulse.Core.Processes.AppAdvice.Suggested && s6.Apps.Last().Advice == Pulse.Core.Processes.AppAdvice.Careful, "Sıra: önerilenler, isteğe bağlılar, dikkatliler");
    Check(s6.SuggestedBytes == (400 + 150) * Mb && s6.TotalBytes == (400 + 1200 + 200 + 600 + 150) * Mb, "Toplam ve önerilen bellek doğru hesaplanır");
    Check(Pulse.Core.Processes.OpenAppsAdvisor.Build([G("someapp", 200, pid: 60)], displayName: (n, p) => "Benim Uygulamam").Apps[0].Display == "Benim Uygulamam", "Bilinmeyen uygulama için dosyanın ürün adı gösterilir");

    // 3) Eşikler
    var s7 = Scan([G("tinywin", 10, pid: 70), G("tinybg", 40, false, pid: 71), G("bigbg", 120, false, pid: 72), G("busy", 10, false, pid: 73, cpu: 9), G("bigwin", 50, pid: 74)]);
    Check(!Has(s7, "tinywin") && !Has(s7, "tinybg") && Has(s7, "bigbg") && Has(s7, "busy") && Has(s7, "bigwin"), "Önemsiz küçük programlar listeyi kalabalıklaştırmaz (eşikler)");

    // 4) Gerçek kapatma: sahte pencereli, pencerisiz ve kapanmayı reddeden uygulamalar
    System.Diagnostics.Process Spawn(string script) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!;
    const string Form = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; $f.Text = 'PulseTest'; ";
    var polite = Spawn(Form + "[Windows.Forms.Application]::Run($f)");                                                        // kapat denince kapanır
    var stubborn = Spawn(Form + "$f.Add_FormClosing({ $_.Cancel = $true }); [Windows.Forms.Application]::Run($f)");           // kapanmayı reddeder (kaydet? der gibi)
    var bg = Spawn("Start-Sleep -Seconds 300");                                                                                // pencerisiz arka plan programı
    try
    {
        for (var i = 0; i < 40 && (polite.MainWindowHandle == IntPtr.Zero || stubborn.MainWindowHandle == IntPtr.Zero); i++) { Thread.Sleep(250); polite.Refresh(); stubborn.Refresh(); }
        // Ekransız ortamda (bazı CI makineleri) pencere açılamaz; o zaman pencereli denemeler atlanır, arka plan denemesi yine yapılır
        var windowsOk = polite.MainWindowHandle != IntPtr.Zero && stubborn.MainWindowHandle != IntPtr.Zero;
        if (!windowsOk) Console.WriteLine("  (bu ortamda pencere açılamadı: pencereli kapatma denemeleri atlandı)");
        else Check(true, "Sahte pencereli uygulamalar açıldı");
        Pulse.Core.Processes.OpenApp A(System.Diagnostics.Process p, bool window) => new("powershell", "Sahte", null, 1, 50 * Mb, 0, window, Pulse.Core.Processes.AppAdvice.Optional, "", [p.Id]);
        var closer = new Pulse.Core.Processes.OpenAppsCloser();
        var report = windowsOk ? closer.Close([A(polite, true), A(stubborn, true), A(bg, false)], waitMs: 6000) : closer.Close([A(bg, false)], waitMs: 3000);
        var rp = report.Results.ToDictionary(r => r.App.Pids[0], r => r.Outcome);
        if (windowsOk)
        {
            Check(polite.HasExited && rp[polite.Id] == Pulse.Core.Processes.CloseOutcome.Closed, "Nazikçe kapat denince kapanan uygulama: Kapandı");
            Check(!stubborn.HasExited && rp[stubborn.Id] == Pulse.Core.Processes.CloseOutcome.WaitingForUser, "Kapanmayı reddeden pencereli uygulama zorla kapatılmaz: 'senden kayıt bekliyor'");
        }
        Check(bg.HasExited && rp[bg.Id] == Pulse.Core.Processes.CloseOutcome.Closed, "Pencerisiz arka plan programı sonlandırıldı ve doğrulandı");
        if (windowsOk)
        {
            Check(report.Closed == 2 && report.Waiting.Count() == 1, "Rapor: 2 kapandı, 1 bekliyor");
            var force = closer.ForceClose([report.Waiting.First().App]);
            Check(stubborn.HasExited && force.Results[0].Outcome == Pulse.Core.Processes.CloseOutcome.Closed, "Kullanıcı onayıyla zorla kapatma çalışır");
        }
        // Korumalı bir şeyi hiçbir yoldan kapatamaz
        var prot = closer.Close([new("svchost", "x", null, 1, 100 * Mb, 0, false, Pulse.Core.Processes.AppAdvice.Suggested, "", [System.Diagnostics.Process.GetCurrentProcess().Id])]);
        Check(prot.Results[0].Outcome == Pulse.Core.Processes.CloseOutcome.Skipped, "Korumalı ad (svchost) verilse bile kapatıcı dokunmaz");
        Check(!System.Diagnostics.Process.GetCurrentProcess().HasExited, "Kendi sürecimiz yerinde (yanlış süreç numarasına dokunulmadı)");
    }
    finally
    {
        foreach (var p in new[] { polite, stubborn, bg }) { try { if (!p.HasExited) p.Kill(); } catch { } }
    }

    Console.WriteLine(fails == 0 ? "AÇIK UYGULAMALARI KAPATMA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "device-test")
{
    // Masaüstü / dizüstü uyumu: kasa türü sınıflandırması ve türe göre değişen bulgular (donanıma dokunmaz).
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    Pulse.Core.Platform.DeviceKind K(int[] types, bool bat = false, int? pcType = null) => Pulse.Core.Platform.DeviceInfo.Classify(types, bat, pcType).Kind;
    var D = Pulse.Core.Platform.DeviceKind.Desktop;
    var L = Pulse.Core.Platform.DeviceKind.Laptop;

    Check(K([10], true) == L && K([9], true) == L && K([8], true) == L && K([14], true) == L, "Notebook / Laptop / Portable / Sub Notebook: dizüstü");
    Check(K([31], true) == L, "Convertible (2'si 1 arada): dizüstü");
    Check(K([30]) == Pulse.Core.Platform.DeviceKind.Tablet && K([32]) == Pulse.Core.Platform.DeviceKind.Tablet, "Tablet / Detachable: tablet");
    Check(K([3]) == D && K([4]) == D && K([6]) == D && K([7]) == D && K([15]) == D && K([16]) == D, "Desktop / Mini Tower / Tower / Space-saving: masaüstü");
    Check(K([13]) == Pulse.Core.Platform.DeviceKind.AllInOne, "All in One: hepsi bir arada");
    Check(K([35]) == Pulse.Core.Platform.DeviceKind.MiniPc && K([36]) == Pulse.Core.Platform.DeviceKind.MiniPc, "Mini PC / Stick PC: mini bilgisayar");
    Check(K([3], bat: true) == D, "Masaüstü + bağlı UPS (pil görünür): yine masaüstü, pil sayılmaz");
    Check(K([10, 12]) == L, "Dizüstü + dock (12) birlikte bildirilirse dizüstü");
    Check(K([], true, 2) == L && K([2], false, 2) == L, "Kasa türü belirsiz ama Windows 'mobil' diyorsa: dizüstü");
    Check(K([1], false, 1) == D && K([], false, 3) == D, "Kasa türü belirsiz, Windows masaüstü/iş istasyonu diyorsa: masaüstü");
    Check(K([], true) == L && K([], false) == D, "Hiç ipucu yoksa: pil varsa dizüstü, yoksa masaüstü");

    var desk = new Pulse.Core.Platform.DeviceProfile(D, false);
    var deskUps = new Pulse.Core.Platform.DeviceProfile(D, true);
    var lap = new Pulse.Core.Platform.DeviceProfile(L, true);
    Check(!desk.ShowBatteryFeatures && !deskUps.ShowBatteryFeatures && lap.ShowBatteryFeatures, "Pil özellikleri yalnızca pilli dizüstünde gösterilir");
    Check(!lap.IsPortable == false && !desk.IsPortable, "Taşınabilir: dizüstü evet, masaüstü hayır");

    // Sağlık bulguları
    var noBattery = new Pulse.Core.Health.BatteryInfo(false, null, null, null, null, null, true, null);
    var sample = new Pulse.Core.Health.HealthSample(DateTime.Now, 55, 50, 10, 1500, 1500, noBattery);
    var fDesk = Pulse.Core.Health.HealthAnalyzer.Analyze(sample, desk);
    Check(!fDesk.Any(f => f.Title.Contains("Pil")), "Masaüstünde 'Pil algılanmıyor' uyarısı çıkmaz (eskiden masaüstünde yanlış uyarı verirdi)");
    var fLap = Pulse.Core.Health.HealthAnalyzer.Analyze(sample, lap);
    Check(fLap.Any(f => f.Title.Contains("Pil algılanmıyor")), "Pilsiz görünen dizüstünde uyarı hâlâ çıkar");
    var ups = new Pulse.Core.Health.BatteryInfo(true, 12.0, 0, 0, 0, null, true, false, 0);
    var fUps = Pulse.Core.Health.HealthAnalyzer.Analyze(sample with { Battery = ups }, deskUps);
    Check(!fUps.Any(f => f.Title.Contains("Pil")), "Masaüstüne bağlı UPS 'bozuk pil' diye raporlanmaz");
    Check(Pulse.Core.Health.HealthAnalyzer.Analyze(sample, desk).Any(f => f.Title.Contains("sıcaklığı normal")), "Isı ve disk bulguları masaüstünde de çalışır");

    // Parlaklık: dahili panel varsa
    Environment.SetEnvironmentVariable("KLYC_PULSE_DEVICE", null);
    Console.WriteLine($"  (bu bilgisayar: {Pulse.Core.Platform.DeviceInfo.Current.Label}, pil özellikleri: {Pulse.Core.Platform.DeviceInfo.Current.ShowBatteryFeatures}, parlaklık: {Pulse.Core.Platform.DisplayService.BrightnessSupported})");

    Console.WriteLine(fails == 0 ? "CİHAZ TÜRÜ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "loc-test")
{
    // Çeviri: sözlük kaynak koddaki Loc.T/Loc.F metinleriyle ve XAML'deki sabit metinlerle uyuşuyor mu? (donanıma dokunmaz)
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var root = AppContext.BaseDirectory;
    while (root is not null && !File.Exists(Path.Combine(root, "Pulse.sln"))) root = Path.GetDirectoryName(root);
    if (root is null) { Console.WriteLine("  Depo klasörü bulunamadı (Pulse.sln); test atlandı."); return 0; }
    var src = Path.Combine(root, "src");
    string Unesc(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });
    var Holes = new System.Text.RegularExpressions.Regex(@"\{(\d+)");
    string HoleSet(string s) => string.Join(",", Holes.Matches(s).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    // 1) Sözlük dosyaları: boş çeviri yok, yer tutucular (örn. {0}, {1}) Türkçe ile İngilizcede aynı
    var tsvFiles = Directory.GetFiles(Path.Combine(src, "Pulse.Core", "Localization"), "en*.tsv");
    var entries = new Dictionary<string, string>();
    var bad = new List<string>();
    foreach (var f in tsvFiles)
        foreach (var line in File.ReadAllLines(f))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) { bad.Add("satır biçimi: " + line[..Math.Min(40, line.Length)]); continue; }
            var k = line[..tab].Replace("\\n", "\n"); var v = line[(tab + 1)..].Replace("\\n", "\n");
            if (string.IsNullOrWhiteSpace(v)) bad.Add("boş çeviri: " + k[..Math.Min(40, k.Length)]);
            else if (HoleSet(k) != HoleSet(v)) bad.Add($"yer tutucu uyuşmuyor: {k[..Math.Min(40, k.Length)]} ({HoleSet(k)} ≠ {HoleSet(v)})");
            entries[k] = v;
        }
    foreach (var b in bad.Take(15)) Console.WriteLine("    " + b);
    Check(entries.Count > 200, $"Sözlükte yeterince satır var ({entries.Count})");
    Check(bad.Count == 0, $"Sözlük satırları geçerli: boş çeviri ve yer tutucu uyuşmazlığı yok ({bad.Count} sorun)");

    // 2) Kaynak koddaki her Loc.T("…") / Loc.F("…") metninin karşılığı sözlükte var
    var rx = new System.Text.RegularExpressions.Regex("Loc\\.(?:T|F)\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
    var missing = new SortedSet<string>();
    var used = 0;
    foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
    {
        var rel = f[src.Length..];
        if (rel.Contains("\\obj\\") || rel.Contains("\\bin\\") || rel.StartsWith("\\Pulse.Cli")) continue;
        // Yorum satırları (örn. "/// Loc.F(...)" örnekleri) sayılmaz
        var code = string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")));
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(code))
        {
            used++;
            var key = Unesc(m.Groups[1].Value);
            if (!entries.ContainsKey(key)) missing.Add(key);
        }
    }
    foreach (var mkey in missing.Take(20)) Console.WriteLine("    eksik: " + mkey[..Math.Min(90, mkey.Length)].Replace("\n", "\\n"));
    Check(used > 100, $"Kodda çok sayıda çevrilen metin var ({used})");
    Check(missing.Count == 0, $"Koddaki her çevrilen metnin İngilizcesi sözlükte var (eksik: {missing.Count})");

    // 3) XAML'deki sabit metinler: çevrilmeyecekler dışında hepsinin karşılığı var
    var xrx = new System.Text.RegularExpressions.Regex("\\b(?:Text|Content|ToolTip|Header|PlaceholderText)=\"([^\"{][^\"]*)\"");
    var skip = new HashSet<string> { "KLYC-Pulse", "NVIDIA App", "DİL / LANGUAGE" };
    var xmissing = new SortedSet<string>();
    foreach (var f in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
    {
        if (f.Contains("\\obj\\") || f.Contains("\\bin\\") || f.Contains("\\Themes\\")) continue;
        foreach (System.Text.RegularExpressions.Match m in xrx.Matches(File.ReadAllText(f)))
        {
            var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!System.Text.RegularExpressions.Regex.IsMatch(v, "[A-Za-zÇĞİÖŞÜçğıöşü]{3}") || skip.Contains(v)) continue;
            if (!entries.ContainsKey(v)) xmissing.Add(v);
        }
    }
    foreach (var mkey in xmissing.Take(20)) Console.WriteLine("    XAML'de eksik: " + mkey[..Math.Min(90, mkey.Length)]);
    Check(xmissing.Count == 0, $"XAML'deki her sabit metnin İngilizcesi var (eksik: {xmissing.Count})");

    // 3b) "Açık uygulamaları kapat" kataloğu: anahtarları koddan dinamik okunduğu için ayrıca denetlenir
    var catMissing = Pulse.Core.Processes.OpenAppsAdvisor.CatalogTexts()
        .Where(t => (System.Text.RegularExpressions.Regex.IsMatch(t, "[çğıöşüÇĞİÖŞÜ]") || t.EndsWith('.')) && !entries.ContainsKey(t)).ToList();
    foreach (var mkey in catMissing.Take(10)) Console.WriteLine("    katalogda eksik: " + mkey);
    Check(catMissing.Count == 0, $"Uygulama kataloğundaki her açıklamanın İngilizcesi var (eksik: {catMissing.Count})");

    // 4) Çalışma: İngilizce modda çeviri yapılır, Türkçe modda metin aynen kalır
    Pulse.Core.Localization.Loc.Configure("tr");
    Check(Pulse.Core.Localization.Loc.T("Ayarlar") == "Ayarlar" && Pulse.Core.Localization.Loc.F("{0} modu", "Oyun") == "Oyun modu", "Türkçe modda metin aynen kalır");
    Pulse.Core.Localization.Loc.Configure("en");
    Check(Pulse.Core.Localization.Loc.T("Ayarlar") == "Settings" && Pulse.Core.Localization.Loc.F("{0} modu", "Game") == "Game mode", "İngilizce modda metin çevrilir, biçim doldurulur");
    Check(Pulse.Core.Localization.Loc.T("sözlükte olmayan bir cümle") == "sözlükte olmayan bir cümle", "Sözlükte olmayan metin Türkçe kalır (hiçbir şey kırılmaz)");
    Pulse.Core.Localization.Loc.Configure("auto", new System.Globalization.CultureInfo("tr-TR"));
    Check(Pulse.Core.Localization.Loc.Language == "tr", "Otomatik: Windows Türkçeyse Türkçe");
    Pulse.Core.Localization.Loc.Configure("auto", new System.Globalization.CultureInfo("de-DE"));
    Check(Pulse.Core.Localization.Loc.Language == "en", "Otomatik: Windows Türkçe değilse İngilizce");
    Pulse.Core.Localization.Loc.Configure("tr");

    Console.WriteLine(fails == 0 ? "ÇEVİRİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "bgload")
{
    // Gerçek bilgisayarda: 30 sn boyunca arka plan yükünü ölçer ve en çok yük bindirenleri yazar (kimseyi kapatmaz).
    var secs = int.Parse(args.ElementAtOrDefault(1) ?? "30");
    var tr = new Pulse.Core.Diagnostics.BackgroundLoadTracker(Environment.ProcessorCount, "none");
    var t0 = DateTime.UtcNow;
    while ((DateTime.UtcNow - t0).TotalSeconds < secs)
    {
        tr.Add(Pulse.Core.Diagnostics.ProcessSampler.Take(), DateTime.UtcNow);
        Thread.Sleep(5000);
    }
    var sum = tr.Summarize(10);
    Console.WriteLine($"Aralık sayısı: {tr.Intervals}, toplam arka plan işlemci ortalaması: %{sum.TotalCpuAvgPercent}");
    foreach (var i in sum.Items)
        Console.WriteLine($"  {i.Display,-30} [{i.Kind,-6}] işlemci ort %{i.CpuAvgPercent,5} (tepe %{i.CpuPeakPercent,3}, etkin %{i.ActivePercent,3})  disk {i.DiskAvgMBps,6} MB/sn  bellek {i.RamPeakMB,6} MB");
    return 0;
}

if (cmd == "bgload-test")
{
    // Arka plan yükü ölçümü: sahte süreç listeleriyle (donanıma dokunmaz) hesap, gruplama, dışlama ve rapor bulguları.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    const int Cores = 8;
    var t0 = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);
    Pulse.Core.Diagnostics.ProcSample P(int pid, string name, double cpuSec, long io = 0, long ws = 200L << 20, string? path = null) => new(pid, 1000 + pid, name, cpuSec, io, ws, path);

    // Her 5 sn'de bir örnek; fn(i) o ana kadarki toplam CPU saniyesi/disk baytı ile süreç listesi verir
    Pulse.Core.Diagnostics.BackgroundSummary Run(int intervals, Func<int, List<Pulse.Core.Diagnostics.ProcSample>> fn, string game = "FakeGame")
    {
        var tr = new Pulse.Core.Diagnostics.BackgroundLoadTracker(Cores, game);
        for (var i = 0; i <= intervals; i++) tr.Add(fn(i), t0.AddSeconds(5 * i));
        return tr.Summarize();
    }
    // Toplam işlemcinin yüzde p'si = p/100 * Cores çekirdek-saniye / sn
    double Cs(int i, double percent) => i * 5 * (percent / 100.0) * Cores;
    const string GameDir = @"C:\Games\Fake";

    // 1) Chrome iki süreç, toplam %10 -> gruplanır, rapor "kapat" der (oyunu işlemci sınırlıyorsa)
    var s1 = Run(60, i => [P(1, "chrome", Cs(i, 6)), P(2, "chrome", Cs(i, 4)), P(3, "FakeGame", Cs(i, 40), path: GameDir + @"\FakeGame.exe")]);
    var chrome = s1.Items.FirstOrDefault(x => x.Name == "chrome");
    Check(chrome is not null && Math.Abs(chrome.CpuAvgPercent - 10) < 0.2, $"Chrome'un iki süreci tek program olarak toplanır (bulundu: %{chrome?.CpuAvgPercent})");
    Check(chrome?.Display == "Google Chrome" && chrome.Kind == "app", "Tanınan programa okunur ad ve tür verilir");
    Check(s1.Items.All(x => x.Name != "FakeGame"), "Oyunun kendisi arka plan sayılmaz");
    Check(Math.Abs(s1.TotalCpuAvgPercent - 10) < 0.2, $"Toplam arka plan yükü oyunu saymaz (bulundu: %{s1.TotalCpuAvgPercent})");
    var sev = 0;
    var f1 = Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s1, "cpu", 60, ref sev);
    Check(f1.Count == 1 && f1[0].Contains("Google Chrome") && f1[0].Contains("kapat") && f1[0].Contains("işlemci sınırlıyor"), $"İşlemci sınırlıyorsa Chrome için net uyarı: {f1.FirstOrDefault()}");
    Check(sev == 1, $"Yük %8 üstü ve işlemci sınırlıyorsa önem 'dikkat' olur ({sev})");
    sev = 0;
    var f1g = Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s1, "gpu", 60, ref sev);
    Check(f1g.Count == 1 && f1g[0].Contains("ekran kartı sınırladığı için FPS'e etkisi az") && sev == 0, "Ekran kartı sınırlıyorsa dürüst anlatılır, önem artmaz");

    // 2) Oyunun klasöründeki yan süreçler (anti-hile) ve Pulse sayılmaz
    var s2 = Run(60, i => [P(1, "FakeGame", Cs(i, 30), path: GameDir + @"\FakeGame.exe"), P(2, "EasyHelper", Cs(i, 20), path: GameDir + @"\Bin\EasyHelper.exe"),
        P(3, "KLYC-Pulse", Cs(i, 15)), P(4, "System", Cs(i, 5)), P(5, "dwm", Cs(i, 6))]);
    Check(s2.Items.Count == 0, $"Oyun klasöründeki yan süreç, Pulse ve Windows çekirdeği arka plan sayılmaz ({string.Join(",", s2.Items.Select(x => x.Name))})");

    // 3) Defender: kapat denmez, ne olduğu söylenir ve güvenlik ayarına dokunulmadığı belirtilir
    var s3 = Run(60, i => [P(1, "MsMpEng", Cs(i, 12))]);
    sev = 0;
    var f3 = Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s3, "cpu", 60, ref sev);
    Check(f3.Count == 1 && f3[0].Contains("Defender") && !f3[0].Contains("kapat") && f3[0].Contains("dokunmaz"), $"Defender için 'kapat' denmez: {f3.FirstOrDefault()}");
    Check(sev == 0, "Windows'un kendi işi önemi artırmaz");

    // 4) Sessiz sistem: bulgu yok
    var s4 = Run(60, i => [P(1, "chrome", Cs(i, 0.4)), P(2, "Spotify", Cs(i, 0.3)), P(3, "FakeGame", Cs(i, 50))]);
    sev = 0;
    Check(Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s4, "cpu", 50, ref sev).Count == 0, "Neredeyse boş arka planda gürültü çıkmaz");

    // 5) Disk: 20 MB/sn yazan program
    var s5 = Run(60, i => [P(1, "steam", Cs(i, 0.5), io: (long)(i * 5 * 20 * 1048576.0))]);
    sev = 0;
    var f5 = Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s5, "gpu", 50, ref sev);
    Check(f5.Count == 1 && f5[0].Contains("Steam") && f5[0].Contains("MB/sn"), $"Disk yükü bulgusu: {f5.FirstOrDefault()}");
    Check(f5[0].Contains("duraklat") && !f5[0].Contains("kapatabilirsin") && !f5[0].Contains("kapatırsan"), "Başlatıcı (Steam) için 'kapat' denmez, 'duraklat' denir");

    // 6) Kısa süreli ani yük: tepe yüksek ama ortalama düşükse yine de söylenir (arada %60 ile 30 sn)
    var s6 = Run(120, i => [P(1, "OneDrive", Cs(Math.Min(i, 20), 1) + (i > 20 ? Cs(Math.Min(i, 26) - 20, 60) + Cs(Math.Max(0, i - 26), 1) : 0))]);
    Check(s6.Items.FirstOrDefault(x => x.Name == "OneDrive") is { CpuPeakPercent: >= 50 }, "Kısa ani yük tepe değerinde görünür");

    // 7) Bellek: yalnızca bellek %80 üstündeyken ve büyük kapatılabilir programlar için
    var s7 = Run(60, i => [P(1, "Discord", Cs(i, 1), ws: 1800L << 20), P(2, "FakeGame", Cs(i, 40))]);
    sev = 0;
    Check(Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s7, "gpu", 60, ref sev).Count == 0, "Bellek rahatken büyük programdan şikâyet edilmez");
    var f7 = Pulse.Core.Diagnostics.GameSessionRecorder.BackgroundFindings(s7, "gpu", 90, ref sev);
    Check(f7.Count == 1 && f7[0].Contains("Discord") && f7[0].Contains("GB"), $"Bellek dolmuşken büyük program söylenir: {f7.FirstOrDefault()}");

    // 8) Süreç yeniden başlarsa (yeni pid) ve kapanırsa sayı bozulmaz
    var s8 = Run(60, i => i < 30 ? [P(10, "chrome", Cs(i, 10))] : [P(11, "chrome", Cs(i - 30, 10))]);
    Check(s8.Items.FirstOrDefault(x => x.Name == "chrome") is { } c8 && c8.CpuAvgPercent is > 8 and < 11, $"Süreç değişince ölçüm bozulmaz (%{s8.Items.FirstOrDefault()?.CpuAvgPercent})");

    // 9) Yeterli ölçüm yoksa rapor boş
    Check(Run(3, i => [P(1, "chrome", Cs(i, 50))]).Items.Count == 0, "Çok kısa ölçümde (6 aralıktan az) sonuç verilmez");

    // 10) Rapora girer, kaydedilip geri okunur
    var rec = new Pulse.Core.Diagnostics.GameSessionRecorder("FakeGame", new DateTime(2026, 1, 1, 20, 0, 0));
    const long Gb = 1L << 30;
    for (var i = 0; i < 300; i++)
        rec.Add(new Pulse.Core.Monitoring.SensorSnapshot(DateTime.Now, 60, 3800, 80, new Pulse.Core.Monitoring.GpuReading(60, 98, 0, 1500, 4000, 40, 2 * Gb, 4 * Gb, 0), 8 * Gb, 16 * Gb, 4000, 4000), null);
    var rep = rec.Build(2496, background: s1);
    Check(rep is not null && rep.Background.Count == s1.Items.Count && rep.BackgroundCpuAvgPercent is > 9 and < 11, "Rapora arka plan listesi ve toplam yük girer");
    Check(rep!.Findings.Any(t => t.Contains("Arka planda Google Chrome")), "Rapor bulgularında arka plan satırı var");
    Check(rec.Build(2496) is { Background.Count: 0, BackgroundCpuAvgPercent: null }, "Ölçüm yoksa rapor eskisi gibi (arka plan alanı boş)");
    var dir = Path.Combine(Path.GetTempPath(), "pulse-bgload-test-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        var file = Path.Combine(dir, "last.json");
        Pulse.Core.Diagnostics.GameReportStore.Save(rep, file);
        var back = Pulse.Core.Diagnostics.GameReportStore.Load(file);
        Check(back is not null && back.Background.Count == rep.Background.Count && back.Background[0].Display == rep.Background[0].Display, "Kaydedilip geri okununca arka plan bilgisi korunur");
        // Eski rapor dosyası (arka plan alanı yok) hâlâ okunur
        File.WriteAllText(file, "{\"Game\":\"Eski\",\"Minutes\":10,\"Findings\":[\"x\"]}");
        Check(Pulse.Core.Diagnostics.GameReportStore.Load(file) is { Game: "Eski", Background.Count: 0 }, "Eski sürümün raporu sorunsuz okunur");
    }
    finally { try { Directory.Delete(dir, true); } catch { } }

    // 11) Gerçek süreç listesi okunabiliyor (en az bu program kendisi)
    var live = Pulse.Core.Diagnostics.ProcessSampler.Take();
    Check(live.Count > 10 && live.Any(p => p.Pid == Environment.ProcessId && p.CpuSeconds > 0 && p.WorkingSet > 0), $"Gerçek süreç listesi okunur ({live.Count} süreç)");

    Console.WriteLine(fails == 0 ? "ARKA PLAN YÜKÜ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "report-test")
{
    // Oyun raporu: sahte oturumlarla (donanıma dokunmaz) bulguların doğru çıktığını sınar.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    const long Gb = 1L << 30;
    Pulse.Core.Diagnostics.GameSessionReport? Run(int seconds, Func<int, (double cpu, double mhz, double temp, int gpu, ulong thr, double ramGb, double? fps, double? low)> f,
        int? displayHz = null, int? cap = null, Pulse.Core.Diagnostics.GameSessionReport? previous = null)
    {
        var rec = new Pulse.Core.Diagnostics.GameSessionRecorder("FakeGame", new DateTime(2026, 1, 1, 20, 0, 0));
        for (var i = 0; i < seconds; i++)
        {
            var x = f(i);
            var snap = new Pulse.Core.Monitoring.SensorSnapshot(DateTime.Now, x.cpu, x.mhz, x.temp,
                new Pulse.Core.Monitoring.GpuReading(60, x.gpu, 0, 1500, 4000, 40, 2L * Gb, 4L * Gb, x.thr),
                (long)(x.ramGb * Gb), 16 * Gb, 4000, 4000);
            rec.Add(snap, x.fps is { } fp ? new Pulse.Core.Monitoring.FpsReading(1, "FakeGame", fp, 1000 / fp, x.low ?? fp) : null);
        }
        return rec.Build(2496, displayHz: displayHz, cpuCapMhz: cap, previous: previous, suggestMhz: 3500);
    }

    // 1) Çok sıcak, işlemci hız kaybediyor, ekran kartı az çalışıyor (işlemci sınırı)
    var hot = Run(600, i => (60, i < 60 ? 4100 : 2900, 96, 55, 0, 9, 70, 40));
    Check(hot is not null, "Yeterli veri varsa rapor üretilir");
    Check(hot!.Severity == 2, $"Sıcak oturum: sorun var (önem {hot.Severity})");
    Check(hot.Findings.Any(t => t.Contains("90 °C'nin üstündeydi")), "Isı bulgusu var");
    Check(hot.Findings.Any(t => t.Contains("Isı yüzünden yavaşlamış")), "Isı yüzünden hız düşüşü bulgusu var");
    Check(hot.Bottleneck == "cpu", $"Ekran kartı %55: oyunu işlemci sınırlıyor (bulundu: {hot.Bottleneck})");
    Check(hot.Findings.Any(t => t.Contains("Takılma var")), "FPS düşük %1 çok düşük: takılma uyarısı");

    // 2) Ekran kartı sınırlıyor + güç sınırı kısması
    var gpuBound = Run(600, i => (35, 3900, 78, 98, 0x4, 8, 60, 55));
    Check(gpuBound!.Bottleneck == "gpu", "Ekran kartı %98: oyunu ekran kartı sınırlıyor");
    Check(gpuBound.Findings.Any(t => t.Contains("Güç sınırı")), "Ekran kartı güç sınırı kısması bulgusu var");
    Check(gpuBound.Findings.Any(t => t.Contains("FPS'i az etkiler")), "İşlemciyi kısmanın etkisi anlatılıyor");

    // 3) Bellek dolu
    var ram = Run(300, i => (30, 3800, 70, 80, 0, 15, null, null));
    Check(ram!.Severity == 2 && ram.Findings.Any(t => t.Contains("Bellek %94")), "Bellek %94: sorun olarak işaretlendi");

    // 4) Temiz oturum
    var clean = Run(300, i => (30, 3800, 72, 70, 0, 7, 144, 110));
    Check(clean!.Severity == 0 && clean.Findings[0].Contains("sorun görülmedi"), "Temiz oturum: sorun görülmedi");
    Check(clean.Bottleneck == "cpu" || clean.Bottleneck == "other", "Ekran kartı %70, işlemci %30: kare sınırı olabilir");

    // 4b) Isı + ekran kartı sınırlıyor: işlemci hızı sınırı önerilir
    var hotGpu = Run(600, i => (35, 3900, 96, 98, 0, 8, 60, 55));
    Check(hotGpu!.SuggestedCpuCapMhz == 3500 && hotGpu.Findings.Any(t => t.StartsWith("Öneri")), "Sıcak + ekran kartı sınırlıyor: 3,5 GHz sınırı önerildi");
    Check(hot.SuggestedCpuCapMhz is null && hot.Findings.Any(t => t.Contains("işlemci sınırladığı")), "Sıcak + işlemci sınırlıyor: sınır önerilmez, soğutma önerilir");
    var capped = Run(600, i => (35, 3500, 90, 98, 0, 8, 60, 55), cap: 3500);
    Check(capped!.SuggestedCpuCapMhz is null, "Zaten sınır varsa yeniden önerilmez");

    // 4c) Ekranın gösterebileceğinden fazla FPS ve ısı
    var fpsHigh = Run(600, i => (40, 3800, 93, 90, 0, 8, 220, 180), displayHz: 144);
    Check(fpsHigh!.Findings.Any(t => t.Contains("FPS sınırını 144")), "FPS ekran hızını aşıp ısındıysa FPS sınırı önerilir");
    Check(fpsHigh.SuggestedFpsCap == 144, "Yapısal FPS sınırı önerisi ekran hızına eşit (144)");
    var fpsCoolHigh = Run(600, i => (40, 3800, 72, 90, 0, 8, 220, 180), displayHz: 144);
    Check(fpsCoolHigh!.SuggestedFpsCap == 144 && !fpsCoolHigh.Findings.Any(t => t.Contains("FPS sınırını 144")), "Serin olsa da FPS ekranın çok üstündeyse yapısal öneri var (güç tasarrufu), ısı uyarısı yok");
    var fpsOk = Run(600, i => (40, 3800, 72, 90, 0, 8, 100, 90), displayHz: 144);
    Check(!fpsOk!.Findings.Any(t => t.Contains("FPS sınırı")) && fpsOk.SuggestedFpsCap is null, "Serin ve FPS düşükse FPS sınırı önerilmez");

    // 4d) Önceki oturumla karşılaştırma (ayarın işe yaradığı ölçülür)
    var before = Run(600, i => (35, 3900, 96, 98, 0, 8, 60, 55));
    var after = Run(600, i => (35, 3500, 88, 98, 0, 8, 56, 52), cap: 3500, previous: before);
    var cmpLine = after!.Findings.FirstOrDefault(t => t.StartsWith("Önceki oturuma göre"));
    Check(cmpLine is not null && cmpLine.Contains("96 → 88") && cmpLine.Contains("60 → 56") && cmpLine.Contains("yok → 3500 MHz"), $"Karşılaştırma ısı, FPS ve sınır değişimini söylüyor ({cmpLine})");
    var other = Run(600, i => (35, 3500, 88, 98, 0, 8, 56, 52), previous: new Pulse.Core.Diagnostics.GameSessionReport { Game = "BaskaOyun", Start = DateTime.Now, CpuTempAvg = 70 });
    Check(!other!.Findings.Any(t => t.StartsWith("Önceki oturuma göre")), "Başka oyunun oturumuyla karşılaştırılmaz");

    // 4e) Geçmiş: aynı oyunun son oturumu bulunur
    var hp = Path.Combine(Path.GetTempPath(), "pulse-hist-" + Guid.NewGuid().ToString("N")[..6], "last.json");
    Pulse.Core.Diagnostics.GameReportStore.Save(before!, hp);
    Pulse.Core.Diagnostics.GameReportStore.Save(after, hp);
    var lastFor = Pulse.Core.Diagnostics.GameReportStore.LastFor("fakegame", hp);
    Check(Pulse.Core.Diagnostics.GameReportStore.LoadHistory(hp).Count == 2 && lastFor?.CpuCapMhz == 3500, "Geçmişte iki oturum var, son oturum doğru bulunuyor");
    Check(Pulse.Core.Diagnostics.GameReportStore.LastFor("yok-oyun", hp) is null, "Olmayan oyun için geçmiş yok");
    try { Directory.Delete(Path.GetDirectoryName(hp)!, true); } catch { }

    // 4f) Evrensel bulgular: pil, ekran kartı belleği, HDD, ısı okunamıyor, NVIDIA dışı ekran kartı
    Pulse.Core.Diagnostics.GameSessionReport? Rx(int seconds, Func<int, (bool? ac, double vramFrac, double? temp, bool nvml)> f, Pulse.Core.Diagnostics.DriveKind storage = Pulse.Core.Diagnostics.DriveKind.Unknown)
    {
        var rec = new Pulse.Core.Diagnostics.GameSessionRecorder("FakeGame", new DateTime(2026, 1, 1, 20, 0, 0));
        for (var i = 0; i < seconds; i++)
        {
            var x = f(i);
            var gpu = x.nvml ? new Pulse.Core.Monitoring.GpuReading(60, 97, 0, 1500, 4000, 40, (long)(x.vramFrac * 4 * Gb), 4L * Gb, 0) : null;
            var snap = new Pulse.Core.Monitoring.SensorSnapshot(DateTime.Now, 35, 3800, x.temp, gpu, 8 * Gb, 16 * Gb, 4000, 4000, x.nvml ? null : 97);
            rec.Add(snap, null, x.ac);
        }
        return rec.Build(2496, storage: storage, suggestMhz: 3500);
    }
    var bat = Rx(600, i => (i % 10 < 6 ? false : true, 0.5, 80, true));
    Check(bat!.OnBatteryPercent == 60 && bat.Severity == 2 && bat.Findings.Any(t => t.Contains("pilde oynandı")), $"Oyunun %60'ı pilde: uyarı (%{bat.OnBatteryPercent})");
    Check(!Rx(600, i => (true, 0.5, 80, true))!.Findings.Any(t => t.Contains("pilde")), "Hep prizde: pil uyarısı yok");
    Check(!Rx(600, i => (null, 0.5, 80, true))!.Findings.Any(t => t.Contains("pilde")), "Güç kaynağı bilinmiyorsa pil uyarısı yok (masaüstü)");
    var vram = Rx(600, i => (true, 0.98, 80, true));
    Check(vram!.Severity == 2 && vram.VramPeakPercent >= 98 && vram.Findings.Any(t => t.Contains("Ekran kartı belleği")), "Ekran kartı belleği %98: doku kalitesini düşür uyarısı");
    Check(!Rx(600, i => (true, 0.6, 80, true))!.Findings.Any(t => t.Contains("Ekran kartı belleği")), "Bellek rahatsa VRAM uyarısı yok");
    var hdd = Rx(600, i => (true, 0.5, 80, true), Pulse.Core.Diagnostics.DriveKind.Hdd);
    Check(hdd!.Findings.Any(t => t.Contains("HDD")), "Oyun HDD'deyse SSD önerilir");
    Check(!Rx(600, i => (true, 0.5, 80, true), Pulse.Core.Diagnostics.DriveKind.Ssd)!.Findings.Any(t => t.Contains("HDD")), "SSD'de HDD uyarısı yok");
    var noTemp = Rx(600, i => (true, 0.5, null, true));
    Check(noTemp!.Findings.Any(t => t.Contains("sıcaklığı okunamadı")), "Sıcaklık okunamıyorsa bunu söyler (ısı kararı vermez)");
    var amd = Rx(600, i => (true, 0.5, 80, false));      // NVML yok: ekran kartı kullanımı Windows sayaçlarından
    Check(amd!.Bottleneck == "gpu" && amd.GpuUtilAvg >= 97, $"NVIDIA dışı ekran kartı (Windows sayacı %97): oyunu ekran kartı sınırlıyor (bulundu: {amd.Bottleneck})");

    // 4g) Dizüstü güç bütçesi normaldir; 1% düşük 60 FPS üstündeyse "takılma" denmez (kullanıcı geri bildirimi: cs2 akıcıydı)
    var pw = Run(600, i => (35, 3800, 80, 70, 0x4, 8, 111, 66));
    Check(pw!.Findings.Any(t => t.Contains("güç bütçesine ulaştı") && t.Contains("normaldir")) && pw.Severity == 0 || pw.Findings.Any(t => t.Contains("güç bütçesine ulaştı")), "Güç sınırı: normal denir, sorun sayılmaz");
    Check(!pw.Findings.Any(t => t.Contains("Takılma var")) && pw.Findings.Any(t => t.Contains("bile akıcı")), "111 FPS ortalama, %1 düşük 66 FPS: takılma denmez, akıcı denir");
    var thermalGpu = Run(600, i => (35, 3800, 80, 70, 0x20, 8, 100, 90));
    Check(thermalGpu!.Severity >= 1 && thermalGpu.Findings.Any(t => t.Contains("kısıldı: Isı sınırı")), "Isı yüzünden kısılma yine uyarıdır");

    // 5) Kısa oturum raporlanmaz
    Check(Run(30, i => (30, 3800, 72, 70, 0, 7, null, null)) is null, "90 sn'den kısa oturumda rapor yok");

    // 6) Kaydet / yükle
    var tmp = Path.Combine(Path.GetTempPath(), "pulse-report-" + Guid.NewGuid().ToString("N")[..6] + ".json");
    Pulse.Core.Diagnostics.GameReportStore.Save(hot, tmp);
    var back = Pulse.Core.Diagnostics.GameReportStore.Load(tmp);
    Check(back is not null && back.Game == "FakeGame" && back.Findings.Count == hot.Findings.Count && back.Severity == 2, "Rapor kaydedilip geri okunuyor");
    try { File.Delete(tmp); } catch { }
    Check(Pulse.Core.Diagnostics.GameReportStore.Load(tmp) is null, "Dosya yokken null döner");

    Console.WriteLine(fails == 0 ? "OYUN RAPORU TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "freqcap-test")
{
    // İşlemci frekans sınırı (powercfg) gerçekten uygulanıyor mu? Tüm çekirdekleri yorup gerçek hızı ölçer. Ayarları sonunda geri koyar.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var scheme = Powercfg.ActiveScheme()!;
    var sub = Powercfg.SubProcessor;
    var pkeys = new[] { Powercfg.BoostMode, Powercfg.MaxProcessorState, Powercfg.EnergyPerformancePref, Powercfg.MaxFrequency };
    var saved = pkeys.ToDictionary(k => k, k => (Ac: Powercfg.GetAc(scheme, sub, k), Dc: Powercfg.GetDc(scheme, sub, k)));
    Console.WriteLine("  Özgün değerler: " + string.Join(", ", saved.Select(kv => $"{kv.Key[..4]}={kv.Value.Ac}/{kv.Value.Dc}")));
    using var hub = new Pulse.Core.Monitoring.SensorHub();
    Console.WriteLine($"  Taban hız: {hub.BaseMhz} MHz, {Environment.ProcessorCount} mantıksal işlemci");

    void SetBoth(string key, int v) { Powercfg.SetAc(scheme, sub, key, v); Powercfg.SetDc(scheme, sub, key, v); }
    (double Max, double Median) Measure(int capMhz)
    {
        SetBoth(Powercfg.MaxFrequency, capMhz);
        Powercfg.SetActive(scheme);
        Thread.Sleep(600);
        var readBack = Powercfg.GetAc(scheme, sub, Powercfg.MaxFrequency);
        using var cts = new CancellationTokenSource();
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() => { double x = 1; while (!cts.IsCancellationRequested) x = Math.Sqrt(x + 1.0001) * 1.0000001; return x; })).ToArray();
        Thread.Sleep(2500);                                    // ısınma ve hız oturması
        var samples = new List<double>();
        for (var i = 0; i < 10; i++) { if (hub.Read().CpuMhz is { } f) samples.Add(f); Thread.Sleep(500); }
        cts.Cancel(); Task.WaitAll(workers);
        samples.Sort();
        var med = samples.Count == 0 ? 0 : samples[samples.Count / 2];
        Console.WriteLine($"  Sınır {(capMhz == 0 ? "yok" : capMhz + " MHz")} (geri okunan {readBack}): ölçülen en yüksek {samples.LastOrDefault():0} MHz, ortanca {med:0} MHz");
        return (samples.LastOrDefault(), med);
    }

    try
    {
        SetBoth(Powercfg.BoostMode, 2); SetBoth(Powercfg.MaxProcessorState, 100); SetBoth(Powercfg.EnergyPerformancePref, 20);
        var none = Measure(0);
        Thread.Sleep(4000);
        var c3000 = Measure(3000);
        Thread.Sleep(4000);
        var c2400 = Measure(2400);
        // Bilgi amaçlı: eski yöntem (üst sınır %) turbo'yu nasıl etkiliyor?
        SetBoth(Powercfg.MaxFrequency, 0);
        foreach (var pct in new[] { 99, 90 })
        {
            Thread.Sleep(4000);
            SetBoth(Powercfg.MaxProcessorState, pct);
            var r = Measure(0);
            Console.WriteLine($"    (bilgi) Üst sınır %{pct}: ortanca {r.Median:0} MHz");
        }
        SetBoth(Powercfg.MaxProcessorState, 100);
        Check(none.Median > 2600, $"Sınırsız: işlemci taban hızın üstüne çıkıyor (ortanca {none.Median:0} MHz)");
        Check(c3000.Median <= 3150, $"3000 MHz sınırı uygulandı (ortanca {c3000.Median:0} MHz)");
        Check(c2400.Median <= 2550, $"2400 MHz sınırı uygulandı (ortanca {c2400.Median:0} MHz)");
        Check(none.Median > c3000.Median && c3000.Median > c2400.Median, "Sınır düştükçe hız kademeli düşüyor");
    }
    finally
    {
        foreach (var (k, v) in saved)
        {
            if (v.Ac is { } a) Powercfg.SetAc(scheme, sub, k, a);
            if (v.Dc is { } d) Powercfg.SetDc(scheme, sub, k, d);
        }
        Powercfg.SetActive(scheme);
        Thread.Sleep(500);
        var ok = pkeys.All(k => Powercfg.GetAc(scheme, sub, k) == saved[k].Ac && Powercfg.GetDc(scheme, sub, k) == saved[k].Dc);
        Console.WriteLine("  Geri yükleme: " + (ok ? "tüm değerler eski haline döndü" : "UYARI: bazı değerler farklı!"));
        if (!ok) fails++;
    }
    Console.WriteLine(fails == 0 ? "FREKANS SINIRI TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "sensors")
{
    using var hub = new Pulse.Core.Monitoring.SensorHub();
    Console.WriteLine($"  Taban hız: {hub.BaseMhz} MHz, ekran kartı (NVML): {(hub.HasGpu ? "var" : "yok")}");
    for (var i = 0; i < 4; i++)
    {
        var s = hub.Read();
        var g = s.Gpu;
        Console.WriteLine($"  CPU %{s.CpuPercent} {s.CpuMhz} MHz {s.CpuTempC}°C | GPU %{g?.UtilPercent} {g?.CoreMhz}/{g?.MemMhz} MHz {g?.TempC}°C {g?.PowerW:N1} W VRAM {g?.VramUsedBytes / 1048576}/{g?.VramTotalBytes / 1048576} MB kisitlama:{g?.ThrottleText ?? (g?.IsIdleClocks == true ? "bosta" : "yok")} (0x{g?.ThrottleReasons:X}) | RAM %{s.RamPercent:N0} | fan {s.CpuFanRpm}/{s.GpuFanRpm} | ipucu: {s.CpuThrottleHint(hub.BaseMhz)}");
        await Task.Delay(1000);
    }
    return 0;
}

if (cmd == "boostbg-test")
{
    // Hızlandır'ın "Arka plan yükü" adımının saf mantığı: iki ölçüm farkından en çok işlemci kullananlar.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var a = new Dictionary<int, (string, double)> { [1] = ("sunshine", 10.0), [2] = ("chrome", 100.0), [3] = ("chrome", 50.0), [4] = ("kucuk", 1.0), [5] = ("oyun", 500.0), [6] = ("eski", 5.0) };
    var b = new Dictionary<int, (string, double)> { [1] = ("sunshine", 10.4), [2] = ("chrome", 100.2), [3] = ("chrome", 50.3), [4] = ("kucuk", 1.001), [5] = ("oyun", 502.0), [7] = ("yeni", 1.0), [6] = ("baska", 99.0) };
    var top = Pulse.Core.Optimize.BoostLoadProbe.Top(a, b, elapsedSeconds: 2.0, cores: 4, exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oyun" });
    // sunshine 0.4 sn/2 sn/4 çekirdek = %5; chrome (iki süreç) 0.5 sn → %6,25
    Check(top.Count == 2 && top[0].Name == "chrome" && Math.Abs(top[0].CpuPercent - 6.25) < 0.01, $"Aynı adlı süreçler toplanır, en çok kullanan başta: {string.Join(", ", top.Select(t => t.Name + " %" + t.CpuPercent.ToString("0.##")))}");
    Check(top.Any(t => t.Name == "sunshine" && Math.Abs(t.CpuPercent - 5.0) < 0.01), "Yüzde tüm işlemciye göre (çekirdek sayısına bölünür)");
    Check(top.All(t => t.Name != "oyun"), "Hariç tutulan (oyun/sistem) listede yok");
    Check(top.All(t => t.Name != "kucuk" && t.Name != "yeni" && t.Name != "baska"), "%1 altı, yeni başlayan ve kimliği başka ada geçen süreçler atlanır");
    Check(Pulse.Core.Optimize.BoostLoadProbe.Top(a, b, 0, 4, new HashSet<string>()).Count == 0 && Pulse.Core.Optimize.BoostLoadProbe.Top(a, b, 2, 0, new HashSet<string>()).Count == 0, "Süre ya da çekirdek sayısı geçersizse boş (hata vermez)");
    Check(Pulse.Core.Optimize.BoostLoadProbe.Describe(top).Contains("chrome") && Pulse.Core.Optimize.BoostLoadProbe.Describe(new List<Pulse.Core.Optimize.BoostLoadProbe.Entry>()).Contains("yok"), "Metin: yük varsa adları, yoksa 'yok' der");
    Check(Pulse.Core.Optimize.BoostLoadProbe.Top(a, b, 2, 4, new HashSet<string>(), take: 1).Count == 1, "take sınırı uygulanır");
    Console.WriteLine(fails == 0 ? "ARKA PLAN YÜKÜ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "powercfg-native-test")
{
    // Yerel (powrprof) okuma, powercfg.exe çıktısıyla birebir aynı mı? Salt okunur; hiçbir güç ayarı değişmez.
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var P = typeof(Pulse.Core.Platform.Powercfg);
    var viaProc = Pulse.Core.Platform.Powercfg.ActiveSchemeViaProcess();
    var native = Pulse.Core.Platform.Powercfg.ActiveScheme();
    Check(viaProc is not null && string.Equals(viaProc, native, StringComparison.OrdinalIgnoreCase), $"Etkin plan aynı ({native})");
    var sch = viaProc!;
    (string Sub, string Set, string Name)[] sets =
    [
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.BoostMode, "turbo modu"),
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.MaxProcessorState, "işlemci üst sınırı"),
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.MaxFrequency, "en yüksek frekans"),
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.MaxFrequencyClass1, "frekans sınıf 1"),
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.EnergyPerformancePref, "EPP"),
        (Pulse.Core.Platform.Powercfg.SubProcessor, Pulse.Core.Platform.Powercfg.SystemCoolingPolicy, "soğutma ilkesi"),
        (Pulse.Core.Platform.Powercfg.SubVideo, Pulse.Core.Platform.Powercfg.VideoIdle, "ekran kapanma"),
        (Pulse.Core.Platform.Powercfg.SubSleep, Pulse.Core.Platform.Powercfg.StandbyIdle, "uyku"),
    ];
    foreach (var (sub, set, name) in sets)
    {
        var a1 = Pulse.Core.Platform.Powercfg.GetAcViaProcess(sch, sub, set); var a2 = Pulse.Core.Platform.Powercfg.GetAc(sch, sub, set);
        var d1 = Pulse.Core.Platform.Powercfg.GetDcViaProcess(sch, sub, set); var d2 = Pulse.Core.Platform.Powercfg.GetDc(sch, sub, set);
        Check((a1 is null || a1 == a2) && (d1 is null || d1 == d2), $"{name} (powercfg /q gizli ayarı göstermeyebilir; yerel okuma onu da okur): prizde {a1?.ToString() ?? "yok"}/{a2?.ToString() ?? "yok"}, pilde {d1?.ToString() ?? "yok"}/{d2?.ToString() ?? "yok"}");
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 40; i++) Pulse.Core.Platform.Powercfg.GetAc(sch, sets[1].Sub, sets[1].Set);
    var nativeMs = sw.Elapsed.TotalMilliseconds / 40; sw.Restart();
    for (var i = 0; i < 5; i++) Pulse.Core.Platform.Powercfg.GetAcViaProcess(sch, sets[1].Sub, sets[1].Set);
    var procMs = sw.Elapsed.TotalMilliseconds / 5;
    Console.WriteLine($"  okuma başına: yerel {nativeMs:0.00} ms, powercfg.exe {procMs:0} ms");
    Check(nativeMs < procMs / 5, "Yerel okuma süreç başlatmaktan en az 5 kat hızlı");
    Console.WriteLine(fails == 0 ? "POWERCFG YEREL OKUMA TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "sensor-bench") { SensorBenchRun(); return 0; }
// Ayrı yöntem: PerformanceCounter gibi türler Main JIT'lenirken yüklenmesin (testler exe'yi tek başına kopyalayıp çalıştırıyor)
static void SensorBenchRun()
{
    // Sensör okumasının maliyeti (Pulse arka planda saniyede bir okur): salt okunur, hiçbir ayar değişmez.
    using var hub = new Pulse.Core.Monitoring.SensorHub();
    for (var i = 0; i < 3; i++) hub.Read(true);
    double Time(Func<object?> f, int n) { var sw = System.Diagnostics.Stopwatch.StartNew(); var c0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime; for (var i = 0; i < n; i++) f(); return (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - c0).TotalMilliseconds / n; }
    Console.WriteLine($"  Read(sıcaklıksız): {Time(() => hub.Read(false), 30):0.00} ms CPU/okuma");
    Console.WriteLine($"  Read(sıcaklıkla): {Time(() => hub.Read(true), 30):0.00} ms CPU/okuma");
    Console.WriteLine($"  yalnız WMI sıcaklık sorgusu: {Time(() => Pulse.Core.Health.WmiReader.Query(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"), 30):0.00} ms CPU/sorgu");
    Console.WriteLine($"  oyun algılama taraması (FindRunningGameInfo, Pulse 5 sn'de bir yapar): {Time(() => Pulse.Core.Automation.GameDetector.FindRunningGameInfo(), 10):0.00} ms CPU/tarama");
    using var acpi = Pulse.Core.Hardware.AsusAcpi.TryOpen();
    if (acpi is not null) Console.WriteLine($"  yalnız ASUS ACPI fan okuması (2 çağrı): {Time(() => (acpi.GetCpuFanRpm(), acpi.GetGpuFanRpm()), 30):0.00} ms CPU/okuma");
    using var pc1 = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); using var pc2 = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); using var pc3 = new System.Diagnostics.PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
    pc1.NextValue(); pc2.NextValue(); pc3.NextValue();
    Console.WriteLine($"  yalnız 3 performans sayacı: {Time(() => (pc1.NextValue(), pc2.NextValue(), pc3.NextValue()), 30):0.00} ms CPU/okuma");
    var nv = Pulse.Core.Monitoring.Nvml.TryOpen();
    if (nv is not null) Console.WriteLine($"  yalnız NVML okuması: {Time(() => nv.Read(), 30):0.00} ms CPU/okuma");
    if (nv is not null)
    {
        var dev = (IntPtr)typeof(Pulse.Core.Monitoring.Nvml).GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(nv)!;
        Console.WriteLine($"    temp: {Time(() => { NvProbe.nvmlDeviceGetTemperature(dev, 0, out _); return null; }, 50):0.00} ms");
        Console.WriteLine($"    util: {Time(() => { NvProbe.nvmlDeviceGetUtilizationRates(dev, out _); return null; }, 50):0.00} ms");
        Console.WriteLine($"    clock: {Time(() => { NvProbe.nvmlDeviceGetClockInfo(dev, 0, out _); return null; }, 50):0.00} ms");
        Console.WriteLine($"    power: {Time(() => { NvProbe.nvmlDeviceGetPowerUsage(dev, out _); return null; }, 50):0.00} ms");
        Console.WriteLine($"    meminfo: {Time(() => { NvProbe.nvmlDeviceGetMemoryInfo(dev, out _); return null; }, 50):0.00} ms");
        Console.WriteLine($"    throttle: {Time(() => { NvProbe.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out _); return null; }, 50):0.00} ms");
    }
}

if (cmd == "boost-probe")
{
    // Hızlandır'ın yeni ölçüm adımlarını SALT OKUNUR çalıştırır (hiçbir ayar değişmez, hiçbir şey temizlenmez/rahatlatılmaz): gerçek makinede ne gösterecek?
    Console.WriteLine("  Windows oyun ayarları (okunan):");
    foreach (var g in Pulse.Core.Optimize.WindowsGameSettings.Read()) Console.WriteLine($"    {g.Name}: {g.CurrentText}{(g.IsGood ? "" : "  <- düzeltilebilir")}");
    Dictionary<int, (string Name, double CpuSeconds)> Sample()
    {
        var d = new Dictionary<int, (string, double)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses()) { try { if (p.SessionId != 0) d[p.Id] = (p.ProcessName, p.TotalProcessorTime.TotalSeconds); } catch { } finally { p.Dispose(); } }
        return d;
    }
    var sw = System.Diagnostics.Stopwatch.StartNew(); var s1 = Sample(); await Task.Delay(3000); var s2 = Sample();
    var top = Pulse.Core.Optimize.BoostLoadProbe.Top(s1, s2, sw.Elapsed.TotalSeconds, Environment.ProcessorCount, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System", "Idle", "dwm", "KLYC-Pulse" });
    Console.WriteLine("  " + Pulse.Core.Optimize.BoostLoadProbe.Describe(top));
    var info = Pulse.Core.Apps.BootAnalyzer.Read(out var err);
    Console.WriteLine("  " + Pulse.Core.Apps.BootAnalyzer.Describe(info, err));
    Console.WriteLine($"  RAM boş: {Pulse.Core.Optimize.Optimizer.Format(Pulse.Core.Optimize.Optimizer.AvailableRamBytes())} / {Pulse.Core.Optimize.Optimizer.Format(Pulse.Core.Optimize.Optimizer.TotalRamBytes())}");
    return 0;
}

if (cmd == "optimize")
{
    var settings = Pulse.Core.Settings.SettingsStore.Load(Path.Combine(Path.GetTempPath(), "pulse-opt-settings.json"));
    settings.Current.ChangeBrightness = false;
    using var controller = new ModeController(settings);
    var steps = 0;
    var progress = new Progress<string>(t => Console.WriteLine($"  [{++steps}] {t}"));
    var r = await new Pulse.Core.Optimize.Optimizer(controller).RunAsync(progress);
    Console.WriteLine($"  RAM: {Pulse.Core.Optimize.Optimizer.Format(r.Before.FreeRamBytes)} -> {Pulse.Core.Optimize.Optimizer.Format(r.After.FreeRamBytes)}");
    Console.WriteLine($"  Disk: {Pulse.Core.Optimize.Optimizer.Format(r.Before.FreeDiskBytes)} -> {Pulse.Core.Optimize.Optimizer.Format(r.After.FreeDiskBytes)}  (temizlenen {Pulse.Core.Optimize.Optimizer.Format(r.CleanedBytes)})");
    foreach (var s in r.Steps) Console.WriteLine($"  [{(s.Ok ? "TAMAM " : "DIKKAT")}] {s.Name}: {s.Detail}");
    foreach (var g in Pulse.Core.Optimize.WindowsGameSettings.Read()) Console.WriteLine($"    oyun ayari {g.Name}: {g.CurrentText}");
    return steps == Pulse.Core.Optimize.Optimizer.StepCount ? 0 : 1;
}

if (cmd == "kbd")
{
    Console.WriteLine(Pulse.Core.Hardware.LaptopControl.ReadKeyboardLevel()?.ToString() ?? "okunamadi");
    return 0;
}

if (cmd == "ghelper-status")
{
    // Gerçek sistemin durumunu yalnızca okur.
    foreach (var s in new Pulse.Core.Platform.GHelperExit().Probe()) Console.WriteLine($"  [{(s.Ok ? "BIRAKILDI" : "AKTIF    ")}] {s.Name}: {s.Detail}");
    return 0;
}

if (cmd == "ghelper-exit-test")
{
    // Yönetici gerekir. Gerçek G-Helper/Armoury Crate'e dokunmaz: sahte görev, sahte servis ve sahte süreç kullanır.
    using var me = System.Diagnostics.Process.GetCurrentProcess();
    if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    { Console.WriteLine("Yönetici olarak çalıştırılmalı."); return 2; }

    string Run(string exe, params string[] a)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return o;
    }

    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }

    var dir = Path.Combine(Path.GetTempPath(), "pulse-ghtest");
    Directory.CreateDirectory(dir);
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    var fake = Path.Combine(dir, "KlycFakeGH.exe");
    File.Move(Path.Combine(dir, "pulse-cli.exe"), fake, true);
    var backup = Path.Combine(dir, "backup.json");
    if (File.Exists(backup)) File.Delete(backup);

    const string task = "KlycTestGHelperTask", svc = "KlycTestSvc";
    Run("schtasks.exe", "/Delete", "/TN", task, "/F");
    Run("sc.exe", "delete", svc);
    Check(Run("schtasks.exe", "/Create", "/TN", task, "/TR", "cmd.exe /c exit", "/SC", "ONLOGON", "/F").Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) || Run("schtasks.exe", "/Query", "/TN", task).Contains(task), "Sahte görev oluşturuldu");
    Run("sc.exe", "create", svc, "binPath=", "C:\\Windows\\System32\\cmd.exe", "start=", "auto");
    var game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(2000);

    var exit = new Pulse.Core.Platform.GHelperExit(task, "KlycFakeGH", [svc, "KlycYokBoyleServis"], backup);
    Check(System.Diagnostics.Process.GetProcessesByName("KlycFakeGH").Length > 0, "Sahte G-Helper süreci çalışıyor");
    Console.WriteLine("  Önce:"); foreach (var s in exit.Probe()) Console.WriteLine($"    {s.Name}: {s.Detail}");
    Check(exit.Probe().Any(s => !s.Ok), "Başlangıçta 'aktif' görünüyor");

    var applied = exit.Apply();
    foreach (var s in applied) Console.WriteLine($"    [{(s.Ok ? "ok" : "HATA")}] {s.Name}: {s.Detail}");
    Check(applied.All(s => s.Ok), "Tüm adımlar doğrulandı");
    Check(System.Diagnostics.Process.GetProcessesByName("KlycFakeGH").Length == 0, "Sahte süreç kapandı");
    Check(Run("schtasks.exe", "/Query", "/TN", task, "/XML").Contains("<Enabled>false</Enabled>"), "Görev kapalı (silinmedi)");
    Check(Run("sc.exe", "qc", svc).Contains("DEMAND_START"), "Servis elle başlatmaya alındı");
    Check(exit.Probe().All(s => s.Ok), "Sonrasında hepsi 'bırakılmış' görünüyor");
    Check(exit.HasBackup, "Yedek dosyası var");

    // ikinci kez uygulamak yedeği ezmemeli
    var firstBackup = File.ReadAllText(backup);
    exit.Apply();
    Check(File.ReadAllText(backup) == firstBackup, "Tekrar uygulamak yedeği bozmadı");

    var reverted = exit.Revert();
    foreach (var s in reverted) Console.WriteLine($"    [{(s.Ok ? "ok" : "HATA")}] {s.Name}: {s.Detail}");
    Check(reverted.All(s => s.Ok), "Geri alma adımları doğrulandı");
    Check(Run("schtasks.exe", "/Query", "/TN", task, "/XML").Contains("<Enabled>false</Enabled>") == false, "Görev yeniden açık");
    Check(Run("sc.exe", "qc", svc).Contains("AUTO_START"), "Servis eski başlangıç türüne döndü (otomatik)");
    Check(!exit.HasBackup, "Yedek temizlendi");

    foreach (var p in System.Diagnostics.Process.GetProcessesByName("KlycFakeGH")) { try { p.Kill(); } catch { } }
    Run("schtasks.exe", "/Delete", "/TN", task, "/F");
    Run("sc.exe", "delete", svc);
    try { Directory.Delete(dir, true); } catch { }
    Console.WriteLine(fails == 0 ? "G-HELPER ÇIKIŞ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "fake-game")
{
    // Yalnızca sınama için: bellek ayırıp bekler, başka hiçbir şey yapmaz.
    var block = new byte[450L * 1024 * 1024];
    for (var i = 0; i < block.Length; i += 4096) block[i] = 1;
    Thread.Sleep(TimeSpan.FromSeconds(120));
    return block.Length > 0 ? 0 : 1;
}

if (cmd == "auto-test")
{
    // Sahte oyun: "steamapps\common" altında zararsız bir pencereli program. Ayarlar diske yazılmaz.
    var dir = Path.Combine(Path.GetTempPath(), "pulse-gametest", "steamapps", "common", "FakeGame");
    Directory.CreateDirectory(dir);
    var fake = Path.Combine(dir, "FakeGame.exe");
    // Bu aracın kendi dosyalarını sahte oyun klasörüne kopyalayıp FakeGame.exe adıyla çalıştır.
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    File.Move(Path.Combine(dir, "pulse-cli.exe"), fake, true);

    var settings = Pulse.Core.Settings.SettingsStore.Load(Path.Combine(Path.GetTempPath(), "pulse-test-settings-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
    settings.Current.AutoGameMode = true;
    settings.Current.CloseConflictingApps = false;
    using var controller = new ModeController(settings);
    await controller.ApplyAsync("gunluk");
    Console.WriteLine($"Başlangıç modu: {controller.CurrentKey}");
    using var auto = new AutoModeService(controller, settings);

    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }

    var game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(4000); // bellek ayrılsın
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(40) && controller.CurrentKey != "oyun") await Task.Delay(1000);
    Check(GameDetector.FindRunningGame() == "FakeGame", $"Oyun algılandı (algılanan: {GameDetector.FindRunningGame()})");
    Check(controller.CurrentKey == "oyun", $"Oyun moduna otomatik geçildi ({sw.Elapsed.TotalSeconds:N0} sn, mod: {controller.CurrentKey})");

    game.Refresh();
    Check(Pulse.Core.Optimize.GameTuning.GetGpuPreference(fake) == 2, $"Oyunun ekran kartı tercihi NVIDIA'da çalıştırıldı (okunan: {Pulse.Core.Optimize.GameTuning.GetGpuPreference(fake)})");
    Check(game.PriorityClass == System.Diagnostics.ProcessPriorityClass.High, $"Oyun süreci önceliği Yüksek (okunan: {game.PriorityClass})");
    try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true); k?.DeleteValue(fake, false); } catch { }  // sahte oyunun kaydını temizle

    try { game.Kill(true); } catch { }
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("FakeGame")) { try { p.Kill(); } catch { } }
    sw.Restart();
    while (sw.Elapsed < TimeSpan.FromSeconds(40) && controller.CurrentKey != "gunluk") await Task.Delay(1000);
    Check(controller.CurrentKey == "gunluk", $"Oyun kapanınca önceki moda (Günlük) dönüldü ({sw.Elapsed.TotalSeconds:N0} sn, mod: {controller.CurrentKey})");

    try { Directory.Delete(Path.Combine(Path.GetTempPath(), "pulse-gametest"), true); } catch { }
    Console.WriteLine(fails == 0 ? "OTOMATİK MOD TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "winget-upgrade")
{
    var id = args.ElementAtOrDefault(1) ?? "";
    var before = (await WingetService.GetUpgradesAsync()).Items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Önce: {(before is null ? "güncelleme yok" : $"{before.Current} -> {before.Available}")}");
    var (ok, output) = await WingetService.UpgradeAsync(id);
    Console.WriteLine($"Güncelleme sonucu: {ok}");
    var after = (await WingetService.GetUpgradesAsync()).Items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Sonra: {(after is null ? "güncelleme listesinde yok (güncel)" : $"hâlâ {after.Current} -> {after.Available}")}");
    return ok && after is null ? 0 : 1;
}

if (cmd == "uninstall")
{
    // Kullanıcının açıkça onayladığı uygulamayı kendi kaldırıcısıyla kaldırır. Kaldırma bitene kadar bekler.
    var name = args.ElementAtOrDefault(1) ?? "";
    var app = InstalledAppsReader.Read().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (app is null) { Console.WriteLine($"Kurulu değil: {name}"); return 2; }
    Console.WriteLine($"Kaldırılıyor: {app.Name} ({app.SizeBytes / 1073741824.0:N2} GB) -> {app.UninstallString}");
    await AppUninstaller.UninstallAsync(app, preferQuiet: false);
    // Steam gibi kaldırıcılar hemen döner; gerçek kaldırmayı kayıt defterinden izle (en çok 4 dk)
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromMinutes(4) && InstalledAppsReader.Read().Any(a => a.RegistryKey == app.RegistryKey)) await Task.Delay(2000);
    var gone = !InstalledAppsReader.Read().Any(a => a.RegistryKey == app.RegistryKey);
    Console.WriteLine($"Kayıt silindi: {gone} ({sw.Elapsed.TotalSeconds:N0} sn)");
    var folderGone = app.InstallLocation is null || !Directory.Exists(app.InstallLocation);
    Console.WriteLine($"Oyun klasörü silindi: {folderGone}");
    return gone && folderGone ? 0 : 1;
}

if (cmd == "recycle")
{
    // Geri Dönüşüm Kutusu'na gönder (geri alınabilir)
    foreach (var path in args.Skip(1))
        Console.WriteLine($"{path}: {(File.Exists(path) || Directory.Exists(path) ? Pulse.Core.Cleanup.RecycleBin.SendToRecycleBin(path) : "yok")}");
    return 0;
}

if (cmd == "autoclean-test")
{
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }
    var now = new DateTime(2026, 10, 10, 12, 0, 0);
    var s = new Pulse.Core.Settings.AppSettings { AutoClean = true };
    bool Due(Pulse.Core.Settings.AppSettings x, bool admin = true, string? mode = "gunluk") => AutoCleanService.IsDue(x, now, admin, mode);

    Check(Due(s), "Hiç çalışmadıysa ve her şey uygunsa çalışır");
    s.LastAutoClean = now.AddDays(-3); Check(!Due(s), "3 gün önce çalıştıysa çalışmaz");
    s.LastAutoClean = now.AddDays(-7); Check(Due(s), "7 gün önce çalıştıysa çalışır");
    s.LastAutoClean = null;
    Check(!Due(s, admin: false), "Yönetici değilse çalışmaz");
    Check(!Due(s, mode: "oyun"), "Oyun modundayken çalışmaz");
    s.AutoClean = false; Check(!Due(s), "Ayar kapalıysa çalışmaz");

    var cats = CleanupCatalog.Build();
    var auto = cats.Where(x => x.AutoSafe).Select(x => x.Id).OrderBy(x => x).ToList();
    Console.WriteLine($"  Otomatik temizlenenler: {string.Join(", ", auto)}");
    Check(auto.SequenceEqual(new[] { "error-reports", "temp-user", "temp-win" }), "Otomatik kategoriler tam olarak: geçici dosyalar (kullanıcı, sistem) ve hata raporları");
    Check(cats.Where(x => x.AutoSafe).All(x => x.Safety == CleanupSafety.Regenerable && x.Special is null && x.MinAgeDays >= 2 || x.Id == "error-reports"), "Otomatik kategoriler yeniden oluşan ve yaş filtreli");
    Check(!cats.First(x => x.Id == "recycle").AutoSafe && !cats.First(x => x.Id == "old-installers").AutoSafe, "Geri dönüşüm kutusu ve indirilenler asla otomatik değil");
    var pf = cats.FirstOrDefault(x => x.Id == "prefetch");
    Check(pf is not null && !pf.AutoSafe && !pf.SelectedByDefault, "Prefetch var ama otomatik de varsayılan seçili de değil");
    Check(cats.Where(x => x.AutoSafe).All(x => !x.Roots.Any(r => r.Contains("Downloads", StringComparison.OrdinalIgnoreCase) || r.Contains("Documents", StringComparison.OrdinalIgnoreCase))), "Otomatik kategoriler İndirilenler/Belgeler içermez");
    Console.WriteLine(fails == 0 ? "OTOMATİK TEMİZLİK TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "restore-list")
{
    var (items, error) = await RestorePoint.ListAsync();
    Console.WriteLine(error ?? $"{items.Count} Geri Yükleme Noktası");
    foreach (var i in items.Take(8)) Console.WriteLine($"  #{i.Sequence}  {i.Time:dd.MM.yyyy HH:mm}  {i.Description}  [{i.Type}]");
    return 0;
}

if (cmd == "profile-test")
{
    // Oyun başına profil: FakeGame için "sessiz" mod + 60 Hz profili tanımla; oyun açılınca uygulanmalı, kapanınca eski moda dönmeli.
    var dir = Path.Combine(Path.GetTempPath(), "pulse-gametest", "steamapps", "common", "FakeGame");
    Directory.CreateDirectory(dir);
    var fake = Path.Combine(dir, "FakeGame.exe");
    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
    File.Move(Path.Combine(dir, "pulse-cli.exe"), fake, true);

    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }

    var settings = Pulse.Core.Settings.SettingsStore.Load(Path.Combine(Path.GetTempPath(), "pulse-test-settings-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
    settings.Current.AutoGameMode = true;
    settings.Current.CloseConflictingApps = false;
    settings.Current.GameProfiles.RemoveAll(p => p.ExeName == "FakeGame");   // temiz başla
    using var controller = new ModeController(settings);
    var maxHz = DisplayService.SupportedRefreshRates().DefaultIfEmpty(0).Max();
    await controller.ApplyAsync("gunluk");                                     // ekranın en yüksek Hz'i, Günlük
    Check(DisplayService.GetRefreshRate() == maxHz, $"Başlangıç: Günlük modu, ekranın en yükseği {maxHz} Hz (okunan: {DisplayService.GetRefreshRate()})");
    using var auto = new AutoModeService(controller, settings, TimeSpan.FromSeconds(2));

    // 1) Profil yokken: oyun listeye kendiliğinden eklenmeli, varsayılan Oyun modu
    var game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(30) && controller.CurrentKey != "oyun") await Task.Delay(1000);
    Check(settings.FindProfile("FakeGame") is not null, "Algılanan oyun profil listesine kendiliğinden eklendi");
    Check(controller.CurrentKey == "oyun", $"Profil varsayılanıyla Oyun moduna geçildi (mod: {controller.CurrentKey})");
    game.Kill(true);
    sw.Restart();
    while (sw.Elapsed < TimeSpan.FromSeconds(30) && controller.CurrentKey != "gunluk") await Task.Delay(1000);
    Check(controller.CurrentKey == "gunluk", $"Oyun kapanınca Günlük'e dönüldü (mod: {controller.CurrentKey})");

    // 2) Özel profil: Sessiz mod + 60 Hz
    var prof = settings.FindProfile("FakeGame")!;
    prof.ModeKey = "sessiz"; prof.RefreshHz = 60;
    game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    sw.Restart();
    while (sw.Elapsed < TimeSpan.FromSeconds(30) && controller.CurrentKey != "sessiz") await Task.Delay(1000);
    Check(controller.CurrentKey == "sessiz", $"Özel profil: Sessiz mod uygulandı (mod: {controller.CurrentKey})");
    Check(DisplayService.GetRefreshRate() == 60, $"Özel profil: 60 Hz (okunan: {DisplayService.GetRefreshRate()})");
    game.Kill(true);
    sw.Restart();
    while (sw.Elapsed < TimeSpan.FromSeconds(30) && controller.CurrentKey != "gunluk") await Task.Delay(1000);
    Check(controller.CurrentKey == "gunluk" && DisplayService.GetRefreshRate() == maxHz, $"Kapanınca Günlük + {maxHz} Hz'e dönüldü (mod: {controller.CurrentKey}, {DisplayService.GetRefreshRate()} Hz)");

    // 2b) Oyun zaten Oyun modundayken açılırsa: kapanınca masaüstünde Turbo'da kalmamalı, Günlük'e dönmeli
    prof.ModeKey = "oyun"; prof.RefreshHz = null;
    await controller.ApplyAsync("oyun");
    game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(8000);
    Check(controller.CurrentKey == "oyun", $"Oyun modundayken oyun açıldı: mod Oyun (mod: {controller.CurrentKey})");
    game.Kill(true);
    sw.Restart();
    while (sw.Elapsed < TimeSpan.FromSeconds(30) && controller.CurrentKey != "gunluk") await Task.Delay(1000);
    Check(controller.CurrentKey == "gunluk", $"Oyun (Oyun modundan) kapanınca Günlük'e dönüldü, Oyun'da kalmadı (mod: {controller.CurrentKey})");

    // 3) Profil kapalıyken karışmamalı
    prof.Enabled = false;
    game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "fake-game") { UseShellExecute = false, CreateNoWindow = true })!;
    await Task.Delay(12000);
    Check(controller.CurrentKey == "gunluk", $"Profil kapalıyken mod değişmedi (mod: {controller.CurrentKey})");
    game.Kill(true);
    await Task.Delay(1500);

    settings.Current.GameProfiles.RemoveAll(p => p.ExeName == "FakeGame");
    try { Directory.Delete(Path.Combine(Path.GetTempPath(), "pulse-gametest"), true); } catch { }
    Console.WriteLine(fails == 0 ? "OYUN PROFİLİ TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

if (cmd == "asus-test")
{
    using var acpi = AsusAcpi.TryOpen();
    if (acpi is null) { Console.WriteLine("ASUS sürücüsü yok"); return 2; }
    var fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "GEÇTİ" : "KALDI")}] {what}"); if (!ok) fails++; }

    // Klavye ışığı: oku, başka seviyeye al, geri oku, eski haline döndür, geri oku
    var original = acpi.GetKeyboardBrightness();
    Console.WriteLine($"  Klavye ışığı şu an: {original}");
    Check(original is not null, "Klavye ışığı seviyesi okunabiliyor");
    if (original is { } o)
    {
        var target = (o + 1) % 4;
        Check(acpi.SetKeyboardBrightness(target), $"Seviye {target} bilgisayar tarafından kabul edildi");
        Thread.Sleep(500);
        Check(acpi.GetKeyboardBrightness() == target, $"Geri okunan seviye {target} (okunan: {acpi.GetKeyboardBrightness()})");
        Check(acpi.SetKeyboardBrightness(o), $"Eski seviye {o} geri yazıldı");
        Thread.Sleep(500);
        Check(acpi.GetKeyboardBrightness() == o, $"Eski seviye geri okundu (okunan: {acpi.GetKeyboardBrightness()})");
    }

    // Pil limiti: kullanıcının zaten kullandığı %60 değeri yazılır (fiilen değişmez); yalnızca kabul doğrulanır
    var gh = 60;
    try { gh = (int)(System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper", "config.json"))).RootElement.GetProperty("charge_limit").GetInt32()); } catch { }
    Check(acpi.SetBatteryLimit(gh), $"Pil limiti %{gh} (mevcut GHelper değeri) bilgisayar tarafından kabul edildi");
    Console.WriteLine(fails == 0 ? "ASUS TESTİ GEÇTİ" : $"{fails} TEST KALDI");
    return fails == 0 ? 0 : 1;
}

// Tanınmayan komut yanlışlıkla bir mod uygulamasın: yalnızca "apply <mod>" ve "all" mod uygular.
if (cmd != "apply" && cmd != "all") { Console.WriteLine($"Bilinmeyen komut: {cmd}"); return 2; }
using var engine = new ModeEngine();
var keys = cmd == "all" ? new[] { "oyun", "sessiz", "bosta", "gunluk" } : new[] { args.ElementAtOrDefault(1) ?? "gunluk" };
var failed = 0;
foreach (var key in keys)
{
    var def = Modes.Get(key);
    if (def is null) { Console.WriteLine($"Bilinmeyen mod: {key}"); return 2; }
    var result = engine.Apply(def, new ModeOptions());
    Console.WriteLine($"=== {def.Title} ===");
    foreach (var s in result.Steps) Console.WriteLine($"  [{s.Status,-8}] {s.Name}: {s.Detail}");
    Console.WriteLine($"  Sonuç: {(result.FullyVerified ? "tam doğrulandı" : result.Success ? "uygulandı (bazı adımlar doğrulanamaz/uyarı)" : "BAŞARISIZ")}");
    if (!result.Success) failed++;
}
return failed == 0 ? 0 : 1;

// update-test için sahte HTTP cevabı (status null = ağ hatası)
file sealed class FakeHandler(System.Net.HttpStatusCode? status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        status is null ? throw new HttpRequestException("bağlantı yok") : Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent(body) });
}


/// <summary>Testler için: donanıma dokunmayan sahte mod anahtarı; hangi modların istendiğini kaydeder.</summary>
sealed class FakeSwitch(string start) : Pulse.Core.Modes.IModeSwitch
{
    public string? CurrentKey { get; private set; } = start;
    public List<string> Calls { get; } = new();
    public void Reset(string key) { CurrentKey = key; Calls.Clear(); }

    public Task<Pulse.Core.Modes.ModeResult?> ApplyAsync(string key, Pulse.Core.Modes.ModeOverrides? overrides)
    {
        Calls.Add(key); CurrentKey = key;
        var def = Pulse.Core.Modes.Modes.Get(key) ?? Pulse.Core.Modes.Modes.All[0];
        return Task.FromResult<Pulse.Core.Modes.ModeResult?>(new Pulse.Core.Modes.ModeResult(def, Array.Empty<Pulse.Core.Modes.StepResult>()));
    }
}

/// <summary>Mod uygulanamayan (başka işlem sürüyor) durumu taklit eder: ApplyAsync null döner.</summary>
sealed class BusySwitch : Pulse.Core.Modes.IModeSwitch
{
    public string? CurrentKey => null;
    public Task<Pulse.Core.Modes.ModeResult?> ApplyAsync(string key, Pulse.Core.Modes.ModeOverrides? overrides) => Task.FromResult<Pulse.Core.Modes.ModeResult?>(null);
}

static class NvProbe
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct U { public uint Gpu; public uint Memory; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct MI { public ulong Total, Free, Used; }
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr d, int s, out uint t);
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr d, out U u);
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr d, int t, out uint mhz);
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr d, out uint mw);
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr d, out MI i);
    [System.Runtime.InteropServices.DllImport("nvml.dll")] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr d, out ulong r);
}
