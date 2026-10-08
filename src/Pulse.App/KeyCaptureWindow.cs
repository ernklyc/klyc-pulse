using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core.Settings;

namespace Pulse.App;

/// <summary>"Yeni kısayol için tuşlara bas" penceresi. Bastığın kombinasyonu yakalar; Esc iptal eder.</summary>
public sealed class KeyCaptureWindow : Window
{
    private readonly TextBlock _hint = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public KeyCaptureWindow(string actionTitle)
    {
        Title = "Kısayol ata";
        Width = 420; Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.White;
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                new TextBlock { Text = actionTitle, FontSize = 16, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Şimdi atamak istediğin tuşa ya da kombinasyona bas (örn. Ctrl+Alt+G, F13, Pause). Esc: iptal.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) },
                _hint,
            },
        };
        PreviewKeyDown += OnKey;
    }

    public HotkeyBinding? Result { get; private set; }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { DialogResult = false; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyBinding.Ctrl;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyBinding.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyBinding.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyBinding.Win;

        var binding = new HotkeyBinding(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!binding.IsValid)
        {
            _hint.Text = $"“{binding}” olmaz: düz tuşlar her yazışta tetiklenir. Ctrl, Alt ya da Win ile birlikte bas (ya da F13-F24, Pause, Print Screen).";
            return;
        }
        Result = binding;
        DialogResult = true;
    }
}