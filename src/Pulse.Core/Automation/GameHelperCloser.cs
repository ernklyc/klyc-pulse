using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulse.Core.Diagnostics;
using Pulse.Core.Platform;

namespace Pulse.Core.Automation;

/// <summary>Oyun için geçici olarak durdurulan şey: bir Windows servisi ya da bir program (yolu bilinirse oyun bitince yeniden başlatılır).</summary>
public sealed record ClosedHelper(string Name, string Kind, string Target, string? Path);

/// <summary>Sistem tarafı (testlerde sahte): çalışan süreçler, servisler, durdurma/başlatma.</summary>
public interface IHelperHost
{
    /// <summary>Verilen adda çalışan süreçler (pid, tam yol ya da null, pencere var mı, oturum 0 mı).</summary>
    IReadOnlyList<(int Pid, string? Path, bool HasWindow, bool InSession0)> Processes(string name);
    /// <summary>Bu program adıyla ilişkili ÇALIŞAN servisler (servis adı ya da yolu program adını içerir). Servis varsa program onu yeniden başlatır; bu yüzden servis durdurulur.</summary>
    IReadOnlyList<string> RunningServicesFor(string name);
    bool StopService(string service);
    bool StartService(string service);
    /// <summary>Programa nazikçe kapat der; kapanmayanı zorla sonlandırır. Hepsi kapandıysa true.</summary>
    bool CloseProcess(int pid);
    bool Launch(string path);
}

/// <summary>
/// "Oyun sırasında kapat" listesi: kullanıcının AÇIKÇA seçtiği arka plan programlarını (ör. Sunshine uzaktan oynatma) oyun açılınca durdurur,
/// oyun bitince geri getirir. Varsayılan liste BOŞTUR; Pulse hiçbir programı kendiliğinden seçmez. Korumalı süreçler (Windows bileşenleri, anti-hile,
/// Pulse/Konsol kendisi) hiçbir zaman kapatılmaz. Servisle çalışan programlarda servis durdurulur (yoksa servis programı hemen yeniden başlatırdı).
/// Durum diske yazılır: Pulse oyun sırasında kapanırsa/çökerse sonraki açılışta kapatılanlar geri getirilir.
/// </summary>
public sealed class GameHelperCloser
{
    private static readonly Regex NameRx = new(@"^[A-Za-z0-9._ \-]{1,64}$", RegexOptions.Compiled);

    private readonly IHelperHost _host;
    private readonly string _statePath;
    private readonly object _gate = new();
    private List<ClosedHelper> _closed = new();

    public GameHelperCloser(IHelperHost? host = null, string? statePath = null)
    {
        _host = host ?? new SystemHelperHost();
        _statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "game_helpers_closed.json");
        _closed = LoadState();
    }

    public IReadOnlyList<ClosedHelper> Closed { get { lock (_gate) return _closed.ToList(); } }

    /// <summary>Program adı geçerli ve korumalı değil mi? (Ayar eklenirken ve kapatırken denetlenir.) İkinci değer: nedeni.</summary>
    public static (bool Ok, string? Why) Validate(string name)
    {
        if (!NameRx.IsMatch(name ?? "")) return (false, "Geçersiz program adı.");
        var n = name!.Trim();
        var (prot, why) = Pulse.Core.Processes.ProcessInspector.Protection(n, null, true, false);
        if (prot) return (false, why ?? "Korumalı program.");
        if (n.Equals("explorer", StringComparison.OrdinalIgnoreCase) || n.Equals("dwm", StringComparison.OrdinalIgnoreCase)) return (false, "Windows için gerekli.");
        return (true, null);
    }

    /// <summary>Listedekileri durdurur. Daha önce kapatılanlar (aynı oyun içinde ikinci çağrı) tekrar işlenmez. Dönüş: bu çağrıda kapatılanlar.</summary>
    public IReadOnlyList<ClosedHelper> CloseListed(IEnumerable<string> names)
    {
        var done = new List<ClosedHelper>();
        foreach (var raw in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = raw.Trim();
            if (!Validate(name).Ok) { Journal.Write($"Oyun sırasında kapatılacak program atlandı ({name}): korumalı ya da geçersiz."); continue; }
            lock (_gate) if (_closed.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var services = _host.RunningServicesFor(name);
                if (services.Count > 0)
                {
                    foreach (var svc in services)
                        if (_host.StopService(svc)) done.Add(new ClosedHelper(name, "service", svc, null)); else Journal.Write($"Servis durdurulamadı: {svc}");
                    continue;
                }
                var procs = _host.Processes(name);
                foreach (var p in procs.Where(p => !p.InSession0))
                {
                    if (Pulse.Core.Processes.ProcessInspector.Protection(name, p.Path, p.HasWindow, p.InSession0).Item1) continue;
                    if (_host.CloseProcess(p.Pid)) done.Add(new ClosedHelper(name, "process", p.Pid.ToString(), p.Path)); else Journal.Write($"Program kapatılamadı: {name} ({p.Pid})");
                }
            }
            catch (Exception ex) { Journal.Write($"Oyun sırasında kapatma hatası ({name}): {ex.Message}"); }
        }
        // aynı programın birden çok süreci tek geri getirme kaydına iner (yoluna göre)
        var merged = done.GroupBy(d => (d.Name.ToLowerInvariant(), d.Kind, d.Kind == "service" ? d.Target : d.Path ?? d.Target)).Select(g => g.First()).ToList();
        if (merged.Count > 0)
        {
            lock (_gate) { _closed.AddRange(merged); SaveState(); }
            Journal.Write("Oyun için geçici kapatıldı: " + string.Join(", ", merged.Select(m => m.Name)) + ".");
        }
        return merged;
    }

    /// <summary>Oyun bitti: kapatılanları geri getirir (servisler başlatılır, programlar yoluyla açılır). Dönüş: geri getirilenlerin adları.</summary>
    public IReadOnlyList<string> RestoreAll()
    {
        List<ClosedHelper> todo;
        lock (_gate) { todo = _closed.ToList(); _closed = new(); SaveState(); }
        var restored = new List<string>();
        foreach (var c in todo)
        {
            try
            {
                var ok = c.Kind == "service" ? _host.StartService(c.Target) : c.Path is { Length: > 0 } && _host.Launch(c.Path);
                if (ok) restored.Add(c.Name); else Journal.Write($"Oyun sonrası geri getirilemedi: {c.Name}");
            }
            catch (Exception ex) { Journal.Write($"Geri getirme hatası ({c.Name}): {ex.Message}"); }
        }
        if (restored.Count > 0) Journal.Write("Oyun bitti, geri getirildi: " + string.Join(", ", restored.Distinct()) + ".");
        return restored;
    }

    private List<ClosedHelper> LoadState()
    {
        try { return File.Exists(_statePath) ? JsonSerializer.Deserialize<List<ClosedHelper>>(File.ReadAllText(_statePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private void SaveState()
    {
        try
        {
            if (_closed.Count == 0) { if (File.Exists(_statePath)) File.Delete(_statePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_closed));
        }
        catch (Exception ex) { Journal.Write("Oyun yardımcıları durumu yazılamadı: " + ex.Message); }
    }
}

/// <summary>Gerçek sistem uygulaması.</summary>
public sealed class SystemHelperHost : IHelperHost
{
    public IReadOnlyList<(int, string?, bool, bool)> Processes(string name)
    {
        var list = new List<(int, string?, bool, bool)>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                string? path = null; try { path = p.MainModule?.FileName; } catch { }
                list.Add((p.Id, path, p.MainWindowHandle != IntPtr.Zero, p.SessionId == 0));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    public IReadOnlyList<string> RunningServicesFor(string name)
    {
        var result = new List<string>();
        try
        {
            foreach (var row in Pulse.Core.Health.WmiReader.Query(@"root\cimv2", "SELECT Name, PathName, State FROM Win32_Service WHERE State='Running'"))
            {
                var svc = row.TryGetValue("Name", out var n) ? n as string : null;
                var path = row.TryGetValue("PathName", out var pn) ? pn as string : null;
                if (svc is null) continue;
                if (path is not null && path.TrimStart('"').StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;   // Windows'un kendi servislerine asla dokunma
                if (svc.Contains(name, StringComparison.OrdinalIgnoreCase) || (path is not null && path.Contains("\\" + name, StringComparison.OrdinalIgnoreCase))) result.Add(svc);
            }
        }
        catch (Exception ex) { Journal.Write("Servis listesi okunamadı: " + ex.Message); }
        return result;
    }

    public bool StopService(string service) => ServiceControl.Stop(service);
    public bool StartService(string service) => ServiceControl.Start(service);

    public bool CloseProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.CloseMainWindow() || !p.WaitForExit(2500)) { p.Kill(); p.WaitForExit(2000); }
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }     // zaten kapanmış
        catch { return false; }
    }

    public bool Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); return true; }
        catch { return false; }
    }
}
