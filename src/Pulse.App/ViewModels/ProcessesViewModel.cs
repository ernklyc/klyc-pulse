using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Processes;

namespace Pulse.App.ViewModels;

/// <summary>Süreç listesindeki bir satır (aynı programın tüm süreçleri).</summary>
public partial class ProcRowVm : ObservableObject
{
    private readonly ProcessesViewModel _owner;
    private bool _loading = true;

    public ProcRowVm(ProcGroup g, ProcessesViewModel owner)
    {
        _owner = owner;
        Name = g.Name;
        Update(g);
        _loading = false;
    }

    public string Name { get; }
    [ObservableProperty] private ProcGroup _group = null!;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private long _memoryBytes;
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private bool _isProtected;
    [ObservableProperty] private string _protectedText = "";
    [ObservableProperty] private int _priorityIndex = 2;
    [ObservableProperty] private bool _ecoMode;

    public static IReadOnlyList<string> Priorities { get; } = ["Düşük", "Normal altı", "Normal", "Normal üstü", "Yüksek"];
    private static readonly ProcessPriorityClass[] Classes =
        [ProcessPriorityClass.Idle, ProcessPriorityClass.BelowNormal, ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal, ProcessPriorityClass.High];

    public void Update(ProcGroup g)
    {
        var was = _loading;
        _loading = true;
        Group = g;
        Title = g.Title.Length > 0 ? g.Title : (g.Path ?? "");
        CountText = g.Count > 1 ? $"×{g.Count}" : "";
        CpuPercent = g.CpuPercent;
        CpuText = g.CpuPercent >= 0.1 ? $"%{g.CpuPercent:0.0}" : "—";
        MemoryBytes = g.MemoryBytes;
        MemoryText = g.MemoryBytes >= 1073741824 ? $"{g.MemoryBytes / 1073741824.0:0.0} GB" : $"{g.MemoryBytes / 1048576} MB";
        IsProtected = g.Protected;
        ProtectedText = g.ProtectedReason ?? "";
        PriorityIndex = g.Priority is { } p ? Math.Max(0, Array.IndexOf(Classes, p)) : 2;
        EcoMode = g.EcoMode;
        _loading = was;
    }

    partial void OnPriorityIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= Classes.Length) return;
        _owner.ApplyPriority(this, Classes[value]);
    }

    partial void OnEcoModeChanged(bool value)
    {
        if (_loading) return;
        _owner.ApplyEco(this, value);
    }
}

public partial class ProcessesViewModel : ObservableObject, IDisposable
{
    private readonly ProcessInspector _inspector = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, ProcRowVm> _byName = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    public ProcessesViewModel()
    {
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        View.IsLiveSorting = true;
        View.LiveSortingProperties.Add(nameof(ProcRowVm.CpuPercent));
        View.LiveSortingProperties.Add(nameof(ProcRowVm.MemoryBytes));
        View.Filter = o => o is ProcRowVm r && Matches(r);
        ApplySort();
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<ProcRowVm> Rows { get; } = new();
    public ListCollectionView View { get; }
    public OperationVm Op { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showProtected;
    [ObservableProperty] private int _sortIndex;          // 0 CPU, 1 Bellek, 2 Ad
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<ChoiceVm> SortChoices { get; } = new()
    {
        new ChoiceVm("CPU", 0) { IsActive = true }, new ChoiceVm("Bellek", 1), new ChoiceVm("Ad", 2),
    };

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnShowProtectedChanged(bool value) => View.Refresh();
    partial void OnSortIndexChanged(int value)
    {
        foreach (var c in SortChoices) c.IsActive = c.Value == value;
        ApplySort();
    }

    private bool Matches(ProcRowVm r) =>
        (ShowProtected || !r.IsProtected) &&
        (Search.Length == 0 || r.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || r.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase));

    private void ApplySort()
    {
        View.SortDescriptions.Clear();
        switch (SortIndex)
        {
            case 0: View.SortDescriptions.Add(new SortDescription(nameof(ProcRowVm.CpuPercent), ListSortDirection.Descending)); View.SortDescriptions.Add(new SortDescription(nameof(ProcRowVm.MemoryBytes), ListSortDirection.Descending)); break;
            case 1: View.SortDescriptions.Add(new SortDescription(nameof(ProcRowVm.MemoryBytes), ListSortDirection.Descending)); break;
            default: View.SortDescriptions.Add(new SortDescription(nameof(ProcRowVm.Name), ListSortDirection.Ascending)); break;
        }
    }

    [RelayCommand]
    private void SetSort(ChoiceVm c) => SortIndex = c.Value;

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var groups = await Task.Run(_inspector.Sample);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                names.Add(g.Name);
                if (_byName.TryGetValue(g.Name, out var row)) row.Update(g);
                else { var nr = new ProcRowVm(g, this); _byName[g.Name] = nr; Rows.Add(nr); }
            }
            foreach (var gone in _byName.Keys.Where(k => !names.Contains(k)).ToList())
            {
                Rows.Remove(_byName[gone]);
                _byName.Remove(gone);
            }
            var user = Rows.Where(r => !r.IsProtected).ToList();
            Summary = $"{user.Count} program, toplam {user.Sum(r => r.MemoryBytes) / 1073741824.0:0.0} GB bellek";
        }
        catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Süreç listesi hatası: " + ex.Message); }
        finally { _busy = false; }
    }

    // ---- Eylemler ----------------------------------------------------------

    [RelayCommand]
    private async Task Close(ProcRowVm? row)
    {
        if (row is null || row.IsProtected) return;
        var g = row.Group;
        var many = g.Count > 1 ? $"{g.Count} süreç" : "süreç";
        var ask = MessageBox.Show($"“{row.Name}” kapatılacak ({many}).\n\nÖnce nazikçe kapanması istenir; kaydedilmemiş işin varsa program sana sorar.", "Kapat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        Op.Begin($"{row.Name} kapatılıyor…");
        var (result, closed, remaining) = await Task.Run(() => _inspector.Close(g));
        Op.End();

        if (result == ProcResult.Ok) { Status = $"{row.Name} kapatıldı."; await RefreshAsync(); return; }

        var force = MessageBox.Show(
            $"{row.Name} kapanmadı ({remaining} süreç hâlâ çalışıyor). Cevap vermiyor olabilir.\n\nZorla sonlandırılsın mı? Kaydedilmemiş veri kaybolur.",
            "Zorla kapat", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (force != MessageBoxResult.Yes) { Status = $"{row.Name} açık bırakıldı."; return; }
        var (r2, _) = await Task.Run(() => _inspector.ForceClose(row.Group with { Pids = g.Pids }));
        Status = r2 == ProcResult.Ok ? $"{row.Name} sonlandırıldı." : $"{row.Name} tamamen sonlandırılamadı.";
        await RefreshAsync();
    }

    public void ApplyPriority(ProcRowVm row, ProcessPriorityClass priority)
    {
        var (result, changed) = _inspector.SetPriority(row.Group, priority);
        Status = result == ProcResult.Ok ? $"{row.Name}: öncelik {ProcRowVm.Priorities[Array.IndexOf(new[] { ProcessPriorityClass.Idle, ProcessPriorityClass.BelowNormal, ProcessPriorityClass.Normal, ProcessPriorityClass.AboveNormal, ProcessPriorityClass.High }, priority)]} yapıldı ve doğrulandı."
            : $"{row.Name}: öncelik {changed}/{row.Group.Count} süreçte değişti.";
    }

    public void ApplyEco(ProcRowVm row, bool on)
    {
        var (result, changed) = _inspector.SetEcoMode(row.Group, on);
        Status = result == ProcResult.Ok ? $"{row.Name}: Verimlilik Modu {(on ? "açıldı" : "kapatıldı")} ve doğrulandı."
            : $"{row.Name}: Verimlilik Modu {changed}/{row.Group.Count} süreçte değişti.";
    }

    public void Dispose() => _timer.Stop();
}