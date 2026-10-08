using Pulse.Core.Localization;
using System.Diagnostics;
using Pulse.Core.Diagnostics;
using Pulse.Core.Platform;

namespace Pulse.App;

/// <summary>
/// Güncelleme denetimi: açılışta (ve her 6 saatte bir) en fazla günde bir kez GitHub'daki en son kararlı sürümü sorar.
/// Yeni sürüm varsa küçük bir uyarı gösterir ve Ana ekranda bir kart çıkar. Hiçbir şey indirmez ya da kurmaz: "İndir" yalnızca
/// sürüm sayfasını tarayıcıda açar. Ayarlardan kapatılabilir; ağ yoksa ya da depo gizliyse sessizce geçer.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly Version Current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    private Timer? _timer;
    private int _busy;

    public UpdateInfo? Available { get; private set; }
    public string Status { get; private set; } = Loc.T("Henüz denetlenmedi.");
    public event Action? Changed;

    public static string CurrentText => Current.ToString(3);

    public void Start()
    {
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(25), TimeSpan.FromHours(6));
    }

    private async Task TickAsync()
    {
        var s = AppServices.Settings.Current;
        if (!s.CheckUpdates) return;
        if (s.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(23)) return;
        await CheckAsync(manual: false);
    }

    /// <summary>manual true ise (kullanıcı bastı) günlük sınır ve "daha sonra" tercihi yok sayılır.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var s = AppServices.Settings.Current;
            var r = await UpdateChecker.CheckAsync(Current);
            s.LastUpdateCheck = DateTime.Now;
            if (r.Error is not null)
            {
                Status = r.Error;
                Journal.Write("Güncelleme denetimi: " + r.Error);
            }
            else if (r.IsNewer && r.Latest is { } l)
            {
                Available = l;
                Status = Loc.F("Yeni sürüm var: {0} (şu an {1}).", l.Version.ToString(3), CurrentText);
                Journal.Write($"Güncelleme denetimi: yeni sürüm {l.Tag}.");
                if (!manual && s.DismissedUpdate != l.Tag)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        NoticeChip.Show(Loc.F("Yeni sürüm var: KLYC-Pulse {0}.", l.Version.ToString(3)), false, () => App.OpenPage(typeof(Pages.HomePage))));
            }
            else
            {
                Available = null;
                Status = Loc.F("Güncelsin ({0}).", CurrentText);
            }
            AppServices.Settings.Save();
            Changed?.Invoke();
        }
        catch (Exception ex) { Journal.Write("Güncelleme denetimi hatası: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>"Daha sonra": bu sürüm için Ana ekran kartı gizlenir (yeni bir sürüm çıkınca yine gösterilir).</summary>
    public void Dismiss()
    {
        if (Available is { } a) { AppServices.Settings.Current.DismissedUpdate = a.Tag; AppServices.Settings.Save(); }
        Changed?.Invoke();
    }

    public bool ShowBanner => Available is { } a && AppServices.Settings.Current.DismissedUpdate != a.Tag;

    /// <summary>Sürüm sayfasını tarayıcıda açar (indirmeyi kullanıcı başlatır).</summary>
    public void OpenReleasePage()
    {
        var url = Available?.Url ?? UpdateChecker.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Journal.Write("Sürüm sayfası açılamadı: " + ex.Message); }
    }

    public void Dispose() => _timer?.Dispose();
}