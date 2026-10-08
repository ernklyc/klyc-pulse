using CommunityToolkit.Mvvm.ComponentModel;
using Pulse.Core.Modes;

namespace Pulse.App.ViewModels;

/// <summary>Bir modun ekrandaki kartı.</summary>
public partial class ModeVm : ObservableObject
{
    public ModeVm(ModeDefinition def)
    {
        Definition = def;
        Key = def.Key;
        Title = def.Title;
        Subtitle = def.Subtitle;
        Glyph = def.Key switch
        {
            Modes.Game => "",
            Modes.Daily => "",
            Modes.Quiet => "",
            _ => "",
        };
        Chips =
        [
            def.Asus.ToString() switch { "Turbo" => "Turbo", "Silent" => "Sessiz profil", _ => "Dengeli profil" },
            def.Boost > 0 ? "CPU turbo açık" : "CPU turbo kapalı",
            def.RefreshHz == Modes.MaxHz ? "Ekranın en yüksek Hz'i" : $"{def.RefreshHz} Hz",
            $"Parlaklık %{def.Brightness}",
            .. def.MaxState < 100 ? new[] { $"CPU ≤ %{def.MaxState}" } : [],
            .. def.GpuCapMhz is { } cap ? new[] { $"GPU ≤ {cap} MHz" } : [],
            .. def.IdlePower ? new[] { "Uyku kapalı" } : [],
        ];
    }

    public ModeDefinition Definition { get; }
    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public string[] Chips { get; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
}
