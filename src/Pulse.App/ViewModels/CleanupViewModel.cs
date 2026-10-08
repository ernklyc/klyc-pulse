using Pulse.Core.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Cleanup;
using Pulse.Core.Platform;

namespace Pulse.App.ViewModels;

public partial class CategoryVm : ObservableObject
{
    public CategoryVm(CleanupCategory c)
    {
        Category = c;
        IsSelected = c.SelectedByDefault && !(c.NeedsAdmin && !CleanupEngine.IsAdmin);
        Badge = c.Safety == CleanupSafety.Quarantine ? Loc.T("7 gün karantina") : c.NeedsAdmin && !CleanupEngine.IsAdmin ? Loc.T("Yönetici gerekir") : "";
    }

    public CleanupCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Badge { get; }
    public bool Unavailable => Category.NeedsAdmin && !CleanupEngine.IsAdmin;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private long _bytes;
}

public sealed class RestorePointVm
{
    public RestorePointVm(RestorePoint.Info i) { Title = i.Description; DateText = i.Time.ToString("dd.MM.yyyy HH:mm"); }
    public string Title { get; }
    public string DateText { get; }
}

public sealed class FolderVm
{
    public FolderVm(FolderSize f)
    {
        Path = f.Path;
        SizeText = Format(f.Bytes);
        DateText = f.LastWrite.ToString("yyyy-MM-dd");
    }

    public string Path { get; }
    public string SizeText { get; }
    public string DateText { get; }
    public static string Format(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:N1} GB" : b >= 1L << 20 ? $"{b / 1048576.0:N0} MB" : $"{b / 1024.0:N0} KB";
}

/// <summary>Eski kalıntı adayı: kullanıcı inceler, seçerse Geri Dönüşüm Kutusu'na gider. Varsayılan: seçili değil.</summary>
public sealed partial class OrphanVm : ObservableObject
{
    public OrphanVm(OrphanFolder f)
    {
        Folder = f;
        Detail = Loc.F("{0}  ·  son kullanım {1:dd.MM.yyyy}  ·  {2}", f.Where, f.LastActivity, f.Path);
        SizeText = FolderVm.Format(f.Bytes);
    }

    public OrphanFolder Folder { get; }
    public string Name => Folder.Name;
    public string Detail { get; }
    public string SizeText { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class DupFileVm : ObservableObject
{
    public DupFileVm(DupFile f, bool keeper)
    {
        File = f;
        IsKeeper = keeper;
        Path = f.Path;
        Detail = $"{f.Modified:dd.MM.yyyy}" + (keeper ? Loc.T("  ·  KORUNUR (asıl kopya)") : "");
    }

    public DupFile File { get; }
    public bool IsKeeper { get; }
    public bool CanSelect => !IsKeeper;
    public string Path { get; }
    public string Detail { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed class DupGroupVm
{
    public DupGroupVm(DuplicateGroup g)
    {
        Group = g;
        Header = Loc.F("{0} aynı dosya  ·  {1} × {2} fazla = {3} boşa", g.Files.Count, FolderVm.Format(g.Size), g.Files.Count - 1, FolderVm.Format(g.WastedBytes));
        Name = System.IO.Path.GetFileName(g.Keeper.Path);
        Files = g.Files.Select((f, i) => new DupFileVm(f, i == 0)).ToList();
    }

    public DuplicateGroup Group { get; }
    public string Header { get; }
    public string Name { get; }
    public List<DupFileVm> Files { get; }
}

public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupEngine _engine = new();

    // ---- Kopya dosyalar ----------------------------------------------------
    public ObservableCollection<DupGroupVm> DupGroups { get; } = new();
    [ObservableProperty] private bool _hasDups;
    [ObservableProperty] private string _dupNote = Loc.T("Belgeler, Masaüstü, Resimler, Videolar, Müzik ve İndirilenler klasörlerinde birebir aynı büyük dosyaları bulur. Hiçbir şeyi kendiliğinden silmez.");

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin(Loc.T("Kopya dosyalar aranıyor…"));
        DupNote = Loc.T("Aranıyor…");
        try
        {
            var progress = new Progress<string>(m => { Op.Message(m); });
            var found = await Task.Run(() => DuplicateFinder.Find(DuplicateFinder.DefaultRoots(), progress: progress));
            DupGroups.Clear();
            foreach (var g in found) DupGroups.Add(new DupGroupVm(g));
            HasDups = DupGroups.Count > 0;
            DupNote = DupGroups.Count == 0
                ? Loc.T("Birebir aynı büyük dosya bulunamadı.")
                : Loc.F("{0} grup, fazladan {1}. Her grupta en eski kopya korunur, silinmez. Hiçbiri seçili değil.", DupGroups.Count, FolderVm.Format(found.Sum(g => g.WastedBytes)));
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private void SelectDupExtras()
    {
        foreach (var g in DupGroups) foreach (var f in g.Files) f.IsSelected = !f.IsKeeper;
        DupNote = Loc.T("Her grubun korunan kopyası dışındakiler seçildi. Gözden geçirip gönderebilirsin.");
    }

    [RelayCommand]
    private async Task RemoveDuplicates()
    {
        if (IsBusy) return;
        var chosen = DupGroups.SelectMany(g => g.Files.Where(f => f.IsSelected && !f.IsKeeper).Select(f => (g, f))).ToList();
        if (chosen.Count == 0) { DupNote = Loc.T("Hiçbir dosya seçili değil."); return; }
        var total = chosen.Sum(c => c.f.File.Bytes);
        var ok = System.Windows.MessageBox.Show(
            Loc.F("{0} fazlalık kopya ({1}) Geri Dönüşüm Kutusu'na gönderilecek. Her grubun asıl kopyası yerinde kalır. Kutudan geri alabilirsin. Devam edilsin mi?", chosen.Count, FolderVm.Format(total)),
            Loc.T("Kopyaları onayla"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Op.Begin(Loc.T("Geri Dönüşüm Kutusu'na gönderiliyor…"), chosen.Count);
        try
        {
            long freed = 0; var done = 0;
            foreach (var (g, f) in chosen)
            {
                Op.Step(Loc.F("Gönderiliyor: {0}", System.IO.Path.GetFileName(f.Path)));
                var sent = await Task.Run(() => DuplicateFinder.Remove(g.Group, f.File));
                Pulse.Core.Diagnostics.Journal.Write($"Kopya dosya Geri Dönüşüm Kutusu'na {(sent ? "gönderildi" : "gönderilemedi")}: {f.Path}");
                if (sent) { freed += f.File.Bytes; done++; }
            }
            DupNote = Loc.F("{0}/{1} kopya Geri Dönüşüm Kutusu'na gönderildi ({2}). Alan, kutuyu boşaltınca açılır. Listeyi yenilemek için “Kopyaları bul”a bas.", done, chosen.Count, FolderVm.Format(freed));
        }
        finally { IsBusy = false; Op.End(); }
    }
    private readonly QuarantineStore _quarantine = new();

    // ---- Eski kalıntılar ---------------------------------------------------
    public ObservableCollection<OrphanVm> Orphans { get; } = new();
    [ObservableProperty] private bool _hasOrphans;
    [ObservableProperty] private string _orphanNote = Loc.T("Eskiden silinmiş uygulamaların bıraktığı klasörleri bulur. Hiçbir şeyi kendiliğinden silmez.");

    [RelayCommand]
    private async Task FindOrphans()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin(Loc.T("Eski kalıntılar aranıyor…"));
        OrphanNote = Loc.T("Aranıyor…");
        try
        {
            var found = await Task.Run(() => OrphanScanner.Scan(Core.Apps.InstalledAppsReader.Read()));
            Orphans.Clear();
            foreach (var f in found) Orphans.Add(new OrphanVm(f));
            HasOrphans = Orphans.Count > 0;
            OrphanNote = Orphans.Count == 0
                ? Loc.T("Kalıntı bulunamadı.")
                : Loc.F("{0} aday, toplam {1}. Hiçbiri seçili değil; emin olduklarını işaretle. Silinenler Geri Dönüşüm Kutusu'na gider, geri alabilirsin.", Orphans.Count, FolderVm.Format(found.Sum(o => o.Bytes)));
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task RemoveOrphans()
    {
        if (IsBusy) return;
        var chosen = Orphans.Where(o => o.IsSelected).ToList();
        if (chosen.Count == 0) { OrphanNote = Loc.T("Hiçbir klasör seçili değil."); return; }

        var list = string.Join("\n", chosen.Select(o => $"• {o.Name}  ({o.SizeText})"));
        var ok = System.Windows.MessageBox.Show(
            Loc.F("Şu klasörler Geri Dönüşüm Kutusu'na gönderilecek:\n\n{0}\n\nİçlerinde işine yarayan bir şey (oyun kayıtları, ayarlar) olabilir. Emin değilsen “Hayır” de. Kutudan geri alabilirsin. Devam edilsin mi?", list),
            Loc.T("Kalıntıları onayla"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Op.Begin(Loc.T("Geri Dönüşüm Kutusu'na gönderiliyor…"), chosen.Count);
        try
        {
            long freed = 0; var done = 0;
            foreach (var o in chosen)
            {
                Op.Step(Loc.F("Gönderiliyor: {0}", o.Name));
                var sent = await Task.Run(() => OrphanScanner.Remove(o.Folder));
                Pulse.Core.Diagnostics.Journal.Write($"Kalıntı klasör Geri Dönüşüm Kutusu'na {(sent ? "gönderildi" : "gönderilemedi")}: {o.Folder.Path} ({o.SizeText})");
                if (sent) { freed += o.Folder.Bytes; done++; Orphans.Remove(o); }
            }
            HasOrphans = Orphans.Count > 0;
            OrphanNote = Loc.F("{0}/{1} klasör Geri Dönüşüm Kutusu'na gönderildi ({2}). Alan, kutuyu boşaltınca açılır.", done, chosen.Count, FolderVm.Format(freed));
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private void OpenOrphan(OrphanVm? o)
    {
        if (o is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{o.Folder.Path}\"") { UseShellExecute = true });
    }

    public CleanupViewModel()
    {
        foreach (var c in CleanupCatalog.Build()) Categories.Add(new CategoryVm(c));
        RefreshQuarantine();
        _ = LoadRestorePoints();
    }

    public ObservableCollection<CategoryVm> Categories { get; } = new();
    public ObservableCollection<FolderVm> Folders { get; } = new();
    public ObservableCollection<RestorePointVm> RestorePoints { get; } = new();

    public OperationVm Op { get; } = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = Loc.T("Taramak için “Tara”ya bas.");
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _totalNote = Loc.T("temizlenebilir");
    [ObservableProperty] private string _quarantineText = "";
    [ObservableProperty] private bool _hasFolders;
    [ObservableProperty] private string _restoreNote = Loc.T("Geri Yükleme Noktaları okunuyor…");
    [ObservableProperty] private bool _hasRestorePoints;

    [RelayCommand]
    private async Task LoadRestorePoints()
    {
        RestoreNote = Loc.T("Geri Yükleme Noktaları okunuyor…");
        var (items, error) = await RestorePoint.ListAsync();
        RestorePoints.Clear();
        foreach (var i in items) RestorePoints.Add(new RestorePointVm(i));
        HasRestorePoints = RestorePoints.Count > 0;
        RestoreNote = error ?? (items.Count == 0 ? Loc.T("Henüz Geri Yükleme Noktası yok. KLYC-Pulse sistem temizliğinden ve uygulama kaldırmadan önce kendiliğinden alır.") : Loc.F("{0} Geri Yükleme Noktası var.", items.Count));
    }

    [RelayCommand]
    private void OpenSystemRestore() => RestorePoint.OpenSystemRestore();

    [RelayCommand]
    private async Task Scan()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = Loc.T("Taranıyor…");
        Op.Begin(Loc.T("Taranıyor…"), Categories.Count);
        try
        {
            var progress = new Progress<string>(name => { Status = Loc.F("Taranıyor: {0}", name); Op.Step(Loc.F("Taranıyor: {0}", name)); });
            var scans = await _engine.ScanAsync(Categories.Select(c => c.Category), progress);
            foreach (var s in scans)
            {
                var vm = Categories.First(c => c.Category.Id == s.Category.Id);
                vm.Bytes = s.Bytes;
                vm.SizeText = s.Note is not null ? s.Note : s.Category.Special == "dism" ? "—" : FolderVm.Format(s.Bytes);
                if (vm.Unavailable) vm.IsSelected = false;
            }
            UpdateTotal();
            Status = Loc.T("Tarama bitti. İstemediklerinin işaretini kaldırıp “Seçilenleri temizle”ye bas.");
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task Clean()
    {
        if (IsBusy) return;
        var chosen = Categories.Where(c => c.IsSelected && !c.Unavailable).ToList();
        if (chosen.Count == 0) { Status = Loc.T("Hiçbir kategori seçili değil."); return; }

        var list = string.Join("\n", chosen.Select(c => "• " + c.Name + (c.Badge.Length > 0 ? $"  ({c.Badge})" : "")));
        var ok = System.Windows.MessageBox.Show(
            Loc.F("Şunlar temizlenecek:\n\n{0}\n\nÖnbellekler ve geçici dosyalar silinir. Eski kurulum dosyaları silinmez, 7 gün karantinada tutulur. Devam edilsin mi?", list),
            Loc.T("Temizliği onayla"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        var needsRestore = chosen.Any(c => c.Category.NeedsAdmin);
        Op.Begin("Temizleniyor…", chosen.Count + (needsRestore ? 1 : 0));
        try
        {
            if (needsRestore)
            {
                Status = Loc.T("Geri Yükleme Noktası alınıyor…");
                Op.Step(Loc.T("Geri Yükleme Noktası alınıyor (güvenlik için)…"));
                var rp = await RestorePoint.CreateAsync(Loc.T("KLYC-Pulse sistem temizliği öncesi"));
                Status = rp.Message;
            }
            var progress = new Progress<string>(name => { Status = $"Temizleniyor: {name}"; Op.Step($"Temizleniyor: {name}"); });
            var results = await _engine.CleanAsync(chosen.Select(c => c.Category), progress);
            long total = 0;
            foreach (var r in results)
            {
                total += r.FreedBytes;
                var vm = Categories.First(c => c.Category.Id == r.Category.Id);
                vm.Bytes = 0;
                vm.SizeText = r.Note ?? "temizlendi";
            }
            UpdateTotal();
            Status = Loc.F("Bitti. {0} alan açıldı.", FolderVm.Format(total));
            RefreshQuarantine();
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task Analyze()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = Loc.T("Disk analiz ediliyor (birkaç dakika sürebilir)…");
        Op.Begin(Loc.T("Disk analiz ediliyor (birkaç dakika sürebilir)…"));
        try
        {
            var progress = new Progress<string>(p => { Status = $"Analiz: {p}"; Op.Message($"Analiz: {p}"); });
            var found = await DiskAnalyzer.AnalyzeAsync(DiskAnalyzer.DefaultRoots(), progress: progress);
            Folders.Clear();
            foreach (var f in found) Folders.Add(new FolderVm(f));
            HasFolders = Folders.Count > 0;
            Status = Loc.T("Analiz bitti. Silme kararı sana ait, KLYC-Pulse bu klasörlere dokunmaz.");
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private void OpenFolder(FolderVm? f)
    {
        if (f is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f.Path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void RestoreQuarantine()
    {
        var n = 0;
        foreach (var i in _quarantine.List()) if (_quarantine.Restore(i.Id)) n++;
        Status = n > 0 ? Loc.F("{0} dosya özgün yerine geri yüklendi.", n) : Loc.T("Karantinada dosya yok.");
        RefreshQuarantine();
    }

    private void UpdateTotal()
    {
        long sum = Categories.Where(c => c.IsSelected && !c.Unavailable).Sum(c => c.Bytes);
        TotalText = FolderVm.Format(sum);
        TotalNote = Loc.T("seçili kategorilerde temizlenebilir");
    }

    private void RefreshQuarantine()
    {
        _quarantine.PurgeExpired();
        var items = _quarantine.List();
        QuarantineText = items.Count == 0 ? Loc.T("Karantinada dosya yok.") : Loc.F("Karantinada {0} dosya ({1}). 7 gün sonra kalıcı silinir.", items.Count, FolderVm.Format(items.Sum(i => i.Bytes)));
    }
}