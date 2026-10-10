using Pulse.Core.Localization;
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
        if (code != 0 && !output.Contains("----")) return (Array.Empty<UpgradeInfo>(), Loc.F("winget çalışmadı (kod {0}).", code));
        return (ParseTable(output), null);
    }

    /// <summary>
    /// Oyun başlatıcıları (Epic, Steam...). Bazılarının kurulum paketi (ör. Epic'inki) yükseltirken eskisini kaldırıp yeniden kurar;
    /// başlatıcı birkaç dakika yok olur. Oyun dosyalarına dokunmaz ama kullanıcı bunu bilmeli ve toplu güncellemeye karışmamalı.
    /// </summary>
    public static bool IsGameLauncher(string id) => GameLauncherIds.Contains(id);

    private static readonly HashSet<string> GameLauncherIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "EpicGames.EpicGamesLauncher", "Valve.Steam", "RockstarGames.Launcher", "ElectronicArts.EADesktop",
        "Ubisoft.Connect", "GOG.Galaxy", "Blizzard.BattleNet", "Playnite.Playnite", "Amazon.Games",
    };

    /// <summary>Güncelleme sonrası doğrulama: paket hâlâ kurulu mu, beklenen sürüm yazıyor mu? (winget'in "başarılı" demesine güvenmez.)</summary>
    public static async Task<(bool Installed, bool VersionOk)> VerifyAsync(string id, string expectedVersion)
    {
        var (code, output) = await RunAsync($"list --id \"{id}\" --exact --accept-source-agreements --disable-interactivity", 60000);
        var installed = code == 0 && output.Contains(id, StringComparison.OrdinalIgnoreCase);
        var versionOk = installed && (expectedVersion.Length == 0 || output.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase));
        Journal.Write($"winget doğrulama {id}: kurulu={installed}, sürüm={versionOk}");
        return (installed, versionOk);
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

        // Sütun başlangıçları başlık satırından alınır: ad sütunu tam dolunca (ör. "... (x86) - 12.0.30501") ad ile kimlik arasında
        // tek boşluk kalır ve boşluğa göre bölmek sürüm numarasını kimlik sanıyordu (yanlış paket kimliğiyle yükseltme denenirdi).
        var starts = start > 0
            ? Regex.Matches(lines[start - 1], @"\S+(?: \S+)*").Select(m => m.Index).ToArray()
            : Array.Empty<int>();

        var items = new List<UpgradeInfo>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            if (starts.Length >= 4 && line.Length > starts[3] && TrySlice(line, starts, out var sliced)) { items.Add(sliced); continue; }
            var cols = Regex.Split(line.Trim(), @"\s{2,}");
            // Tablo bitti: alt bilgi satırı ("N yükseltme var." vb.) sütun yapısında değil
            if (cols.Length < 4) { if (items.Count > 0) break; continue; }
            var id = cols[1];
            if (!LooksLikeId(id)) continue;
            items.Add(new UpgradeInfo(cols[0], id, cols[2], cols[3]));
        }
        return items;
    }

    /// <summary>Paket kimliği "Satıcı.Ürün" biçimindedir: boşluk içermez ve yalnızca sürüm numarası (12.0.30501.0) değildir.</summary>
    private static bool LooksLikeId(string id) =>
        id.Length > 2 && !id.Contains(' ') && !Regex.IsMatch(id, @"^\d+(\.\d+)+$");

    private static bool TrySlice(string line, int[] starts, out UpgradeInfo info)
    {
        info = null!;
        string Cut(int a, int b) => (b < 0 ? line[a..] : line[a..Math.Min(b, line.Length)]).Trim();
        var name = Cut(starts[0], starts[1]);
        var id = Cut(starts[1], starts[2]);
        var cur = Cut(starts[2], starts[3]);
        var avail = Cut(starts[3], starts.Length > 4 ? starts[4] : -1);
        if (name.Length == 0 || !LooksLikeId(id) || cur.Length == 0 || avail.Length == 0 || avail.Contains(' ')) return false;
        info = new UpgradeInfo(name, id, cur, avail);
        return true;
    }

    /// <summary>winget'i arka plan iş parçacığında çalıştırır. Çağıran (arayüz) iş parçacığını asla bloke etmez.</summary>
    // winget aynı anda tek işlem kaldırır (ikinci kurulum "başka bir kurulum sürüyor" diye düşer); çağrılar sıraya girer.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Task<(int Code, string Output)> RunAsync(string args, int timeoutMs) => Task.Run(async () =>
    {
        await Gate.WaitAsync();
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
                return (-1, Loc.T("Zaman aşımı"));
            }
            return (p.ExitCode, await read);
        }
        catch (Exception ex) { return (-1, ex.Message); }
        finally { Gate.Release(); }
    });
}