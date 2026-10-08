using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Optimize;

/// <summary>
/// Oyun başına iki güvenli, belgeli ayar: Windows "Grafik ayarları"ndaki ekran kartı tercihi (çift ekran kartlı
/// dizüstülerde oyunun yanlışlıkla Intel'de çalışmasını önler) ve süreç önceliği. Her yazma geri okunarak doğrulanır.
/// </summary>
public static class GameTuning
{
    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>0 Windows seçer, 1 güç tasarrufu, 2 yüksek performans; kayıt yoksa null.</summary>
    public static int? GetGpuPreference(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuKey);
            if (key?.GetValue(exePath) is not string value) return null;
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[14..], out var n)) return n;
            return 0;
        }
        catch { return null; }
    }

    /// <summary>Yüksek performanslı ekran kartını seçer. Aynı kaydın diğer bayraklarını (AppStatus vb.) korur. Doğrulanırsa true.</summary>
    public static bool SetHighPerformanceGpu(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuKey, true);
            var parts = (key.GetValue(exePath) as string ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            parts.Add("GpuPreference=2");
            key.SetValue(exePath, string.Join(';', parts) + ";", RegistryValueKind.String);
            var ok = GetGpuPreference(exePath) == 2;
            Journal.Write($"Ekran kartı tercihi (yüksek performans): {exePath} -> {(ok ? "doğrulandı" : "doğrulanamadı")}");
            return ok;
        }
        catch (Exception ex) { Journal.Write("Ekran kartı tercihi yazılamadı: " + ex.Message); return false; }
    }

    /// <summary>Süreç önceliğini "Yüksek" yapar (Gerçek zamanlı asla). Geri okuyup doğrular.</summary>
    public static bool SetHighPriority(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.PriorityClass is ProcessPriorityClass.High or ProcessPriorityClass.RealTime) return true;
            p.PriorityClass = ProcessPriorityClass.High;
            p.Refresh();
            var ok = p.PriorityClass == ProcessPriorityClass.High;
            Journal.Write($"Süreç önceliği Yüksek: {p.ProcessName} ({pid}) -> {(ok ? "doğrulandı" : "doğrulanamadı")}");
            return ok;
        }
        catch (Exception ex) { Journal.Write($"Öncelik ayarlanamadı ({pid}): {ex.Message}"); return false; }
    }
}
