using System.Runtime.InteropServices;
using System.Windows.Interop;
using Pulse.Core.Diagnostics;
using Pulse.Core.Hardware;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.App;

/// <summary>
/// Genel kısayollar (RegisterHotKey). Fn tuşları ASUS firmware'ine bağlı olduğundan dışarıdan yakalanamıyor; bunun yerine
/// her eyleme Windows'un gördüğü istediğin tuş/kombinasyon atanır. Her pencerede, uygulama tepsideyken de çalışır.
/// UI iş parçacığında oluşturulmalı.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, (string ActionId, string Text)> _active = new();
    private bool _enabled;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("KlycPulseHotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    /// <summary>Başka bir program tarafından kullanıldığı için kaydedilemeyen kısayollar.</summary>
    public List<string> Failed { get; } = new();

    /// <summary>Bir kısayol kullanıcıya gösterilecek bir mesaj ürettiğinde tetiklenir.</summary>
    public event Action<string>? Message;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            _enabled = value;
            Reload();
        }
    }

    /// <summary>Ayarlardaki atamaları yeniden kaydeder (atama değişince çağrılır).</summary>
    public void Reload()
    {
        UnregisterAll();
        Failed.Clear();
        if (!_enabled) return;

        var id = 1;
        foreach (var (action, binding) in HotkeyActions.Resolve(AppServices.Settings.Current.HotkeyBindings))
        {
            if (binding is not { IsValid: true } b) continue;
            if (RegisterHotKey(_source.Handle, id, b.Modifiers | ModNoRepeat, b.VirtualKey)) _active[id] = (action, b.ToString());
            else { Failed.Add(b.ToString()); Journal.Write($"Kısayol alınamadı: {b}"); }
            id++;
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _active.Keys) UnregisterHotKey(_source.Handle, id);
        _active.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _active.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            _ = Run(entry.ActionId, entry.Text);
        }
        return IntPtr.Zero;
    }

    private async Task Run(string action, string text)
    {
        try
        {
            Journal.Write($"Kısayol: {text} → {action}");
            switch (action)
            {
                case "kbd:cycle": Message?.Invoke((await Task.Run(LaptopControl.CycleKeyboard)).Message); break;
                case "overlay:toggle": AppServices.Overlay.Toggle(); break;
                case "mode:next":
                    var order = new[] { Modes.Game, Modes.Daily, Modes.Quiet };
                    var i = Array.IndexOf(order, AppServices.Modes.CurrentKey);
                    await AppServices.Modes.ApplyAsync(order[(i + 1) % order.Length]);
                    break;
                default:
                    if (action.StartsWith("mode:")) await AppServices.Modes.ApplyAsync(action[5..]);
                    break;
            }
        }
        catch (Exception ex) { Journal.Write($"Kısayol hatası ({text}): {ex.Message}"); }
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
