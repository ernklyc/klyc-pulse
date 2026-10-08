using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>
/// Oyun üstü gösterge: kenarda duran, tıklamayı geçiren, odak çalmayan küçük pencere. Oyuna hiçbir şey enjekte etmez
/// (anti-hile yazılımlarıyla çakışmaz). Tam ekran "özel" modlarda görünmeyebilir; kenarlıksız/pencereli modda görünür.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _text = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),   // soluk: oyunu bölmesin
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        LineHeight = 17,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0x11, 0x18, 0x27)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 12, 7),
            Child = _text,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        };
        SizeChanged += (_, _) => Place();
    }

    public int Corner { get; set; }

    public void Update(SensorSnapshot s, string? fpsLine = null)
    {
        var lines = new List<string>();
        var cpu = $"CPU {Fmt(s.CpuPercent, "0")}%  {(s.CpuMhz is { } f ? (f / 1000.0).ToString("0.0") : "-")} GHz";
        if (s.CpuTempC is { } ct) cpu += $"  {ct:0}°C";
        lines.Add(cpu);
        if (s.Gpu is { } g)
        {
            lines.Add($"GPU {Fmt(g.UtilPercent, "0")}%  {g.CoreMhz} MHz  {g.TempC}°C  {(g.PowerW is { } w ? w.ToString("0") : "-")} W");
            if (g.VramTotalBytes is > 0) lines.Add($"GPU bellek {g.VramUsedBytes / 1073741824.0:0.0}/{g.VramTotalBytes / 1073741824.0:0.0} GB");
            if (g.ThrottleText is { } tt) lines.Add("! " + tt);
        }
        lines.Add($"RAM {s.RamUsedBytes / 1073741824.0:0.0}/{s.RamTotalBytes / 1073741824.0:0.0} GB");
        if (fpsLine is not null) lines.Insert(0, fpsLine);
        _text.Text = string.Join("\n", lines);
    }

    public void Place()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 14;
        Left = Corner is 1 or 3 ? area.Right - ActualWidth - margin : area.Left + margin;
        Top = Corner is 2 or 3 ? area.Bottom - ActualHeight - margin : area.Top + margin;
    }

    private static string Fmt(double? v, string f) => v?.ToString(f) ?? "-";
    private static string Fmt(int? v, string f) => v?.ToString(f) ?? "-";

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
