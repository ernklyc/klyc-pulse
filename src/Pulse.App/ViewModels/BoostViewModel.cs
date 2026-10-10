using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Localization;
using Pulse.Core.Processes;

namespace Pulse.App.ViewModels;

/// <summary>"Açık uygulamaları kapat" listesindeki bir satır.</summary>
public partial class AppChoiceVm : ObservableObject
{
    public AppChoiceVm(OpenApp app)
    {
        App = app;
        _isSelected = app.Advice == AppAdvice.Suggested;          // yalnızca zararsız olanlar ön seçili gelir
        (Badge, BadgeBrush) = app.Advice switch
        {
            AppAdvice.Suggested => (Loc.T("ÖNERİLEN"), Res("GoodBrush")),
            AppAdvice.Careful => (Loc.T("DİKKAT"), Res("WarnBrush")),
            _ => (Loc.T("İSTEĞE BAĞLI"), Res("AccentGrayBrush")),
        };
        MemoryText = app.MemoryBytes >= 1073741824 ? $"{app.MemoryBytes / 1073741824.0:0.0} GB" : $"{app.MemoryBytes / 1048576} MB";
        CountText = app.Count > 1 ? $"×{app.Count}" : "";
        KindText = Loc.T(app.HasWindow ? "Pencereli uygulama" : "Arka plan programı");
    }

    public OpenApp App { get; }
    public string Display => App.Display;
    public string Why => App.Why;
    public string Badge { get; }
    public Brush BadgeBrush { get; }
    public string MemoryText { get; }
    public string CountText { get; }
    public string KindText { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private Brush _statusBrush = Res("MutedBrush");
    [ObservableProperty] private bool _done;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>"Açık uygulamaları kapat" penceresi: tarar, seçtirir, nazikçe kapatır, gerçekten kapandığını doğrular, boşalan belleği ölçer.</summary>
public partial class BoostViewModel : ObservableObject
{
    private readonly ProcessInspector _inspector = new();
    private readonly OpenAppsCloser _closer = new();

    public BoostViewModel() => _ = ScanAsync();

    public ObservableCollection<AppChoiceVm> Items { get; } = new();
    public OperationVm Op { get; } = new();

    [ObservableProperty] private string _summary = Loc.T("Açık uygulamalar taranıyor…");
    [ObservableProperty] private string _selectedText = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canForce;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _gameNote = "";
    private List<OpenApp> _waiting = new();

    private static string Fmt(long b) => b >= 1073741824 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576} MB";

    [RelayCommand]
    private async Task Scan() => await ScanAsync();

    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin(Loc.T("Açık uygulamalar taranıyor…"));
        try
        {
            var (scan, game) = await Task.Run(() =>
            {
                _inspector.Sample(); Thread.Sleep(1200);                 // işlemci kullanımı için iki ölçüm
                var groups = _inspector.Sample();
                var g = Pulse.Core.Automation.GameDetector.FindRunningGameInfo();
                var s = OpenAppsAdvisor.Build(groups, OpenAppsAdvisor.ForegroundPid(), g?.Name,
                    g is null ? null : System.IO.Path.GetDirectoryName(g.Path), DisplayName);
                return (s, g);
            });

            foreach (var i in Items) i.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var a in scan.Apps) { var vm = new AppChoiceVm(a); vm.PropertyChanged += OnItemChanged; Items.Add(vm); }
            IsEmpty = Items.Count == 0;
            Summary = IsEmpty ? Loc.T("Kapatılacak bir şey bulunamadı. Her şey zaten sade.")
                : Loc.F("{0} uygulama açık, toplam {1}. Önerilen {2} uygulama ({3}) ön seçili.", Items.Count, Fmt(scan.TotalBytes), scan.Suggested.Count(), Fmt(scan.SuggestedBytes));
            GameNote = game is null ? "" : Loc.F("{0} çalışıyor: oyunun kendisi ve oyun başlatıcıları listede yok, kapatılmaz.", game.Name);
            UpdateSelected();
        }
        catch (Exception ex) { Pulse.Core.Diagnostics.Journal.Write("Açık uygulama taraması hatası: " + ex.Message); Summary = Loc.T("Tarama yapılamadı."); }
        finally { Op.End(); IsBusy = false; }
    }

    private static string DisplayName(string name, string? path)
    {
        try
        {
            if (path is not null)
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var d = !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName : v.FileDescription;
                if (!string.IsNullOrWhiteSpace(d) && d!.Length <= 40) return d.Trim();
            }
        }
        catch { }
        return name;
    }

    private void OnItemChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppChoiceVm.IsSelected)) UpdateSelected();
    }

    private void UpdateSelected()
    {
        var sel = Items.Where(i => i.IsSelected && !i.Done).ToList();
        SelectedText = sel.Count == 0 ? Loc.T("Hiçbir uygulama seçili değil.") : Loc.F("{0} uygulama seçili · {1}", sel.Count, Fmt(sel.Sum(i => i.App.MemoryBytes)));
    }

    [RelayCommand]
    private void SelectSuggested() { foreach (var i in Items.Where(i => !i.Done)) i.IsSelected = i.App.Advice == AppAdvice.Suggested; }

    [RelayCommand]
    private void SelectNone() { foreach (var i in Items) i.IsSelected = false; }

    [RelayCommand]
    private async Task CloseSelected()
    {
        var chosen = Items.Where(i => i.IsSelected && !i.Done).ToList();
        if (chosen.Count == 0 || IsBusy) return;
        var careful = chosen.Where(i => i.App.Advice == AppAdvice.Careful).Select(i => i.Display).ToList();
        if (careful.Count > 0)
        {
            var ask = MessageBox.Show(
                Loc.F("Şu uygulamalarda kaydedilmemiş iş olabilir: {0}.\n\nKapatırken uygulamalar sana soracak; kaydetmek istemezsen kapanmaz. Devam edilsin mi?", string.Join(", ", careful)),
                Loc.T("Açık uygulamaları kapat"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
        }

        IsBusy = true; HasResult = false; CanForce = false;
        Op.Begin(Loc.T("Uygulamalar kapatılıyor…"), chosen.Count);
        CloseReport report;
        try
        {
            var progress = new Progress<string>(t => Op.Step(t));
            report = await Task.Run(() => _closer.Close(chosen.Select(c => c.App).ToList(), progress));
        }
        finally { Op.End(); }

        ApplyResults(report, chosen);
        _waiting = report.Waiting.Select(r => r.App).ToList();
        CanForce = _waiting.Count > 0;
        var text = Loc.F("{0} uygulama kapandı, yaklaşık {1} bellek boşaldı.", report.Closed, Fmt(report.FreedBytes));
        if (_waiting.Count > 0) text += " " + Loc.F("{0} uygulama senden kayıt bekliyor ya da yanıt vermiyor ({1}); zorla kapatılmadı.", _waiting.Count, string.Join(", ", _waiting.Select(w => w.Display)));
        if (report.Failed.Any()) text += " " + Loc.F("{0} uygulama kapatılamadı.", report.Failed.Count());
        ResultText = text;
        HasResult = true;
        IsBusy = false;
        UpdateSelected();
    }

    [RelayCommand]
    private async Task ForceWaiting()
    {
        if (_waiting.Count == 0 || IsBusy) return;
        var ask = MessageBox.Show(
            Loc.F("{0} zorla kapatılacak. Kaydedilmemiş veri KAYBOLUR.\n\nEmin misin?", string.Join(", ", _waiting.Select(w => w.Display))),
            Loc.T("Zorla kapat"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ask != MessageBoxResult.Yes) return;

        IsBusy = true; CanForce = false;
        Op.Begin(Loc.T("Zorla kapatılıyor…"));
        CloseReport report;
        try { report = await Task.Run(() => _closer.ForceClose(_waiting)); }
        finally { Op.End(); }
        ApplyResults(report, Items.Where(i => _waiting.Contains(i.App)).ToList());
        ResultText = Loc.F("{0} uygulama zorla kapatıldı, yaklaşık {1} bellek daha boşaldı.", report.Closed, Fmt(report.FreedBytes));
        _waiting = new();
        IsBusy = false;
        UpdateSelected();
    }

    private static void ApplyResults(CloseReport report, List<AppChoiceVm> rows)
    {
        foreach (var r in report.Results)
        {
            var row = rows.FirstOrDefault(x => ReferenceEquals(x.App, r.App));
            if (row is null) continue;
            row.Status = r.Outcome switch { CloseOutcome.Closed => "✓ " + r.Note, CloseOutcome.WaitingForUser => "⏳ " + r.Note, _ => "✗ " + r.Note };
            row.StatusBrush = (Brush)Application.Current.FindResource(r.Outcome switch { CloseOutcome.Closed => "GoodBrush", CloseOutcome.WaitingForUser => "WarnBrush", _ => "BadBrush" });
            if (r.Outcome == CloseOutcome.Closed) { row.IsSelected = false; row.Done = true; }
        }
    }
}
