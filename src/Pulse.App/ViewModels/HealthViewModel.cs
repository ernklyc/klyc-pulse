using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Cleanup;
using Pulse.Core.Health;

namespace Pulse.App.ViewModels;

public sealed class FindingVm
{
    public FindingVm(HealthFinding f)
    {
        Title = f.Title;
        Detail = f.Detail;
        (Label, Brush) = f.Level switch
        {
            FindingLevel.Bad => ("SORUN", Res("BadBrush")),
            FindingLevel.Warning => ("DİKKAT", Res("WarnBrush")),
            FindingLevel.Info => ("BİLGİ", Res("InfoBrush")),
            _ => ("İYİ", Res("GoodBrush")),
        };
    }

    public string Title { get; }
    public string Detail { get; }
    public string Label { get; }
    public Brush Brush { get; }

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
}

public partial class HealthViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 90; // 3 sn x 90 = 4,5 dk
    private readonly HealthSampler _sampler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<double> _cpu = new(), _gpu = new(), _fan = new();
    private int _tick;
    private bool _busy;

    public HealthViewModel()
    {
        _timer.Tick += async (_, _) => await SampleAsync();
        _timer.Start();
        _ = SampleAsync();
    }

    public ObservableCollection<FindingVm> Findings { get; } = new();

    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _gpuHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _fanHistory = Array.Empty<double>();

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _cpuNote = "Yaklaşık sıcaklık";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _gpuNote = "";
    [ObservableProperty] private string _fanText = "—";
    [ObservableProperty] private string _fanNote = "CPU fanı";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _batteryNote = "";
    [ObservableProperty] private Brush _batteryBrush = (Brush)System.Windows.Application.Current.FindResource("InkBrush");

    [ObservableProperty] private string _summaryText = "Ölçülüyor…";
    [ObservableProperty] private Brush _summaryBrush = (Brush)System.Windows.Application.Current.FindResource("MutedBrush");

    [RelayCommand]
    private async Task Refresh() => await SampleAsync(forceFindings: true);

    private async Task SampleAsync(bool forceFindings = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = await Task.Run(_sampler.Sample);
            Push(_cpu, s.CpuTempC); Push(_gpu, s.GpuTempC); Push(_fan, s.CpuFanRpm);
            CpuHistory = _cpu.ToList(); GpuHistory = _gpu.ToList(); FanHistory = _fan.ToList();

            CpuText = s.CpuTempC is { } c ? $"{c:N0} °C" : "—";
            CpuNote = s.CpuTempC is null ? (CleanupEngine.IsAdmin ? "Sensör okunamadı" : "Yönetici izni gerekir") : "Yaklaşık sıcaklık";
            GpuText = s.GpuTempC is { } g ? $"{g:N0} °C" : "—";
            GpuNote = s.GpuUtilPercent is { } u ? $"%{u} kullanım" : "";
            FanText = s.CpuFanRpm is { } f ? $"{f:N0}" : "—";
            FanNote = s.GpuFanRpm is { } gf ? $"CPU fanı · GPU {gf:N0} RPM" : "CPU fanı (RPM)";

            var b = s.Battery;
            if (!b.Present) { BatteryText = "Yok"; BatteryNote = "Pil algılanmadı"; BatteryBrush = Res("WarnBrush"); }
            else if (b.Volts is < 6.0) { BatteryText = $"{b.Volts:N2} V"; BatteryNote = "Pil sorunlu görünüyor"; BatteryBrush = Res("BadBrush"); }
            else { BatteryText = b.WearPercent is { } w ? $"%{100 - w:N0}" : "—"; BatteryNote = "Kalan kapasite (tasarıma göre)"; BatteryBrush = Res("InkBrush"); }

            if (forceFindings || _tick++ % 5 == 0)
            {
                var findings = await Task.Run(() => HealthAnalyzer.Analyze(s));
                Findings.Clear();
                foreach (var x in findings) Findings.Add(new FindingVm(x));
                var (level, text) = HealthAnalyzer.Summarize(findings);
                SummaryText = text;
                SummaryBrush = level switch { FindingLevel.Bad => Res("BadBrush"), FindingLevel.Warning => Res("WarnBrush"), _ => Res("GoodBrush") };
            }
        }
        catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Sağlık ölçümü hatası: " + ex.Message); }
        finally { _busy = false; }
    }

    private static void Push(List<double> list, double? v)
    {
        if (v is null) return;
        list.Add(v.Value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

    public void Dispose()
    {
        _timer.Stop();
        _sampler.Dispose();
    }
}
