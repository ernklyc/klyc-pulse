using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.App.ViewModels;

public sealed record ModeOption(string Key, string Title);

/// <summary>Bir oyun profilinin satırı. Değişiklikler anında kaydedilir.</summary>
public partial class GameProfileVm : ObservableObject
{
    private readonly GameProfile _profile;
    private readonly SettingsStore _store;
    private bool _loading = true;

    public GameProfileVm(GameProfile profile, SettingsStore store)
    {
        _profile = profile;
        _store = store;
        _modeKey = profile.ModeKey;
        // Eski sürümde kaydedilen 144 de "ekranın en yükseği" sayılır (sabit 144 artık yok).
        _cpuCapIndex = Math.Max(0, Array.IndexOf(CpuCaps, profile.CpuMaxMhz));
        _refreshIndex = profile.RefreshHz switch { 60 => 1, Core.Modes.Modes.MaxHz or 144 => 2, _ => 0 };
        _enabled = profile.Enabled;
        _loading = false;
        RefreshGpu();
    }

    public GameProfile Profile => _profile;
    public string Name => string.IsNullOrWhiteSpace(_profile.DisplayName) ? _profile.ExeName : _profile.DisplayName;
    public string ExeText => _profile.ExeName + ".exe";

    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private bool _gpuNeedsFix;

    /// <summary>Ekran kartı tercihini Windows kaydından okur.</summary>
    public void RefreshGpu()
    {
        if (string.IsNullOrEmpty(_profile.ExePath)) { GpuText = "Ekran kartı: oyun yolu bilinmiyor (oyunu bir kez açınca öğrenilir)"; GpuNeedsFix = false; return; }
        var pref = Core.Optimize.GameTuning.GetGpuPreference(_profile.ExePath);
        GpuNeedsFix = pref != 2;
        GpuText = pref == 2 ? "Ekran kartı: yüksek performans (NVIDIA) ✓" : "Ekran kartı: Windows seçiyor, Intel ekran kartında çalışabilir";
    }

    public static IReadOnlyList<ModeOption> Modes { get; } = Core.Modes.Modes.All.Select(m => new ModeOption(m.Key, m.Title + " modu")).ToList();
    public static IReadOnlyList<string> CpuCapOptions { get; } = ["Sınırsız (en hızlı)", "En çok 3,8 GHz", "En çok 3,5 GHz (daha serin)", "En çok 3,2 GHz (serin)", "En çok 3,0 GHz (en serin)"];
    private static readonly int?[] CpuCaps = [null, 3800, 3500, 3200, 3000];
    public static IReadOnlyList<string> RefreshOptions { get; } = ["Modun varsayılanı", "60 Hz", "Ekranın en yükseği"];

    [ObservableProperty] private string _modeKey;
    [ObservableProperty] private int _refreshIndex;
    [ObservableProperty] private int _cpuCapIndex;
    [ObservableProperty] private bool _enabled;

    partial void OnModeKeyChanged(string value) => Apply(p => p.ModeKey = value);
    partial void OnRefreshIndexChanged(int value) => Apply(p => p.RefreshHz = value switch { 1 => 60, 2 => Core.Modes.Modes.MaxHz, _ => null });
    partial void OnEnabledChanged(bool value) => Apply(p => p.Enabled = value);
    partial void OnCpuCapIndexChanged(int value) => Apply(p => p.CpuMaxMhz = CpuCaps[Math.Clamp(value, 0, CpuCaps.Length - 1)]);

    private void Apply(Action<GameProfile> change)
    {
        if (_loading) return;
        change(_profile);
        _store.Save();
    }
}

/// <summary>Windows oyun ayarı satırı (HAGS, Windows Oyun Modu, oyun kaydı) ve tek tıkla düzeltme durumu.</summary>
public sealed partial class GameSettingVm : ObservableObject
{
    public GameSettingVm(Core.Optimize.GameSetting setting) { Setting = setting; Update(); }
    public Core.Optimize.GameSetting Setting { get; }
    public string Name => Setting.Name;
    public string Why => Setting.Why;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _canFix;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private System.Windows.Media.Brush _brush = System.Windows.Media.Brushes.Gray;

    public void Update()
    {
        CanFix = !Setting.IsGood;
        Label = Setting.IsGood ? "UYGUN" : "DÜZELTİLEBİLİR";
        State = Setting.IsGood ? "Uygun" : $"Şu an: {Setting.CurrentText}";
        Brush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(Setting.IsGood ? "GoodBrush" : "WarnBrush");
    }
}

public partial class GamesViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store = AppServices.Settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };

    public GamesViewModel()
    {
        AutoGameMode = _store.Current.AutoGameMode;
        Reload();
        LoadGameSettings();
        _timer.Tick += (_, _) => { Reload(); GameText = Detected(); foreach (var g in Games) g.RefreshGpu(); };
        _timer.Start();
        GameText = Detected();
        ShowReport(AppServices.GameReport.Last);
        AppServices.GameReport.Ready += OnReportReady;
    }

    // ---- Son oyun raporu -------------------------------------------------
    public ObservableCollection<string> ReportLines { get; } = new();
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _noReport = true;
    [ObservableProperty] private bool _showSuggestion;
    [ObservableProperty] private string _suggestionText = "";
    [ObservableProperty] private string _reportNote = "";
    private Core.Diagnostics.GameSessionReport? _report;

    [RelayCommand]
    private void ApplySuggestion()
    {
        if (_report?.SuggestedCpuCapMhz is not { } cap) return;
        _store.EnsureProfile(_report.Game, null, null);
        if (_store.FindProfile(_report.Game) is not { } profile) return;
        profile.CpuMaxMhz = cap;
        _store.Save();
        foreach (var g in Games.ToList()) if (ReferenceEquals(g.Profile, profile)) g.CpuCapIndex = Math.Max(0, Array.IndexOf(new int?[] { null, 3800, 3500, 3200, 3000 }, cap));
        ShowSuggestion = false;
        ReportNote = $"Uygulandı: {_report.Game} için işlemci en çok {cap / 1000.0:0.0} GHz. Bir sonraki oyunda geçerli olur; rapor önceki oturumla karşılaştırır. İstersen aşağıdaki listeden değiştirebilirsin.";
    }
    [ObservableProperty] private System.Windows.Media.Brush _reportBrush = System.Windows.Media.Brushes.Gray;

    private void OnReportReady(Core.Diagnostics.GameSessionReport r) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowReport(r));

    private void ShowReport(Core.Diagnostics.GameSessionReport? r)
    {
        _report = r;
        ReportLines.Clear();
        ReportNote = "";
        ShowSuggestion = r?.SuggestedCpuCapMhz is not null;
        SuggestionText = r?.SuggestedCpuCapMhz is { } sc ? $"Bu oyun için işlemciyi en çok {sc / 1000.0:0.0} GHz'e sınırla" : "";
        HasReport = r is not null;
        NoReport = r is null;
        if (r is null) return;
        ReportTitle = r.Title;
        ReportBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(r.Severity switch { 2 => "BadBrush", 1 => "WarnBrush", _ => "GoodBrush" });
        foreach (var f in r.Findings) ReportLines.Add(f);
    }

    public ObservableCollection<GameProfileVm> Games { get; } = new();
    public ObservableCollection<GameSettingVm> GameSettings { get; } = new();
    [ObservableProperty] private string _settingsNote = "";

    private void LoadGameSettings()
    {
        GameSettings.Clear();
        foreach (var s in Core.Optimize.WindowsGameSettings.Read()) GameSettings.Add(new GameSettingVm(s));
        var bad = GameSettings.Count(g => g.CanFix);
        SettingsNote = bad == 0 ? "Windows'un oyun ayarlarının hepsi doğru." : $"{bad} ayar düzeltilebilir.";
    }

    [RelayCommand]
    private void FixSetting(GameSettingVm? vm)
    {
        if (vm is null) return;
        var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var (ok, message) = Core.Optimize.WindowsGameSettings.Fix(vm.Setting, admin);
        vm.Setting.Current = Core.Optimize.WindowsGameSettings.Read().First(s => s.Id == vm.Setting.Id).Current;
        vm.Update();
        SettingsNote = ok ? $"{vm.Name}: {message}" : $"{vm.Name}: {message}";
    }

    [RelayCommand]
    private void FixGpu(GameProfileVm? g)
    {
        if (g?.Profile.ExePath is not { } path) return;
        Core.Optimize.GameTuning.SetHighPerformanceGpu(path);
        g.RefreshGpu();
    }

    [ObservableProperty] private bool _autoGameMode;
    [ObservableProperty] private string _gameText = "";
    [ObservableProperty] private bool _isEmpty;

    partial void OnAutoGameModeChanged(bool value)
    {
        _store.Current.AutoGameMode = value;
        _store.Save();
    }

    private static string Detected() =>
        AppServices.Auto.DetectedGame is { } g ? $"Şu an çalışan oyun: {g}" : "Şu an çalışan oyun algılanmadı.";

    /// <summary>Otomatik öğrenilen yeni oyunları listeye ekler (var olanlara dokunmaz).</summary>
    private void Reload()
    {
        var known = Games.Select(g => g.Profile).ToHashSet();
        foreach (var p in _store.Current.GameProfiles.ToList())
            if (!known.Contains(p)) Games.Add(new GameProfileVm(p, _store));
        for (var i = Games.Count - 1; i >= 0; i--)
            if (!_store.Current.GameProfiles.Contains(Games[i].Profile)) Games.RemoveAt(i);
        IsEmpty = Games.Count == 0;
    }

    [RelayCommand]
    private void AddGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Oyunun .exe dosyasını seç",
            Filter = "Uygulama (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        _store.EnsureProfile(name, name, dlg.FileName);
        Core.Optimize.GameTuning.SetHighPerformanceGpu(dlg.FileName);
        Reload();
        foreach (var g in Games) g.RefreshGpu();
    }

    [RelayCommand]
    private void RemoveGame(GameProfileVm? g)
    {
        if (g is null) return;
        _store.Current.GameProfiles.Remove(g.Profile);
        _store.Save();
        Reload();
    }

    public void Dispose()
    {
        _timer.Stop();
        AppServices.GameReport.Ready -= OnReportReady;
    }
}
