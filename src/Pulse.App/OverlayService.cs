using System.Windows;
using Pulse.Core.Monitoring;

namespace Pulse.App;

/// <summary>Oyun üstü göstergeyi açıp kapatır. Kapalıyken sensör okuması da durur.</summary>
public sealed class OverlayService
{
    private OverlayWindow? _window;
    private IDisposable? _subscription;

    public bool IsOn => _window is not null;
    public event Action<bool>? Changed;

    public void Toggle() => Set(!IsOn);

    public void Set(bool on)
    {
        if (on == IsOn) return;
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess()) { dispatcher.Invoke(() => Set(on)); return; }

        if (on)
        {
            _window = new OverlayWindow { Corner = AppServices.Settings.Current.OverlayCorner };
            _window.Show();
            _subscription = AppServices.Sensors.Subscribe(wantFps: true);
            AppServices.Sensors.Updated += OnSensors;
            if (AppServices.Sensors.Latest is { } s) _window.Update(s, FpsLine());
            _window.Place();
        }
        else
        {
            AppServices.Sensors.Updated -= OnSensors;
            _subscription?.Dispose();
            _subscription = null;
            _window?.Close();
            _window = null;
        }
        Changed?.Invoke(IsOn);
    }

    private static string? FpsLine() =>
        AppServices.Sensors.LatestFps is { } f ? $"FPS {f.Fps:0}  {f.FrameMs:0.0} ms  1% {f.LowFps:0}  ({f.ProcessName})" : null;

    public void SetCorner(int corner)
    {
        AppServices.Settings.Current.OverlayCorner = corner;
        AppServices.Settings.Save();
        if (_window is not null) { _window.Corner = corner; _window.Place(); }
    }

    private void OnSensors(SensorSnapshot s) =>
        Application.Current.Dispatcher.BeginInvoke(() => _window?.Update(s, FpsLine()));
}
