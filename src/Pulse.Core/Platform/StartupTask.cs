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
            return (r.Code == 0, r.Code == 0 ? "Açılışta otomatik başlatma etkin." : r.Output.Trim());
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static (bool Ok, string Message) Disable()
    {
        var r = Run("/Delete", "/TN", TaskName, "/F");
        return (r.Code == 0, r.Code == 0 ? "Açılışta başlatma kapatıldı." : r.Output.Trim());
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