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
| `Companion/` | ThrottleStop / Afterburner / G-Helper ile ortak çalışma |
| `Platform/` | powercfg, servis, zamanlanmış görev, geri yükleme noktası |
| `Settings/` | Ayarlar (atomik kayıt), kısayol bağlamaları |

## İlkeler

1. **Uygula, geri oku, doğrula.** Okunamayan değer asla "doğrulandı" sayılmaz.
2. **Geri alınabilirlik.** Silmeler karantinaya / Geri Dönüşüm Kutusu'na gider; sihirbazlar yedek alır.
3. **Sınırlı ve kalıcı olmayan donanım ayarları.** Ekran kartı ofsetleri bir tavanın altında kalır, yeniden başlatınca sıfırlanır.
4. **Aynı anda tek mod işlemi** (`SemaphoreSlim`).
5. **Arayüz bloklanmaz.** Ağır sensör/donanım başlatmaları arka plandadır; sayfa açılışları 1,5–2,5 sn.

## Test

- `Pulse.Cli`: `guard-test`, `governor-test`, `hotkey-test`, `keeper-test`, `proc-test`, `auto-test`, `profile-test`, `cleanup-test` ... (bir kısmı yönetici ister).
- `KLYC-Pulse.exe --selftest`: gerçek ortamda uçtan uca sınama (canlı pencere).
