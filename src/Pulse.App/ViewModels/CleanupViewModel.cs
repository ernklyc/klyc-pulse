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

public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupEngine _engine = new();
    private readonly QuarantineStore _quarantine = new();

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