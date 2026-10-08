using System.Diagnostics;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Modes;

/// <summary>Pulse'ın seçtiği modu bozabilen diğer ASUS araçları.</summary>
public static class ConflictService
{
    private static readonly (string Process, string Label)[] Known =
    [
        ("GHelper", "G-Helper"),
        ("ArmouryCrate.UserSessionHelper", "Armoury Crate"),
        // ArmouryCrateKeyControl yalnızca Fn tuşlarına bakar, modları değiştirmez; bu yüzden uyarı listesinde değil.
    ];

    public static IReadOnlyList<string> Running() =>
        Known.Where(k => Process.GetProcessesByName(k.Process).Length > 0).Select(k => k.Label).ToList();

    /// <summary>Çakışan uygulamaları nazikçe, olmazsa zorla kapatır. Kapatılanların adını döndürür.</summary>
    public static IReadOnlyList<string> CloseAll()
    {
        var closed = new List<string>();
        foreach (var (name, label) in Known)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) continue;
            foreach (var p in procs)
            {
                try
                {
                    if (!p.CloseMainWindow() || !p.WaitForExit(1500)) p.Kill();
                }
                catch (Exception ex) { Journal.Write($"{label} kapatılamadı: {ex.Message}"); }
            }
            closed.Add(label);
            Journal.Write($"{label} kapatıldı (mod çakışması).");
        }
        return closed;
    }
}