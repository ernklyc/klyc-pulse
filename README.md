<div align="center">

<img src="src/Pulse.App/logo.png" alt="KLYC-Pulse" width="96" />

# KLYC-Pulse

**Windows oyun bilgisayarları için tek pencerede performans, ısı, pil ve bakım yöneticisi.**
Yaptığı her ayarın gerçekten tuttuğunu kontrol eder.

[![Sürüm](https://img.shields.io/github/v/release/ernklyc/klyc-pulse?color=black&label=s%C3%BCr%C3%BCm)](https://github.com/ernklyc/klyc-pulse/releases)
[![Derleme](https://github.com/ernklyc/klyc-pulse/actions/workflows/build.yml/badge.svg)](https://github.com/ernklyc/klyc-pulse/actions/workflows/build.yml)
![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8-512BD4)
![Lisans](https://img.shields.io/badge/lisans-MIT-black)

**Türkçe** · [English](README.en.md)

<img src="docs/img/home.png" alt="Ana ekran" width="780" />

</div>

---

## İçindekiler

[Neden](#neden) · [Özellikler](#özellikler) · [İndir ve kur](#indir-ve-kur) · [İlk kullanım](#ilk-kullanım) · [Uyumluluk](#uyumluluk) · [Güvenlik ilkeleri](#güvenlik-ilkeleri) · [SSS](#sık-sorulan-sorular) · [Sorun giderme](#sorun-giderme) · [Kaynaktan derleme](#kaynaktan-derleme) · [Kaldırma](#kaldırma) · [Katkı](#katkı) · [Lisans](#lisans)

## Neden?

Oyun bilgisayarlarında aynı işler için ayrı ayrı programlar gerekir: performans profili için G-Helper / Armoury Crate, izleme ve ayar için MSI Afterburner / ThrottleStop, sürücüler için üretici yardımcıları, temizlik için bir optimizasyon aracı. **KLYC-Pulse bunların günlük işlerini tek uygulamada toplar.** Amaç, oyun sırasında bilgisayardan en iyi verimi almak ve donanım ömrünü korumaktır.

Tasarım ilkesi: **uygula → geri oku → doğrula.** Bir ayar yazıldıktan sonra yeniden okunur. Okunamayan şeyler (örneğin bazı pil sınırları) dürüstçe "kabul edildi" diye yazılır, "doğrulandı" diye değil.

## Özellikler

| Sayfa | Ne yapar |
|---|---|
| **Ana ekran** | 4 mod: **Oyun, Günlük, Sessiz, Boşta**. Tek tıkla ASUS profili, işlemci ek hızı (turbo), üst sınır, hız/güç dengesi (prizde ve pilde), ekran hızı (Hz), parlaklık ve ekran kartı hız sınırı uygulanır; her adım kontrol edilir. Canlı sıcaklık/yük kartı. **Hızlandır**: geçici dosya temizliği + bellek rahatlatma + önce/sonra raporu. |
| **Oyunlar** | Oyun açılınca modu kendiliğinden değiştirir, kapanınca eskisine döner. Oyun başına profil, ekran kartı tercihi (NVIDIA'da çalışsın), süreç önceliği. Windows'un oyun ayarlarını (HAGS, Oyun Modu, oyun kaydı) denetler ve düzeltir. **Oyun raporu:** oyun boyunca ısı, işlemci hızı, ekran kartı kısılması, bellek, FPS ve **arka plandaki programların yükü** (örn. "Chrome oyun boyunca %10 işlemci kullandı") kaydedilir; kapanınca "neden takıldı?" sade dille söylenir, oyunu neyin sınırladığı gösterilir ve uygunsa oyun başına işlemci hızı sınırı **kendiliğinden ayarlanır** (sonraki oturumla karşılaştırılır, FPS belirgin düşerse geri alınır ve kilitlenir). |
| **Temizlik** | Zararsız geçici dosyaları bulur ve siler; riskli olanlar silinmeden önce 7 gün karantinada bekler. Tarayıcı, Steam, Epic, Discord ve NVIDIA kurulum artıkları. **Kopya dosyalar:** birebir aynı büyük dosyaları bulur, her grupta en eskisini korur, fazlalıkları onayınla Geri Dönüşüm Kutusu'na gönderir. **Eski kalıntılar:** daha önce silinmiş uygulamaların AppData/ProgramData'da bıraktığı klasörleri temkinli bulur (kurulu uygulama, oyun, sistem klasörü ve yakın zamanda kullanılanlar hiç gösterilmez); hiçbiri kendiliğinden silinmez, seçtiklerin Geri Dönüşüm Kutusu'na gider. Disk analizi ve Geri Yükleme Noktaları. Hiçbir şeyi onaysız silmez. |
| **Süreçler** | Programları gruplayarak işlemci/bellek gösterir; kapat, öncelik ayarla, **Eko mod** (EcoQoS). Windows, güvenlik ve anti-hile süreçleri korumalıdır. |
| **Uygulamalar** | winget ile güncelleme, kaldırma (artık klasörler Geri Dönüşüm Kutusu'na), açılışta başlayanlar, arka plan servisleri ve zamanlanmış görevler. |
| **Sağlık** | Pil (aşınma, geçmiş), ısı, fan, disk sağlığı. Sorunları sade dille söyler. |
| **İzleme** | İşlemci/ekran kartı sıcaklık, hız, güç, bellek, fan; neden yavaşladığını söyler. **Oyun üstü gösterge ve FPS** (ETW; oyuna enjekte etmez, anti-hile ile çakışmaz). |
| **Sürücüler** | BIOS ve sürücü sürümleri; Windows Update'te gerçekten yeni olan sürücüleri tarar (yalnızca okur). |
| **Araçlar** | G-Helper, Afterburner, ThrottleStop, Intel DSA, PC Manager için "Pulse bunu yapıyor mu?" tablosu; kapatma/geri alma; ortak çalışma. |
| **Donanım** (Dizüstü sayfası) | Ekran kartı hızlandırma (NVIDIA, otomatik arama dahil), kısayol atama ve üretici modülleri: pil şarj sınırı, klavye ışığı ve RGB rengi (şu an ASUS modülü). |
| **Ayarlar** | Otomatik mod, ısı koruması, sıcaklık sınırı, mod koruyucu, otomatik temizlik. |

<p align="center">
  <img src="docs/img/izleme.png" alt="İzleme" width="380" />
  <img src="docs/img/oyunlar.png" alt="Oyunlar" width="380" />
</p>

**Koruma özellikleri:** *ısı koruması* (tehlikeli sıcaklık sürerse Sessiz moda geçer), *sıcaklık sınırı* (fan eğrisi yerine; işlemci/GPU'yu kademeli kısar), *mod koruyucu* (başka bir program güç ayarını bozarsa dakikada bir düzeltir).

## İndir ve kur

**[Releases](https://github.com/ernklyc/klyc-pulse/releases)** sayfasından birini indirin. **En kolayı kurulum dosyasıdır:**

| Dosya | Boyut | Ne zaman |
|---|---|---|
| `KLYC-Pulse-Setup-v1.7.1.exe` | ~80 MB | **Önerilen.** Çift tıkla kur: Başlat menüsü ve masaüstü kısayolu, "Uygulamalar"dan kaldırma. Hiçbir şey ayrıca kurmak gerekmez, yönetici izni istemez |
| `KLYC-Pulse-v1.7.1-win-x64.zip` | ~11 MB (zip) | [.NET 8 Masaüstü Çalışma Zamanı](https://dotnet.microsoft.com/download/dotnet/8.0) kuruluysa |
| `KLYC-Pulse-v1.7.1-win-x64-self-contained.zip` | ~68 MB (zip) | Hiçbir şey kurmak istemiyorsanız (her şey içinde) |

**Kurulum dosyasıyla:** çalıştırın, bitince Başlat menüsünden "KLYC-Pulse"u açın (uygulama kendisi yönetici izni ister). Kaldırırsanız ayarlarınız ve geçmişiniz (`%LOCALAPPDATA%\Pulse`) silinmez; yeniden kurunca kaldığınız yerden devam edersiniz. Silmek isterseniz o klasörü elle silin.

**Zip ile (kurulumsuz):**

1. Zip'i açın, `KLYC-Pulse.exe`'yi istediğiniz bir klasöre koyun (ör. `C:\Programlar\KLYC-Pulse`).
2. Çift tıklayın, **yönetici izni** isteyecektir (sensörler, ekran kartı ve servis ayarları için gerekir).
3. İsterseniz Ayarlar'dan "Bilgisayar açılınca Pulse de açılsın"ı açın.

> **Windows SmartScreen / antivirüs uyarısı:** Uygulama henüz dijital imzalı değildir, bu yüzden "Bilinmeyen yayıncı" uyarısı çıkabilir. Güvenmek için indirdiğiniz dosyanın SHA-256 özetini sürüm sayfasındaki `SHA256SUMS.txt` ile karşılaştırın ya da kaynaktan kendiniz derleyin.

```powershell
# özet kontrolü
Get-FileHash .\KLYC-Pulse-v1.7.1-win-x64.zip -Algorithm SHA256
```

## İlk kullanım

1. **Ana ekran**'dan bir mod seçin. Oyun için **Oyun**, günlük iş için **Günlük**.
2. **Oyunlar** sayfasında "Oyun açılınca modu kendiliğinden değiştir"i açın. Kurulu oyunlar ilk açılışta listeye eklenir.
3. **Dizüstü** sayfasından (destekleniyorsa) pil şarj sınırını seçin; bilgisayarı hep prizde kullanıyorsanız %60-80 pil ömrünü uzatır.
4. Bir sorun olursa **Kendini sınama**yı çalıştırın ([Sorun giderme](#sorun-giderme)).

## Uyumluluk

Her Windows 10 (2004+) / 11 x64 bilgisayarda çalışır; **donanıma özel adımlar donanım yoksa "atlandı" olarak görünür, hata vermez.**

| Özellik | Gerekli donanım |
|---|---|
| Temizlik, uygulamalar, süreçler, açılış/servis yönetimi, sürücü bilgisi, kısayollar | Herhangi bir Windows PC |
| Mod: güç planı (turbo, üst sınır, hız/güç dengesi), ekran Hz, parlaklık | Herhangi bir PC (parlaklık ekrana bağlı) |
| GPU izleme, hız sınırı, hızlandırma | NVIDIA ekran kartı |
| Üretici modülü: performans profili, pil şarj sınırı, klavye ışığı | Desteklenen markalar: şu an ASUS (ATKACPI sürücüsü). Diğer markalar için modül eklenebilir |
| Klavye RGB rengi | ACPI RGB sunan ASUS TUF modelleri |
| CPU sıcaklığı | ACPI termal bölgesi sunan sistem + yönetici |
| FPS | DirectX 10/11/12 oyunları |

Üretici modülleri isteğe bağlıdır: donanım yoksa ilgili adım "atlandı" olur ve geri kalan her şey çalışır. Ayrıntı için [docs/UYUMLULUK.md](docs/UYUMLULUK.md).

## Güvenlik ilkeleri

- **Silme her zaman onaylıdır.** Temizlik kategorileri yalnızca yeniden oluşan dosyalardır; riskli olanlar karantinaya gider ve geri getirilebilir.
- **Geri alınabilirlik.** Diğer programları kapatma sihirbazı yedek alır, "Geri al" ile eski haline döner; hiçbir program silinmez.
- **Sınırlı donanım ayarları.** Ekran kartı hızlandırma tavanı çekirdek +150 / bellek +700 MHz'dir, **kalıcı değildir** (mod değişince, uygulama kapanınca ya da yeniden başlatınca fabrika hızına döner).
- **Isı koruması.** Tehlikeli sıcaklık (işlemci 97 °C, ekran kartı 90 °C, 10 sn) sürerse Sessiz moda geçer.
- **Güvenlik ayarlarına dokunmaz** (Defender, Bellek Bütünlüğü vb.) ve çekirdek sürücüsü yüklemez.
- **Veri toplamaz.** İnternete yalnızca üç yerde bağlanır: (1) günde en fazla bir kez GitHub'dan en son sürüm numarasını sormak için (indirme/kurma yapmaz, kişisel bilgi göndermez; Ayarlar > Güncellemeler'den kapatılır), (2) sizin başlattığınız winget güncelleme kontrolü, (3) sizin başlattığınız Windows Update sürücü taraması.

## Sık sorulan sorular

**Neden yönetici izni istiyor?** İşlemci sıcaklığı, servis/görev yönetimi, ekran kartı ayarı ve FPS ölçümü Windows'ta yönetici yetkisi ister.

**Başka bir markada çalışır mı?** Evet. Temel özellikler (temizlik, süreçler, uygulamalar, güç planı modları, izleme) her PC'de çalışır. Üretici modülleri (şu an ASUS) yalnızca desteklenen markada etkinleşir; yeni marka modülleri katkıya açıktır.

**G-Helper / Armoury Crate açık olmalı mı?** Hayır. Pulse tek başına yeter. G-Helper'ı açık tutarsanız ayarı bozabilir; Pulse bunu fark edip düzeltir ya da Araçlar sayfasından kapatabilirsiniz.

**Undervolt / fan eğrisi var mı?** Hayır. Birçok sistemde BIOS bunları kilitler ve yazmak için çekirdek sürücüsü gerekir; Pulse güvenli olmayan yollara girmez. Yerine *sıcaklık sınırı* vardır.

**Ekran kartı hızlandırması güvenli mi?** Tavan düşüktür, kalıcı değildir ve "Otomatik bul" önce kararlılığı ve kazancı ölçer. Yine de görüntü bozulması görürseniz Dizüstü'nden "Fabrika"yı seçin.

## Sorun giderme

```
KLYC-Pulse.exe --selftest        # hızlı sınama, ~1 dk (canlı pencere)
KLYC-Pulse.exe --selftest=gpu    # + ekran kartı otomatik arama (~4 dk)
```

Sonuç `%USERPROFILE%\KLYC-Pulse-selftest.txt` dosyasına yazılır. İşlem günlüğü: `%LOCALAPPDATA%\Pulse\logs`. Hata bildirirken bu iki dosyayı ekleyin (kişisel bilgi içermez; yine de göndermeden önce göz atın).

## Kaynaktan derleme

Gereksinim: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Windows.

```powershell
git clone https://github.com/ernklyc/klyc-pulse.git
cd klyc-pulse
dotnet build -c Release
dotnet publish src/Pulse.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
# .NET'siz tek dosya için: --self-contained true
```

Donanıma dokunmayan mantık testleri:

```powershell
dotnet run --project src/Pulse.Cli -c Release -- guard-test
dotnet run --project src/Pulse.Cli -c Release -- governor-test
dotnet run --project src/Pulse.Cli -c Release -- hotkey-test
```

Mimari için [docs/MIMARI.md](docs/MIMARI.md).

## Kaldırma

1. Ayarlar'dan "Bilgisayar açılınca Pulse de açılsın"ı kapatın (zamanlanmış görevi siler).
2. Uygulamayı tepsiden **Çıkış** ile kapatın (yönetici izniyle çalıştığı için kaldırıcı onu kendisi kapatamaz).
3. Kurulum dosyasıyla kurduysanız Windows **Ayarlar → Uygulamalar**'dan "KLYC-Pulse"u kaldırın; zip ile kullandıysanız `KLYC-Pulse.exe`'yi silin.
4. İsterseniz `%LOCALAPPDATA%\Pulse` klasörünü silin (ayarlar, günlükler, karantina).

Donanım ayarları kalıcı değildir; yeniden başlatınca varsayılana döner. Pulse'ın değiştirdiği Windows güç planı değerleri için Windows'un "Güç planı"ndan varsayılanları geri yükleyebilirsiniz.

## Katkı

[CONTRIBUTING.md](CONTRIBUTING.md) dosyasına bakın. Hata bildirimi ve donanım uyumluluk raporları (özellikle farklı marka/model) çok değerlidir.

## Lisans

[MIT](LICENSE). Üçüncü taraf bileşenler ve yazı tipleri: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Güvenlik: [SECURITY.md](SECURITY.md).

> Bu yazılım donanım ayarlarını değiştirir. Kendi sorumluluğunuzda kullanın; garanti verilmez. Markalar (ASUS, NVIDIA, Intel, Microsoft, MSI, G-Helper, ThrottleStop) ilgili sahiplerine aittir; KLYC-Pulse bu şirketlerle bağlantılı değildir.
