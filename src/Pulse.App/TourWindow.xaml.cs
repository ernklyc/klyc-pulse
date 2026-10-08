using System.Windows;

namespace Pulse.App;

/// <summary>İlk açanlar için kısa tur: ne işe yaradığı, modlar, oyunlar, gösterge, temizlik, gizlilik. Atlanabilir; Ayarlar'dan yeniden açılır.</summary>
public partial class TourWindow : Window
{
    public sealed record Bullet(string Head, string Body);
    private sealed record Step(string Title, string Lead, Bullet[] Bullets, string? Tip, string? GoLabel, Type? GoPage);

    private static readonly Step[] Steps =
    [
        new("Pulse ne işe yarar?",
            "Bilgisayarını oyun, günlük iş ya da sessiz çalışma için tek tıkla ayarlar ve ayarın gerçekten tuttuğunu kontrol eder. Kısa bir tur: yaklaşık bir dakika.",
            [
                new("Hızlı", "Oyuna girince en iyi performansı, işin bitince serin ve sessiz hâli kendisi ayarlar."),
                new("Dürüst", "Bir ayarı yazdıktan sonra geri okuyup doğrular. Tutmadıysa söyler."),
                new("Güvenli", "Sormadan hiçbir şeyi silmez, güvenlik ayarlarına dokunmaz."),
            ],
            null, null, null),

        new("4 mod: hangisi ne zaman?",
            "Ana ekrandan bir mod seçersin, Pulse işlemci, ekran, parlaklık ve gücü ona göre ayarlar.",
            [
                new("Oyun", "En yüksek performans. Oyun oynarken."),
                new("Günlük", "Dengeli. İnternet, ofis, yazılım için."),
                new("Sessiz", "Serin ve sessiz. Hafif işler, pilde."),
                new("Boşta", "İndirme, yedekleme, bilgisayarı başında olmadan bırakırken."),
            ],
            "Çoğu zaman Günlük modda kalıp “oyun açılınca otomatik geç”i açık bırakmak yeter.", null, null),

        new("Oyunlar kendiliğinden",
            "Bir oyunu açınca Pulse onu tanır, Oyun moduna geçer, kapatınca eski moda döner. Oyunlar listeye kendiliğinden eklenir.",
            [
                new("Oyun raporu", "Oyun kapanınca “neden takıldı?”yı sade dille söyler: ısı, işlemci hızı, ekran kartı, bellek, FPS."),
                new("Akıllı oyun ayarı", "İşlemci çok ısınıyorsa hızını FPS'i bozmadan hafifçe kısmayı dener, sonraki oyunda ölçer. FPS düşerse geri alır, kendi kendine öğrenir."),
                new("Oyun ekle", "Listede olmayan bir oyunu Oyunlar sayfasından elle ekleyebilirsin."),
            ],
            null, "Oyunlar sayfasına git", typeof(Pages.GamesPage)),

        new("Oyunda ekranda FPS ve ısı",
            "Oyun açılınca ekranın köşesinde işlemci, ekran kartı ve (ölçülebiliyorsa) FPS bilgisi çıkar. Soluk ve oyuna karışmaz. Açma/kapama ve köşe İzleme sayfasında, kısayol Ctrl+Alt+O.",
            [
                new("Görünmüyorsa", "Oyunun görüntü ayarında “Tam ekran” yerine “Kenarlıksız pencere” (ya da “Tam ekran penceresi”) seç. Özel tam ekranda gösterge görünmez."),
                new("Güvenli", "Oyuna hiçbir şey enjekte etmez; Vanguard, EAC, VAC gibi korumalarla sorun çıkarmaz."),
            ],
            null, "İzleme sayfasına git", typeof(Pages.MonitorPage)),

        new("Temizlik",
            "Yalnızca zaten yeniden oluşan dosyalar silinir; biraz riskli olanlar silinmeden önce 7 gün ayrı bir yerde bekler.",
            [
                new("Eski kalıntılar", "Çoktan sildiğin uygulamaların bıraktığı klasörleri bulur. Sen seçmeden hiçbir şeye dokunmaz; seçtiklerin Geri Dönüşüm Kutusu'na gider."),
                new("Kopya dosyalar", "Birebir aynı büyük dosyaları bulur, her grupta en eskisini korur."),
            ],
            "Registry temizleyici ve shader önbelleği silme bilerek yok: yararı yok, zararı var.", "Temizlik sayfasına git", typeof(Pages.CleanupPage)),

        new("Gizlilik ve ayarların",
            "Pulse veri toplamaz. Ayarların ve oyun raporların bu bilgisayarda saklanır.",
            [
                new("Ayarların güvende", "Ayarlar %LOCALAPPDATA%\\Pulse klasöründedir. Güncelleyince ya da yeniden kurunca silinmez; her kayıtta otomatik yedeği alınır."),
                new("Güncelleme denetimi", "Günde en fazla bir kez yeni sürüm var mı diye bakar (kişisel bilgi göndermez, hiçbir şey indirmez). Ayarlar > Güncellemeler'den kapatılır."),
                new("Yönetici izni", "Sıcaklık okumak, ekran kartı ayarı ve FPS ölçümü için Pulse yönetici olarak açılır."),
            ],
            "Bu turu istediğin zaman Ayarlar sayfasından yeniden açabilirsin.", null, null),
    ];

    private int _index;
    private Type? _goPage;

    /// <summary>Tur bitince ya da atlanınca istenen sayfa (varsa).</summary>
    public Type? NavigateTo { get; private set; }

    public TourWindow()
    {
        InitializeComponent();
        Show(0);
    }

    private void Show(int i)
    {
        _index = Math.Clamp(i, 0, Steps.Length - 1);
        var s = Steps[_index];
        StepLabel.Text = $"HIZLI TUR  ·  {_index + 1} / {Steps.Length}";
        TitleText.Text = s.Title;
        LeadText.Text = s.Lead;
        Bullets.ItemsSource = s.Bullets;
        TipBox.Visibility = s.Tip is null ? Visibility.Collapsed : Visibility.Visible;
        TipText.Text = s.Tip ?? "";
        GoButton.Visibility = s.GoLabel is null ? Visibility.Collapsed : Visibility.Visible;
        GoButton.Content = s.GoLabel ?? "";
        _goPage = s.GoPage;
        BackButton.IsEnabled = _index > 0;
        var last = _index == Steps.Length - 1;
        NextButton.Content = last ? "Başlayalım" : "İleri";
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        Dots.Text = string.Concat(Enumerable.Range(0, Steps.Length).Select(k => k == _index ? "●  " : "○  "));
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= Steps.Length - 1) { DialogResult = true; Close(); return; }
        Show(_index + 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo = _goPage;
        DialogResult = true;
        Close();
    }
}
