# Değişiklikler / Changelog

Biçim [Keep a Changelog](https://keepachangelog.com/tr/) esaslıdır; sürümleme [SemVer](https://semver.org/lang/tr/).

## [1.6.1] — 2026-10-09

### Değişenler (Isı denemesi sonucuna göre)
- Gerçek ölçüm (ASUS TUF F15, i5-10300H, tam yük): sınırsız 3893 MHz / 82 °C → **3600 MHz sınırı: hız %8 azaldı, ısı 11,9 °C düştü**; 3300 MHz sınırı ısıyı fazladan yalnızca 0,3 °C düşürdü (hız %15 azaldı). Yani kazancın neredeyse tamamı **ilk kademede**.
- **Kademeler artık kısa süreli tepe hıza değil, ölçülen SÜREKLİ yük altı hıza göre kurulur** (yoksa tepe hızın üstündeki ilk kademeler hiçbir şey yapmıyordu). Sürekli hız Isı denemesinden ve sınırsız oynanan oyun raporlarından öğrenilir.
- **Akıllı oyun ayarı en hafif anlamlı kademeden başlar** (~%92 hız); ısı hâlâ yüksekse ve FPS güvendeyse sonraki kademeye iner.
- Isı hedefi / oyunda acil fren kademeleri ilk adımı ~%8 olacak şekilde yeniden ayarlandı.

### Düzeltilenler
- Kendini sınama: kısayollar Ayarlar'dan kapatılmışsa "kaldı" denmez.

## [1.6.0] — 2026-10-09

### Eklenenler
- **Oyunda yumuşak acil fren:** Oyun sırasında sıcaklık **95 °C üstünde 10 sn** kalırsa işlemci hızı ~300 MHz'lik küçük adımlarla kısılır (hedef ~92 °C), serinleyince kademeler tek tek geri verilir. Donanımın ~100 °C'deki ani ve sert kısmasından önce, FPS'i pürüzsüz tutmak için. Normal sıcaklıkta oyunda hiçbir şeye dokunmaz; ani Sessiz mod geçişi yok. Kullanıldıysa oyun raporu bunu söyler.
- **Isı denemesi** (`--selftest=heat`, masaüstündeki "Isı Denemesi" kısayolu): İşlemci hız sınırının sıcaklığı bu bilgisayarda gerçekten ne kadar düşürdüğünü ve hızı ne kadar azalttığını ölçer (sınırsız / iki kademe, her biri 80 sn tam yük, ~6 dk). Sonuç `%USERPROFILE%\KLYC-Pulse-selftest.txt` dosyasına yazılır.

### Değişenler
- Bildirimlerin varsayılan köşesi **sağ üst** oldu (Ayarlar'dan değiştirilir; kendin seçtiysen seçimin korunur).

## [1.5.0] — 2026-10-09

### Eklenenler
- **Soğutma önceliği ("önce soğut, sonra yavaşlat"):** Sıcaklık uzun süre yüksek kalırsa (88 °C üstü, 15 sn) önce **fan desteği** açılır (ASUS Turbo profili; bu bilgisayarda aynı yükte fan devri %14 arttı: 4900 → 5600). Fan yetmezse (93 °C üstü, 40 sn) işlemci hızı kademeli düşürülür; serinleyince aynı yoldan geri döner. Oyun modunda işlemciyi kısmaz (yalnız fan). Ayarlar'dan kapatılır. Isı bekçisi (97 °C) son çare olarak çalışmaya devam eder.
- Windows "sistem soğutma ilkesi" (varsa): Oyun/Günlük modunda Etkin (önce fan), Sessiz/Boşta modunda Pasif.

## [1.4.0] — 2026-10-09

### Eklenenler
- **Hızlı tur:** İlk açanlar için 6 adımlık, atlanabilir tanıtım (modlar, oyunlar, gösterge, temizlik, gizlilik). Ayarlar'dan yeniden açılır.
- **Tıklanabilir bildirimler:** Oyun bittikten sonra çıkan bildirimlere (oyun raporu, yeni sürüm) tıklayınca ilgili sayfa açılır. Oyun içi uyarılar tıklamayı geçirir, oyuna karışmaz.
- **Bildirim köşesi seçilebilir** (varsayılan sağ alt); gösterge aynı köşedeyse bildirim yanına/üstüne yerleşir.
- **Oyunda gösterge kendiliğinden açılır** (Ayarlar'dan kapatılır) ve oyun kapanınca kapanır.
- **Ayarlar kaybolmasın:** Her kayıtta otomatik yedek (`settings.json.bak`); dosya bozulursa son yedekten geri yüklenir, bozuk dosya ayrıca saklanır. Ayarlar > "Ayar klasörünü aç".

### Düzeltilenler
- Oyun raporu: ekran kartının "Güç sınırı"na ulaşması dizüstülerde normal olduğu için sorun sayılmaz (ısı sınırı hâlâ uyarıdır).
- Oyun raporu: %1 düşük FPS 60'ın üstündeyse "takılma var" denmez, "en kötü karelerde bile akıcı" denir.

## [1.3.0] — 2026-10-09

### Eklenenler
- **Güncelleme denetimi:** Günde en fazla bir kez GitHub'dan en son sürüm numarasını sorar; yeni sürüm varsa küçük bir uyarı ve Ana ekranda "Sürüm notları ve indir" kartı çıkar (indirmeyi kullanıcı başlatır, uygulama kendisi indirmez). Ayarlar > Güncellemeler'den kapatılır. Yalnızca github.com adreslerini açar.
- **Her bilgisayara uyum:** Frekans sınırı denemesi (bu bilgisayarda sınır gerçekten uygulanıyor mu, ölçülür; uygulanmıyorsa hız sınırı özellikleri kapanır), hibrit Intel için performans çekirdeği sınıfı da yazılır, sınır kademeleri bu bilgisayarın gerçek hızından türetilir (sabit 3,5 GHz yok), NVIDIA dışı ekran kartlarında Windows GPU sayaçları, ısı okunamazsa bunu söyleme.
- **Oyun raporu:** pilde oynama, ekran kartı belleğinin (VRAM) dolması ve oyunun HDD'de durması da bulunur. Oyun başlarken pildeysen uyarır.
- Mod Koruyucu ve Isı hedefi, kısa süreli güç ayarı denemeleri sırasında bekletilir.

## [1.2.0] — 2026-10-08

### Eklenenler
- **Akıllı oyun ayarı (oyuna göre kendini ayarlama):** Her oyun oturumundan sonra rapor incelenir. İşlemci çok ısınıyor ve oyunu ekran kartı/kare sınırı belirliyorsa işlemci hız sınırı bir kademe düşürülür (3,5 → 3,2 → 3,0 GHz); sonraki oturumda ısı ve FPS önceki oturumla karşılaştırılır. FPS %7'den fazla düşerse (FPS ölçülemiyorsa ekran kartı kullanımı 8 puan düşerse) ayar geri alınır ve o oyun için kilitlenir; yararlıysa ve ısı hâlâ yüksekse bir kademe daha iner; ısı normale dönünce kilitlenir. Oyunu işlemci sınırlıyorsa dokunmaz. 8 dakikadan kısa oturumlarda karar vermez. Elle seçilen sınıra dokunmaz. Genel ve oyun başına kapatılabilir.
- Oyun algılama genişledi: elle eklenen (ya da önceden öğrenilen) oyunlar klasör kuralına uymasa da algılanır; `\Games\`, `\SteamLibrary\`, `\Battle.net\Games\`, `\Blizzard\` klasörleri eklendi.

## [1.1.0] — 2026-10-08

### Eklenenler
- **Oyun raporu:** Oyun boyunca ısı, işlemci hızı, ekran kartı kısılması, bellek ve FPS kaydedilir; oyun kapanınca Oyunlar sayfasında "neden takıldı?" sade dille gösterilir. Oyunu işlemcinin mi ekran kartının mı sınırladığı da söylenir.
- **Eski kalıntılar:** Daha önce silinmiş uygulamaların AppData/ProgramData'da bıraktığı klasörleri temkinli bulur. Kurulu uygulama, yayıncı, çalışan süreç/servis, Store paketi, Başlat Menüsü, Steam/Epic oyunu, sistem klasörü ve son 90 günde kullanılanlar hiç gösterilmez. Hiçbir şey kendiliğinden silinmez; seçilenler onayla Geri Dönüşüm Kutusu'na gider.
- **Oyun başına işlemci hızı sınırı:** Oyunlar listesinde her oyuna "İşlemci hızı" seçilir (3,8 / 3,5 / 3,2 / 3,0 GHz). Rapor, ekran kartı oyunu sınırlarken işlemci çok ısındıysa bunu önerir ve tek tıkla uygular; bir sonraki oturumda ısı ve FPS değişimini önceki oturumla karşılaştırır.
- Rapor, oyun ekranın hızından fazla FPS üretip ısındıysa FPS sınırı önerir; oturum geçmişi (son 20) tutulur.
- **Kopya dosyalar:** Belgeler, Masaüstü, Resimler, Videolar, Müzik, İndirilenler içinde birebir aynı büyük dosyaları bulur (boyut → kısmi özet → tam özet). Her grupta en eski kopya korunur; seçilen fazlalıklar Geri Dönüşüm Kutusu'na gider. Buluttan indirilmemiş (OneDrive) dosyalara dokunmaz.
- Yeni temizlik kategorileri: Firefox, Steam, Epic Games Launcher, Discord kod önbelleği, NVIDIA sürücü kurulum artıkları, LiveKernelReports ve kullanıcı hata raporları.

### Değişenler
- **Sıcaklık sınırı artık frekansla çalışır:** İşlemcinin en yüksek hızını ~300 MHz'lik küçük adımlarla düşürür. Eski yöntem (üst sınır %) Windows'ta turbo'yu tamamen kapatıyordu (bu bilgisayarda ölçüldü: %99'da 4,1 → 2,5 GHz tek adımda); yeni yöntem 3000 MHz sınırında 2995 MHz, 2400 MHz sınırında 2300 MHz ölçüldü. Sınır, mod değişince ve uygulama kapanınca/açılınca temizlenir.
- Her mod, "frekans sınırı yok"tan başlar.
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