using Pulse.Core.Localization;
using System.Diagnostics;
using System.Security;

namespace Pulse.Core.Platform;

/// <summary>
/// Windows açılışında yönetici yetkisiyle, UAC sormadan başlatma (Görev Zamanlayıcı).
/// Etkinleştirmek için uygulamanın yönetici olarak çalışması gerekir.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "KLYC-Pulse";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName).Code == 0;

    public static (bool Ok, string Message) Enable(string exePath)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>KLYC-Pulse açılışta tepsiye başlar</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "klyc-pulse-task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            var r = Run("/Create", "/TN", TaskName, "/XML", tmp, "/F");
            return (r.Code == 0, r.Code == 0 ? Loc.T("Açılışta otomatik başlatma etkin.") : r.Output.Trim());
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Görev XML'inden çalıştırılacak program yolunu okur (bozuk/boşsa null).</summary>
    public static string? ParseCommand(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var cmd = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
            return string.IsNullOrWhiteSpace(cmd) ? null : cmd.Trim().Trim('"');
        }
        catch { return null; }
    }

    /// <summary>
    /// Kendi kendini onarma: görev var ama gösterdiği program artık yoksa (taşınabilir exe silinmiş ya da kurulum klasörü değişmiş; Velopack kurulumuna geçince oldu)
    /// görevi bu çalışan exe'ye yönlendirir. Program hâlâ varsa dokunmaz (bilerek başka bir yol seçilmiş olabilir). Yönetici gerekir; yoksa sessizce atlar.
    /// <paramref name="read"/>/<paramref name="enable"/>/<paramref name="exists"/> testlerde sahtelenir. Dönüş: görev yeniden yazıldıysa true.
    /// </summary>
    public static bool EnsureCurrent(string currentExe, Func<string?>? read = null, Func<string, (bool Ok, string Message)>? enable = null, Func<string, bool>? exists = null)
    {
        read ??= () => { var r = Run("/Query", "/TN", TaskName, "/XML"); return r.Code == 0 ? ParseCommand(r.Output) : null; };
        enable ??= Enable; exists ??= File.Exists;
        var cmd = read();
        if (cmd is null) return false;                                  // görev yok (ya da okunamadı): açılışta başlatma kapalı, dokunma
        if (string.Equals(cmd, currentExe, StringComparison.OrdinalIgnoreCase) || exists(cmd)) return false;
        var (ok, msg) = enable(currentExe);
        Pulse.Core.Diagnostics.Journal.Write($"Açılış görevi onarıldı: {cmd} (yok) → {currentExe}: {(ok ? "tamam" : msg)}");
        return ok;
    }

    public static (bool Ok, string Message) Disable()
    {
        var r = Run("/Delete", "/TN", TaskName, "/F");
        return (r.Code == 0, r.Code == 0 ? Loc.T("Açılışta başlatma kapatıldı.") : r.Output.Trim());
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, o);
    }
}