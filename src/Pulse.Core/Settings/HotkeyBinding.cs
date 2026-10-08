using Pulse.Core.Localization;
namespace Pulse.Core.Settings;

/// <summary>Bir kısayol: değiştirici tuşlar (Ctrl=2, Alt=1, Shift=4, Win=8; RegisterHotKey değerleriyle aynı) ve sanal tuş kodu.</summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ScrollLock"] = 0x91, ["Pause"] = 0x13, ["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Space"] = 0x20,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["NumLock"] = 0x90, ["CapsLock"] = 0x14, ["Menu"] = 0x5D,
    };

    /// <summary>"Ctrl+Alt+3" gibi metni çözer. Geçersizse null.</summary>
    public static HotkeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Ctrl; continue;
                case "alt": mods |= Alt; continue;
                case "shift": mods |= Shift; continue;
                case "win": mods |= Win; continue;
            }
            if (vk != 0) return null;
            vk = KeyFromName(part);
            if (vk == 0) return null;
        }
        return vk == 0 ? null : new HotkeyBinding(mods, vk);
    }

    /// <summary>Tek başına (değiştirici tuş olmadan) kaydedilebilecek güvenli tuşlar: F13-F24, Kaydırma Kilidi, Pause, Print Screen.</summary>
    public bool IsSafeStandalone => VirtualKey is >= 0x7C and <= 0x87 or 0x91 or 0x13 or 0x2C;

    /// <summary>Kaydedilebilir mi? Normal tuşlar için en az bir değiştirici (Ctrl/Alt/Win) gerekir; yoksa her yazışta tetiklenirdi.</summary>
    public bool IsValid => (Modifiers & (Ctrl | Alt | Win)) != 0 || IsSafeStandalone;

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(NameFromKey(VirtualKey));
        return string.Join("+", parts);
    }

    public static uint KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            var ch = char.ToUpperInvariant(name[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return ch;
        }
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        return Named.TryGetValue(name, out var vk) ? vk : 0;
    }

    public static string NameFromKey(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return Named.FirstOrDefault(p => p.Value == vk).Key ?? $"0x{vk:X2}";
    }
}

/// <summary>Kısayol eylemleri ve varsayılan atamaları.</summary>
public static class HotkeyActions
{
    public static readonly (string Id, string Title, string Default)[] All =
    [
        ("mode:oyun", Loc.T("Oyun modu"), "Ctrl+Alt+1"),
        ("mode:gunluk", Loc.T("Günlük mod"), "Ctrl+Alt+2"),
        ("mode:sessiz", Loc.T("Sessiz mod"), "Ctrl+Alt+3"),
        ("mode:bosta", Loc.T("Boşta modu"), "Ctrl+Alt+4"),
        ("mode:next", Loc.T("Sonraki mod (Oyun, Günlük, Sessiz sırayla)"), ""),
        ("kbd:cycle", Loc.T("Klavye ışığı seviyesi"), "Ctrl+Alt+K"),
        ("overlay:toggle", Loc.T("Oyun üstü gösterge"), "Ctrl+Alt+O"),
    ];

    /// <summary>Kayıtlı atamalara varsayılanları ekleyip eylem → bağlama sözlüğü verir (boş = atanmamış).</summary>
    public static Dictionary<string, HotkeyBinding?> Resolve(IReadOnlyDictionary<string, string>? saved)
    {
        var map = new Dictionary<string, HotkeyBinding?>();
        foreach (var (id, _, def) in All)
        {
            var text = saved is not null && saved.TryGetValue(id, out var s) ? s : def;
            map[id] = HotkeyBinding.Parse(text);
        }
        return map;
    }
}
