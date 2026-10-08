# Katkı / Contributing

Katkılar için teşekkürler! / Thanks for contributing!

## Hızlı başlangıç

```powershell
git clone https://github.com/ernklyc/klyc-pulse.git
cd klyc-pulse
dotnet build -c Release
dotnet run --project src/Pulse.Cli -c Release -- guard-test
```

Gereksinim: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## En değerli katkılar

1. **Donanım uyumluluk raporları.** Farklı marka/model bilgisayarda `KLYC-Pulse.exe --selftest` çıktısını ve ne gördüğünüzü paylaşın.
2. **Hata bildirimleri.** Şablonu doldurun; `%USERPROFILE%\KLYC-Pulse-selftest.txt` ve `%LOCALAPPDATA%\Pulse\logs` dosyalarını (göz atarak) ekleyin.
3. **Çeviri.** Arayüz şu an yalnızca Türkçedir.

## Kod kuralları

- **Uygula → geri oku → doğrula:** donanıma/sisteme yazan her yeni adım, yazdığını geri okuyup doğrulamalı; okunamıyorsa "kabul edildi" demeli.
- **Silme/değiştirme geri alınabilir** olmalı (karantina, Geri Dönüşüm Kutusu, yedek).
- **Donanıma dokunan değişiklikler sınırlı ve kalıcı olmayan** olmalı; güvenlik ayarlarına dokunulmaz.
- Arayüz iş parçacığını bloklamayın; ağır işleri arka planda yapın.
- Yeni mantık için `src/Pulse.Cli` altına donanıma dokunmayan bir test komutu ekleyin (çıkış kodu 0/1).
- Commit mesajları: kısa ve açıklayıcı (`feat:`, `fix:`, `docs:`, `test:` önekleri tercih edilir).

## Pull request

Küçük, tek konulu PR'lar açın. PR şablonundaki kontrol listesini doldurun ve CI'nin geçtiğinden emin olun.