using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Core.Companion;
using Pulse.Core.Modes;
using Pulse.Core.Platform;
using Pulse.Core.Settings;

namespace Pulse.App.ViewModels;

/// <summary>Bir özelliğin KLYC-Pulse'taki durumu: VAR (Pulse kendisi yapar), ORTAK (Pulse aracı sürerek yapar), KISMEN.</summary>
public sealed class ParityVm
{
    public ParityVm(string status, string text)
    {
        Text = text;
        (Label, var key) = status switch { "ok" => ("VAR", "GoodBrush"), "link" => ("ORTAK", "InfoBrush"), _ => ("KISMEN", "WarnBrush") };
        Brush = (Brush)Application.Current.FindResource(key);
    }

    public string Text { get; }
    public string Label { get; }
    public Brush Brush { get; }
}

/// <summary>"Bu modda araç profili şu olsun" satırı.</summary>
public sealed partial class ModeProfileVm : ObservableObject
{
    private readonly ToolLinkVm _owner;
    private bool _loading = true;

    public ModeProfileVm(ToolLinkVm owner, ModeDefinition mode, IReadOnlyList<string> options, int selected)
    {
        _owner = owner;
        ModeKey = mode.Key;
        Title = mode.Title + " modunda";
        Options = options;
        _selectedIndex = selected;
        _loading = false;
    }

    public string ModeKey { get; }
    public string Title { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private int _selectedIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_loading) _owner.SetProfile(ModeKey, value);
    }
}

/// <summary>Bir aracın Pulse ile ortak çalışma ayarları: birlikte başlat ve moda göre profil.</summary>
public sealed partial class ToolLinkVm : ObservableObject
{
    private readonly string _tool;
    private readonly ToolLink _link;
    private bool _loading = true;

    public ToolLinkVm(string tool, ToolLink link, IReadOnlyList<string>? profiles)
    {
        _tool = tool;
        _link = link;
        _startWithPulse = link.StartWithPulse;
        if (profiles is not null)
        {
            var options = new[] { "Dokunma" }.Concat(profiles).ToList();
            foreach (var m in Modes.All)
                Rows.Add(new ModeProfileVm(this, m, options, link.ModeProfiles.TryGetValue(m.Key, out var p) && p < options.Count ? p : 0));
        }
        _loading = false;
    }

    public ObservableCollection<ModeProfileVm> Rows { get; } = new();
    public bool HasProfiles => Rows.Count > 0;
    [ObservableProperty] private bool _startWithPulse;
    [ObservableProperty] private string _status = "";

    partial void OnStartWithPulseChanged(bool value)
    {
        if (_loading) return;
        _link.StartWithPulse = value;
        AppServices.Settings.Save();
        if (value) _ = Task.Run(AppServices.Companion.LaunchEnabled);
    }

    public void SetProfile(string modeKey, int index)
    {
        if (index == 0) _link.ModeProfiles.Remove(modeKey); else _link.ModeProfiles[modeKey] = index;
        AppServices.Settings.Save();
        // Seçim şu anki moda aitse hemen uygula ki sonucu göresin.
        if (AppServices.Modes.CurrentKey == modeKey && index > 0) _ = Task.Run(() => AppServices.Companion.ApplyForMode(modeKey));
    }

    public void RefreshStatus() => Status = AppServices.Companion.Status.TryGetValue(_tool, out var s) ? s : "";
}

public partial class ToolVm : ObservableObject
{
    private readonly GHelperExit _exit;
    private readonly Func<bool> _installed;
    private readonly Func<string?> _extraNote;

    public ToolVm(string name, string what, GHelperExit exit, Func<bool> installed, IEnumerable<ParityVm> parity, string? caution = null, Func<string?>? extraNote = null,
        ToolLink? link = null, IReadOnlyList<string>? profiles = null, string linkTitle = "", string linkHelp = "", Action? open = null, string openText = "Aracı aç")
    {
        Name = name;
        What = what;
        _exit = exit;
        _installed = installed;
        _extraNote = extraNote ?? (() => null);
        Caution = caution ?? "";
        LinkTitle = linkTitle;
        LinkHelp = linkHelp;
        OpenText = openText;
        if (link is not null) Link = new ToolLinkVm(name, link, profiles);
        if (open is not null) OpenCommand = new RelayCommand(open);
        foreach (var p in parity) Parity.Add(p);
    }

    public string Name { get; }
    public string What { get; }
    public string Caution { get; }
    public string LinkTitle { get; }
    public string LinkHelp { get; }
    public string OpenText { get; }
    public ToolLinkVm? Link { get; }
    public bool HasLink => Link is not null;
    public RelayCommand? OpenCommand { get; }
    public bool CanOpen => OpenCommand is not null;
    public ObservableCollection<ParityVm> Parity { get; } = new();

    [ObservableProperty] private string _state = "";
    [ObservableProperty] private Brush _stateBrush = Brushes.Gray;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _hasBackup;
    [ObservableProperty] private bool _busy;

    public void Refresh()
    {
        IsInstalled = _installed();
        HasBackup = _exit.HasBackup;
        var probe = _exit.Probe();
        IsActive = probe.Any(s => !s.Ok);
        var key = !IsInstalled ? "MutedBrush" : IsActive ? "WarnBrush" : "GoodBrush";
        State = !IsInstalled ? "Kurulu değil" : IsActive ? "Çalışıyor / açılışta başlıyor" : "Çalışmıyor";
        StateBrush = (Brush)Application.Current.FindResource(key);
        var detail = string.Join("  ·  ", probe.Where(s => !s.Ok).Select(s => s.Name));
        var extra = _extraNote();
        Detail = extra is null ? detail : string.IsNullOrEmpty(detail) ? extra : detail + "\n" + extra;
        Link?.RefreshStatus();
    }

    public Task<IReadOnlyList<ExitStep>> ApplyAsync() => Task.Run(_exit.Apply);
    public Task<IReadOnlyList<ExitStep>> RevertAsync() => Task.Run(_exit.Revert);
}

public partial class ToolsViewModel : ObservableObject
{
    private static readonly bool Admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public ToolsViewModel()
    {
        string Backup(string id) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", $"tool_exit_{id}.json");
        bool ServiceExists(string name) => ServiceControl.State(name) is not null;
        var settings = AppServices.Settings.Current;

        Tools.Add(new ToolVm("G-Helper",
            "ASUS dizüstüler için performans modu, pil sınırı, klavye ışığı ve ekran ayarı programı.",
            new GHelperExit(),
            () => GHelperControl.IsInstalled,
            [
                new("ok", "Oyun, Günlük, Sessiz ve Boşta modları; her ayarın tuttuğu kontrol edilir"),
                new("ok", "Pil şarj sınırı, klavye ışığı ve rengi (Dizüstü sayfası)"),
                new("ok", "Ekran hızı, parlaklık ve ekran kartı hız sınırı (modlarla birlikte)"),
                new("ok", "Canlı sıcaklık ve fan hızı (İzleme sayfası)"),
                new("link", "Fan hızı ayarı: bu bilgisayarın fanı yazılımla ayarlanamıyor (G-Helper da yapamaz). Onun yerine Pulse'ın “Sıcaklık sınırı” özelliği (Ayarlar) bilgisayarı serin tutar"),
                new("link", "Fn tuşları: ASUS bu tuşları yalnızca kendi programına veriyor. Pulse'ta her işe istediğin başka bir tuşu atarsın (Dizüstü > Kısayollar)"),
                new("link", "Ekran kartı Eco/MUX anahtarı: bu bilgisayarda yok. Ekran kartı Windows'un grafik tercihi ve hız sınırıyla yönetilir"),
            ],
            "İkisi aynı anda açıksa birbirinin ayarını bozabilir. G-Helper'ı kullanmayacaksan kapalı tut; Pulse onun yaptığı her şeyi yapıyor.",
            link: settings.GHelperLink,
            linkTitle: "G-Helper'ı Pulse ile birlikte aç",
            linkHelp: "Normalde gerek yok. G-Helper'ın kendi ekranını da kullanmak istersen aç.",
            open: () => { var exe = GHelperControl.FindExe(); if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); },
            openText: "G-Helper'ı aç"));

        Tools.Add(new ToolVm("MSI Afterburner",
            "Ekran kartını izleyen, oyun sırasında ekranda bilgi gösteren ve ekran kartını hızlandıran program.",
            new GHelperExit("MSIAfterburner", "MSIAfterburner", [], Backup("afterburner")),
            () => AfterburnerControl.IsInstalled,
            [
                new("ok", "Canlı sıcaklık, hız, güç, bellek ve kullanım (İzleme sayfası)"),
                new("ok", "Oyun sırasında ekranda gösterge ve kare hızı (FPS)"),
                new("ok", "Ekran kartı hız sınırı (güç ve ısı için) Pulse'ın kendisinde"),
                new("link", "Ekran kartını hızlandırma: Pulse kendisi de yapabiliyor (Dizüstü sayfası). Afterburner'da kaydettiğin özel bir profilin varsa Pulse hangi modda hangisinin kullanılacağını yönetir"),
            ],
            null,
            () =>
            {
                try
                {
                    var n = AfterburnerControl.SavedProfileCount();
                    return n > 0 ? $"{n} kayıtlı profilin var; aşağıdan hangi modda hangisinin kullanılacağını seç." : "Afterburner'da kayıtlı profilin yok. Gerekmiyor: Pulse ekran kartını kendisi de hızlandırabiliyor. İstersen Afterburner'da bir profil kaydedip burada bir moda bağlayabilirsin.";
                }
                catch { return null; }
            },
            link: settings.AfterburnerLink,
            profiles: ["Profil 1", "Profil 2", "Profil 3", "Profil 4", "Profil 5"],
            linkTitle: "Afterburner'ı Pulse ile birlikte aç",
            linkHelp: "Seçtiğin moda geçince Pulse, Afterburner'a o profili uygulamasını söyler. Afterburner cevap vermediği için burada “komut gönderildi” yazar.",
            open: () => AfterburnerControl.Launch(),
            openText: "Afterburner'ı aç"));

        Tools.Add(new ToolVm("ThrottleStop",
            "İşlemciyi izleyen, voltajını düşürüp güç sınırını ayarlayan program.",
            new GHelperExit("ThrottleStop", "ThrottleStop", [], Backup("throttlestop")),
            () => ThrottleStopControl.IsInstalled,
            [
                new("ok", "İşlemci sıcaklığı, hızı, yükü ve yavaşlama nedeni (İzleme sayfası)"),
                new("link", "İşlemciyi daha az voltajla çalıştırma (undervolt) ve güç sınırı: bu bilgisayarın BIOS'u bunları kilitlemiş, ThrottleStop dahil hiçbir program değiştiremiyor (denedik). Başka bir bilgisayarda mümkünse: ThrottleStop'ta bir kez ayarlarsın, Pulse onu başlatır ve moda göre profilini seçer"),
            ],
            "Bu bilgisayarda ThrottleStop'tan kazanacağın bir şey yok: ayarların hepsi kilitli. Kapalı tutmak en iyisi.",
            link: settings.ThrottleStopLink,
            profiles: ThrottleStopControl.ProfileNames(),
            linkTitle: "ThrottleStop'u Pulse ile birlikte aç",
            linkHelp: "Mod değişince Pulse, ThrottleStop'un profilini seçer. Bu bilgisayarda bir etkisi olmaz, başka bir bilgisayar için var.",
            open: () => { var exe = ThrottleStopControl.FindExe(); if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); },
            openText: "ThrottleStop'u aç"));

        Tools.Add(new ToolVm("Intel Driver & Support Assistant",
            "Intel sürücülerini denetleyip güncelleyen, sürekli arka planda çalışan yardımcı.",
            new GHelperExit("DSATray", "DSATray", ["DSAService", "DSAUpdateService"], Backup("intel_dsa")),
            () => ServiceExists("DSAService"),
            [
                new("ok", "BIOS ve sürücü sürümleri, eskiyenlerin işaretlenmesi (Sürücüler sayfası)"),
                new("ok", "Windows Update'te bekleyen sürücü taraması; gerçekten yeni olanı ayırır"),
                new("link", "Kurulumu Windows Update ya da NVIDIA App yapar (güvenli, geri alınabilir). Pulse seni oraya götürür"),
            ]));

        Tools.Add(new ToolVm("Microsoft PC Manager",
            "Temizlik, hızlandırma, süreç ve başlangıç yönetimi aracı.",
            new GHelperExit("MSPCManager", "MSPCManager", ["MSPCManagerService"], Backup("pcmanager")),
            () => ServiceExists("MSPCManagerService") || Process.GetProcessesByName("MSPCManager").Length > 0,
            [
                new("ok", "Geçici dosya temizliği ve derin temizlik (Temizlik sayfası)"),
                new("ok", "Tek tuşla hızlandır, açılış ve arka plan yönetimi, sağlık raporu"),
                new("ok", "Süreç yöneticisi: kapat, öncelik, Eko mod (Süreçler sayfası)"),
            ]));

        AppServices.Companion.StatusChanged += () => Application.Current.Dispatcher.BeginInvoke(() => { foreach (var t in Tools) t.Link?.RefreshStatus(); });
        _ = RefreshAsync();
    }

    public ObservableCollection<ToolVm> Tools { get; } = new();
    public OperationVm Op { get; } = new();
    [ObservableProperty] private string _note = "";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        foreach (var t in Tools) await Task.Run(t.Refresh);
    }

    [RelayCommand]
    private async Task Leave(ToolVm? tool)
    {
        if (tool is null || tool.Busy) return;
        if (!Admin) { Note = "Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."; return; }
        var warn = tool.Caution.Length > 0 ? $"\n\nDikkat: {tool.Caution}" : "";
        var ask = MessageBox.Show(
            $"{tool.Name} kapatılacak ve Windows ile başlamayacak.\n\nHiçbir şey silinmez. \"Geri al\" ile eski haline döner.{warn}\n\nDevam edilsin mi?",
            $"{tool.Name}'ı kapat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        tool.Busy = true;
        Op.Begin($"{tool.Name} kapatılıyor ve doğrulanıyor…");
        var steps = await tool.ApplyAsync();
        await Task.Run(tool.Refresh);
        Op.End();
        tool.Busy = false;
        Note = steps.All(s => s.Ok) ? $"{tool.Name}: tamam, her adım doğrulandı." : $"{tool.Name}: {steps.Count(s => !s.Ok)} adım doğrulanamadı.";
    }

    [RelayCommand]
    private async Task Revert(ToolVm? tool)
    {
        if (tool is null || tool.Busy) return;
        if (!Admin) { Note = "Bu işlem için KLYC-Pulse'ın yönetici olarak çalışması gerekir."; return; }
        tool.Busy = true;
        Op.Begin($"{tool.Name} eski haline döndürülüyor…");
        var steps = await tool.RevertAsync();
        await Task.Delay(1200);
        await Task.Run(tool.Refresh);
        Op.End();
        tool.Busy = false;
        Note = steps.All(s => s.Ok) ? $"{tool.Name}: geri alındı." : $"{tool.Name}: {steps.Count(s => !s.Ok)} adım geri alınamadı.";
    }
}
