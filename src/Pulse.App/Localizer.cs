using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core.Localization;

namespace Pulse.App;

/// <summary>
/// Arayüz İngilizceyse, ekrana gelen her öğenin sabit (XAML'de yazılmış) Türkçe metnini sözlükten çevirir.
/// Veri bağlamalı metinlere dokunmaz; onlar ViewModel'de <see cref="Loc.T"/> ile çevrilir. Dil Türkçeyse hiçbir şey yapmaz.
/// TextBlock kendi Loaded olayını göndermez; bu yüzden herhangi bir öğe yüklenince, boşta kalınca tüm pencerelerin görsel ağacı bir kez gezilir
/// (ardışık olaylar tek geziye birleştirilir; çeviri tekrarlansa da zararsızdır).
/// </summary>
public static class Localizer
{
    private static bool _installed;
    private static bool _scheduled;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("KLYC_PULSE_LANG_DEBUG") == "1";

    public static void Install()
    {
        if (_installed || !Loc.IsEnglish) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Schedule()));
        // Şablonla sonradan oluşan öğeler (ör. liste satırı düğmeleri) her zaman Loaded olayından yakalanmayabilir:
        // pencere görünürken 1,5 sn'de bir ağaç yeniden gezilir (görünmüyorsa hiçbir şey yapılmaz; gezi birkaç ms sürer).
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { if (Application.Current?.Windows.OfType<Window>().Any(w => w.IsVisible) == true) Schedule(); };
        t.Start();
    }

    private static void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scheduled = false;
            try { foreach (Window w in Application.Current.Windows) Walk(w); }
            catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Çeviri hatası: " + ex.Message); }
        });
    }

    private static void Walk(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb when !IsBound(tb, TextBlock.TextProperty):
                Translate(tb.Text, v => tb.Text = v);
                break;
            case TextBlock tbb:
                if (Debug) { ReportTurkish(tbb.Text); ReportUnknown(tbb.Text); }
                break;
            case Wpf.Ui.Controls.TextBox box when !string.IsNullOrEmpty(box.PlaceholderText):
                box.PlaceholderText = Loc.T(box.PlaceholderText);
                break;
            case Window w when !string.IsNullOrEmpty(w.Title) && !IsBound(w, Window.TitleProperty):
                w.Title = Loc.T(w.Title);
                break;
        }
        if (d is ContentControl cc && cc.Content is string s)
        {
            if (!IsBound(cc, ContentControl.ContentProperty)) Translate(s, v => cc.Content = v);
            else if (Debug) { ReportTurkish(s); ReportUnknown(s); }
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip)
            fe.ToolTip = Loc.T(tip);

        // Henüz şablonu uygulanmamış içerik öğelerinin (ör. Button içindeki TextBlock) mantıksal çocukları da gezilir
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
    }

    private static void Translate(string current, Action<string> set)
    {
        var t = Loc.T(current);
        if (t != current) set(t);
        else if (Debug) ReportUnknown(current);
    }

    private static readonly System.Text.RegularExpressions.Regex TurkishChars = new("[çğıöşüÇĞİÖŞÜ]");
    private static readonly HashSet<string> Reported = new();

    /// <summary>Sınama: İngilizce modda ekranda hâlâ Türkçe harf içeren (çevrilmemiş) metni günlüğe yazar.</summary>
    private static void ReportTurkish(string text)
    {
        if (text.Length > 2 && TurkishChars.IsMatch(text) && Reported.Add(text))
            Pulse.Core.Diagnostics.Journal.Write("Çeviri yok (görünen): " + text);
    }

    /// <summary>Sınama: ne anahtar ne de çeviri olan, harf içeren her metni yazar (aksansız Türkçe metinleri yakalamak için).</summary>
    private static void ReportUnknown(string text)
    {
        if (text.Length > 3 && !Loc.Has(text) && !Loc.IsTranslatedText(text) && System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-zÇĞİÖŞÜçğıöşü]{4}") && Reported.Add(text))
            Pulse.Core.Diagnostics.Journal.Write("Çeviri yok: " + text);
    }

    private static bool IsBound(DependencyObject d, DependencyProperty p) => System.Windows.Data.BindingOperations.IsDataBound(d, p);
}
