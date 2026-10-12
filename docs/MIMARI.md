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

- **Kod:** `Pulse.Core/Companion/KonsolLink.cs` (saf mantık: okuma, yazma, yollar; testlerde `KLYC_KONSOL_DIR` / `KLYC_PULSE_DIR`), `Pulse.App/KonsolLinkService.cs` (`FileSystemWatcher`, zamanlayıcılar, mod koruyucu bekletme), `Pulse.Core/Automation/AutoModeService.cs` (`NotifyGameLaunching/Playing/KonsolIdle`).
- **Oyun ipucu:** `launching` → profil hemen uygulanır (otomatik oyun modu açıksa); süreç 180 sn içinde gelmezse geri alınır. `playing` → yalnızca algılamaya yardım eder. Konsol `launcher/desktop/background` olunca ipucu silinir. Süreç denetimi her durumda karar vericidir; ipuçları yalnızca ipucudur.
- **Güç planı sahipliği:** `powerPlanManagedBy: "konsol"` ve oyun sürerken mod koruyucu `Pause()` ile bekletilir (12 saat sonra kendiliğinden bırakılır). `"pulse"` ise Konsol plana hiç dokunmaz. Konsol "Pulse yönetir" kararını yalnızca Pulse çalışıyorsa **ve** feed'deki `autoGameMode` açıksa verir (Pulse'ın otomatik oyun modu varsayılan kapalıdır; kapalıyken Pulse oyunda plana dokunmaz, Konsol da çekilirse oyun hızlı plana hiç geçmezdi).
- **Bilerek yapılmayanlar:** Konsol'dan komut (mod değiştirme) kanalı yok: Pulse yönetici yetkisiyle kullanıcının yazabildiği bir klasörden komut alırdı, kazancı güvenlik yüzeyine değmez. `feed.acceptsCommands` hep `false`.
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
