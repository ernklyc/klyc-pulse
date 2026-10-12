# Mimari

```
src/
├─ Pulse.Core   Donanım ve sistem mantığı (arayüzden bağımsız, test edilebilir)
├─ Pulse.App    WPF arayüzü (MVVM), tepsi, servisler
└─ Pulse.Cli    Donanıma dokunmayan ve gerçek donanımı deneyen test komutları
```

## Pulse.Core

| Klasör | İçerik |
|---|---|
| `Modes/` | Mod tanımları, `ModeEngine` (uygula → geri oku → doğrula), `ModeController` (tek giriş noktası, aynı anda tek işlem) |
| `Hardware/` | ASUS ATKACPI (`AsusAcpi`), `LaptopControl`, `GpuClocks` (NVML hız sınırı), `GpuOverclock` (NvAPI ofset), `GpuTuner` |
| `Monitoring/` | `Nvml`, `SensorHub`, `FpsMonitor` (ETW), `ThermalGuard`, `StepGovernor` |
| `Cleanup/` | Kategoriler, `CleanupEngine`, karantina, geri dönüşüm kutusu, disk analizi |
| `Apps/` | Kurulu uygulamalar, winget, kaldırma, açılış, arka plan servis/görevleri |
| `Optimize/` | `Optimizer` (Hızlandır), oyun ayarları, oyun başına ayarlar |
| `Processes/` | `ProcessInspector` (gruplama, öncelik, EcoQoS, koruma listesi) |
| `Companion/` | ThrottleStop / Afterburner / G-Helper ile ortak çalışma; `KonsolLink` (KLYC Konsol: `status.json` okuma, `konsol-feed.json` atomik yazma) |
| `Platform/` | powercfg, servis, zamanlanmış görev, geri yükleme noktası |
| `Settings/` | Ayarlar (atomik kayıt), kısayol bağlamaları |

## KLYC Konsol bağlantısı (isteğe bağlı)

Dosya tabanlı, olay tabanlı iki kanal; ikisi de kullanıcı profilinde, UTF-8, atomik yazım (geçici dosya + taşıma), bilinmeyen alanlar yok sayılır, bozuk/bayat dosya "veri yok" sayılır.

| Kanal | Yön | Dosya | Ne zaman |
|---|---|---|---|
| Konsol durumu | Konsol → Pulse | `%AppData%\KLYC-Konsol\status.json` (şema 2; şema 1 de okunur) | Konsol'un durumu değişince (açıldı, oyun başlıyor, oyun bitti, gizlendi) |
| Pulse özeti | Pulse → Konsol | `%LocalAppData%\Pulse\konsol-feed.json` | Mod değişince, oyun raporu hazır olunca, Konsol açıkken ~8 sn'de bir (Konsol "Pulse var mı" diye bakar: `running` ve en çok 20 sn eski). Oyun sırasında yazılmaz (rapor hariç) |
| Konsol komutu | Konsol → Pulse | `%AppData%\KLYC-Konsol\pulse-command.json` | **Yalnızca Ayarlar → "Konsol'dan mod değiştirmeye izin ver" açıksa (varsayılan KAPALI).** Konsol'da kolla mod seçilince ya da Hızlandır'a basılınca tek komut yazılır; Pulse işleyip dosyayı siler ve sonucu özetteki `lastCommand` ile bildirir |

- **Kod:** `Pulse.Core/Companion/KonsolLink.cs` (saf mantık: okuma, yazma, yollar; testlerde `KLYC_KONSOL_DIR` / `KLYC_PULSE_DIR`), `Pulse.App/KonsolLinkService.cs` (`FileSystemWatcher`, zamanlayıcılar, mod koruyucu bekletme), `Pulse.Core/Automation/AutoModeService.cs` (`NotifyGameLaunching/Playing/KonsolIdle`).
- **Oyun ipucu:** `launching` → profil hemen uygulanır (otomatik oyun modu açıksa); süreç 180 sn içinde gelmezse geri alınır. `playing` → yalnızca algılamaya yardım eder. Konsol `launcher/desktop/background` olunca ipucu silinir. Süreç denetimi her durumda karar vericidir; ipuçları yalnızca ipucudur.
- **Güç planı sahipliği:** `powerPlanManagedBy: "konsol"` ve oyun sürerken mod koruyucu `Pause()` ile bekletilir (12 saat sonra kendiliğinden bırakılır). `"pulse"` ise Konsol plana hiç dokunmaz. Konsol "Pulse yönetir" kararını yalnızca Pulse çalışıyorsa **ve** feed'deki `autoGameMode` açıksa verir (Pulse'ın otomatik oyun modu varsayılan kapalıdır; kapalıyken Pulse oyunda plana dokunmaz, Konsol da çekilirse oyun hızlı plana hiç geçmezdi).
- **Komut kanalı (1.11.0, varsayılan KAPALI):** Pulse yönetici yetkisiyle çalışabildiği için kanal bilerek dar ve çok denetimlidir. Kabul edilen eylemler yalnızca `mode` (bilinen dört moddan biri: `oyun`, `gunluk`, `sessiz`, `bosta`) ve `boost` (Hızlandır; onay penceresi olmadan, arayüzdeki ile aynı işlem). `KonsolCommandRunner` her komutta sırayla denetler: ayar açık mı, Konsol açık ve süreci yaşıyor mu, dosya ≤ 1 KB, **sahibi bu kullanıcı / Yöneticiler / SYSTEM**, dosya ya da klasörü **bağlantı noktası (reparse/junction) değil**, şema 1, kimlik `[A-Za-z0-9-]{8,64}`, kimlik daha önce **görülmemiş** (tekrar saldırısı), zaman damgası ≤ 20 sn eski, eylem ve mod **izin listesinde**, son komuttan ≥ 2 sn. Dosya her durumda silinir; reddedilen komutun nedeni de `lastCommand`'a yazılır (Konsol "Pulse: Komut reddedildi (sahip)" gösterir). Bilinmeyen alanlar (ör. `"komut":"calc.exe"`) yok sayılır: hiçbir zaman program çalıştırılmaz. Test: `konsol-cmd-test`.
- **Canlı değerler:** Konsol durum dosyasına `wantsLive: true` yazarsa (üst çubuktaki Pulse bilgisi açıkken) ve yalnızca Konsol ekrandayken Pulse `SensorService`'e abone olur ve özete `live` (işlemci/ekran kartı ısısı, yük) yazar; Konsol gizlenince ya da oyun başlayınca abonelik bırakılır (sıfır yük). İşlemci ısısı yönetici izni ister; ekran kartı ısısı (NVML) istemez.
- **Özet alanları (ek, geriye uyumlu):** `modeTitle`, `modes[]` (yalnızca komut kabul ediliyorsa), `live`, `lastCommand`, `acceptsCommands` (ayar açıksa `true`).
- Tasarım belgesi ve Konsol tarafı: [KLYC-Konsol/docs/PULSE-ENTEGRASYON.md](https://github.com/ernklyc/klyc-konsol/blob/main/docs/PULSE-ENTEGRASYON.md).

## İlkeler

1. **Uygula, geri oku, doğrula.** Okunamayan değer asla "doğrulandı" sayılmaz.
2. **Geri alınabilirlik.** Silmeler karantinaya / Geri Dönüşüm Kutusu'na gider; sihirbazlar yedek alır.
3. **Sınırlı ve kalıcı olmayan donanım ayarları.** Ekran kartı ofsetleri bir tavanın altında kalır, yeniden başlatınca sıfırlanır.
4. **Aynı anda tek mod işlemi** (`SemaphoreSlim`).
5. **Arayüz bloklanmaz.** Ağır sensör/donanım başlatmaları arka plandadır; sayfa açılışları 1,5–2,5 sn.

## Test

- `Pulse.Cli`: `guard-test`, `governor-test`, `hotkey-test`, `keeper-test`, `proc-test`, `auto-test`, `profile-test`, `cleanup-test` ... (bir kısmı yönetici ister).
- `KLYC-Pulse.exe --selftest`: gerçek ortamda uçtan uca sınama (canlı pencere).
