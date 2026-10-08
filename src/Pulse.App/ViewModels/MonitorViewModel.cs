using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Monitoring;

namespace Pulse.App.ViewModels;

/// <summary>İzleme sayfası: canlı CPU/GPU/bellek/fan değerleri, kısıtlama algılama ve oyun üstü gösterge ayarı.</summary>
public partial class MonitorViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;
    private readonly List<double> _cpuH = new(), _gpuH = new(), _gpuTempH = new(), _ramH = new(), _cpuMhzH = new();

    public MonitorViewModel(bool withFps = true)
    {
        foreach (var (title, value) in new[] { ("Sol üst", 0), ("Sağ üst", 1), ("Sol alt", 2), ("Sağ alt", 3) })
            Corners.Add(new ChoiceVm(title, value));
        MarkCorner(AppServices.Settings.Current.OverlayCorner);
        OverlayOn = AppServices.Overlay.IsOn;
        AppServices.Overlay.Changed += OnOverlayChanged;

        HasGpu = AppServices.Sensors.HasGpu;
        _subscription = AppServices.Sensors.Subscribe(withFps);
        AppServices.Sensors.Updated += OnSensors;
        if (AppServices.Sensors.Latest is { } s) Apply(s);
    }

    public ObservableCollection<ChoiceVm> Corners { get; } = new();
    public bool HasGpu { get; }

    [ObservableProperty] private bool _overlayOn;

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private string _cpuLoadNote = "";
    [ObservableProperty] private string _gpuLoadNote = "";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private string _cpuNote = "";
    [ObservableProperty] private string _cpuFlag = "";
    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();

    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _gpuNote = "";
    [ObservableProperty] private string _gpuFlag = "";
    [ObservableProperty] private IReadOnlyList<double> _gpuHistory = Array.Empty<double>();

    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _ramNote = "";
    [ObservableProperty] private IReadOnlyList<double> _ramHistory = Array.Empty<double>();

    [ObservableProperty] private string _fanText = "—";
    [ObservableProperty] private string _fanNote = "";
    [ObservableProperty] private string _fpsText = "Kare hızı: bir DirectX oyunu açıkken ölçülür.";
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private string _vramNote = "";

    [ObservableProperty] private string _verdict = "Ölçülüyor…";
    [ObservableProperty] private Brush _verdictBrush = (Brush)System.Windows.Application.Current.FindResource("MutedBrush");

    private void OnSensors(SensorSnapshot s) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(s));

    private void OnOverlayChanged(bool on) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OverlayOn = on);

    private static void Push(List<double> l, double v) { l.Add(v); if (l.Count > 90) l.RemoveAt(0); }

    private void Apply(SensorSnapshot s)
    {
        // İşlemci
        CpuText = s.CpuPercent is { } c ? $"%{c:0}" : "—";
        var ghz = s.CpuMhz is { } f ? $"{f / 1000.0:0.0} GHz" : "—";
        CpuNote = ghz + (s.CpuTempC is { } t ? $"  ·  {t:0}°C" : "  ·  sıcaklık için yönetici gerekir");
        var hint = s.CpuThrottleHint(AppServices.Sensors.BaseMhz);
        CpuFlag = hint ?? "";
        CpuTempText = s.CpuTempC is { } tc ? $"{tc:0}°C" : CpuText;
        CpuLoadNote = $"{CpuText} yük  ·  {ghz}";
        if (s.CpuPercent is { } cp) Push(_cpuH, cp);
        CpuHistory = _cpuH.ToArray();

        // Ekran kartı
        if (s.Gpu is { } g)
        {
            GpuText = g.UtilPercent is { } u ? $"%{u}" : "—";
            GpuNote = $"{g.CoreMhz} MHz  ·  {g.TempC}°C  ·  {(g.PowerW is { } w ? w.ToString("0.0") : "—")} W" + (g.IsIdleClocks ? "  ·  boşta" : "");
            GpuFlag = g.ThrottleText ?? "";
            GpuTempText = g.TempC is { } gt ? $"{gt}°C" : GpuText;
            GpuLoadNote = $"{GpuText} yük  ·  {(g.PowerW is { } pw ? pw.ToString("0") : "—")} W" + (g.IsIdleClocks ? "  ·  boşta" : "");
            VramText = g.VramUsedBytes is { } vu ? $"{vu / 1073741824.0:0.0} GB" : "—";
            VramNote = g.VramTotalBytes is { } vt ? $"/ {vt / 1073741824.0:0.0} GB video belleği" : "";
            Push(_gpuH, g.UtilPercent ?? 0);
            GpuHistory = _gpuH.ToArray();
        }
        else { GpuText = "—"; GpuNote = "NVIDIA sürücüsü bulunamadı"; }

        // Bellek
        RamText = $"{s.RamUsedBytes / 1073741824.0:0.0} GB";
        RamNote = $"/ {s.RamTotalBytes / 1073741824.0:0.0} GB  ·  %{s.RamPercent:0}";
        Push(_ramH, s.RamPercent);
        RamHistory = _ramH.ToArray();

        var fps = AppServices.Sensors.LatestFps;
        FpsText = fps is not null
            ? $"Kare hızı: {fps.Fps:0} FPS, {fps.FrameMs:0.0} ms, %1 düşük {fps.LowFps:0}  ({fps.ProcessName})"
            : AppServices.Sensors.FpsError ?? "Kare hızı: bir DirectX oyunu açıkken ölçülür (DirectX 10/11/12).";

        // Fan
        FanText = s.CpuFanRpm is { } cf ? $"{cf}" : "—";
        FanNote = s.GpuFanRpm is { } gf ? $"CPU  ·  GPU {gf} devir/dk" : "devir / dakika";

        // Genel yorum: en önemli sorun neyse onu söyler
        var (text, brushKey) = Judge(s, hint);
        Verdict = text;
        VerdictBrush = (Brush)System.Windows.Application.Current.FindResource(brushKey);
    }

    private static (string, string) Judge(SensorSnapshot s, string? cpuHint)
    {
        var g = s.Gpu;
        if (g?.ThrottleText is { } gt && g.UtilPercent > 50) return ($"Ekran kartı kısılıyor: {gt}.", "BadBrush");
        if (s.CpuTempC is > 92) return ("İşlemci çok sıcak. Fan ve havalandırmayı kontrol et.", "BadBrush");
        if (s.CpuTempC is > 85) return ("İşlemci sıcak ama sınır içinde.", "WarnBrush");
        if (cpuHint is not null) return ($"İşlemci: {cpuHint}.", "WarnBrush");
        if (s.RamPercent > 90) return ("Bellek dolmak üzere. Arka plandaki uygulamaları kapatmak iyi olur.", "WarnBrush");
        return ("Her şey normal. Bilgisayar yavaşlamıyor.", "GoodBrush");
    }

    [RelayCommand]
    private void ToggleOverlay() => AppServices.Overlay.Toggle();

    [RelayCommand]
    private void SetCorner(ChoiceVm choice)
    {
        AppServices.Overlay.SetCorner(choice.Value);
        MarkCorner(choice.Value);
    }

    private void MarkCorner(int value) { foreach (var c in Corners) c.IsActive = c.Value == value; }

    public void Dispose()
    {
        AppServices.Sensors.Updated -= OnSensors;
        AppServices.Overlay.Changed -= OnOverlayChanged;
        _subscription.Dispose();
    }
}
