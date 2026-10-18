# Oyun performansı ve hafiflik araştırması (2026-10-18)

Amaç: KLYC Pulse + KLYC Konsol bu bilgisayarda **gerçekten** neyi hızlandırabilir, neyi bozmadan bırakmalı, neyi yapmamalı? Kanıt düzeyi her maddede yazılı:
**[ölçüldü]** bu makinede ölçtüm · **[belge]** resmi belge · **[zayıf]** bloglar/forumlar, yön gösterir ama sayıya güvenme · **[yok]** kanıt bulamadım.

## 1. Makine

ASUS TUF Gaming F15 (FX506LI) · Intel Core i5-10300H (4 çekirdek / 8 iş parçacığı, TjMax 100 °C) · GeForce GTX 1650 Ti **4 GB** + Intel UHD 630 (hibrit) · 15,8 GB RAM · 1 TB NVMe · Windows 11.
Şu anki ayarlar **[ölçüldü]**: güç planı "Nihai Performans" · HAGS açık · Oyun Modu açık · Game DVR kapalı · VBS açık (bellek bütünlüğü/HVCI kapalı) · sayfa dosyası yalnızca **1 GB** · oyunların çoğunda "yüksek performanslı GPU" tercihi tanımlı (Pulse yazmış).

## 2. Kendi verimiz (oyun raporları + günlükler, 14 Ekim)

| Oyun | Süre | Bulgu |
|---|---|---|
| FC25 | 20 dk | GPU zamanın %85'inde güç sınırında · VRAM %99 · işlemci 95–96 °C · fan zaten tam hızda (Turbo profili devri artırmadı) · GPU %85 → büyük olasılıkla işlemci sınırlı |
| Forza Horizon 4 | 12 dk | GPU güç sınırı %64 · VRAM %99 · işlemci 96 °C · fan tam hız · işlemci hızı kısılmıştı (3,8 → 3,3 GHz) |
| Silksong | 2 dk | 143 FPS ort., 116 FPS %1 low, sorun yok; arka planda Sunshine ortalama %2,6 işlemci |

Yorum: bu bir **ısı ve güç bütçesi sınırlı** dizüstü. Fanlar zaten tam hızda olduğu halde 95 °C'de kalması, yazılımla çözülemeyen bir soğutma kısıtına (toz / macun yaşı / hava akışı) işaret eder. 4 GB VRAM'in %99'a dolması dokuların sistem belleğine taşmasına, yani kare süresi sıçramalarına yol açar.

## 3. Kaldıraçlar: ne kazandırır, riski ne, ne yaptık

| # | Kaldıraç | Beklenen etki | Kanıt | Risk | Durum |
|---|---|---|---|---|---|
| 1 | **Oyunda işlemci frenini geç devreye sok** (95 → 98 °C, 15 sn) | İşlemciyi sınırlayan oyunlarda %5–13 FPS geri kazanımı (3,3 → 3,8 GHz fark) | [ölçüldü] kayıtlarda fren + Pulse'un kendi raporu "işlemci sınırlıyor" | Donanım 100 °C'de kendi kısar; frenin yokluğunda kısa süreli tepe olabilir | **Yapıldı (1.12.1)**; gerçek oyunda doğrulanmadı |
| 2 | **Arka plan süreçlerini söyle / azalt** (Sunshine %2,6, EAAntiCheat %3,4) | 4 çekirdekli işlemcide %3–6 ek pay | [ölçüldü] raporlar | Sunshine kullanıcının uzaktan oynatması; otomatik kapatmak istenmeyebilir | Hızlandır adıyla söylüyor (1.12.0); otomatik kapatma yok |
| 3 | **FPS sınırı önerisi** (CPU'ya bağlı + sıcak oyunda 90/120'ye sınırla) | Daha az ısı → daha yüksek sürekli saat, daha az kısılma; ortalama FPS biraz düşer, 1% low iyileşir | [zayıf] tüm kaynaklar "güç düşünce saat artar" der | Düşük | Pulse'ta `SuggestedFpsCap` var; Konsol'dan görünmüyor |
| 4 | **Sayfa dosyası 1 GB** → sistem yönetimli | RDR2/GTA V gibi büyük oyunlarda çökme/bellek hatası riskini azaltır | [belge] Windows commit modeli; [ölçüldü] ayar 1 GB | Düşük (Windows ayarı; kullanıcı onaylı) | **Yapılmadı**: Pulse yalnızca uyarabilir; sistem ayarı kullanıcının |
| 5 | **VRAM %99 uyarısı → doku kalitesini bir kademe düşür** | Dokuların sisteme taşması kalkar; takılma azalır. En büyük "ücretsiz" kazanç | [zayıf] forumlar; [ölçüldü] %99 | Yok | Pulse raporunda söylüyor; oyuna özel öneri yok |
| 6 | **VBS (sanallaştırma tabanlı güvenlik)** | Oyunlarda %0–5 (kaynaklar arasında 1–10 arası değişiyor) | [zayıf] | Güvenlik özelliğini kapatmak: karar kullanıcının | **Yalnızca bilgi** verilebilir; değiştirmiyoruz |
| 7 | HAGS / Oyun Modu | GPU'ya bağlı sahnede ≈0, CPU'ya bağlıda %1–3, bazen artefakt | [zayıf] | Düşük | Mevcut açık hali iyi; Hızlandır yanlışsa düzeltir |
| 8 | **Overlay** (Pulse FPS/ısı göstergesi) | Flip-model oyunda Windows "bağımsız flip"i bırakıp kompozisyona dönebilir (üstte içerik varsa); MPO desteğiyle korunabilir | [belge] Microsoft DXGI flip-model rehberi; **FPS maliyeti ölçülmedi** | Küçük gecikme/FPS bedeli olabilir | Ayar mevcut; **A/B ölçüm aracı önerisi** (aşağıda) |
| 9 | Güç planı / EPP / çekirdek park | Nihai Performans zaten park etmez | [zayıf] | — | Modlar yönetiyor |
| 10 | **Undervolt** (ThrottleStop FIVR) | Aynı sıcaklıkta daha yüksek sürekli saat = gerçek FPS; en iyi kanıtlı yazılım kazancı | [zayıf] | Kararsızlık; 10. nesil bazı laptoplarda kilitli | Pulse ThrottleStop profillerini yönetebiliyor (Araçlar); **undervolt (FIVR) kilidi bu makinede doğrulanmadı** |
| 11 | **Soğutma bakımı** (toz, termal macun, taban) | 5–15 °C | [zayıf] | Donanıma müdahale (kullanıcı) | Yazılım yapamaz; Pulse "fan tam hızda ama 95 °C" teşhisini söyleyebilir |
| 12 | Zamanlayıcı çözünürlüğü, "RAM temizleyici", servis kapatma, ağ kısma anahtarları, güvenlik önlemlerini kapatma | Ölçülebilir kazanç yok / zararlı olabilir | [yok] / [zayıf] | Orta–yüksek | **Yapmıyoruz** |

## 4. Yazılımın kendi ayak izi (hafif olma şartı)

Ölçüm **[ölçüldü]**, bu makinede, boşta:

| Bileşen | Önce | Sonra | Not |
|---|---|---|---|
| Konsol (ekranda, boşta) | ~%2,8 tek çekirdek | ~%1,7–2,4 | kol yoklaması uyarlanır; RAM ~195 MB (çoğu WPF) |
| Konsol (oyun sırasında) | — | kapalı; ~1,5 MB bekçi %0,1 | tasarım ilkesi |
| Pulse (tepside, boşta) | %3,1 tek çekirdek, 182 MB | %2,0–2,5 | NVML "kısılma nedeni" çağrısı okuma başına 16 ms'ti → 5 okumada bir |
| Pulse güç planı okuma | 39 ms + süreç / okuma | 0,12 ms | `powercfg.exe` yerine powrprof; dakikada birkaç süreç açılmıyor |
| Pulse oyun algılama taraması | ~5 ms / 5 sn | aynı | önemsiz |

## 5. Önerilen fazlar (sıra = değer / risk)

1. **Sistem denetimi (salt okunur) → Pulse Hızlandır ve Konsol özeti:** sayfa dosyası çok küçük, VBS açık, VRAM taşması, fan tam hızda + sıcaklık yüksek ("temizlik/macun" önerisi), hibrit GPU tercihi eksik. Hiçbir ayar değiştirmez, nedenini ve etkisini söyler. *(düşük risk, yüksek bilgi değeri)*
2. **Overlay A/B ölçümü:** oyun raporu overlay'in açık/kapalı olduğunu kaydeder; aynı oyunun oturumlarını ortalama FPS ve %1 low olarak yan yana gösterir. Gerçek maliyet ölçülmeden overlay'i kapatmıyoruz.
3. **FPS sınırı önerisini Konsol'a taşı:** oyun özeti panelinde "işlemci sınırlı ve 95 °C: 90 FPS sınırı daha akıcı olabilir".
4. **Ödünç alınmış kapasite:** Hızlandır'ın "arka plan yükü" adımına "oyun sırasında kapatılabilir" listesi (Sunshine, EA hizmetleri…) — yalnızca kullanıcı onayıyla, geri alınabilir.
5. **ThrottleStop/undervolt yolu:** önce bu makinede FIVR kilidi var mı (salt okunur kontrol), sonra karar.

## 6. Kaynaklar

- Microsoft Learn — DXGI flip model, DirectFlip / Independent Flip / MPO: <https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model> **[belge]**
- Windows 11 oyun ayarı rehberleri (Game Mode, HAGS, VBS; sayılar tutarsız, yönü gösterir): <https://gamerhardware.org/optimize-windows-11-gaming/> · <https://mementomorigaming.com/guides/pc/windows-11-gaming-optimization> **[zayıf]**
- Dizüstü ısıl kısılma / undervolt (tek tutarlı sonuç: ısıl sınırlı dizüstüde undervolt ısıyı düşürmez, saati yükseltir): <https://tier1settings.com/undervolt-gaming-laptop/> · <https://gpubottleneckcalculator.com/blog/thermal-throttling-guide/> **[zayıf]**
- PROCHOT / TjMax tartışmaları (resmi veri yok): <https://community.frame.work/t/excessive-cpu-thermal-throttling-on-12th-gen/21569> **[zayıf]**
- Güç planı / çekirdek park (ölçülü FPS verisi yok): <https://bitsum.com/parkcontrol/> **[zayıf]**
- RTSS/Afterburner overlay maliyeti (yalnızca nitel; "A/B ölç" önerisi): <https://usekudu.com/guides/gaming/fix-msi-afterburner-osd-causing-fps-drop> **[zayıf]**
