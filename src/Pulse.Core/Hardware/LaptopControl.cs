using Pulse.Core.Diagnostics;
using Pulse.Core.Settings;

namespace Pulse.Core.Hardware;

public sealed record HardwareResult(bool Ok, string Message);

/// <summary>
/// ASUS dizüstüne özgü donanım ayarları: pil şarj limiti ve klavye ışığı.
/// Her yazma geri okunarak doğrulanır; okunamayan değerler dürüstçe "kabul edildi" olarak bildirilir.
/// </summary>
public static class LaptopControl
{
    public static bool IsAvailable()
    {
        using var acpi = AsusAcpi.TryOpen();
        return acpi is not null;
    }

    public static int? ReadKeyboardLevel()
    {
        using var acpi = AsusAcpi.TryOpen();
        return acpi?.GetKeyboardBrightness();
    }

    /// <summary>Klavye ışığını ayarlar ve geri okuyup doğrular.</summary>
    public static HardwareResult SetKeyboardLevel(int level)
    {
        level = Math.Clamp(level, 0, 3);
        using var acpi = AsusAcpi.TryOpen();
        if (acpi is null) return new(false, "ASUS sürücüsü bulunamadı.");
        if (acpi.GetKeyboardBrightness() is null) return new(false, "Bu cihazda klavye ışığı okunamıyor.");
        if (!acpi.SetKeyboardBrightness(level)) return new(false, "Bilgisayar isteği kabul etmedi.");
        Thread.Sleep(300);
        var read = acpi.GetKeyboardBrightness();
        var ok = read == level;
        Journal.Write($"Klavye ışığı {level} istendi, okunan {read}.");
        return new(ok, ok ? $"Klavye ışığı doğrulandı: {Label(level)}." : $"Yazıldı ama okunan değer {read}.");
    }

    public static bool IsRgbAvailable()
    {
        using var acpi = AsusAcpi.TryOpen();
        return acpi?.IsKeyboardRgbSupported() == true;
    }

    /// <summary>Klavye RGB rengini/modunu yazar. Bu modelde geri okunamaz: yalnızca firmware kabulü doğrulanır, rengi gözle teyit etmek gerekir.</summary>
    public static HardwareResult SetKeyboardRgb(KeyboardRgb c)
    {
        using var acpi = AsusAcpi.TryOpen();
        if (acpi is null) return new(false, "ASUS sürücüsü bulunamadı.");
        if (!acpi.IsKeyboardRgbSupported()) return new(false, "Bu klavyede RGB renk kontrolü yok.");
        var ok = acpi.SetKeyboardRgb(c.Mode, c.R, c.G, c.B, c.Speed);
        Journal.Write($"Klavye RGB: mod {c.Mode}, renk #{c.R:X2}{c.G:X2}{c.B:X2}, hız {c.Speed}, kabul: {ok}.");
        return new(ok, ok ? "Klavye rengi bilgisayar tarafından kabul edildi." : "Bilgisayar klavye rengini kabul etmedi.");
    }

    /// <summary>Bir sonraki seviyeye geçer (0, 1, 2, 3, sonra tekrar 0).</summary>
    public static HardwareResult CycleKeyboard()
    {
        var current = ReadKeyboardLevel();
        return current is null ? new(false, "Klavye ışığı okunamıyor.") : SetKeyboardLevel((current.Value + 1) % 4);
    }

    /// <summary>Pil şarj limitini yazar. Bu modelde geri okunamaz; yalnızca firmware'in kabulü doğrulanabilir.</summary>
    public static HardwareResult SetBatteryLimit(int percent)
    {
        percent = Math.Clamp(percent, 40, 100);
        using var acpi = AsusAcpi.TryOpen();
        if (acpi is null) return new(false, "ASUS sürücüsü bulunamadı.");
        var accepted = acpi.SetBatteryLimit(percent);
        Journal.Write($"Pil şarj limiti %{percent} yazıldı, kabul: {accepted}.");
        return new(accepted, accepted ? $"%{percent} limiti bilgisayar tarafından kabul edildi." : "Bilgisayar sınırı kabul etmedi.");
    }

    /// <summary>Uygulama açılışında kaydedilmiş seçimleri yeniden uygular (firmware yeniden başlatmada sıfırlayabilir).</summary>
    public static void ApplySaved(AppSettings s)
    {
        if (s.BatteryLimit is { } b) SetBatteryLimit(b);
        if (s.KeyboardColor is { } rgb) SetKeyboardRgb(rgb);
        if (s.KeyboardLevel is { } k) SetKeyboardLevel(k);
    }

    public static string Label(int level) => level switch { 0 => "Kapalı", 1 => "Seviye 1", 2 => "Seviye 2", _ => "Seviye 3" };
}
