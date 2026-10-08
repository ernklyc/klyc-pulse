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
        Badge = c.Safety == CleanupSafety.Quarantine ? "7 gün karantina" : c.NeedsAdmin && !CleanupEngine.IsAdmin ? "Yönetici gerekir" : "";
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
        Detail = $"{f.Where}  ·  son kullanım {f.LastActivity:dd.MM.yyyy}  ·  {f.Path}";
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
        Detail = $"{f.Modified:dd.MM.yyyy}" + (keeper ? "  ·  KORUNUR (asıl kopya)" : "");
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
        Header = $"{g.Files.Count} aynı dosya  ·  {FolderVm.Format(g.Size)} × {g.Files.Count - 1} fazla = {FolderVm.Format(g.WastedBytes)} boşa";
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
    [ObservableProperty] private string _dupNote = "Belgeler, Masaüstü, Resimler, Videolar, Müzik ve İndirilenler klasörlerinde birebir aynı büyük dosyaları bulur. Hiçbir şeyi kendiliğinden silmez.";

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin("Kopya dosyalar aranıyor…");
        DupNote = "Aranıyor…";
        try
        {
            var progress = new Progress<string>(m => { Op.Message(m); });
            var found = await Task.Run(() => DuplicateFinder.Find(DuplicateFinder.DefaultRoots(), progress: progress));
            DupGroups.Clear();
            foreach (var g in found) DupGroups.Add(new DupGroupVm(g));
            HasDups = DupGroups.Count > 0;
            DupNote = DupGroups.Count == 0
                ? "Birebir aynı büyük dosya bulunamadı."
                : $"{DupGroups.Count} grup, fazladan {FolderVm.Format(found.Sum(g => g.WastedBytes))}. Her grupta en eski kopya korunur, silinmez. Hiçbiri seçili değil.";
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private void SelectDupExtras()
    {
        foreach (var g in DupGroups) foreach (var f in g.Files) f.IsSelected = !f.IsKeeper;
        DupNote = "Her grubun korunan kopyası dışındakiler seçildi. Gözden geçirip gönderebilirsin.";
    }

    [RelayCommand]
    private async Task RemoveDuplicates()
    {
        if (IsBusy) return;
        var chosen = DupGroups.SelectMany(g => g.Files.Where(f => f.IsSelected && !f.IsKeeper).Select(f => (g, f))).ToList();
        if (chosen.Count == 0) { DupNote = "Hiçbir dosya seçili değil."; return; }
        var total = chosen.Sum(c => c.f.File.Bytes);
        var ok = System.Windows.MessageBox.Show(
            $"{chosen.Count} fazlalık kopya ({FolderVm.Format(total)}) Geri Dönüşüm Kutusu'na gönderilecek. Her grubun asıl kopyası yerinde kalır. Kutudan geri alabilirsin. Devam edilsin mi?",
            "Kopyaları onayla", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Op.Begin("Geri Dönüşüm Kutusu'na gönderiliyor…", chosen.Count);
        try
        {
            long freed = 0; var done = 0;
            foreach (var (g, f) in chosen)
            {
                Op.Step($"Gönderiliyor: {System.IO.Path.GetFileName(f.Path)}");
                var sent = await Task.Run(() => DuplicateFinder.Remove(g.Group, f.File));
                Pulse.Core.Diagnostics.Journal.Write($"Kopya dosya Geri Dönüşüm Kutusu'na {(sent ? "gönderildi" : "gönderilemedi")}: {f.Path}");
                if (sent) { freed += f.File.Bytes; done++; }
            }
            DupNote = $"{done}/{chosen.Count} kopya Geri Dönüşüm Kutusu'na gönderildi ({FolderVm.Format(freed)}). Alan, kutuyu boşaltınca açılır. Listeyi yenilemek için “Kopyaları bul”a bas.";
        }
        finally { IsBusy = false; Op.End(); }
    }
    private readonly QuarantineStore _quarantine = new();

    // ---- Eski kalıntılar ---------------------------------------------------
    public ObservableCollection<OrphanVm> Orphans { get; } = new();
    [ObservableProperty] private bool _hasOrphans;
    [ObservableProperty] private string _orphanNote = "Eskiden silinmiş uygulamaların bıraktığı klasörleri bulur. Hiçbir şeyi kendiliğinden silmez.";

    [RelayCommand]
    private async Task FindOrphans()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin("Eski kalıntılar aranıyor…");
        OrphanNote = "Aranıyor…";
        try
        {
            var found = await Task.Run(() => OrphanScanner.Scan(Core.Apps.InstalledAppsReader.Read()));
            Orphans.Clear();
            foreach (var f in found) Orphans.Add(new OrphanVm(f));
            HasOrphans = Orphans.Count > 0;
            OrphanNote = Orphans.Count == 0
                ? "Kalıntı bulunamadı."
                : $"{Orphans.Count} aday, toplam {FolderVm.Format(found.Sum(o => o.Bytes))}. Hiçbiri seçili değil; emin olduklarını işaretle. Silinenler Geri Dönüşüm Kutusu'na gider, geri alabilirsin.";
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task RemoveOrphans()
    {
        if (IsBusy) return;
        var chosen = Orphans.Where(o => o.IsSelected).ToList();
        if (chosen.Count == 0) { OrphanNote = "Hiçbir klasör seçili değil."; return; }

        var list = string.Join("\n", chosen.Select(o => $"• {o.Name}  ({o.SizeText})"));
        var ok = System.Windows.MessageBox.Show(
            $"Şu klasörler Geri Dönüşüm Kutusu'na gönderilecek:\n\n{list}\n\nİçlerinde işine yarayan bir şey (oyun kayıtları, ayarlar) olabilir. Emin değilsen “Hayır” de. Kutudan geri alabilirsin. Devam edilsin mi?",
            "Kalıntıları onayla", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Op.Begin("Geri Dönüşüm Kutusu'na gönderiliyor…", chosen.Count);
        try
        {
            long freed = 0; var done = 0;
            foreach (var o in chosen)
            {
                Op.Step($"Gönderiliyor: {o.Name}");
                var sent = await Task.Run(() => OrphanScanner.Remove(o.Folder));
                Pulse.Core.Diagnostics.Journal.Write($"Kalıntı klasör Geri Dönüşüm Kutusu'na {(sent ? "gönderildi" : "gönderilemedi")}: {o.Folder.Path} ({o.SizeText})");
                if (sent) { freed += o.Folder.Bytes; done++; Orphans.Remove(o); }
            }
            HasOrphans = Orphans.Count > 0;
            OrphanNote = $"{done}/{chosen.Count} klasör Geri Dönüşüm Kutusu'na gönderildi ({FolderVm.Format(freed)}). Alan, kutuyu boşaltınca açılır.";
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
    [ObservableProperty] private string _status = "Taramak için “Tara”ya bas.";
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _totalNote = "temizlenebilir";
    [ObservableProperty] private string _quarantineText = "";
    [ObservableProperty] private bool _hasFolders;
    [ObservableProperty] private string _restoreNote = "Geri Yükleme Noktaları okunuyor…";
    [ObservableProperty] private bool _hasRestorePoints;

    [RelayCommand]
    private async Task LoadRestorePoints()
    {
        RestoreNote = "Geri Yükleme Noktaları okunuyor…";
        var (items, error) = await RestorePoint.ListAsync();
        RestorePoints.Clear();
        foreach (var i in items) RestorePoints.Add(new RestorePointVm(i));
        HasRestorePoints = RestorePoints.Count > 0;
        RestoreNote = error ?? (items.Count == 0 ? "Henüz Geri Yükleme Noktası yok. KLYC-Pulse sistem temizliğinden ve uygulama kaldırmadan önce kendiliğinden alır." : $"{items.Count} Geri Yükleme Noktası var.");
    }

    [RelayCommand]
    private void OpenSystemRestore() => RestorePoint.OpenSystemRestore();

    [RelayCommand]
    private async Task Scan()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Taranıyor…";
        Op.Begin("Taranıyor…", Categories.Count);
        try
        {
            var progress = new Progress<string>(name => { Status = $"Taranıyor: {name}"; Op.Step($"Taranıyor: {name}"); });
            var scans = await _engine.ScanAsync(Categories.Select(c => c.Category), progress);
            foreach (var s in scans)
            {
                var vm = Categories.First(c => c.Category.Id == s.Category.Id);
                vm.Bytes = s.Bytes;
                vm.SizeText = s.Note is not null ? s.Note : s.Category.Special == "dism" ? "—" : FolderVm.Format(s.Bytes);
                if (vm.Unavailable) vm.IsSelected = false;
            }
            UpdateTotal();
            Status = "Tarama bitti. İstemediklerinin işaretini kaldırıp “Seçilenleri temizle”ye bas.";
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task Clean()
    {
        if (IsBusy) return;
        var chosen = Categories.Where(c => c.IsSelected && !c.Unavailable).ToList();
        if (chosen.Count == 0) { Status = "Hiçbir kategori seçili değil."; return; }

        var list = string.Join("\n", chosen.Select(c => "• " + c.Name + (c.Badge.Length > 0 ? $"  ({c.Badge})" : "")));
        var ok = System.Windows.MessageBox.Show(
            $"Şunlar temizlenecek:\n\n{list}\n\nÖnbellekler ve geçici dosyalar silinir. Eski kurulum dosyaları silinmez, 7 gün karantinada tutulur. Devam edilsin mi?",
            "Temizliği onayla", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        var needsRestore = chosen.Any(c => c.Category.NeedsAdmin);
        Op.Begin("Temizleniyor…", chosen.Count + (needsRestore ? 1 : 0));
        try
        {
            if (needsRestore)
            {
                Status = "Geri Yükleme Noktası alınıyor…";
                Op.Step("Geri Yükleme Noktası alınıyor (güvenlik için)…");
                var rp = await RestorePoint.CreateAsync("KLYC-Pulse sistem temizliği öncesi");
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
            Status = $"Bitti. {FolderVm.Format(total)} alan açıldı.";
            RefreshQuarantine();
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private async Task Analyze()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Disk analiz ediliyor (birkaç dakika sürebilir)…";
        Op.Begin("Disk analiz ediliyor (birkaç dakika sürebilir)…");
        try
        {
            var progress = new Progress<string>(p => { Status = $"Analiz: {p}"; Op.Message($"Analiz: {p}"); });
            var found = await DiskAnalyzer.AnalyzeAsync(DiskAnalyzer.DefaultRoots(), progress: progress);
            Folders.Clear();
            foreach (var f in found) Folders.Add(new FolderVm(f));
            HasFolders = Folders.Count > 0;
            Status = "Analiz bitti. Silme kararı sana ait, KLYC-Pulse bu klasörlere dokunmaz.";
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
        Status = n > 0 ? $"{n} dosya özgün yerine geri yüklendi." : "Karantinada dosya yok.";
        RefreshQuarantine();
    }

    private void UpdateTotal()
    {
        long sum = Categories.Where(c => c.IsSelected && !c.Unavailable).Sum(c => c.Bytes);
        TotalText = FolderVm.Format(sum);
        TotalNote = "seçili kategorilerde temizlenebilir";
    }

    private void RefreshQuarantine()
    {
        _quarantine.PurgeExpired();
        var items = _quarantine.List();
        QuarantineText = items.Count == 0 ? "Karantinada dosya yok." : $"Karantinada {items.Count} dosya ({FolderVm.Format(items.Sum(i => i.Bytes))}). 7 gün sonra kalıcı silinir.";
    }
}