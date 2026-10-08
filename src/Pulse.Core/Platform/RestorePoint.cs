using Pulse.Core.Localization;
using System.Diagnostics;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Platform;

/// <summary>
/// Riskli işlemlerden önce Windows Geri Yükleme Noktası. Yönetici gerekir; Windows aynı 24 saatte
/// birden fazla nokta oluşturmaz, bu durumda mevcut nokta yeterlidir.
/// </summary>
public static class RestorePoint
{
    public sealed record Result(bool Created, string Message);
    public sealed record Info(int Sequence, string Description, DateTime Time, string Type);

    /// <summary>Mevcut Geri Yükleme Noktaları. Okumak için yönetici gerekir.</summary>
    public static Task<(IReadOnlyList<Info> Items, string? Error)> ListAsync() => Task.Run<(IReadOnlyList<Info>, string?)>(() =>
    {
        try
        {
            var script = "$ErrorActionPreference='Stop'; @(Get-ComputerRestorePoint | ForEach-Object { [pscustomobject]@{ Seq=[int]$_.SequenceNumber; Desc=[string]$_.Description; Type=[string]$_.RestorePointType; Time=[Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime).ToString('o') } }) | ConvertTo-Json -Compress";
            var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            if (p.ExitCode != 0 && output.Length == 0)
                return (Array.Empty<Info>(), error.Contains("Access", StringComparison.OrdinalIgnoreCase) || error.Contains("erişim", StringComparison.OrdinalIgnoreCase) ? Loc.T("Listeyi okumak için KLYC-Pulse'ın yönetici olarak çalışması gerekir.") : Loc.T("Liste okunamadı."));
            if (output.Length == 0 || output == "[]") return (Array.Empty<Info>(), null);

            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var items = new List<Info>();
            var elements = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in elements)
                items.Add(new Info(e.GetProperty("Seq").GetInt32(), e.GetProperty("Desc").GetString() ?? "", DateTime.Parse(e.GetProperty("Time").GetString()!), e.GetProperty("Type").GetString() ?? ""));
            return (items.OrderByDescending(i => i.Time).ToList(), null);
        }
        catch (Exception ex) { return (Array.Empty<Info>(), ex.Message); }
    });

    /// <summary>Windows'un kendi Sistem Geri Yükleme penceresini açar. Geri yüklemeyi kullanıcı orada kendisi başlatır.</summary>
    public static void OpenSystemRestore()
    {
        try { Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true }); }
        catch (Exception ex) { Journal.Write("Sistem Geri Yükleme açılamadı: " + ex.Message); }
    }

    public static Task<Result> CreateAsync(string description) => Task.Run(() =>
    {
        try
        {
            var script =
                "$ErrorActionPreference='Stop'; " +
                "try { Enable-ComputerRestore -Drive 'C:\\' } catch {}; " +
                $"$before=(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object).Count; " +
                $"$w=$null; Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS -WarningVariable w -WarningAction SilentlyContinue; " +
                "$after=(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object).Count; " +
                "if($after -gt $before){ 'CREATED' } elseif($w){ 'LIMIT' } else { 'UNKNOWN' }";
            var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Journal.Write("Geri yükleme noktası: " + output.Trim());
            if (output.Contains("CREATED")) return new Result(true, Loc.T("Geri Yükleme Noktası oluşturuldu."));
            if (output.Contains("LIMIT")) return new Result(false, Loc.T("Son 24 saatte zaten bir nokta var, o geçerli."));
            return new Result(false, Loc.T("Geri Yükleme Noktası oluşturulamadı (yönetici izni ya da Sistem Koruması kapalı olabilir)."));
        }
        catch (Exception ex) { return new Result(false, ex.Message); }
    });
}