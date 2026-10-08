using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pulse.App;

/// <summary>
/// Küçük, rahatsız etmeyen bildirim: ekranın seçilen köşesinde birkaç saniye görünür (Ayarlar'dan köşe seçilir; gösterge aynı köşedeyse
/// yanına yerleşir). Odak çalmaz. İki türü vardır:
/// - <b>Oyun içi uyarı</b> (onClick yok): tıklamayı geçirir, oyuna hiç karışmaz.
/// - <b>Tıklanabilir bildirim</b> (onClick var): "tıkla" yazar; tıklayınca ilgili sayfa açılır. Oyun bittikten sonra çıkanlar bunlardır.
/// </summary>
public sealed class NoticeChip : Window
{
    private static NoticeChip? _current;
    private readonly DispatcherTimer _timer;
    private readonly Action? _onClick;

    private NoticeChip(string text, bool warn, Action? onClick)
    {
        _onClick = onClick;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(onClick is null ? 7 : 12) };
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 400;
        var accent = new SolidColorBrush(warn ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0x9C, 0xA3, 0xAF));
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        if (onClick is not null)
            body.Children.Add(new TextBlock { Text = "Tıkla, aç ›", Foreground = accent, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) });
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(onClick is null ? (byte)0xB0 : (byte)0xE0, 0x11, 0x18, 0x27)),
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 12, 6),
            Child = body,
        };
        if (onClick is not null)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) => { var a = _onClick; Close(); a?.Invoke(); };
        }
        Content = border;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GwlExStyle);
            // Oyun içi uyarı tıklamayı geçirir; tıklanabilir bildirim tıklanır ama yine odak almaz.
            SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow | (onClick is null ? WsExTransparent : 0));
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => { _timer.Stop(); if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>Bildirimi gösterir; öncekinin yerine geçer. UI iş parçacığından çağrılmalıdır.</summary>
    /// <param name="onClick">Verilirse bildirim tıklanabilir olur (ör. ilgili sayfayı açmak için).</param>
    public static void Show(string text, bool warn, Action? onClick = null)
    {
        try
        {
            _current?.Close();
            var chip = new NoticeChip(text, warn, onClick);
            _current = chip;
            ((Window)chip).Show();
            chip._timer.Start();
        }
        catch { /* bildirim gösterilemezse sessizce geç; ana iş etkilenmez */ }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 14;
        var corner = AppServices.Settings.Current.NoticeCorner;              // 0 sol üst, 1 sağ üst, 2 sol alt, 3 sağ alt
        var right = corner is 1 or 3;
        var bottom = corner is 2 or 3;
        Left = right ? area.Right - ActualWidth - margin : area.Left + margin;

        // Gösterge aynı köşedeyse üst üste binmesin: üst köşede altına, alt köşede üstüne yerleş.
        if (AppServices.Overlay.VerticalSpan(corner) is { } span)
            Top = bottom ? span.Top - ActualHeight - 6 : span.Bottom + 6;
        else
            Top = bottom ? area.Bottom - ActualHeight - margin : area.Top + margin;
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
