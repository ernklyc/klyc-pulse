using Microsoft.Win32;
using Pulse.Core.Diagnostics;

namespace Pulse.Core.Optimize;

/// <summary>Oyun performansını etkileyen belgeli Windows ayarlarından biri.</summary>
public sealed class GameSetting
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Why { get; init; }
    public required RegistryHive Hive { get; init; }
    public required string KeyPath { get; init; }
    public required string ValueName { get; init; }
    public required int Desired { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool NeedsReboot { get; init; }
    /// <summary>Değer hiç yazılmamışsa Windows'un varsayılanı (iyi mi?).</summary>
    public bool DefaultIsGood { get; init; }

    public int? Current { get; set; }
    public bool IsGood => Current is null ? DefaultIsGood : Current == Desired;
    public string CurrentText => Current is null ? "varsayılan" : Current == Desired ? "uygun" : $"değer {Current}";
}

/// <summary>
/// Oyun ayarlarını okur ve (yalnızca kullanıcı isterse) düzeltir. Her yazma geri okunarak doğrulanır,
/// eski değer Pulse günlüğüne yazılır. Güvenlik ayarlarına (Defender, Bellek Bütünlüğü vb.) dokunmaz.
/// </summary>
public static class WindowsGameSettings
{
    public static IReadOnlyList<GameSetting> Read()
    {
        var list = Catalog();
        foreach (var s in list) s.Current = ReadValue(s);
        return list;
    }

    private static List<GameSetting> Catalog() =>
    [
        new()
        {
            Id = "hags", Name = "Ekran kartı zamanlayıcısı (HAGS)", Why = "Ekran kartı kendi iş sırasını yönetir, işlemci rahatlar. Açık olması iyi.",
            Hive = RegistryHive.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "HwSchMode",
            Desired = 2, NeedsAdmin = true, NeedsReboot = true,
        },
        new()
        {
            Id = "gamemode", Name = "Windows Oyun Modu", Why = "Oyun açıkken Windows arka plandaki işleri geri çeker. Açık olması iyi.",
            Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\GameBar", ValueName = "AutoGameModeEnabled",
            Desired = 1, DefaultIsGood = true,
        },
        new()
        {
            Id = "dvr", Name = "Arka planda oyun kaydı", Why = "Kapalıyken Windows oyun sırasında sürekli video kaydetmez. Ekran kartı ve disk rahatlar. Kapalı olması iyi.",
            Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR", ValueName = "AppCaptureEnabled",
            Desired = 0, DefaultIsGood = false,
        },
        new()
        {
            Id = "dvrstore", Name = "Oyun kaydı (ikinci ayar)", Why = "Yukarıdakiyle birlikte çalışır; ikisi de kapalı olmalı.",
            Hive = RegistryHive.CurrentUser, KeyPath = @"System\GameConfigStore", ValueName = "GameDVR_Enabled",
            Desired = 0, DefaultIsGood = false,
        },
    ];

    private static int? ReadValue(GameSetting s)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(s.Hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(s.KeyPath);
            return key?.GetValue(s.ValueName) is int i ? i : null;
        }
        catch { return null; }
    }

    /// <summary>Ayarı istenen değere getirir ve geri okuyarak doğrular.</summary>
    public static (bool Ok, string Message) Fix(GameSetting s, bool isAdmin)
    {
        if (s.NeedsAdmin && !isAdmin) return (false, "Bu ayar için KLYC-Pulse'ın yönetici olarak çalışması gerekir.");
        try
        {
            var old = ReadValue(s);
            using var baseKey = RegistryKey.OpenBaseKey(s.Hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(s.KeyPath, true);
            key.SetValue(s.ValueName, s.Desired, RegistryValueKind.DWord);
            var now = ReadValue(s);
            Journal.Write($"Oyun ayarı {s.Id}: {old?.ToString() ?? "yok"} -> {now}");
            if (now != s.Desired) return (false, "Yazıldı ama geri okunan değer farklı.");
            return (true, s.NeedsReboot ? "Düzeltildi ve doğrulandı. Etkisi için yeniden başlatma gerekir." : "Düzeltildi ve doğrulandı.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
