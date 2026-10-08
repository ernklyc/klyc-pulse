using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Apps;

public sealed record UpgradeInfo(string Name, string Id, string Current, string Available);

/// <summary>Güncellemeler Windows Paket Yöneticisi (winget) üzerinden listelenir ve kurulur.</summary>
public static class WingetService
{
    public static bool IsAvailable() => RunAsync("--version", 8000).GetAwaiter().GetResult().Code == 0;

    public static async Task<(IReadOnlyList<UpgradeInfo> Items, string? Error)> GetUpgradesAsync()
    {
        var (code, output) = await RunAsync("upgrade --accept-source-agreements --disable-interactivity", 90000);
        if (code != 0 && !output.Contains("----")) return (Array.Empty<UpgradeInfo>(), $"winget çalışmadı (kod {code}).");
        return (ParseTable(output), null);
    }

    public static async Task<(bool Ok, string Output)> UpgradeAsync(string id)
    {
        Journal.Write($"winget güncelleme: {id}");
        var (code, output) = await RunAsync($"upgrade --id \"{id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", 30 * 60 * 1000);
        Journal.Write($"winget güncelleme sonucu {id}: kod {code}");
        return (code == 0, output);
    }

    /// <summary>winget tablosunu çözer. Başlık dilinden bağımsız: çizgi satırından sonrası, sütunlar 2+ boşlukla ayrılır.</summary>
    public static IReadOnlyList<UpgradeInfo> ParseTable(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l.Trim(), "^-{8,}$"));
        if (start < 0) return Array.Empty<UpgradeInfo>();

        var items = new List<UpgradeInfo>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            var cols = Regex.Split(line.Trim(), @"\s{2,}");
            // Tablo bitti: alt bilgi satırı ("N yükseltme var." vb.) sütun yapısında değil
            if (cols.Length < 4) { if (items.Count > 0) break; continue; }
            var id = cols[1];
            if (id.Contains(' ')) continue;
            items.Add(new UpgradeInfo(cols[0], id, cols[2], cols[3]));
        }
        return items;
    }

    /// <summary>winget'i arka plan iş parçacığında çalıştırır. Çağıran (arayüz) iş parçacığını asla bloke etmez.</summary>
    private static Task<(int Code, string Output)> RunAsync(string args, int timeoutMs) => Task.Run(async () =>
    {
        try
        {
            var psi = new ProcessStartInfo("winget.exe", args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, "Zaman aşımı");
            }
            return (p.ExitCode, await read);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    });
}