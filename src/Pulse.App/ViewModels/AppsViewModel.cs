using Pulse.Core.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Apps;
using Pulse.Core.Cleanup;
using Pulse.Core.Platform;

namespace Pulse.App.ViewModels;

public partial class TabVm : ObservableObject
{
    public TabVm(int index, string title) { Index = index; Title = title; }
    public int Index { get; }
    public string Title { get; }
    [ObservableProperty] private bool _isActive;
}

public partial class UpdateVm : ObservableObject
{
    public UpdateVm(UpgradeInfo u) { Info = u; }
    public UpgradeInfo Info { get; }
    public string Name => Info.Name;
    public string Id => Info.Id;
    public string Versions => $"{Info.Current}  →  {Info.Available}";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDone;
}

public sealed class AppVm
{
    public AppVm(InstalledApp a)
    {
        App = a;
        SizeText = a.SizeBytes > 0 ? FolderVm.Format(a.SizeBytes) : "—";
        DateText = a.InstallDate?.ToString("yyyy-MM-dd") ?? "";
        var hint = BloatCatalog.Evaluate(a);
        Hint = hint?.Reason ?? "";
        HintLabel = hint?.Level switch { BloatLevel.Recommended => Loc.T("Gereksiz olabilir"), BloatLevel.Optional => Loc.T("İsteğe bağlı"), _ => "" };
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string SizeText { get; }
    public string DateText { get; }
    public string Hint { get; }
    public string HintLabel { get; }
    public bool CanUninstall => App.CanUninstall;
}

public partial class StartupVm : ObservableObject
{
    private readonly AppsViewModel _owner;
    private bool _loading = true;

    public StartupVm(StartupItem item, AppsViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isEnabled = item.Enabled;
        _loading = false;
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Detail => string.IsNullOrWhiteSpace(Item.Publisher) ? Item.Location : $"{Item.Publisher}  ·  {Item.Location}";

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        if (!StartupManager.SetEnabled(Item, value))
        {
            _loading = true;
            IsEnabled = !value;
            _loading = false;
            _owner.Status = Item.Machine ? Loc.T("Sistem öğesini değiştirmek için KLYC-Pulse'ın yönetici olarak çalışması gerekir.") : Loc.T("Bu öğe değiştirilemedi.");
        }
        else _owner.Status = value ? Loc.F("{0} açılışta başlayacak.", Item.Name) : Loc.F("{0} açılışta başlamayacak.", Item.Name);
    }
}

/// <summary>Arka planda başlayan bir servis ya da görev satırı. Anahtar kapalıysa Windows ile başlamaz.</summary>
public partial class BackgroundVm : ObservableObject
{
    private readonly AppsViewModel _owner;
    private bool _loading = true;

    public BackgroundVm(BgItem item, AppsViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isEnabled = item.StartsAtBoot;
        _loading = false;
        (Label, BrushKey) = item.Advice switch
        {
            BgAdvice.Keep => ("DOKUNMA", "GoodBrush"),
            BgAdvice.Optional => (Loc.T("KAPATILABİLİR"), "WarnBrush"),
            _ => (Loc.T("BİLİNMİYOR"), "MutedBrush"),
        };
        CanToggle = item.Advice == BgAdvice.Optional || (item.Advice == BgAdvice.Unknown && !item.StartsAtBoot);
    }

    public BgItem Item { get; }
    public string Name => Item.Display;
    public string Detail => Item.IsService ? Loc.F("Servis  ·  {0}  ·  {1}", Loc.T(Item.Running ? "çalışıyor" : "durmuş"), Item.Reason) : Loc.F("Görev  ·  {0}", Item.Reason);
    public string Label { get; }
    public string BrushKey { get; }
    public System.Windows.Media.Brush Brush => (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(BrushKey);
    public bool CanToggle { get; }

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading) return;
        var (ok, message) = _owner.Background.SetStartAtBoot(Item, value);
        if (!ok)
        {
            _loading = true;
            IsEnabled = !value;
            _loading = false;
        }
        _owner.Status = $"{Item.Display}: {message}";
    }
}

public partial class AppsViewModel : ObservableObject
{
    public BackgroundInspector Background { get; } = new();
    public ObservableCollection<BackgroundVm> BackgroundItems { get; } = new();
    [ObservableProperty] private string _backgroundNote = "";

    private IReadOnlyList<AppVm> _allApps = [];
    private bool _updatesLoaded;

    public AppsViewModel()
    {
        Tabs = new ObservableCollection<TabVm>
        {
            new(0, Loc.T("Güncellemeler")), new(1, Loc.T("Kurulu uygulamalar")), new(2, Loc.T("Açılışta başlayanlar")), new(3, Loc.T("Arka plan")),
        };
        Tabs[0].IsActive = true;
        _ = LoadAsync();
    }

    public ObservableCollection<TabVm> Tabs { get; }
    public ObservableCollection<UpdateVm> Updates { get; } = new();
    public ObservableCollection<AppVm> Apps { get; } = new();
    public ObservableCollection<StartupVm> Startups { get; } = new();

    public OperationVm Op { get; } = new();
    [ObservableProperty] private int _tab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _updatesNote = Loc.T("Güncellemeler denetleniyor…");
    [ObservableProperty] private string _appsNote = "";

    partial void OnSearchChanged(string value) => FilterApps();

    [RelayCommand]
    private async Task SelectTab(TabVm? t)
    {
        if (t is null) return;
        Tab = t.Index;
        foreach (var x in Tabs) x.IsActive = x.Index == t.Index;
        if (t.Index == 0 && !_updatesLoaded) await RefreshUpdates();
        if (t.Index == 3) await LoadBackground();
    }

    private async Task LoadAsync()
    {
        var apps = await Task.Run(() => InstalledAppsReader.Read().Select(a => new AppVm(a)).ToList());
        _allApps = apps;
        FilterApps();
        var startups = await Task.Run(StartupManager.List);
        Startups.Clear();
        foreach (var s in startups) Startups.Add(new StartupVm(s, this));
        await RefreshUpdates();
    }

    private async Task LoadBackground()
    {
        Op.Begin(Loc.T("Servisler ve zamanlanmış görevler taranıyor…"));
        var items = await Task.Run(Background.List);
        Op.End();
        BackgroundItems.Clear();
        foreach (var i in items) BackgroundItems.Add(new BackgroundVm(i, this));
        var optional = items.Count(i => i.Advice == BgAdvice.Optional && i.StartsAtBoot);
        BackgroundNote = Loc.F("{0} öğe. {1} tanesi kapatılabilir; kapatmak silmez, istediğin an geri açılır.", items.Count, optional);
    }

    private void FilterApps()
    {
        var q = Search.Trim();
        var list = _allApps
            .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(a => a.App.SizeBytes).ThenBy(a => a.Name).ToList();
        Apps.Clear();
        foreach (var a in list) Apps.Add(a);
        var hints = _allApps.Count(a => a.HintLabel.Length > 0);
        AppsNote = Loc.F("{0} uygulama", _allApps.Count) + (hints > 0 ? Loc.F(", {0} tanesi için öneri var", hints) : "");
    }

    [RelayCommand]
    private async Task RefreshUpdates()
    {
        UpdatesNote = Loc.T("Güncellemeler denetleniyor…");
        Op.Begin(Loc.T("Güncellemeler denetleniyor (winget)…"));
        var (items, error) = await WingetService.GetUpgradesAsync();
        Op.End();
        Updates.Clear();
        foreach (var i in items) Updates.Add(new UpdateVm(i));
        _updatesLoaded = true;
        UpdatesNote = error ?? (items.Count == 0 ? Loc.T("Tüm uygulamalar güncel.") : Loc.F("{0} uygulamanın güncellemesi var.", items.Count));
    }

    [RelayCommand]
    private async Task UpdateOne(UpdateVm? u)
    {
        if (u is null || u.IsBusy) return;
        u.IsBusy = true; u.State = Loc.T("Güncelleniyor…");
        var (ok, _) = await WingetService.UpgradeAsync(u.Id);
        u.IsBusy = false; u.IsDone = ok;
        u.State = ok ? Loc.T("Güncellendi") : Loc.T("Güncellenemedi (yönetici izni ya da açık uygulama olabilir)");
    }

    [RelayCommand]
    private async Task UpdateAll()
    {
        if (IsBusy) return;
        IsBusy = true;
        var pending = Updates.Where(x => !x.IsDone).ToList();
        Op.Begin(Loc.T("Güncelleniyor…"), pending.Count);
        try
        {
            foreach (var u in pending)
            {
                Op.Step(Loc.F("Güncelleniyor: {0}", u.Name));
                await UpdateOne(u);
            }
        }
        finally { IsBusy = false; Op.End(); }
        UpdatesNote = Loc.F("{0} / {1} güncellendi.", Updates.Count(x => x.IsDone), Updates.Count);
    }

    [RelayCommand]
    private async Task Uninstall(AppVm? a)
    {
        if (a is null || IsBusy) return;
        var ok = System.Windows.MessageBox.Show(
            Loc.F("“{0}” kaldırılacak.\n\nUygulamanın kendi kaldırıcısı çalışır. Bitince geride kalan klasörler varsa ayrıca sorulur.", a.Name),
            Loc.T("Kaldırmayı onayla"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Op.Begin(Loc.T("Geri Yükleme Noktası alınıyor (güvenlik için)…"), 3);
        try
        {
            Status = Loc.T("Geri Yükleme Noktası alınıyor…");
            Op.Step(Loc.T("Geri Yükleme Noktası alınıyor (güvenlik için)…"));
            var rp = await RestorePoint.CreateAsync(Loc.F("KLYC-Pulse: {0} kaldırma öncesi", a.Name));
            Status = Loc.F("{0} {1} kaldırılıyor…", rp.Message, a.Name);
            Op.Step(Loc.F("{0} kaldırılıyor (kaldırıcı penceresini onayla)…", a.Name));
            var code = await AppUninstaller.UninstallAsync(a.App, preferQuiet: false);
            var stillThere = await Task.Run(() => InstalledAppsReader.Read().Any(x => x.RegistryKey == a.App.RegistryKey));
            if (stillThere) { Status = code == 0 ? Loc.F("{0} hâlâ kurulu görünüyor (kaldırma iptal edilmiş olabilir).", a.Name) : Loc.F("{0} kaldırılamadı.", a.Name); return; }

            Op.Step(Loc.T("Artık dosyalar aranıyor…"));
            var left = await Task.Run(() => AppUninstaller.FindLeftovers(a.App));
            if (left.Count > 0)
            {
                var list = string.Join("\n", left.Select(l => $"• {l.Path}  ({FolderVm.Format(l.Bytes)})"));
                var del = System.Windows.MessageBox.Show(
                    Loc.F("{0} kaldırıldı. Geride şu klasörler kalmış:\n\n{1}\n\nGeri Dönüşüm Kutusu'na gönderilsin mi? (Geri alınabilir)", a.Name, list),
                    Loc.T("Artık dosyalar"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (del == System.Windows.MessageBoxResult.Yes)
                {
                    var n = left.Count(AppUninstaller.RemoveLeftover);
                    Status = Loc.F("{0} kaldırıldı, {1} artık klasör Geri Dönüşüm Kutusu'na gönderildi.", a.Name, n);
                }
                else Status = Loc.F("{0} kaldırıldı. Artık klasörlere dokunulmadı.", a.Name);
            }
            else Status = Loc.F("{0} kaldırıldı, artık dosya bulunamadı.", a.Name);

            _allApps = await Task.Run(() => InstalledAppsReader.Read().Select(x => new AppVm(x)).ToList());
            FilterApps();
        }
        finally { IsBusy = false; Op.End(); }
    }
}
