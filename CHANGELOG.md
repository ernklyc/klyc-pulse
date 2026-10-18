# Değişiklikler / Changelog

Biçim [Keep a Changelog](https://keepachangelog.com/tr/) esaslıdır; sürümleme [SemVer](https://semver.org/lang/tr/).

## [1.13.0] — 2026-10-18

### Düzeltilenler (oyun performansı ve hafiflik)
- **Oyunda işlemci frenlemesi artık FPS öncelikli:** gerçek kayıtlarda (FC25, Forza Horizon 4) oyun sırasında işlemci 95 °C'yi 10 sn geçince hız 3,8 → 3,3 GHz'e kısılıyordu; işlemciyi sınırlayan oyunlarda bu doğrudan FPS kaybıydı. Soğutma önceliğinin oyun içi "acil freni" şimdi yalnızca donanımın kendi kısmasına (~100 °C) çok yakınken devreye girer: **98 °C ve 15 sn**, hedef 96 °C, bırakma 93 °C. Isı bekçisi oyunda zaten yalnızca uyarıyordu (mod değiştirmez).
- **Arka plan sensör okuması ~%60 daha ucuz:** NVIDIA sürücüsünün "kısılma nedeni" çağrısı okuma başına ~16 ms işlemci harcıyordu (diğer 6 çağrının ~4 katı). Artık 5 okumada bir sorulur (nedenler saniyelerce sürer; oyun raporu etkilenmez). Ölçüm (`pulse-cli sensor-bench`): okuma başına 26 ms → ~9–14 ms; Pulse arka planda saniyede bir okuduğu için sürekli yük aynı oranda düşer.
- **Mod koruyucu artık süreç başlatmıyor:** güç planı okumaları (etkin plan, prizde/pilde değerler) her seferinde ayrı bir `powercfg.exe` süreci açıyordu (~39 ms ve bir süreç) ve "Modu koru" ayarı (varsayılan açık) bunu dakikada bir birkaç kez yapıyordu; oyun sırasında bile. Artık doğrudan `powrprof.dll` ile okunur (0,12 ms, ~300 kat hızlı; `powercfg-native-test` çıktıyı `powercfg.exe` ile karşılaştırır). Yazma yolları değişmedi; yerel okuma başarısız olursa eski yönteme düşer.
- **Windows ile başlat görevi kendini onarır:** görev, artık var olmayan bir exe'yi gösteriyorsa (taşınabilir exe silinip Velopack kurulumuna geçilince bu olmuştu ve Pulse Windows ile hiç açılmıyordu) yönetici olarak ilk açılışta görev çalışan exe'ye yönlendirilir. Exe hâlâ varsa dokunulmaz.
- **Riot başlatıcısı oyun sayılmıyor:** `RiotClientServices` "oyun" diye algılanınca asıl oyun (VALORANT-Win64-Shipping) arka plan yükü görünüyor ve raporlar yanlış oyuna yazılıyordu.
- **Konsol'dan başlatılan Steam/Epic oyunlarında mod gidip geliyordu:** Konsol oyunu bekçiye devredince hemen "oynuyor" der; Pulse bu ipucuyla "süreç gelsin" bekleme süresini siliyordu, oyun süreci ~25–30 sn sonra gelince mod önce Günlük'e dönüp sonra yeniden Oyun'a geçiyordu. Gerçek Hades denemesinde görüldü ve düzeltildi (testle).

### Eklenenler
- `pulse-cli sensor-bench`: sensör okumasının işlemci maliyetini parça parça ölçer (salt okunur).
- **Sistem denetimi (salt okunur, Hızlandır'ın 9. adımı ve `pulse-cli system-audit`):** sayfa dosyası (elle sabitlenmiş ve küçükse), ekran kartı belleği taşması, işlemcinin sık 95 °C üstüne çıkması (toz/macun/altlık önerisi), oyun sırasında arka plan işlemci yükü (oyunun kendi süreçleri ve başlatıcılar sayılmaz), FPS sınırı önerisi, yanlış Windows oyun ayarları ve VBS bilgisi. Hiçbir ayarı değiştirmez; nedenini söyler. Konsol'un Ayarlar → PULSE → "Sistem denetimi" satırında da görünür (`konsol-feed.json` → `audit`).
- **Gösterge (overlay) A/B ölçümü:** oyun raporu göstergenin açık olup olmadığını kaydeder; aynı oyunun göstergeli/göstergesiz oturumları (≥ 8 dk, karşı durumdan son 3'ün ortalaması) ortalama FPS ve %1 low olarak karşılaştırılıp rapora yazılır (fark %3'ün altındaysa "belirgin fark yok"). Göstergeyi kendisi kapatmaz.

## [1.12.0] — 2026-10-18

### Eklenenler
- **Konsol'dan daha fazlasını yönet (izin açıksa):** mod ve Hızlandır'a ek olarak **Temizlik** (zararsız geçici dosyalar), **oyun açılınca otomatik mod** (aç/kapat), **ısı hedefi** (Kapalı / 75 / 80 / 85 / 90 °C) ve **fan desteği** (aç/kapat). Her eylemin değer listesi dardır (`on`/`off`, sabit sıcaklık listesi); başka değer ya da eylem reddedilir. Reddedilen komutun sonucu artık kendi kimliğiyle Konsol'a döner (gerçek ret nedeni görünür, "yanıt yok" değil). Özete `heatTarget` eklendi. `konsol-cmd-test` 46 → 59 denetim.
- Not: bu ayar KAPALI başlar (Ayarlar → "Konsol'dan mod değiştirmeye izin ver").
- **Hızlandır artık ölçüp somut söylüyor:** (1) Windows oyun ayarlarından yanlış olanları (arka plan oyun kaydı, Oyun Modu) **düzeltir** — yalnızca Pulse'un kendi Hızlandır düğmesinde; Konsol'dan gelen "boost" komutu hiçbir ayarı değiştirmez; her yazma geri okunarak doğrulanır, eski değer günlüğe yazılır, yönetici gerektiren ayar yönetici değilken atlanır; (2) **arka plan işlemci yükü** 2 sn örneklenir ve en çok yük bindiren üç program adıyla söylenir (kapatmaz; Pulse, Konsol ve önde çalışan uygulama sayılmaz); (3) yönetici olarak çalışıyorsa **açılış süresi analizi** sonuca eklenir (açılış öğeleri değiştirilmez). Adım sayısı 7 → 8. `boostbg-test` eklendi (CI listesinde); `pulse-cli boost-probe` aynı ölçümleri salt okunur çalıştırır.
- **Konsol'dan oyun başına mod:** Konsol'un oyun ayrıntılarındaki "Bu oyun için Pulse modu" satırı için yeni `gamemode` komutu: Pulse'un **var olan** oyun profilinin modunu değiştirir ya da o oyunda otomatik geçişi kapatır (profil yoksa yenisi uydurulmaz; oyun bir kez oynanınca oluşur). Özet (`konsol-feed.json`) `gameProfiles` listesini taşır (yalnızca komutlara izin açıkken).

### Düzeltilenler (gerçek kullanım günlüklerinden)
- **Ekran kartı saat listesi bazen okunamıyordu** (açılışta ve Boşta moduna geçişte "Failed"): kart uykudayken / sürücü meşgulken ilk okuma boş dönüyordu. Artık yarım saniye arayla 4 kez yeniden denenir; yine okunamazsa kırmızı hata değil, "şu an okunamadı, sonraki mod değişiminde yeniden denenir" uyarısı olur.
- **Yenileme hızı doğrulaması:** ekran yeni uyandığında 180 Hz yerine 60 Hz okunup adım "Failed" oluyordu; artık bekleyip yeniden yazar ve yeniden okur (3 tur).
- **Isı uyarıları seyrekleşir:** uzun süre sıcak kalınca aynı uyarı 10 dk'da bir tekrarlanıyordu (kayıtlarda 15 uyarı); şimdi aralık her uyarıdan sonra iki katına çıkar (10, 20, 40, 60 dk; 3 saatte 5 uyarı, eskiden 18) ve gerçekten serinleyince sıfırlanır. Uyarı, Sıcaklık sınırı kapalıysa bunu önerir.
- **CLI testleri artık gerçek günlüğü kirletmiyor** (`Ayarlar okunamadı…` satırları test gürültüsüydü): test günlükleri geçici klasöre yazılır (`KLYC_PULSE_LOGDIR`).

## [1.11.0] — 2026-10-12

### Eklenenler
- **Konsol'dan mod değiştirme ve Hızlandır (Pulse ↔ Konsol "Birlikte"):** Ayarlar → "Konsol'dan mod değiştirmeye izin ver" (varsayılan **KAPALI**). Açıkken KLYC Konsol'un Ayarlar → PULSE satırlarından Oyun/Günlük/Sessiz/Boşta modu seçilir ve Hızlandır çalıştırılır; sonuç Konsol'da bildirilir. Kanal bilerek dardır (yalnızca bu iki eylem; program çalıştıran hiçbir komut yoktur) ve her komut sahip, boyut, bağlantı noktası, yaş (20 sn), kimlik tekrarı, hız sınırı ve izin listesi denetiminden geçer; ayrıntı: `docs/MIMARI.md`. Test: `konsol-cmd-test` (59 denetim; junction ve yabancı sahip dahil), CI'da.
- **Konsol üst çubuğu için canlı değerler:** Konsol isterse (ve yalnızca Konsol ekrandayken) özete işlemci/ekran kartı ısısı ve yükü yazılır; Konsol gizlenince ya da oyun başlayınca sensör aboneliği bırakılır.
- **Sorun bildir:** Ayarlar'dan tek tuşla kişisel veri içermeyen tanı paketi (günlük, ayar bayrakları, sürüm; kullanıcı adı/bilgisayar adı/yol temizlenir) masaüstüne yazılır; kendiliğinden hiçbir yere gönderilmez.
- **Araçlar → KLYC Konsol kartı:** Konsol kuruluysa durumu, "Pulse ile birlikte başlat" ve Konsol'u aç; kurulu değilse indirme bağlantısı.
- **Haftalık özet** (Sağlık → "Bu hafta": oyun süresi, ortalama FPS, en sıcak oyun, yapılan temizlik), **açılış süresi analizi** (Uygulamalar → hangi başlangıç öğesi açılışı ne kadar uzatıyor), **oyun başına FPS sınırı önerisi** (Oyunlar).
- **Disk sağlığı nedenleri:** "Dikkat/Sorun" denildiğinde SMART'taki hangi değerin neden uyardığı yazılır. Açılış öğesi kapatma artık geri okunarak doğrulanır; korunan öğeler için neden gösterilir. Kaldırma/güncelleme uyarıları iyileştirildi.
- **Isı denemesi:** tam deneme (sınırsız, 3200 ve 2700 MHz sınırı, işlemci + ekran kartı) uçtan uca doğrulandı: hız sınırı gerçekten uygulanıyor, ayarlar eski haline dönüyor; metinler çevrildi, eski oyun raporlarının metni dile göre yeniden çevrilir.

### Düzeltilenler
- Isı denemesi sonunda Edge'in yardımcı süreçleri profil klasörünü kilitli tutarsa geçici yük klasörü (`pulse-gputune`) geride kalabiliyordu: silinene kadar yeniden denenir (gerçek denemede doğrulandı).

## [1.10.1] — 2026-10-12

### Düzeltilenler
- **KLYC Konsol ile kısayol çakışması:** Klavye ışığı eylemi varsayılan olarak Ctrl+Alt+K'yı kullanıyordu; bu, Konsol'un genel aç/gizle kısayoludur (Windows ile ikisi birden açılınca ilk açılan tuşu alır, öteki sessizce kaybederdi). Varsayılan Ctrl+Alt+B oldu; eski varsayılan kayıtlı atamalar da otomatik taşınır.
- **KLYC Konsol süreçleri korumalı:** "Açık uygulamaları kapat" ve Süreçler sayfası KlycKonsol ve KlycGuard'ı artık kapatamaz (kol arayüzünü yanlışlıkla kapatmak, kolla kullanılan bir bilgisayarı kilitlenmiş gibi bırakırdı).

## [1.10.0] — 2026-10-12

### Eklenenler
- **KLYC Konsol ile birlikte çalışma** (kardeş proje, isteğe bağlı): Konsol kuruluysa Pulse onun `status.json` dosyasını (şema 1 ve 2) olay tabanlı izler, yoklama yapmaz.
  - Konsol bir oyunu başlatırken Pulse oyun profilini **oyun penceresi açılmadan** uygular; süreç 180 sn içinde görünmezse uygulanan modu geri alır.
  - Konsol'un söylediği kurulum klasöründeki süreç, klasör kuralına (`\steamapps\common\`, `\Epic Games\`…) uymasa da oyun sayılır ve profile eklenir.
  - Konsol güç planını yönetiyorsa (Pulse'ın otomatik oyun modu kapalıyken ya da Pulse yokken) mod koruyucu o oyun boyunca bekler (çakışma biter); oyun bitince yeniden çalışır. Otomatik oyun modu açıkken plan Pulse'ta kalır.
  - Konsol oyunları: oyun sürerken güncelleme (winget) yapılmaz, oyun bitince yapılır (oyunu takıltmasın); süreç listesinde pencere bilgisi tek geçişte toplanır.
  - Pulse, Konsol'a `konsol-feed.json` özeti yazar (çalışıyor işareti, mod, son oyun raporu); Konsol yoksa hiçbir şey yazılmaz, oyun sırasında da yazılmaz.
  - Ayarlar → "KLYC Konsol ile birlikte çalış" (varsayılan açık, kapatılabilir) ve durum satırı. Komut kutusu (Konsol'dan mod değiştirme) bilerek yapılmadı.
- Test: `konsol-link-test` (ayrıştırma, bayat/bozuk dosya, atomik yazım, oyun ipuçları; sahte saat ve sahte mod anahtarı ile donanıma dokunmaz); CI'da.
- `AutoModeService` artık `IModeSwitch` üzerinden çalışır (testlerde sahte mod anahtarı kullanılabilir).

## [1.9.3] — 2026-10-11

### Düzeltilenler
- **Sekme değiştirince işlemler durmuyor/baştan başlamıyor:** Sayfalar artık yok edilip yeniden kurulmuyor; aynı sayfa ve içindeki durum korunur. Görünmezken yalnızca ölçüm ve yenileme zamanlayıcıları durur (İzleme sensör aboneliğini bırakır), geri gelince kaldığı yerden devam eder. Oyunlar sayfasında süren hız denemesi, Süreçler'de süren kapatma gibi işlemler sekme değişince kaybolmaz.
- **Hatalar artık görünür:** Temizlik, Uygulamalar, Sürücüler, Araçlar ve Dizüstü sayfalarında bir işlem hata verirse ekranda "Hata: …" yazar, işlem çubuğu ve düğmeler takılı kalmaz (eskiden hata sessizce yutuluyordu). Mod uygulanırken beklenmeyen hata olursa Ana ekran "uygulanıyor"da takılmaz.
- **winget:** Aynı anda yalnızca bir winget işlemi çalışır (iki güncelleme üst üste açılıp biri düşmesin). Güncelleme sürerken "Yenile" listeyi silemez; arka plan sekmesine art arda basınca aynı tarama üst üste başlamaz.
- **Disk analizi:** Profil içindeki AppData gibi başka bir kökü kapsayan klasörler iki kez sayılıp listelenmiyor; OneDrive'ın "yalnızca çevrimiçi" dosyaları diskte yer kaplamadığı için sayılmıyor; klasörler paralel taranıyor (gerçek diskte ~7 sn). Testi eklendi (`diskscan-test`).
- **Temizlik taraması/silmesi** iş parçacığını düşük öncelikli yapıp işi bitince eski önceliğe döndürür (eskiden öncelik havuzda kalıyordu).
- Sağlık sayfasında pil yoksa "Yok" yazısı İngilizce arayüzde de çevrilir.

## [1.9.2] — 2026-10-11

### Düzeltilenler
- **Oyun başlatıcısı güncellemesi:** Epic Games Launcher gibi bazı başlatıcıların kurulum paketi yükseltirken eskisini kaldırıp yeniden kuruyor; başlatıcı birkaç dakika kayboluyordu (oyun dosyaları silinmez). Oyun başlatıcıları (Epic, Steam, Rockstar, EA, Ubisoft, GOG, Battle.net...) artık listede uyarı notuyla işaretlenir, "Tümünü güncelle"ye girmez, tek tek güncellerken onay sorulur.
- **Güncelleme sonrası doğrulama:** winget "başarılı" dese de Pulse paketin hâlâ kurulu olduğunu ve sürümünü yeniden okuyup kontrol eder; eksikse uyarır.
- **winget tablosu ayrıştırma hatası:** Ad sütunu tam dolunca (ör. "Microsoft Visual C++ 2013 Redistributable (x86) - 12.0.30501") sürüm numarası paket kimliği sanılıp yanlış kimlikle yükseltme deneniyordu. Sütunlar artık başlık satırındaki konumdan okunur. Testi eklendi.

## [1.9.1] — 2026-10-11

### Düzeltilenler
- **Sekme değiştirince işlem durmuş/sonuç kaybolmuş görünmesi:** Sayfalar artık bellekte tutulur. Temizlik taraması, güncelleme, disk analizi gibi süren işler başka sekmeye geçip dönünce kaybolmaz; sonuç yerinde durur. Canlı ölçüm yapan sayfalar (İzleme, Sağlık, Süreçler, Oyunlar, Ayarlar) ekrandan çıkınca ölçmeyi bırakır, dönünce yeniden başlar (arka planda boşuna iş yapmaz).
- **Ekranın "nefes alması" ve beklenmedik açılan listeler:** Süreçler sayfasında satırlar her 2 saniyede yer değiştirdiği için tıklama başka satıra denk gelip öncelik listesini aniden açabiliyordu. Canlı sıralama kapatıldı; fare listedeyken sıra sabit kalır, değilken birkaç saniyede bir toplu sıralanır.
- **Kayan sayfa düzeni:** İşlem çubuğu (tarama, güncelleme vb.) artık sayfanın üstünde yüzer; açılıp kapanınca altındaki kartlar aşağı-yukarı kaymaz. Ana ekran mod kartında "AKTİF" yazısı ve düğme genişliği için yer ayrıldı, aktif çerçeve kalınlaşırken kart ölçüsü değişmez.

## [1.9.0] — 2026-10-11

### Eklenenler
- **Açık uygulamaları kapat** (PC Manager benzeri, gerçekten çalışan): Ana ekran düğmesi ve tepsi menüsü. Bellek yiyen açık uygulamaları listeler; Discord/OneDrive/Widgets gibi zararsızlar **önerilen** (ön seçili), tarayıcılar **isteğe bağlı**, Word/kod editörü/OBS gibi kayıp riski olanlar **dikkat**. Sistem ve güvenlik süreçleri, donanım/sürücü yardımcıları (ASUS, NVIDIA, Intel, Realtek, Logitech...), ekrandaki uygulama, çalışan oyun ve oyun başlatıcıları listeye girmez. Pencereli uygulamalar nazikçe kapatılır (kaydedilmemiş iş için uygulama sorar, zorla kapatılmaz; ayrı onayla zorla kapatılabilir), pencerisiz arka plan programları cevap vermezse sonlandırılır. Sonuç yeniden okunarak doğrulanır ve boşalan bellek ölçülür.
- **Masaüstü ve dizüstü uyumu:** Bilgisayar türü Windows kasa türünden algılanır (dizüstü, tablet, masaüstü, hepsi bir arada, mini bilgisayar). Masaüstünde yanlış "Pil algılanmıyor" uyarısı çıkmaz, pil/klavye ışığı/"fişi çekince" ayarları gizlenir, "Dizüstü" menüsü "Donanım" olur, harici monitör için parlaklık adımı atlanır.
- Testler: `openapps-test` (sahte süreç listeleri + gerçek pencereli/pencerisiz/kapanmayı reddeden sahte uygulamalar), `device-test`; ikisi de CI'da. `pulse-cli openapps` adayları hiçbir şey kapatmadan yazar.

## [1.8.1] — 2026-10-10

### Düzeltilenler
- **Boşta modunda ekran 1 dk'da kararıyordu** (bilgisayar başındayken bile rahatsız ediyordu). Artık fişteyken ekran kapanma ve uyku **30 dakika** sonra devreye girer. Mod kapanınca eski ekran/uyku ayarları aynen geri yüklenir.

## [1.8.0] — 2026-10-09

### Eklenenler
- **İngilizce arayüz (beta):** Ayarlar > Dil'den seçilir; "Otomatik" ise Windows Türkçe değilse İngilizce açılır. Menüler, başlıklar, açıklamalar, düğmeler, mod kartları, canlı durum, oyun raporu cümleleri, otomatik ayar notları, sağlık bulguları, temizlik kategorileri, uygulama/süreç/sürücü sayfaları, tur, tepsi menüsü ve bildirimler çevrildi (~890 metin). Sürücü adı gibi dışarıdan gelen teknik metinler özgün kalır. Dil değişikliği uygulamayı yeniden açınca geçerlidir. Çeviriler `src/Pulse.Core/Localization/*.tsv` dosyalarındadır; `loc-test` kaynak koddaki her çevrilen metnin ve XAML'deki her sabit metnin İngilizcesi olduğunu ve `{0}` yer tutucularının tuttuğunu denetler (CI'da çalışır).
- **Destek:** Ayarlar'da "Bir kahve ısmarla" kartı ve GitHub "Sponsor" düğmesi (Buy Me a Coffee). Hiçbir özellik buna bağlı değil.

### Değişenler
- Dil, hiçbir servis kurulmadan en başta belirlenir (`Loc.ConfigureEarly`): statik metin dizileri ve ilk açılan pencereler doğru dilde oluşur. `KLYC_PULSE_LANG=en|tr` ortam değişkeni dili zorlar (sınama için).
- Ekran kartı yük sayfası (`GpuLoad`) ve ısı denemesi 1.7.0'dan beri ortak; bu sürümde değişmedi.

## [1.7.1] — 2026-10-09

### Eklenenler
- **Kurulum dosyası (`KLYC-Pulse-Setup-v1.7.1.exe`, Velopack):** Çift tıkla kurulur (yönetici izni istemez), Başlat menüsü ve masaüstü kısayolu oluşturur, Windows "Uygulamalar"dan kaldırılır. Kaldırınca ayarlar ve geçmiş (`%LOCALAPPDATA%\Pulse`) silinmez. Gerçek kurulum/kaldırma bu bilgisayarda denendi: kısayol, kayıt ve klasör eksiksiz oluşup eksiksiz silindi; kısayoldan açınca uygulama yönetici izniyle başladı. Velopack'in kurulum sırasındaki özel çağrısı yönetici gerektiren uygulamayı başlatamadığı için günlükte zararsız bir uyarı (`yükseltme gerekiyor`) yazar; kurulum etkilenmez. Kurulum dosyası henüz dijital imzalı değil (SmartScreen uyarısı çıkabilir).

### Değişenler
- Derleme klasörünün yolu (Windows kullanıcı adı) artık exe'nin içine yazılmıyor (`Directory.Build.props`, `PathMap`); sürümler yeniden üretilebilir derleniyor.

## [1.7.0] — 2026-10-09

### Eklenenler
- **Arka plan yükü ölçümü (kasma sebebi bulucu):** Oyun sırasında 5 sn'de bir çalışan programların işlemci, disk ve bellek kullanımı ölçülür (oyunun kendisi, yan süreçleri ve Pulse hariç; aynı adlı süreçler toplanır, örn. Chrome'un onlarca süreci tek satır). Oyun bitince raporda sade bulgu çıkar: *"Arka planda Google Chrome oyun boyunca ortalama %10 işlemci kullandı… oyundan önce kapat"*. Öneri oyunu neyin sınırladığına göre değişir (ekran kartı sınırlıyorsa "FPS'e etkisi az" der). Oyun başlatıcıları (Steam, Epic…) için "kapat" denmez (oyun kapanabilir), indirmeyi duraklatmak önerilir. Windows'un kendi işleri (Defender, Update, arama) için ne olduğu söylenir; güvenlik ayarlarına dokunulmaz. Hiçbir program otomatik kapatılmaz. Bellek yalnızca %80 üstündeyken büyük programlar için söylenir.
- **Isı denemesi artık ekran kartını da çalıştırır (oyundaki gibi):** Önce yalnız işlemci (sürekli hızı öğrenmek için), sonra işlemci + ekran kartı birlikte; her ikisinde sınırsız ve iki kademe ölçülür (~9 dk). Özet ikisini ayrı anlatır ve ekran kartı da çalışınca işlemcinin ne kadar daha sıcak olduğunu söyler. İlk gerçek ölçümde ekran kartı çalışırken işlemci sürekli hızı 3909 → 3680 MHz'e düştü (ortak güç/ısı bütçesi), yani yalnız işlemci yüküyle ölçülen hız oyunda geçerli olmayabilir.
- Ekran kartı yükü (Edge'de WebGL sayfası) ortak bir parçaya ayrıldı (`GpuLoad`): ekran kartı hız denemesi ve ısı denemesi aynı kodu kullanır; kapanınca pencere, geçici klasör ve geçici grafik tercihi geri alınır.
- `pulse-cli bgload [sn]` (gerçek ölçüm), `bgload-test` (sahte süreçlerle sınama) ve `heatbench-gpu` (gerçek ekran kartlı kısa ısı denemesi) komutları.

### Düzeltilenler
- `pulse-cli`: tanınmayan komut artık yanlışlıkla bir mod uygulamaz (hata verip çıkar); yalnızca `apply <mod>` ve `all` mod uygular.

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