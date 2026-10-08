using Pulse.Core.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Modes;
using Pulse.Core.Platform;
using Pulse.Core.Settings;

namespace Pulse.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store = AppServices.Settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loading = true;

    public SettingsViewModel()
    {
        var s = _store.Current;
        MinimizeToTray = s.MinimizeToTray;
        AutoGameMode = s.AutoGameMode;
        AutoQuietOnBattery = s.AutoQuietOnBattery;
        CloseConflictingApps = s.CloseConflictingApps;
        AutoClean = s.AutoClean;
        AutoCleanText = AutoCleanInfo();
        foreach (var (title, v) in new[] { ("Kapalı", 0), ("75 °C", 75), ("80 °C", 80), ("85 °C", 85), ("90 °C", 90) })
            HeatChoices.Add(new ChoiceVm(Loc.T(title), v) { IsActive = v == (s.HeatTarget ?? 0) });
        HeatText = HeatInfo(s.HeatTarget);
        StartWithWindows = StartupTask.IsEnabled();
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        _loading = false;

        RefreshLive();
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
    }

    public bool IsAdmin { get; }
    public string VersionText { get; } = Loc.F("KLYC-Pulse {0} · MIT lisansı", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));

    [ObservableProperty] private bool _coolingFirst = AppServices.Settings.Current.CoolingFirst;
    partial void OnCoolingFirstChanged(bool value) { Save(s => s.CoolingFirst = value); AppServices.Cooling.Configure(value); }

    // ---- Görünüm: bildirim köşesi, oyunda gösterge, tur ----
    public ObservableCollection<ChoiceVm> NoticeCorners { get; } = BuildCorners();
    private static ObservableCollection<ChoiceVm> BuildCorners()
    {
        var list = new ObservableCollection<ChoiceVm>();
        foreach (var (title, v) in new[] { ("Sol üst", 0), ("Sağ üst", 1), ("Sol alt", 2), ("Sağ alt", 3) })
            list.Add(new ChoiceVm(Loc.T(title), v) { IsActive = v == AppServices.Settings.Current.NoticeCorner });
        return list;
    }

    [RelayCommand]
    private void SetNoticeCorner(ChoiceVm c)
    {
        Save(s => { s.NoticeCorner = c.Value; s.NoticeCornerChosen = true; });
        foreach (var x in NoticeCorners) x.IsActive = x.Value == c.Value;
        NoticeChip.Show(Loc.T("Bildirimler artık burada çıkacak."), false);
    }

    [ObservableProperty] private bool _autoOverlay = AppServices.Settings.Current.AutoOverlay;
    partial void OnAutoOverlayChanged(bool value) => Save(s => s.AutoOverlay = value);

    // ---- Dil ----
    public ObservableCollection<ChoiceVm> Languages { get; } = BuildLanguages();
    private static readonly string[] LanguageKeys = ["auto", "tr", "en"];
    private static ObservableCollection<ChoiceVm> BuildLanguages()
    {
        var cur = Array.IndexOf(LanguageKeys, (AppServices.Settings.Current.Language ?? "auto").ToLowerInvariant());
        var list = new ObservableCollection<ChoiceVm>();
        var i = 0;
        foreach (var title in new[] { "Otomatik / Auto", "Türkçe", "English (beta)" })
        {
            list.Add(new ChoiceVm(title, i) { IsActive = i == Math.Max(cur, 0) });
            i++;
        }
        return list;
    }

    [ObservableProperty] private string _languageNote = "English (beta): arayüz, raporlar ve çoğu mesaj çevrildi; birkaç teknik ayrıntı (sürücü adları gibi) Türkçe/orijinal kalabilir. Değişiklik uygulamayı yeniden açınca geçerli olur. / English (beta): the interface, reports and most messages are translated; a few technical details (such as driver names) may stay in their original form. Takes effect after restarting the app.";

    [RelayCommand]
    private void SetLanguage(ChoiceVm c)
    {
        Save(s => s.Language = LanguageKeys[Math.Clamp(c.Value, 0, 2)]);
        foreach (var x in Languages) x.IsActive = x.Value == c.Value;
        NoticeChip.Show("Dil kaydedildi; uygulamayı yeniden açınca geçerli olur. / Language saved; restart the app to apply.", false);
    }

    [RelayCommand] private void ShowTour() => App.ShowTour();

    private const string SupportUrl = "https://buymeacoffee.com/13tpsxlcea";

    [RelayCommand]
    private void OpenSupportPage()
    {
        try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Destek sayfası açılamadı: " + ex.Message); }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(Pulse.Core.Diagnostics.Journal.Directory)}\"") { UseShellExecute = true }); }
        catch { }
    }

    // ---- Güncellemeler ----
    [ObservableProperty] private bool _checkUpdates = AppServices.Settings.Current.CheckUpdates;
    partial void OnCheckUpdatesChanged(bool value) => Save(s => s.CheckUpdates = value);
    [ObservableProperty] private string _updateStatus = AppServices.Update.Status;
    [ObservableProperty] private bool _updateAvailable = AppServices.Update.Available is not null;

    [RelayCommand]
    private async Task CheckUpdateNow()
    {
        UpdateStatus = "Denetleniyor…";
        await AppServices.Update.CheckAsync(manual: true);
        UpdateStatus = AppServices.Update.Status;
        UpdateAvailable = AppServices.Update.Available is not null;
    }

    [RelayCommand]
    private void OpenReleasePage() => AppServices.Update.OpenReleasePage();

    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoGameMode;
    [ObservableProperty] private bool _autoQuietOnBattery;
    [ObservableProperty] private bool _closeConflictingApps;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _autoClean;
    [ObservableProperty] private bool _keepMode = AppServices.Settings.Current.KeepMode;
    partial void OnKeepModeChanged(bool value) => Save(s => s.KeepMode = value);
    [ObservableProperty] private bool _thermalGuard = AppServices.Settings.Current.ThermalGuard;
    public ObservableCollection<ChoiceVm> HeatChoices { get; } = new();
    [ObservableProperty] private string _heatText = "";

    [RelayCommand]
    private void SetHeat(ChoiceVm c)
    {
        var target = c.Value == 0 ? (int?)null : c.Value;
        Save(s => s.HeatTarget = target);
        AppServices.Heat.Configure(target);
        foreach (var x in HeatChoices) x.IsActive = x.Value == c.Value;
        HeatText = HeatInfo(target);
    }

    private static string HeatInfo(int? t) => t is null
        ? Loc.T("Kapalı. Bilgisayarın fanı yazılımla ayarlanamıyor; bunun yerine bir sıcaklık sınırı koyabilirsin.")
        : Loc.F("Sınır {0} °C. İşlemci bu sıcaklığı geçerse en yüksek hızı küçük adımlarla (her seferinde ~300 MHz) düşürülür, ekran kartı {1}°C'yi geçerse hızı kısılır. Soğuyunca eski hızına döner. Ani düşüş olmaz.", t, Math.Max(65, t.Value - 5));
    [ObservableProperty] private string _autoCleanText = "";
    [ObservableProperty] private string _startupMessage = "";
    [ObservableProperty] private string _conflictsText = "";
    [ObservableProperty] private string _gameText = "";

    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);
    partial void OnAutoGameModeChanged(bool value) => Save(s => s.AutoGameMode = value);
    partial void OnThermalGuardChanged(bool value) { Save(s => s.ThermalGuard = value); AppServices.Guard.Enabled = value; }
    partial void OnAutoCleanChanged(bool value) { Save(s => s.AutoClean = value); AutoCleanText = AutoCleanInfo(); }

    private string AutoCleanInfo()
    {
        var s = _store.Current;
        var last = s.LastAutoClean is { } d ? Loc.F("Son çalışma: {0:dd.MM.yyyy HH:mm}, {1:N0} MB temizlendi.", d, s.LastAutoCleanBytes / 1048576.0) : Loc.T("Henüz çalışmadı.");
        return IsAdmin ? last : last + Loc.T(" Yönetici olarak çalışmıyor, otomatik temizlik çalışmaz.");
    }
    partial void OnAutoQuietOnBatteryChanged(bool value) => Save(s => s.AutoQuietOnBattery = value);
    partial void OnCloseConflictingAppsChanged(bool value) => Save(s => s.CloseConflictingApps = value);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var (ok, msg) = value ? StartupTask.Enable(exe) : StartupTask.Disable();
        StartupMessage = ok ? msg : IsAdmin ? Loc.F("Yapılamadı: {0}", msg) : Loc.T("Bu ayar için KLYC-Pulse'ın yönetici olarak çalışması gerekir.");
        if (!ok)
        {
            _loading = true;
            StartWithWindows = StartupTask.IsEnabled();
            _loading = false;
        }
        else Save(s => s.StartWithWindows = value);
    }

    [RelayCommand]
    private void CloseConflicts()
    {
        var closed = ConflictService.CloseAll();
        ConflictsText = closed.Count > 0 ? Loc.F("Kapatıldı: {0}", string.Join(", ", closed)) : Loc.T("Kapatılacak uygulama yoktu.");
    }

    private void RefreshLive()
    {
        var running = ConflictService.Running();
        if (!ConflictsText.StartsWith(Loc.T("Kapatıldı:"), StringComparison.Ordinal))
            ConflictsText = running.Count > 0 ? Loc.F("Şu an çalışıyor: {0}", string.Join(", ", running)) : Loc.T("Çakışan uygulama çalışmıyor.");
        GameText = AppServices.Auto.DetectedGame is { } g ? Loc.F("Algılanan oyun: {0}", g) : Loc.T("Şu an çalışan oyun algılanmadı.");
    }

    private void Save(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_store.Current);
        _store.Save();
    }

    public void Dispose() => _timer.Stop();
}