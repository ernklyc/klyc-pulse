# Uyumluluk

## Desteklenen sistemler

- **İşletim sistemi:** Windows 10 sürüm 2004 (19041) ve üstü, Windows 11. Yalnızca x64.
- **Çalışma zamanı:** .NET 8 Masaüstü Çalışma Zamanı (ya da `--self-contained true` ile yayınlanan sürüm).
- **Yetki:** Yönetici (uygulama manifestinde `requireAdministrator`). Sensörler, servis/görev yönetimi, ekran kartı ayarı ve FPS ölçümü bunu gerektirir.

## Çekirdek özellikler ve marka modülleri

KLYC-Pulse **çekirdek** özellikleri her Windows PC'de çalışır. **Marka/donanım modülleri** ise yalnızca ilgili donanım varsa devreye girer; yoksa o adım **"Atlandı"** olarak görünür ve modun geri kalanı uygulanır. Hata vermez.

| Özellik | Gerekli donanım |
|---|---|
| Temizlik, uygulamalar, süreçler, açılış/servis yönetimi | Herhangi bir Windows PC |
| Sürücü ve BIOS bilgisi, Windows Update taraması | Herhangi bir Windows PC |
| Mod: güç planı (turbo, üst sınır, hız/güç dengesi) | Herhangi bir PC (Intel/AMD) |
| Mod: ekran Hz ve parlaklık | Ekranın desteğine bağlı |
| İzleme: işlemci yükü/hızı, bellek | Herhangi bir Windows PC |
| İzleme: CPU sıcaklığı | ACPI termal bölgesi sunan sistem + yönetici yetkisi |
| GPU izleme, hız sınırı, hızlandırma, otomatik bul | NVIDIA ekran kartı |
| FPS ölçümü | DirectX 10/11/12 oyunları |
| **Üretici modülü (şu an ASUS):** performans profili, pil şarj sınırı, klavye ışığı | ASUS dizüstü (ATKACPI sürücüsü) |
| Klavye RGB rengi | ACPI RGB sunan ASUS TUF modelleri |

Yeni bir marka modülü eklemek için bkz. [CONTRIBUTING.md](../CONTRIBUTING.md) ve [MIMARI.md](MIMARI.md) (`Hardware/`).

## Donanıma göre olası farklar

- AMD işlemcilerde ek hız ve "hız/güç dengesi" ayarlarının güç planındaki karşılığı farklı olabilir; adımlar doğrulanamazsa uyarı olarak görünür.
- AMD/Intel ekran kartlarında GPU izleme ve ayarları kapalıdır (yalnızca NVIDIA).
- Pil şarj sınırı ve profil numaralandırması model ailesine göre değişebilir.
- Klavye RGB için ACPI cihaz numaraları modele göre farklı olabilir.

Yeni bir bilgisayarda ilk iş `KLYC-Pulse.exe --selftest` çalıştırıp sonucu inceleyin.

## Her bilgisayara uyum: ölçerek karar verir

KLYC-Pulse varsayım yapmaz, bu bilgisayarda ölçer:

- **İşlemci hız sınırı:** Windows'un "işlemci en yüksek frekansı" ayarı çoğu Intel dizüstü/masaüstünde çalışır ama bazı sistemlerde (Ryzen, bazı HWP/üretici yazılımı yapılandırmaları) yok sayılabilir. Pulse ilk sınırı koymadan önce tüm çekirdekleri ~20 sn yorup sınırsız ve sınırlı gerçek hızı ölçer; sınır çalışmıyorsa hız sınırı özelliklerini kapatır (Oyunlar sayfasında "Şimdi dene" ile elle de yapılır). Hibrit Intel (12. nesil ve sonrası) için performans çekirdeği sınıfı da yazılır.
- **Kademeler:** Hız sınırı kademeleri sabit sayı değil, bu bilgisayarın yük altındaki gerçek en yüksek hızının yüzdesidir ve taban hızın altına inmez. 5,5 GHz'lik bir masaüstü de 4 GHz'lik bir dizüstü de kendine uygun kademeleri alır. Turbo payı olmayan işlemcide sınırlanacak yer yoktur, otomatik ayar dokunmaz.
- **Ekran kartı kullanımı:** NVIDIA'da NVML, AMD/Intel'de Windows'un "GPU Engine" sayaçları (Görev Yöneticisi ile aynı kaynak) kullanılır; oyunu neyin sınırladığı her markada bulunabilir.
- **İşlemci sıcaklığı:** Windows'un ACPI/termal bölgesinden okunur (yönetici gerekir). Okunamazsa rapor bunu söyler ve ısıya dayalı otomatik ayar yapılmaz.
- **Pil, ekran kartı belleği, disk:** Rapor pilde oynamayı, 4 GB'lık kartlarda ekran kartı belleğinin dolmasını ve oyunun HDD'de durmasını ayrıca bulur.

## Bilinen kısıtlar

- İşlemci voltajı (undervolt) ve güç sınırı yazma: çekirdek sürücüsü gerektirir, çoğu sistemde BIOS tarafından kilitlidir; desteklenmez.
- Fan eğrisi: üretici fan eğrisi cihazlarını yazılıma açmayan modellerde yoktur; yerine "sıcaklık sınırı" sunulur.
- Oyun üstü gösterge: "özel tam ekran" modda görünmeyebilir; kenarlıksız/pencereli modda çalışır.
- FPS: Vulkan/OpenGL oyunlarında ölçülemez.
