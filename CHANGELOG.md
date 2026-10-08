# Değişiklikler / Changelog

Biçim [Keep a Changelog](https://keepachangelog.com/tr/) esaslıdır; sürümleme [SemVer](https://semver.org/lang/tr/).

## [1.0.3] — 2026-10-08

### Değişenler
- Isı bekçisi, Oyun modundayken oyunu bozmamak için Sessiz moda **geçmez**; yalnızca uyarır.
- Isı uyarıları sağ alttaki büyük Windows balonu yerine **sağ üstte küçük, kısa süreli yazı** olarak gösterilir (gösterge varsa altında).
- Oyun üstü gösterge daha soluk (yarı saydam).
- Oyun bitince Oyun modundan **Günlük'e** dönülür (masaüstünde Turbo/yüksek fanla kalmaz).

## [1.0.2] — 2026-10-08

### Düzeltilenler
- İzleme: işlemci hafif yükteyken (%30 altı) 75 °C'yi aşıyorsa artık 'Her şey normal' yerine uyarı gösterilir (havalandırma/termal macun ipucuyla).

## [1.0.1] — 2026-10-08

### Düzeltilenler
- Oyun ve Günlük modları ekran yenileme hızını sabit 144 Hz yerine **ekranın desteklediği en yüksek hıza** ayarlıyor (ör. 180 Hz monitörde 180 Hz). Oyun profilinde seçenek 'Ekranın en yükseği' oldu; eski 144 kayıtları bu seçeneğe taşınır.

## [1.0.0] — 2026-10-08

İlk kararlı sürüm.

### Eklenenler
- **Modlar** (Oyun, Günlük, Sessiz, Boşta): ASUS profili, işlemci ek hızı, üst sınır, hız/güç dengesi (prizde ve pilde), ekran Hz, parlaklık, ekran kartı hız sınırı; her adım doğrulanır.
- **Otomatik oyun modu**, oyun profilleri, ekran kartı tercihi, süreç önceliği, Windows oyun ayarları denetimi.
- **Temizlik** (karantina, geri yükleme noktası), **Hızlandır** (önce/sonra raporu), **süreç yöneticisi** (Eko mod), uygulama güncelleme/kaldırma, açılış ve arka plan yönetimi.
- **İzleme:** işlemci/ekran kartı, yavaşlama nedeni, oyun üstü gösterge, FPS (ETW).
- **Sağlık:** pil geçmişi, disk sağlığı, ısı. Sürücü ve BIOS bilgisi, Windows Update sürücü taraması.
- **ASUS:** pil şarj sınırı, klavye ışığı ve RGB; ekran kartı hızlandırma (otomatik arama dahil); atanabilir kısayollar.
- **Koruma:** ısı koruması, sıcaklık sınırı (fan eğrisi yerine), mod koruyucu.
- **Araçlar:** G-Helper, Afterburner, ThrottleStop, Intel DSA, PC Manager için karşılık tablosu, kapatma/geri alma, ortak çalışma.
- `--selftest` kendini sınama modu (canlı pencere + sonuç dosyası).

### Bilinen sınırlar
- İşlemci undervolt/güç sınırı ve fan eğrisi çoğu sistemde BIOS tarafından kilitli olduğundan desteklenmez.
- Isı koruması ve sıcaklık sınırının karar mantığı sahte sıcaklık akışlarıyla test edilmiştir.
- Arayüz yalnızca Türkçe. Uygulama henüz dijital imzalı değil.