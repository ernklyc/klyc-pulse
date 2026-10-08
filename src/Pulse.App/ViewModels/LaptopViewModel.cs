using Pulse.Core.Localization;
using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Hardware;
using Pulse.Core.Platform;
using Pulse.Core.Settings;

namespace Pulse.App.ViewModels;

/// <summary>Bir seçenek düğmesi (pil limiti, klavye ışığı seviyesi).</summary>
public partial class ChoiceVm : ObservableObject
{
    public ChoiceVm(string title, int value) { Title = title; Value = value; }
    public string Title { get; }
    public int Value { get; }
    [ObservableProperty] private bool _isActive;
}

/// <summary>Klavye RGB renk düğmesi.</summary>
public sealed partial class ColorChoiceVm : ObservableObject
{
    public ColorChoiceVm(string name, byte r, byte g, byte b)
    {
        Name = name; R = r; G = g; B = b;
        Brush = new SolidColorBrush(Color.FromRgb(r, g, b));
    }
    public string Name { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public Brush Brush { get; }
    [ObservableProperty] private bool _isActive;
}

/// <summary>Kısayol listesindeki bir satır.</summary>
public sealed partial class HotkeyRowVm : ObservableObject
{
    public HotkeyRowVm(string id, string title) { Id = id; Title = title; }
    public string Id { get; }
    public string Title { get; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _assigned;
}

/// <summary>Sihirbaz listesindeki bir satır.</summary>
public sealed class ExitRowVm
{
    public ExitRowVm(ExitStep s)
    {
        Name = s.Name;
        Detail = s.Detail;
        Label = s.Ok ? "TAMAM" : Loc.T("AKTİF");
        Brush = (Brush)Application.Current.FindResource(s.Ok ? "GoodBrush" : "WarnBrush");
    }

    public string Name { get; }
    public string Detail { get; }
    public string Label { get; }
    public Brush Brush { get; }
}

public partial class LaptopViewModel : ObservableObject
{
    private readonly SettingsStore _store = AppServices.Settings;
    private readonly GHelperExit _exit = new();

    public LaptopViewModel()
    {
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        HasDriver = LaptopControl.IsAvailable();

        foreach (var p in new[] { 60, 70, 80, 90, 100 }) BatteryChoices.Add(new ChoiceVm(Loc.F("%{0}", p), p));
        for (var l = 0; l <= 3; l++) KeyboardChoices.Add(new ChoiceVm(l == 0 ? Loc.T("Kapalı") : $"Seviye {l}", l));

        MarkBattery(_store.Current.BatteryLimit);
        BatteryStatus = _store.Current.BatteryLimit is { } b
            ? Loc.F("Seçili limit: %{0}. Açılışta yeniden uygulanır.", b)
            : Loc.T("Limit seçilmedi. Pil %100'e kadar şarj olur.");

        var level = LaptopControl.ReadKeyboardLevel();
        MarkKeyboard(level);
        KeyboardStatus = level is null ? Loc.T("Klavye ışığı bu cihazda okunamıyor.") : Loc.F("Şu an: {0}.", LaptopControl.Label(level.Value));

        foreach (var (id, title, _) in HotkeyActions.All) HotkeyRows.Add(new HotkeyRowVm(id, title));
        RefreshHotkeyRows();
        // Kart her zaman gösterilir (NVIDIA kartı varsa); uyuyan kartın okunması birkaç saniye sürebileceği için durum arka planda yüklenir.
        HasGpuOc = Pulse.Core.Monitoring.Nvml.IsAvailable;
        for (var i = 0; i < GpuOcLadder.Length; i++) GpuOcChoices.Add(new ChoiceVm(GpuOcLadder[i].Title, i));
        MarkGpuOc();
        GpuOcStatus = HasGpuOc ? Loc.T("Ekran kartı kontrol ediliyor…") : Loc.T("NVIDIA ekran kartı bulunamadı.");
        if (HasGpuOc)
            _ = Task.Run(() => Pulse.Core.Hardware.GpuOverclock.Read()).ContinueWith(t =>
                Application.Current.Dispatcher.BeginInvoke(() => GpuOcStatus = t.Result is { Editable: true } ? GpuOcInfo() : Loc.T("Bu ekran kartında hız ayarı yapılamıyor ya da kart uyuyor. Birkaç saniye sonra sayfayı yeniden aç.")));
        HasRgb = LaptopControl.IsRgbAvailable();
        _rgb = _store.Current.KeyboardColor ?? new KeyboardRgb();
        foreach (var (t, v) in new[] { (Loc.T("Sabit"), 0), (Loc.T("Nefes"), 1), (Loc.T("Renk döngüsü"), 2), (Loc.T("Gökkuşağı"), 3) }) RgbModes.Add(new ChoiceVm(t, v));
        foreach (var (t, v) in new[] { (Loc.T("Yavaş"), 0), ("Normal", 1), (Loc.T("Hızlı"), 2) }) RgbSpeeds.Add(new ChoiceVm(t, v));
        foreach (var (n, cr, cg, cb) in new[] { (Loc.T("Beyaz"), 255, 255, 255), (Loc.T("Kırmızı"), 255, 0, 0), (Loc.T("Turuncu"), 255, 110, 0), (Loc.T("Sarı"), 255, 220, 0), (Loc.T("Yeşil"), 0, 255, 0), (Loc.T("Turkuaz"), 0, 220, 255), (Loc.T("Mavi"), 0, 60, 255), (Loc.T("Mor"), 150, 0, 255), (Loc.T("Pembe"), 255, 40, 160) })
            RgbColors.Add(new ColorChoiceVm(n, (byte)cr, (byte)cg, (byte)cb));
        MarkRgb();
        RgbStatus = HasRgb ? Loc.T("Seçtiğin renk uygulanır ve her açılışta yeniden yazılır.") : Loc.T("Bu klavyede RGB renk kontrolü yok.");
        Hotkeys = _store.Current.Hotkeys;
        HotkeyStatus = HotkeyInfo();
        _loading = false;

        _ = RefreshExitAsync();
    }

    private readonly bool _loading = true;

    public bool IsAdmin { get; }
    public bool HasDriver { get; }
    public bool HasNoDriver => !HasDriver;
    public ObservableCollection<ChoiceVm> BatteryChoices { get; } = new();
    public ObservableCollection<ChoiceVm> KeyboardChoices { get; } = new();
    public ObservableCollection<HotkeyRowVm> HotkeyRows { get; } = new();
    public ObservableCollection<ChoiceVm> GpuOcChoices { get; } = new();
    private static readonly (string Title, int Core, int Mem)[] GpuOcLadder = [(Loc.T("Fabrika"), 0, 0), (Loc.T("Hafif +50 / +250"), 50, 250), (Loc.T("Orta +100 / +500"), 100, 500), (Loc.T("Yüksek +150 / +700"), 150, 700)];
    public bool HasGpuOc { get; private set; }
    [ObservableProperty] private string _gpuOcStatus = "";
    [ObservableProperty] private bool _gpuOcBusy;
    public ObservableCollection<ChoiceVm> RgbModes { get; } = new();
    public ObservableCollection<ChoiceVm> RgbSpeeds { get; } = new();
    public ObservableCollection<ColorChoiceVm> RgbColors { get; } = new();
    public bool HasRgb { get; private set; }
    [ObservableProperty] private string _rgbStatus = "";
    private KeyboardRgb _rgb = new();
    public ObservableCollection<ExitRowVm> ExitRows { get; } = new();
    public OperationVm Op { get; } = new();

    [ObservableProperty] private string _batteryStatus = "";
    [ObservableProperty] private string _keyboardStatus = "";
    [ObservableProperty] private bool _hotkeys;
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private string _exitStatus = "";
    [ObservableProperty] private bool _exitBusy;
    [ObservableProperty] private bool _hasBackup;

    partial void OnHotkeysChanged(bool value)
    {
        if (_loading) return;
        _store.Current.Hotkeys = value;
        _store.Save();
        AppServices.Hotkeys.Enabled = value;
        HotkeyStatus = HotkeyInfo();
    }

    private string HotkeyInfo()
    {
        var failed = AppServices.Hotkeys.Failed;
        return failed.Count == 0
            ? Loc.T("Her eyleme istediğin tuşu ata. Hangi pencere açık olursa olsun, tepsideyken de çalışır.")
            : Loc.T("Şu kısayollar başka bir program tarafından kullanıldığı için alınamadı: ") + string.Join(", ", failed) + Loc.T(". “Değiştir” ile başka bir tuş seç.");
    }

    private void RefreshHotkeyRows()
    {
        var map = HotkeyActions.Resolve(_store.Current.HotkeyBindings);
        foreach (var row in HotkeyRows)
        {
            var b = map[row.Id];
            row.Assigned = b is not null;
            row.Text = b is { } bb ? bb.ToString() : Loc.T("Atanmamış");
        }
    }

    private void SaveHotkey(string id, string text)
    {
        var dict = HotkeyActions.All.ToDictionary(a => a.Id, a => HotkeyActions.Resolve(_store.Current.HotkeyBindings)[a.Id]?.ToString() ?? "");
        dict[id] = text;
        _store.Current.HotkeyBindings = dict;
        _store.Save();
        AppServices.Hotkeys.Reload();
        RefreshHotkeyRows();
        HotkeyStatus = HotkeyInfo();
    }

    [RelayCommand]
    private void ChangeHotkey(HotkeyRowVm row)
    {
        var dlg = new KeyCaptureWindow(row.Title) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result is not { } b) return;
        var map = HotkeyActions.Resolve(_store.Current.HotkeyBindings);
        var clash = HotkeyActions.All.FirstOrDefault(a => a.Id != row.Id && map[a.Id] is { } o && o == b);
        if (clash.Id is not null) { HotkeyStatus = Loc.F("{0} zaten “{1}” için kullanılıyor. Önce onu değiştir ya da başka bir tuş seç.", b, clash.Title); return; }
        SaveHotkey(row.Id, b.ToString());
        if (AppServices.Hotkeys.Failed.Contains(b.ToString())) HotkeyStatus = Loc.F("{0} başka bir program tarafından kullanılıyor, kaydedilemedi. Başka bir tuş seç.", b);
        else HotkeyStatus = Loc.F("{0}: {1} atandı ve kaydedildi.", row.Title, b);
    }

    [RelayCommand]
    private void ClearHotkey(HotkeyRowVm row) { SaveHotkey(row.Id, ""); HotkeyStatus = Loc.F("{0}: atama kaldırıldı.", row.Title); }

    private int GpuOcIndex()
    {
        var c = _store.Current.GpuOcCore ?? 0; var m = _store.Current.GpuOcMem ?? 0;
        var i = Array.FindIndex(GpuOcLadder, x => x.Core == c && x.Mem == m);
        return i < 0 ? 0 : i;
    }

    private void MarkGpuOc() { var idx = GpuOcIndex(); foreach (var c in GpuOcChoices) c.IsActive = c.Value == idx; }

    private string GpuOcInfo()
    {
        var l = GpuOcLadder[GpuOcIndex()];
        return l.Core == 0 ? Loc.T("Fabrika hızı. Seçtiğin ayar yalnızca Oyun modunda uygulanır, başka modda ya da yeniden başlatınca fabrika hızına döner.")
            : Loc.F("Oyun modunda çekirdek +{0}, bellek +{1} MHz uygulanır. Başka modda ya da yeniden başlatınca fabrika hızına döner.", l.Core, l.Mem);
    }

    [RelayCommand]
    private async Task SetGpuOc(ChoiceVm c)
    {
        if (!IsAdmin) { GpuOcStatus = Loc.T("Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."); return; }
        var l = GpuOcLadder[c.Value];
        _store.Current.GpuOcCore = l.Core == 0 ? null : l.Core;
        _store.Current.GpuOcMem = l.Mem == 0 ? null : l.Mem;
        _store.Save();
        MarkGpuOc();
        if (AppServices.Modes.CurrentKey == Core.Modes.Modes.Game || l.Core == 0)
        {
            Op.Begin(Loc.T("Ekran kartı hızlandırılıyor ve kontrol ediliyor…"));
            var r = await Task.Run(() => Pulse.Core.Hardware.GpuOverclock.Apply(AppServices.Modes.CurrentKey == Core.Modes.Modes.Game ? l.Core : 0, AppServices.Modes.CurrentKey == Core.Modes.Modes.Game ? l.Mem : 0));
            Op.End();
            GpuOcStatus = r.Message;
        }
        else GpuOcStatus = GpuOcInfo() + Loc.T(" Oyun moduna geçince uygulanır.");
    }

    [RelayCommand]
    private async Task AutoTuneGpu()
    {
        if (GpuOcBusy) return;
        if (!IsAdmin) { GpuOcStatus = Loc.T("Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."); return; }
        var ask = System.Windows.MessageBox.Show(
            Loc.T("Ekran kartı için en iyi ayarı arayacağım.\n\n• Yaklaşık 2 dakika sürer, ekranda ağır bir grafik penceresi açılır, fanlar hızlanır\n• Çekirdek ve bellek saati kademe kademe artırılır; her kademede kare hızı, ısı ve sürücü hataları ölçülür\n• Kare hızı artmayı bırakınca, 84 °C geçilince ya da sürücü hata verince durur\n• Bitince ekran kartı fabrika hızına döner; sonucu sen seçersin\n\nÇalışan oyunları ve açık işlerini kaydedip öyle başla. Devam edilsin mi?"),
            Loc.T("Ekran kartı hızını otomatik bul"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ask != System.Windows.MessageBoxResult.Yes) return;

        GpuOcBusy = true;
        Op.Begin(Loc.T("Ekran kartı hızlandırma aranıyor…"));
        try
        {
            var progress = new Progress<string>(t => Op.Message(t));
            var res = await Pulse.Core.Hardware.GpuTuner.RunAsync(progress);
            var table = string.Join("\n", res.Steps.Select(s => Loc.F("+{0}/+{1}: {2:0.0} kare/sn, {3:0} MHz, {4:0} °C {5} {6}", s.Core, s.Mem, s.Fps, s.CoreMhz, s.MaxTempC, Loc.T(s.Stable ? "kararlı" : "KARARSIZ"), s.Note)));
            var best = GpuOcLadder.FirstOrDefault(x => x.Core == res.BestCore && x.Mem == res.BestMem);
            var use = System.Windows.MessageBox.Show(Loc.F("{0}\n\n{1}\n\nBu ayar Oyun modunda kullanılsın mı?", table, res.Summary), Loc.T("Sonuç"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (use == System.Windows.MessageBoxResult.Yes)
            {
                _store.Current.GpuOcCore = res.BestCore == 0 ? null : res.BestCore;
                _store.Current.GpuOcMem = res.BestMem == 0 ? null : res.BestMem;
                _store.Save();
            }
            MarkGpuOc();
            GpuOcStatus = res.Summary + " " + GpuOcInfo();
        }
        catch (Exception ex) { GpuOcStatus = Loc.T("Arama tamamlanamadı: ") + ex.Message; }
        finally { Op.End(); GpuOcBusy = false; }
    }

    private void MarkRgb()
    {
        foreach (var c in RgbModes) c.IsActive = c.Value == _rgb.Mode;
        foreach (var c in RgbSpeeds) c.IsActive = c.Value == _rgb.Speed;
        foreach (var c in RgbColors) c.IsActive = c.R == _rgb.R && c.G == _rgb.G && c.B == _rgb.B;
    }

    private async Task ApplyRgb(string what)
    {
        Op.Begin(Loc.T("Klavye rengi yazılıyor…"));
        var result = await Task.Run(() => LaptopControl.SetKeyboardRgb(_rgb));
        Op.End();
        if (result.Ok) { _store.Current.KeyboardColor = new KeyboardRgb { Mode = _rgb.Mode, R = _rgb.R, G = _rgb.G, B = _rgb.B, Speed = _rgb.Speed }; _store.Save(); }
        MarkRgb();
        RgbStatus = result.Ok ? Loc.F("{0}: bilgisayar kabul etti. Rengi bilgisayar geri söyleyemiyor, klavyene bakıp sen kontrol et.", what) : result.Message;
    }

    [RelayCommand] private Task SetRgbMode(ChoiceVm c) { _rgb.Mode = c.Value; return ApplyRgb($"Mod {c.Title}"); }
    [RelayCommand] private Task SetRgbSpeed(ChoiceVm c) { _rgb.Speed = c.Value; return ApplyRgb(Loc.F("Hız {0}", c.Title)); }
    [RelayCommand] private Task SetRgbColor(ColorChoiceVm c) { _rgb.R = c.R; _rgb.G = c.G; _rgb.B = c.B; return ApplyRgb(c.Name); }

    private void MarkBattery(int? v) { foreach (var c in BatteryChoices) c.IsActive = c.Value == v; }
    private void MarkKeyboard(int? v) { foreach (var c in KeyboardChoices) c.IsActive = c.Value == v; }

    [RelayCommand]
    private async Task SetBattery(ChoiceVm choice)
    {
        BatteryStatus = Loc.T("Uygulanıyor…");
        Op.Begin(Loc.T("Pil sınırı ayarlanıyor…"));
        var result = await Task.Run(() => LaptopControl.SetBatteryLimit(choice.Value));
        if (result.Ok)
        {
            _store.Current.BatteryLimit = choice.Value;
            _store.Save();
            MarkBattery(choice.Value);
        }
        Op.End();
        BatteryStatus = result.Message + (result.Ok ? Loc.T("(Bu model değeri geri söylemiyor, bilgisayarın kabul ettiğini biliyoruz.)") : "");
    }

    [RelayCommand]
    private async Task SetKeyboard(ChoiceVm choice)
    {
        KeyboardStatus = Loc.T("Uygulanıyor…");
        Op.Begin(Loc.T("Klavye ışığı ayarlanıyor…"));
        var result = await Task.Run(() => LaptopControl.SetKeyboardLevel(choice.Value));
        if (result.Ok)
        {
            _store.Current.KeyboardLevel = choice.Value;
            _store.Save();
        }
        MarkKeyboard(LaptopControl.ReadKeyboardLevel());
        Op.End();
        KeyboardStatus = result.Message;
    }

    // ---- G-Helper'ı kapat ----

    [RelayCommand]
    private Task RefreshExit() => RefreshExitAsync();

    private async Task RefreshExitAsync()
    {
        var steps = await Task.Run(_exit.Probe);
        Show(steps);
        HasBackup = _exit.HasBackup;
        ExitStatus = steps.All(s => s.Ok)
            ? Loc.T("G-Helper ve Armoury Crate'in gereksiz parçaları kapalı. KLYC-Pulse tek başına yönetiyor.")
            : Loc.T("G-Helper veya Armoury Crate hâlâ arka planda çalışıyor, modları bozabilir.");
    }

    [RelayCommand]
    private async Task LeaveGHelper()
    {
        if (!IsAdmin) { ExitStatus = Loc.T("Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."); return; }
        var ask = MessageBox.Show(
            Loc.T("Şunlar yapılacak:\n\n• G-Helper kapatılır ve Windows açılışında başlamaz\n• Armoury Crate'in 5 arka plan servisi \"elle başlat\"a alınır\n\nHiçbir program silinmez. İstediğin an \"Geri al\" ile her şey eski haline döner.\n\nDevam edilsin mi?"),
            Loc.T("G-Helper'ı kapat"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        ExitBusy = true;
        ExitStatus = Loc.T("Uygulanıyor ve doğrulanıyor…");
        Op.Begin(Loc.T("G-Helper kapatılıyor ve Armoury Crate'in gereksiz parçaları durduruluyor…"));
        var steps = await Task.Run(_exit.Apply);
        Show(steps);
        HasBackup = _exit.HasBackup;
        ExitStatus = steps.All(s => s.Ok) ? Loc.T("Tamam: her adım yapıldı ve kontrol edildi.") : Loc.F("{0} adım doğrulanamadı, yukarıdaki listeye bak.", steps.Count(s => !s.Ok));
        ExitBusy = false;
        Op.End();
    }

    [RelayCommand]
    private async Task RevertGHelper()
    {
        if (!IsAdmin) { ExitStatus = Loc.T("Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."); return; }
        ExitBusy = true;
        ExitStatus = Loc.T("Geri alınıyor…");
        Op.Begin(Loc.T("Yedekten eski ayarlar geri yükleniyor…"));
        var steps = await Task.Run(_exit.Revert);
        Show(steps);
        HasBackup = _exit.HasBackup;
        ExitStatus = steps.All(s => s.Ok) ? Loc.T("Geri alındı: her şey eski haline döndü.") : Loc.F("{0} adım geri alınamadı.", steps.Count(s => !s.Ok));
        ExitBusy = false;
        Op.End();
        await Task.Delay(1500);
        await RefreshExitAsync();
    }

    private void Show(IEnumerable<ExitStep> steps)
    {
        ExitRows.Clear();
        foreach (var s in steps) ExitRows.Add(new ExitRowVm(s));
    }
}
