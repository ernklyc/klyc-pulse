# Üçüncü taraf bileşenler

KLYC-Pulse aşağıdaki açık kaynak bileşenleri kullanır. Her biri kendi lisansı altındadır.

## NuGet paketleri

| Paket | Sürüm | Lisans |
|---|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.3.0 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) | 3.2.8 | MIT |
| [System.Diagnostics.PerformanceCounter](https://github.com/dotnet/runtime) | 8.0.1 | MIT |
| [NvAPIWrapper.Net](https://github.com/falahati/NvAPIWrapper) | 0.8.1.101 | **LGPL-3.0** |

**NvAPIWrapper.Net (LGPL-3.0) hakkında:** Ekran kartı hızlandırma özelliği bu kütüphaneyi çağırır. Tek dosyalık yayında kütüphane uygulamayla aynı paketin içine konur; LGPL'nin "kütüphaneyi değiştirip yeniden bağlama" hakkı için kaynak koddan derleme (`dotnet publish`) her zaman mümkündür ve kütüphane `PackageReference` ile değiştirilebilir. Dağıtım yapmayı düşünüyorsanız bu noktayı bir hukukçuyla gözden geçirin.

## Yazı tipleri (src/Pulse.App/Fonts)

| Yazı tipi | Lisans | Telif |
|---|---|---|
| Geist | SIL Open Font License 1.1 | Copyright 2024 The Geist Project Authors (https://github.com/vercel/geist-font) — metin: https://openfontlicense.org |
| Instrument Serif | SIL Open Font License 1.1 | Lisans metni `src/Pulse.App/Fonts/OFL-InstrumentSerif.txt` dosyasındadır |

## Sistem arayüzleri

Uygulama; Windows'un belgeli arayüzlerini (powercfg, WMI, Görev Zamanlayıcı, Windows Update API, ETW), NVIDIA'nın sürücüyle gelen NVML/NvAPI arayüzlerini ve ASUS'un ATKACPI sürücü arayüzünü kullanır. Bu arayüzlerin adları ve markalar (ASUS, NVIDIA, Intel, Microsoft, MSI, G-Helper, ThrottleStop) kendi sahiplerine aittir; KLYC-Pulse bu şirketlerle bağlantılı değildir.