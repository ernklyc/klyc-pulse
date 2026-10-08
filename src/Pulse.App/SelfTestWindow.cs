using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pulse.App;

/// <summary>Kendini sınama penceresi: her adım başlarken "çalışıyor", bitince geçti/kaldı olarak canlı görünür.</summary>
public sealed class SelfTestWindow : Window
{
    private readonly TextBlock _header = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _sub = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly ProgressBar _bar = new() { Height = 4, IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button _close = new() { Content = "Kapat", Width = 90, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, TextBlock> _rows = new();
    private readonly TaskCompletionSource _closed = new();
    private int _done;

    public SelfTestWindow()
    {
        Title = "KLYC-Pulse kendini sınıyor";
        Width = 760; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _header.Text = "Kendini sınıyor…";
        _sub.Text = "Her özellik gerçek ortamında deneniyor. Bu sırada bilgisayarı kullanma, ekran ve fan hareketleri normaldir. Bitince bu pencere sonucu gösterir.";
        Grid.SetRow(_header, 0); Grid.SetRow(_sub, 1); Grid.SetRow(_bar, 2); Grid.SetRow(_scroll, 3); Grid.SetRow(_close, 4);
        grid.Children.Add(_header); grid.Children.Add(_sub); grid.Children.Add(_bar); grid.Children.Add(_scroll); grid.Children.Add(_close);
        Content = grid;
        _close.Click += (_, _) => Close();
        Closed += (_, _) => _closed.TrySetResult();
    }

    public Task WhenClosed => _closed.Task;

    public void Running(string name) => Dispatcher.Invoke(() =>
    {
        var tb = new TextBlock { Text = $"…  {name}", Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        _rows[name] = tb;
        _list.Children.Add(tb);
        _scroll.ScrollToBottom();
        _header.Text = $"Kendini sınıyor… {_done + 1}. adım";
    });

    public void Done(string name, bool ok, string detail) => Dispatcher.Invoke(() =>
    {
        _done++;
        if (!_rows.TryGetValue(name, out var tb)) { tb = new TextBlock { Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap }; _rows[name] = tb; _list.Children.Add(tb); }
        tb.Text = $"{(ok ? "✓" : "✗")}  {name}\n     {detail}";
        tb.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x3E)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
        _scroll.ScrollToBottom();
    });

    public void Finish(string summary, bool allOk) => Dispatcher.Invoke(() =>
    {
        _bar.IsIndeterminate = false; _bar.Value = 100;
        _header.Text = summary;
        _header.Foreground = allOk ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x3E)) : new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
        _sub.Text = "Ayrıntılı sonuç dosyaya da yazıldı. Bu pencereyi kapatınca uygulama kapanır.";
        _close.Visibility = Visibility.Visible;
    });
}