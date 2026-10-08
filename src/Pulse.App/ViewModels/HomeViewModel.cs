using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Hardware;
using Pulse.Core.Modes;

namespace Pulse.App.ViewModels;

/// <summary>Hızlandır raporundaki bir satır (ad, ayrıntı, durum).</summary>
public sealed class OptRowVm
{
    public OptRowVm(string name, string detail, bool ok)
    {
        Name = name;
        Detail = detail;
        Label = ok ? "TAMAM" : "DİKKAT";
        Brush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(ok ? "GoodBrush" : "WarnBrush");
    }

    public string Name { get; }
    public string Detail { get; }
    public string Label { get; }
    public System.Windows.Media.Brush Brush { get; }
}

public partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ModeController _controller = AppServices.Modes;

    public HomeViewModel()
    {
        Modes = new ObservableCollection<ModeVm>(Core.Modes.Modes.All.Select(d => new ModeVm(d)));
        var current = _controller.CurrentKey;
        foreach (var m in Modes) m.IsActive = m.Key == current;
        ChangeBrightness = AppServices.Settings.Current.ChangeBrightness;
        _controller.Applied += OnApplied;
        _controller.Busy += OnBusy;

    }

    public ObservableCollection<ModeVm> Modes { get; }
    public ObservableCollection<StepVm> Steps { get; } = new();
    public OperationVm Op { get; } = new();

    /// <summary>Ana ekrandaki canlı izleme kartı; sayfa açıkken oluşturulur, kapanınca bırakılır.</summary>
    [ObservableProperty] private MonitorViewModel? _live;

    [ObservableProperty] private bool _showWelcome = !AppServices.Settings.Current.OnboardingDone;
    [ObservableProperty] private bool _changeBrightness = true;

    // ---- Hızlandır ----
    public ObservableCollection<OptRowVm> OptRows { get; } = new();
    public ObservableCollection<OptRowVm> OptStats { get; } = new();
    [ObservableProperty] private bool _hasOpt;
    [ObservableProperty] private bool _isOptimizing;
    [ObservableProperty] private string _optTitle = "";
    [ObservableProperty] private string _optSummary = "";

    [RelayCommand]
    private async Task Optimize()
    {
        if (IsOptimizing) return;
        var ask = System.Windows.MessageBox.Show(
            "Şunlar yapılacak:\n\n• 2 günden eski zararsız geçici dosyalar silinir (belgelerine, oyunlarına, indirilenlerine dokunulmaz)\n• Arka plandaki büyük uygulamaların belleği rahatlatılır (hiçbir uygulama kapanmaz)\n• Seçili modun ayarları yeniden uygulanıp doğrulanır\n• Windows oyun ayarları ve açılış öğeleri denetlenir (değiştirilmez)\n\nİşlem bitince önce/sonra ölçümü gösterilir. Devam edilsin mi?",
            "Hızlandır", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ask != System.Windows.MessageBoxResult.Yes) return;

        IsOptimizing = true;
        HasOpt = false;
        Op.Begin("Başlıyor…", Core.Optimize.Optimizer.StepCount);
        try
        {
            var progress = new Progress<string>(text => Op.Step(text));
            var report = await new Core.Optimize.Optimizer(_controller).RunAsync(progress);

            OptRows.Clear();
            foreach (var s in report.Steps) OptRows.Add(new OptRowVm(s.Name, s.Detail, s.Ok));

            OptStats.Clear();
            string F(long b) => Core.Optimize.Optimizer.Format(b);
            OptStats.Add(new OptRowVm("Boş RAM", $"{F(report.Before.FreeRamBytes)}  →  {F(report.After.FreeRamBytes)}", report.RamGained >= 0));
            OptStats.Add(new OptRowVm("Boş disk (C:)", $"{F(report.Before.FreeDiskBytes)}  →  {F(report.After.FreeDiskBytes)}", report.After.FreeDiskBytes >= report.Before.FreeDiskBytes));
            OptStats.Add(new OptRowVm("Açılışta başlayan", $"{report.Before.StartupEnabled}  →  {report.After.StartupEnabled} uygulama", true));

            OptTitle = "Hızlandır raporu";
            OptSummary = report.AllOk ? "Her adım tamam" : $"{report.Steps.Count(s => !s.Ok)} adım dikkat istiyor";
            HasOpt = true;
        }
        catch (Exception ex)
        {
            OptTitle = "Hızlandır tamamlanamadı";
            OptSummary = ex.Message;
            OptRows.Clear();
            OptStats.Clear();
            HasOpt = true;
        }
        finally
        {
            IsOptimizing = false;
            Op.End();
        }
    }

    [RelayCommand]
    private void DismissWelcome()
    {
        ShowWelcome = false;
        AppServices.Settings.Current.OnboardingDone = true;
        AppServices.Settings.Save();
    }

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty] private string _cpuFanValue = "—";
    [ObservableProperty] private string _gpuFanValue = "—";
    [ObservableProperty] private string _ramValue = "—";
    [ObservableProperty] private string _ramDetail = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _diskValue = "—";
    [ObservableProperty] private string _diskDetail = "";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _hardwareNote = "";
    [ObservableProperty] private IReadOnlyList<double> _fanHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _ramHistory = Array.Empty<double>();
    private readonly List<double> _fanH = new(), _ramH = new();

    private static void Push(List<double> l, double v) { l.Add(v); if (l.Count > 80) l.RemoveAt(0); }


    [RelayCommand]
    private async Task SelectMode(ModeVm? mode)
    {
        if (mode is null) return;
        await _controller.ApplyAsync(mode.Key);
    }

    partial void OnChangeBrightnessChanged(bool value)
    {
        AppServices.Settings.Current.ChangeBrightness = value;
        AppServices.Settings.Save();
    }

    private void OnBusy(string key) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var m in Modes) m.IsBusy = m.Key == key;
            var title = Modes.FirstOrDefault(m => m.Key == key)?.Title ?? key;
            Op.Begin($"{title} modu uygulanıyor ve doğrulanıyor (güç planı, ekran, fan profili)…");
        });

    private void OnApplied(ModeResult result) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var m in Modes) { m.IsBusy = false; m.IsActive = m.Key == result.Mode.Key; }
            Op.End();

            Steps.Clear();
            foreach (var s in result.Steps) Steps.Add(new StepVm(s));
            var bad = result.Steps.Count(s => s.Status == StepStatus.Failed);
            var warn = result.Steps.Count(s => s.Status == StepStatus.Warning);
            var applied = result.Steps.Any(s => s.Status == StepStatus.Applied);
            ResultTitle = $"{result.Mode.Title} modu";
            ResultSummary = bad > 0 ? $"{bad} ayar doğrulanamadı"
                : warn > 0 ? $"Uygulandı, {warn} uyarı var"
                : applied ? "Windows ayarları doğrulandı, ASUS profili kabul edildi"
                : "Tüm ayarlar uygulandı ve doğrulandı";
            HasResult = true;
        });
    public void Dispose()
    {
        _controller.Applied -= OnApplied;
        _controller.Busy -= OnBusy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
