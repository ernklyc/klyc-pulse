using Pulse.Core.Apps;
using Pulse.Core.Automation;
using Pulse.Core.Cleanup;
using Pulse.Core.Hardware;
using Pulse.Core.Health;
using Pulse.Core.Modes;
using Pulse.Core.Platform;

// Motoru arayüzsüz sınamak için: pulse-cli status | apply <mod> | all
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
    Console.WriteLine(ok ? "AYRIŞTIRICI TESTİ GEÇTİ" : "AYRIŞTIRICI TESTİ KALDI");
    return ok ? 0 : 1;
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
        return rec.Build(2496, displayHz: displayHz, cpuCapMhz: cap, previous: previous);
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
    var fpsOk = Run(600, i => (40, 3800, 72, 90, 0, 8, 100, 90), displayHz: 144);
    Check(!fpsOk!.Findings.Any(t => t.Contains("FPS sınırı")), "Serin ve FPS düşükse FPS sınırı önerilmez");

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