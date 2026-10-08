using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Drivers;

namespace Pulse.App.ViewModels;

public sealed class DriverRowVm
{
    public DriverRowVm(string name, string detail, string label, string brushKey)
    {
        Name = name; Detail = detail; Label = label;
        Brush = (Brush)System.Windows.Application.Current.FindResource(brushKey);
    }
    public string Name { get; }
    public string Detail { get; }
    public string Label { get; }
    public Brush Brush { get; }
}

public partial class DriversViewModel : ObservableObject
{
    private IReadOnlyList<DriverInfo> _installed = [];
    private BiosInfo? _bios;

    public DriversViewModel() => _ = LoadAsync();

    public OperationVm Op { get; } = new();
    public ObservableCollection<DriverRowVm> Installed { get; } = new();
    public ObservableCollection<DriverRowVm> Updates { get; } = new();

    [ObservableProperty] private string _biosText = "Okunuyor…";
    [ObservableProperty] private string _biosNote = "";
    [ObservableProperty] private string _nvidiaText = "—";
    [ObservableProperty] private string _updateNote = "Taramak için “Windows Update'te ara”ya bas. Yalnızca arar, hiçbir şey indirmez.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasNvidiaApp;

    private async Task LoadAsync()
    {
        (_bios, _installed) = await Task.Run(() => (DriverCenter.ReadBios(), DriverCenter.ReadInstalled()));
        BiosText = _bios is null ? "Okunamadı" : $"{_bios.Model}  ·  BIOS {_bios.Version}";
        BiosNote = _bios?.Date is { } d ? $"BIOS tarihi {d:yyyy-MM-dd} ({(DateTime.Now - d).TotalDays / 365.25:0.0} yıl önce). BIOS kendiliğinden güncellenmez; ASUS destek sayfasından sen karar verirsin." : "";
        NvidiaText = DriverCenter.NvidiaVersion(_installed) is { } v ? $"NVIDIA sürücüsü {v}" : "NVIDIA sürücüsü bulunamadı";
        HasNvidiaApp = System.IO.File.Exists(NvidiaAppPath);

        Installed.Clear();
        foreach (var dr in _installed.Where(x => !x.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase) && !x.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)))
        {
            var old = dr.AgeYears is > 3 && dr.Class is "DISPLAY" or "NET" or "BLUETOOTH";
            Installed.Add(new DriverRowVm(dr.Name, $"{ClassName(dr.Class)}  ·  sürüm {dr.Version}  ·  {dr.Date:yyyy-MM-dd}", old ? "ESKİ" : "TAMAM", old ? "WarnBrush" : "GoodBrush"));
        }
    }

    private static string ClassName(string c) => c switch { "DISPLAY" => "Ekran", "NET" => "Ağ", "BLUETOOTH" => "Bluetooth", "MEDIA" => "Ses", _ => c };

    private const string NvidiaAppPath = @"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe";

    [RelayCommand]
    private async Task Search()
    {
        if (IsBusy) return;
        IsBusy = true;
        Op.Begin("Windows Update'te sürücü güncellemeleri aranıyor (yaklaşık bir dakika sürebilir)…");
        UpdateNote = "Aranıyor…";
        try
        {
            var (list, error) = await Task.Run(DriverCenter.SearchWindowsUpdate);
            Updates.Clear();
            if (error is not null) { UpdateNote = error; return; }
            var newer = 0;
            foreach (var u in list)
            {
                var (isNewer, note) = DriverCenter.Judge(u, _installed, _bios);
                if (isNewer) newer++;
                Updates.Add(new DriverRowVm(u.Title, note, isNewer ? "YENİ" : "KURMA", isNewer ? "WarnBrush" : "MutedBrush"));
            }
            UpdateNote = list.Count == 0 ? "Bekleyen sürücü güncellemesi yok."
                : newer == 0 ? $"{list.Count} öneri var ama hiçbiri kurulu sürümden yeni değil. Yapılacak bir şey yok."
                : $"{newer} gerçekten yeni güncelleme var. Windows Update'ten kurabilirsin (Windows kendi geri alma desteğiyle kurar).";
        }
        finally { IsBusy = false; Op.End(); }
    }

    [RelayCommand]
    private void OpenWindowsUpdate() => Process.Start(new ProcessStartInfo("ms-settings:windowsupdate-optionalupdates") { UseShellExecute = true });

    [RelayCommand]
    private void OpenNvidiaApp()
    {
        if (System.IO.File.Exists(NvidiaAppPath)) Process.Start(new ProcessStartInfo(NvidiaAppPath) { UseShellExecute = true });
    }
}