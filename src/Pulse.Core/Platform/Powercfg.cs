using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Pulse.Core.Platform;

/// <summary>Windows güç planı ayarları (powercfg.exe sarmalayıcısı). Dil bağımsız çalışır.</summary>
public static class Powercfg
{
    // Alt grup ve ayar kimlikleri
    public const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    public const string BoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    public const string MaxProcessorState = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    public const string EnergyPerformancePref = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    /// <summary>İşlemci en yüksek frekansı (MHz, 0 = sınırsız). "Üst sınır %"dan farkı: %100'ün altı turbo'yu tamamen kapatabilir, bu ise hızı MHz olarak kademeli sınırlar.</summary>
    public const string MaxFrequency = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";
    /// <summary>Aynı ayar, "verimlilik sınıfı 1" (hibrit Intel 12. nesil+ işlemcilerde performans çekirdekleri). Hibrit olmayanlarda etkisizdir.</summary>
    public const string MaxFrequencyClass1 = "75b0ae3f-bce0-45a7-8c89-c9611c25e101";
    /// <summary>Sistem soğutma ilkesi: 1 = Etkin (önce fan), 0 = Pasif (önce işlemci yavaşlar). Modern bekleme destekli bazı dizüstülerde yoktur.</summary>
    public const string SystemCoolingPolicy = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
    public const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    public const string VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    public const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    public const string StandbyIdle = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";

    private static readonly Regex GuidRx = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex HexRx = new("0x[0-9a-fA-F]+", RegexOptions.Compiled);

    public static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powercfg.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(8000);
        return output;
    }

    public static string? ActiveScheme()
    {
        var m = GuidRx.Match(Run("/getactivescheme"));
        return m.Success ? m.Value : null;
    }

    public static IReadOnlyList<string> AllSchemes() =>
        GuidRx.Matches(Run("/list")).Select(m => m.Value).Distinct().ToList();

    /// <summary>Prize takılıyken (AC) geçerli değer. Okunamazsa null.</summary>
    public static int? GetAc(string scheme, string sub, string setting)
    {
        var hex = HexRx.Matches(Run("/q", scheme, sub, setting));
        // Çıktının son iki değeri sırasıyla AC ve DC geçerli değerleridir.
        if (hex.Count < 2) return null;
        return Convert.ToInt32(hex[^2].Value, 16);
    }

    /// <summary>Pildayken (DC) geçerli değer. Okunamazsa null.</summary>
    public static int? GetDc(string scheme, string sub, string setting)
    {
        var hex = HexRx.Matches(Run("/q", scheme, sub, setting));
        return hex.Count < 2 ? null : Convert.ToInt32(hex[^1].Value, 16);
    }

    public static void SetDc(string scheme, string sub, string setting, int value) =>
        Run("/setdcvalueindex", scheme, sub, setting, value.ToString());

    public static void SetAc(string scheme, string sub, string setting, int value) =>
        Run("/setacvalueindex", scheme, sub, setting, value.ToString());

    public static void SetActive(string scheme) => Run("/setactive", scheme);

    /// <summary>İşlemci en yüksek frekansını (MHz, 0 = sınırsız) prizde ve pilde, iki çekirdek sınıfı için de yazar. Planı etkinleştirmez.</summary>
    public static void SetFrequencyCap(string scheme, int mhz)
    {
        foreach (var setting in new[] { MaxFrequency, MaxFrequencyClass1 })
        {
            SetAc(scheme, SubProcessor, setting, mhz);
            SetDc(scheme, SubProcessor, setting, mhz);
        }
    }
}
