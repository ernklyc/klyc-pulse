using Pulse.Core.Localization;
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
        BuildCaps();
        _cpuCapIndex = Math.Max(0, Array.IndexOf(_caps, profile.CpuMaxMhz));
        _autoTune = profile.AutoTune;
        _autoTuneNote = profile.AutoTuneNote ?? "";
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
        if (string.IsNullOrEmpty(_profile.ExePath)) { GpuText = Loc.T("Ekran kartı: oyun yolu bilinmiyor (oyunu bir kez açınca öğrenilir)"); GpuNeedsFix = false; return; }
        var pref = Core.Optimize.GameTuning.GetGpuPreference(_profile.ExePath);
        GpuNeedsFix = pref != 2;
        GpuText = pref == 2 ? Loc.T("Ekran kartı: yüksek performans (NVIDIA) ✓") : Loc.T("Ekran kartı: Windows seçiyor, Intel ekran kartında çalışabilir");
    }

    public static IReadOnlyList<ModeOption> Modes { get; } = Core.Modes.Modes.All.Select(m => new ModeOption(m.Key, Loc.F("{0} modu", Loc.T(m.Title)))).ToList();
    /// <summary>İşlemci hız sınırı seçenekleri: bu bilgisayarın gerçek hızından türetilir (her işlemciye uyar).</summary>
    public ObservableCollection<string> CpuCapOptions { get; } = new();
    private int?[] _caps = [null];

    /// <summary>Kademeleri (yeniden) kurar; profilde ladder dışı bir değer varsa "elle" olarak eklenir. Değiştiyse true.</summary>
    private bool BuildCaps()
    {
        var ladder = AppServices.GameLadder().ToList();
        if (_profile.CpuMaxMhz is { } cur && !ladder.Contains(cur)) ladder.Add(cur);
        var caps = ladder.OrderBy(x => x is null ? 0 : 1).ThenByDescending(x => x ?? 0).ToArray();
        if (caps.SequenceEqual(_caps) && CpuCapOptions.Count == caps.Length) return false;
        _caps = caps;
        var onLadder = AppServices.GameLadder();
        CpuCapOptions.Clear();
        for (var i = 0; i < caps.Length; i++)
            CpuCapOptions.Add(caps[i] is not { } mhz ? Loc.T("Sınırsız (en hızlı)")
                : onLadder.Contains(mhz) ? Core.Hardware.CpuLadder.Label(i, caps.Length, mhz) : Loc.F("En çok {0:0.0} GHz (elle)", mhz / 1000.0));
        return true;
    }
    public static IReadOnlyList<string> RefreshOptions { get; } = [Loc.T("Modun varsayılanı"), "60 Hz", Loc.T("Ekranın en yükseği")];

    [ObservableProperty] private string _modeKey;
    [ObservableProperty] private int _refreshIndex;
    [ObservableProperty] private int _cpuCapIndex;
    [ObservableProperty] private bool _autoTune;
    [ObservableProperty] private string _autoTuneNote = "";

    /// <summary>Raporlardan sonra otomatik ayar sınırı/notu değiştirdiyse kartı yeniler.</summary>
    public void SyncAuto()
    {
        _loading = true;
        BuildCaps();
        CpuCapIndex = Math.Max(0, Array.IndexOf(_caps, _profile.CpuMaxMhz));
        AutoTuneNote = _profile.AutoTuneNote ?? "";
        _loading = false;
    }
    [ObservableProperty] private bool _enabled;

    partial void OnModeKeyChanged(string value) => Apply(p => p.ModeKey = value);
    partial void OnRefreshIndexChanged(int value) => Apply(p => p.RefreshHz = value switch { 1 => 60, 2 => Core.Modes.Modes.MaxHz, _ => null });
    partial void OnEnabledChanged(bool value) => Apply(p => p.Enabled = value);
    // Elle seçilen sınıra otomatik ayar dokunmaz (Otomatik ayarı kapatıp açınca yeniden öğrenir).
    partial void OnCpuCapIndexChanged(int value) => Apply(p =>
    {
        p.CpuMaxMhz = _caps[Math.Clamp(value, 0, _caps.Length - 1)];
        p.AutoTuneLocked = true;
        p.AutoTuneNote = Loc.T("Elle seçildi; otomatik ayar dokunmuyor. Otomatik ayarı kapatıp açarsan yeniden öğrenir.");
        AutoTuneNote = p.AutoTuneNote;
    });
    partial void OnAutoTuneChanged(bool value) => Apply(p => { p.AutoTune = value; if (value) p.AutoTuneLocked = false; });

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
        Label = Loc.T(Setting.IsGood ? "UYGUN" : "DÜZELTİLEBİLİR");
        State = Setting.IsGood ? Loc.T("Uygun") : Loc.F("Şu an: {0}", Loc.T(Setting.CurrentText));
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
        AutoTuneGames = _store.Current.AutoTuneGames;
        RefreshFreqCapText();
        _timer.Tick += (_, _) => { Reload(); GameText = Detected(); foreach (var g in Games) { g.RefreshGpu(); g.SyncAuto(); } };
        _timer.Start();
        GameText = Detected();
        ShowReport(AppServices.GameReport.Last);
        AppServices.GameReport.Ready += OnReportReady;
    }

    // ---- İşlemci hız sınırı desteği ---------------------------------------
    [ObservableProperty] private string _freqCapText = "";
    [ObservableProperty] private bool _probing;

    private void RefreshFreqCapText()
    {
        var s = _store.Current;
        var peak = s.CpuPeakMhz > 0 ? Loc.F(" Öğrenilen tepe hız: {0:0.0} GHz.", s.CpuPeakMhz / 1000.0) : "";
        FreqCapText = s.FreqCapSupported switch
        {
            true => Loc.T("✓ Bu bilgisayarda işlemci hız sınırı çalışıyor. ") + s.FreqCapNote + peak,
            false => "✗ " + s.FreqCapNote + Loc.T(" Hız sınırı özellikleri bu bilgisayarda kapalı."),
            _ => Loc.T("İşlemci hız sınırının bu bilgisayarda çalışıp çalışmadığı henüz denenmedi. Otomatik ayar ilk sınırı koymadan önce kendiliğinden dener.") + peak,
        };
    }

    [RelayCommand]
    private async Task ProbeFreqCap()
    {
        if (Probing || FreqCapService.IsRunning) return;
        var ok = System.Windows.MessageBox.Show(
            Loc.T("İşlemcinin hız sınırı bu bilgisayarda çalışıyor mu diye ölçülecek. Bunun için işlemci yaklaşık 20 saniye tam yüklenir (fan hızlanır). Oyun açıkken yapma. Devam edilsin mi?"),
            Loc.T("Hız sınırı denemesi"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        Probing = true;
        FreqCapText = Loc.T("Deneniyor… (yaklaşık 20 saniye, işlemci tam yüklenir)");
        try { await FreqCapService.RunAsync(); }
        finally { Probing = false; RefreshFreqCapText(); foreach (var g in Games) g.SyncAuto(); }
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
        foreach (var g in Games.ToList()) if (ReferenceEquals(g.Profile, profile)) g.SyncAuto();
        ShowSuggestion = false;
        ReportNote = Loc.F("Uygulandı: {0} için işlemci en çok {1:0.0} GHz. Bir sonraki oyunda geçerli olur; rapor önceki oturumla karşılaştırır. İstersen aşağıdaki listeden değiştirebilirsin.", _report.Game, cap / 1000.0);
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
        SuggestionText = r?.SuggestedCpuCapMhz is { } sc ? Loc.F("Bu oyun için işlemciyi en çok {0:0.0} GHz'e sınırla", sc / 1000.0) : "";
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
        SettingsNote = bad == 0 ? Loc.T("Windows'un oyun ayarlarının hepsi doğru.") : Loc.F("{0} ayar düzeltilebilir.", bad);
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
    [ObservableProperty] private bool _autoTuneGames;
    partial void OnAutoTuneGamesChanged(bool value)
    {
        _store.Current.AutoTuneGames = value;
        _store.Save();
    }
    [ObservableProperty] private string _gameText = "";
    [ObservableProperty] private bool _isEmpty;

    partial void OnAutoGameModeChanged(bool value)
    {
        _store.Current.AutoGameMode = value;
        _store.Save();
    }

    private static string Detected() =>
        AppServices.Auto.DetectedGame is { } g ? Loc.F("Şu an çalışan oyun: {0}", g) : Loc.T("Şu an çalışan oyun algılanmadı.");

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
            Title = Loc.T("Oyunun .exe dosyasını seç"),
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
