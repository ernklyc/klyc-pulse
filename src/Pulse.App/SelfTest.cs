using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Pulse.App.ViewModels;
using Pulse.Core.Apps;
using Pulse.Core.Cleanup;
using Pulse.Core.Companion;
using Pulse.Core.Diagnostics;
using Pulse.Core.Drivers;
using Pulse.Core.Hardware;
using Pulse.Core.Health;
using Pulse.Core.Modes;
using Pulse.Core.Monitoring;
using Pulse.Core.Optimize;
using Pulse.Core.Platform;
using Pulse.Core.Processes;

namespace Pulse.App;

/// <summary>
/// Kendini sınama (KLYC-Pulse.exe --selftest): uygulamanın gerçek, yönetici yetkili ortamında her özelliği sırayla dener ve
/// sonucu bir dosyaya yazar. Hiçbir ayarı kalıcı değiştirmez: bittiğinde mod Günlük, ekran kartı fabrika hızında olur.
/// </summary>
public static class SelfTest
{
    private sealed record Row(string Name, string Status, string Detail);

    public static async Task RunAsync(bool includeGpuTune, bool includeHeat = false)
    {
        var win = await Application.Current.Dispatcher.InvokeAsync(() => { var w = new SelfTestWindow(); w.Show(); return w; });
        var rows = new List<Row>();
        var logLinesBefore = JournalLineCount();
        var sw = Stopwatch.StartNew();

        async Task Step(string name, Func<Task<(bool Ok, string Detail)>> test, int timeoutSec = 60)
        {
            win.Running(name);
            var before = rows.Count;
            var t = Stopwatch.StartNew();
            try
            {
                var task = Task.Run(test);
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSec))) != task)
                    rows.Add(new(name, "KALDI", $"{timeoutSec} sn içinde bitmedi (takıldı)"));
                else
                {
                    var (ok, detail) = await task;
                    rows.Add(new(name, ok ? "GEÇTİ" : "KALDI", $"{detail} [{t.Elapsed.TotalSeconds:0.0} sn]"));
                }
            }
            catch (Exception ex) { rows.Add(new(name, "KALDI", "HATA: " + ex.Message)); }
            if (rows.Count > before) { var r = rows[^1]; win.Done(name, r.Status == "GEÇTİ", r.Detail); }
        }
        Task Quick(string name, Func<(bool, string)> f, int timeoutSec = 60) => Step(name, () => Task.FromResult(f()), timeoutSec);

        var admin = CleanupEngine.IsAdmin;
        rows.Add(new("Yönetici yetkisi", admin ? "GEÇTİ" : "KALDI", admin ? "Evet" : "HAYIR: bu test yönetici olarak açılmalı"));
        win.Done("Yönetici yetkisi", admin, admin ? "Evet" : "HAYIR: bu test yönetici olarak açılmalı");

        await Quick("Ayar dosyası kaydet/oku", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "pulse-selftest-settings.json");
            var s = Pulse.Core.Settings.SettingsStore.Load(tmp);
            s.Current.BatteryLimit = 77; s.Save();
            var back = Pulse.Core.Settings.SettingsStore.Load(tmp).Current.BatteryLimit;
            try { File.Delete(tmp); } catch { }
            return (back == 77, $"yazılan 77, okunan {back}");
        });

        await Quick("ASUS sürücüsü: fan ve klavye ışığı okuma", () =>
        {
            using var a = AsusAcpi.TryOpen();
            if (a is null) return (false, "ATKACPI açılamadı");
            return (a.GetCpuFanRpm() is not null, $"CPU fanı {a.GetCpuFanRpm()} devir/dk, GPU fanı {a.GetGpuFanRpm()}, klavye ışığı {a.GetKeyboardBrightness()}");
        });

        await Quick("Sensörler (işlemci sıcaklığı dahil)", () =>
        {
            using var hub = new SensorHub();
            hub.Read(true); Thread.Sleep(1200);
            var s = hub.Read(true);
            var ok = s.CpuTempC is not null && s.CpuMhz is not null && s.Gpu is not null;
            return (ok, $"CPU %{s.CpuPercent} {s.CpuMhz} MHz {s.CpuTempC}°C | GPU {s.Gpu?.TempC}°C {s.Gpu?.PowerW:0.0} W | RAM %{s.RamPercent:0}");
        });

        await Quick("Kare hızı ölçümü (ETW) başlatılıyor", () =>
        {
            using var f = new FpsMonitor();
            var ok = f.Start();
            return (ok, ok ? "oturum açıldı" : f.Error ?? "başlamadı");
        });

        await Quick("Ekran kartı hızlandırma arayüzü (uyuyan kart dahil)", () =>
        {
            var s = GpuOverclock.Read();
            return (s is { Editable: true }, s is null ? "okunamadı" : $"düzenlenebilir={s.Editable}, çekirdek {s.CoreMhz}, bellek {s.MemMhz}, aralık {s.CoreMin}..{s.CoreMax} / {s.MemMin}..{s.MemMax}");
        });

        await Quick("Ekran kartı hız sınırı: uygula ve kaldır", () =>
        {
            var a = GpuClocks.Cap(1350);
            var r = GpuClocks.Release();
            return (a.Ok && a.Verified && r.Ok, $"{a.Message} | {r.Message}");
        });

        await Quick("Ekran kartı hızlandırma: +25/+100 uygula, doğrula, sıfırla", () =>
        {
            var a = GpuOverclock.Apply(25, 100);
            var r = GpuOverclock.Reset();
            var end = GpuOverclock.Read();
            return (a.Ok && a.Verified && r.Verified && end is { CoreMhz: 0, MemMhz: 0 }, $"{a.Message} | {r.Message}");
        });

        await Step("Modlar: Sessiz, Oyun, Günlük sırayla (her adım doğrulanır)", async () =>
        {
            var sb = new StringBuilder();
            var allOk = true;
            foreach (var key in new[] { Modes.Quiet, Modes.Game, Modes.Daily })
            {
                var res = await AppServices.Modes.ApplyAsync(key);
                if (res is null) { allOk = false; sb.Append($"{key}: uygulanamadı; "); continue; }
                var failed = res.Steps.Where(s => s.Status == StepStatus.Failed).Select(s => s.Name).ToList();
                var warn = res.Steps.Where(s => s.Status == StepStatus.Warning).Select(s => s.Name).ToList();
                if (failed.Count > 0) allOk = false;
                sb.Append($"{res.Mode.Title}: {(failed.Count == 0 ? "tamam" : "BAŞARISIZ " + string.Join("/", failed))}{(warn.Count > 0 ? " uyarı:" + string.Join("/", warn) : "")}; ");
            }
            var oc = GpuOverclock.Read();
            var clean = (oc is null || oc is { CoreMhz: 0, MemMhz: 0 }) && AppServices.Modes.CurrentKey == Modes.Daily;   // oc null = kart uykuda = fabrika hızı
            return (allOk && clean, sb + $"son mod {AppServices.Modes.CurrentKey}, ekran kartı ofseti {oc?.CoreMhz}/{oc?.MemMhz}");
        }, 120);

        await Quick("Süreç yöneticisi", () =>
        {
            var g = new ProcessInspector().Sample();
            return (g.Count > 30 && g.Any(x => x.Protected) && g.Any(x => !x.Protected), $"{g.Count} program, {g.Count(x => x.Protected)} korumalı");
        });

        await Quick("Arka plan servisleri ve görevleri (yönetici)", () =>
        {
            var l = new BackgroundInspector().List();
            return (l.Count > 0 && l.Any(x => !x.IsService), $"{l.Count} öğe ({l.Count(x => x.IsService)} servis, {l.Count(x => !x.IsService)} görev)");
        });

        await Quick("Sürücü ve BIOS bilgisi", () =>
        {
            var b = DriverCenter.ReadBios(); var d = DriverCenter.ReadInstalled();
            return (b is not null && d.Count > 0, $"BIOS {b?.Version}, {d.Count} sürücü");
        });

        await Quick("Disk sağlığı", () => { var d = DiskHealth.Read(); return (d.Count > 0, string.Join("; ", d.Select(x => $"{x.Name}: {x.HealthText}"))); });
        await Quick("Windows oyun ayarları", () => { var g = WindowsGameSettings.Read(); return (g.Count == 4, string.Join(", ", g.Select(x => $"{x.Name}={(x.IsGood ? "uygun" : "düzeltilebilir")}"))); });

        await Quick("Temizlik taraması (yalnızca okuma)", () =>
        {
            var engine = new CleanupEngine();
            var scans = engine.ScanAsync(CleanupCatalog.Build()).GetAwaiter().GetResult();
            return (scans.Count > 5, $"{scans.Count} kategori, toplam {scans.Sum(x => x.Bytes) / 1048576} MB");
        });

        await Quick("Kurulu uygulamalar ve açılış öğeleri", () =>
        {
            var a = InstalledAppsReader.Read(); var s = StartupManager.List();
            return (a.Count > 5, $"{a.Count} uygulama, {s.Count} açılış öğesi");
        });

        await Quick("Ortak çalışma araçları (yalnızca algılama)", () =>
            (true, $"ThrottleStop {ThrottleStopControl.IsInstalled}, Afterburner {AfterburnerControl.IsInstalled}, G-Helper {GHelperControl.IsInstalled}"));

        await Quick("Kısayollar", () =>
        {
            var failed = AppServices.Hotkeys.Failed;
            if (!AppServices.Settings.Current.Hotkeys) return (true, "kısayollar Ayarlar'dan kapalı (hata değil)");
            return (AppServices.Hotkeys.Enabled && failed.Count == 0, failed.Count == 0 ? "hepsi kayıtlı" : "alınamayanlar: " + string.Join(", ", failed));
        });

        await Step("Sayfaların arka planı (her sayfanın verisi yönetici ortamında kurulur)", async () =>
        {
            var errors = 0; var sb = new StringBuilder();
            async Task<T> OnUi<T>(Func<T> f) => await Application.Current.Dispatcher.InvokeAsync(f);
            async Task Make(string name, Func<IDisposable?> create)
            {
                try
                {
                    var vm = await OnUi(create);
                    await Task.Delay(1800);
                    await OnUi<object?>(() => { vm?.Dispose(); return null; });
                    sb.Append(name + " ✓ ");
                }
                catch (Exception ex) { errors++; sb.Append($"{name} ✗({ex.Message}) "); }
            }
            await Make("Ana ekran", () => new HomeViewModel());
            await Make("İzleme", () => new MonitorViewModel());
            await Make("Sağlık", () => new HealthViewModel());
            await Make("Süreçler", () => new ProcessesViewModel());
            await Make("Oyunlar", () => new GamesViewModel());
            await Make("Dizüstü", () => { _ = new LaptopViewModel(); return null; });
            await Make("Araçlar", () => { _ = new ToolsViewModel(); return null; });
            await Make("Sürücüler", () => { _ = new DriversViewModel(); return null; });
            await Make("Uygulamalar", () => { _ = new AppsViewModel(); return null; });
            await Make("Ayarlar", () => new SettingsViewModel());
            await Make("Temizlik", () => { _ = new CleanupViewModel(); return null; });
            return (errors == 0, sb.ToString());
        }, 90);

        await Step("Uygulama güncellemeleri (winget)", async () =>
        {
            var (items, error) = await WingetService.GetUpgradesAsync();
            return (error is null, error ?? $"{items.Count} güncelleme bulundu");
        }, 60);

        if (includeGpuTune)
        {
            await Step("Ekran kartı otomatik bul (gerçek yük, ~2 dk)", async () =>
            {
                var res = await GpuTuner.RunAsync();
                var end = GpuOverclock.Read();
                var table = string.Join(" | ", res.Steps.Select(s => $"+{s.Core}/{s.Mem}: {s.Fps:0.0} kare/sn {s.CoreMhz:0} MHz {s.MaxTempC:0}°C {(s.Stable ? "kararlı" : "KARARSIZ " + s.Note)}"));
                return (res.Steps.Count > 0 && (end is null || end is { CoreMhz: 0, MemMhz: 0 }), $"{table} => {res.Summary} (bitişte ofset {end?.CoreMhz}/{end?.MemMhz})");
            }, 240);
        }

        if (includeHeat)
        {
            await Step("Hız sınırı ısıyı düşürüyor mu? (gerçek yük, ~6 dk, işlemci tam yüklenir)", async () =>
            {
                var ladder = AppServices.GameLadder();
                var caps = new List<int?> { null };
                caps.AddRange(ladder.Skip(2).Take(2));                                   // ikinci ve üçüncü kademe (örn. ~3500 ve ~3200 MHz)
                using var keeper = AppServices.Keeper.Pause();
                using var heat = AppServices.Heat.Pause();
                using var cool = AppServices.Cooling.Pause();
                var phases = await Task.Run(() => HeatBenchmark.Run(caps, restSec: 40, loadSec: 80, new Progress<string>(m => win.Running("Isı denemesi: " + m))));
                var summary = HeatBenchmark.Summarize(phases);
                // Sınırsız aşamadaki hız = bu bilgisayarın sürekli yük altı hızı: kademeler buna göre kurulur
                if (phases.Count > 0 && phases[0].MedianMhz > 0)
                {
                    AppServices.Settings.Current.CpuSustainedMhz = phases[0].MedianMhz;
                    AppServices.Settings.Save();
                    summary += $" Sürekli hız {phases[0].MedianMhz:0} MHz olarak kaydedildi (kademeler buna göre kuruluyor).";
                }
                var hasTemps = phases.All(p => p.AvgTempC is not null);
                return (phases.Count == caps.Count && hasTemps, summary + (hasTemps ? "" : " (Sıcaklık okunamadı: Pulse yönetici olarak çalışmıyor ya da bu bilgisayar sıcaklık sunmuyor.)"));
            }, 600);
        }

        // Test sırasında günlüğe düşen hatalar
        var newLog = JournalLines().Skip(logLinesBefore).Where(l => l.Contains("hatası", StringComparison.OrdinalIgnoreCase) || l.Contains("Exception") || l.Contains("kaydedilemedi") || l.Contains("takıldı")).ToList();
        rows.Add(new("Günlükte hata", newLog.Count == 0 ? "GEÇTİ" : "KALDI", newLog.Count == 0 ? "yok" : string.Join(" || ", newLog.Take(6))));
        win.Done("Günlükte hata", newLog.Count == 0, newLog.Count == 0 ? "yok" : string.Join(" || ", newLog.Take(6)));

        var passed = rows.Count(r => r.Status == "GEÇTİ");
        var failed2 = rows.Count(r => r.Status == "KALDI");
        var report = new StringBuilder();
        report.AppendLine($"KLYC-Pulse {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} kendini sınama, {DateTime.Now:dd.MM.yyyy HH:mm}, süre {sw.Elapsed.TotalSeconds:0} sn");
        foreach (var r in rows) report.AppendLine($"[{r.Status}] {r.Name}: {r.Detail}");
        report.AppendLine($"SONUÇ: {passed} geçti, {failed2} kaldı");

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "selftest.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        // Kopya: kullanıcı klasörünün kökü (kolay bulunsun).
        try { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KLYC-Pulse-selftest.txt"), report.ToString(), new UTF8Encoding(true)); } catch { }
        Journal.Write($"Kendini sınama bitti: {passed} geçti, {failed2} kaldı.");

        win.Finish(failed2 == 0 ? $"Her şey tamam: {passed} test geçti" : $"{passed} test geçti, {failed2} test kaldı", failed2 == 0);
        await win.WhenClosed;
    }

    private static IEnumerable<string> JournalLines()
    {
        var file = Path.Combine(Journal.Directory, $"{DateTime.Now:yyyy-MM-dd}.log");
        try { return File.Exists(file) ? File.ReadAllLines(file) : []; } catch { return []; }
    }

    private static int JournalLineCount() => JournalLines().Count();
}
