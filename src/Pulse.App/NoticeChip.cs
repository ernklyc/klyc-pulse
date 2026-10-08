using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pulse.App;

/// <summary>
/// Oyun sırasında rahatsız etmeyen küçük uyarı: sağ üst köşede (gösterge varsa altında) birkaç saniye görünür, tıklamayı geçirir, odak çalmaz.
/// (Sağ alttaki büyük Windows balonu yerine kullanılır.)
/// </summary>
public sealed class NoticeChip : Window
{
    private static NoticeChip? _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    private NoticeChip(string text, bool warn)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 380;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x18, 0x27)),
            BorderBrush = new SolidColorBrush(warn ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0x9C, 0xA3, 0xAF)),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 5, 11, 5),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        _timer.Tick += (_, _) => Close();
        Closed += (_, _) => { _timer.Stop(); if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>Uyarıyı gösterir; önceki uyarı varsa yerine geçer. UI iş parçacığından çağrılmalıdır.</summary>
    public static void Show(string text, bool warn)
    {
        try
        {
            _current?.Close();
            var chip = new NoticeChip(text, warn);
            _current = chip;
            ((Window)chip).Show();
            chip._timer.Start();
        }
        catch { /* uyarı gösterilemezse sessizce geç; ana iş etkilenmez */ }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 14;
        // Sağ üstte; gösterge de sağ üstteyse üst üste binmesin diye hemen altına.
        Left = area.Right - ActualWidth - margin;
        Top = AppServices.Overlay.TopRightBottom is { } below ? below + 6 : area.Top + margin;
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
