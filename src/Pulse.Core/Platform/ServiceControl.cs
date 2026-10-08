using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pulse.Core.Platform;

/// <summary>Windows servisleri (sc.exe sarmalayıcısı, ek paket gerektirmez). Durdurma/başlatma yönetici ister.</summary>
public static class ServiceControl
{
    public static bool IsRunning(string name) => State(name) == 4;

    /// <summary>1 durdu, 4 çalışıyor, null: bulunamadı.</summary>
    public static int? State(string name)
    {
        var m = Regex.Match(Run("query", name).Output, @"STATE\s*:\s*(\d)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static bool Stop(string name, int timeoutSeconds = 15)
    {
        Run("stop", name);
        var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < end)
        {
            if (State(name) is 1 or null) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    public static bool Start(string name) => Run("start", name).Code == 0;

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, o);
    }
}