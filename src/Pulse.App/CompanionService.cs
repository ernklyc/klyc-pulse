using Pulse.Core.Companion;
using Pulse.Core.Diagnostics;
using Pulse.Core.Modes;
using Pulse.Core.Settings;

namespace Pulse.App;

/// <summary>
/// Harici araçlarla ortak çalışma: ThrottleStop, MSI Afterburner ve G-Helper'ı Pulse ile birlikte açır,
/// mod değişince seçilen profili onlara uygulatır. Böylece Pulse'ın doğrudan yapamadığı işler (undervolt, hız aşırtma)
/// o araçların kendi, doğru ve güvenli yoluyla, Pulse'ın yönetiminde yapılır.
/// </summary>
public sealed class CompanionService
{
    private readonly SettingsStore _settings;
    private readonly object _gate = new();

    public CompanionService(SettingsStore settings) => _settings = settings;

    /// <summary>Her aracın son durum metni (arayüzde gösterilir).</summary>
    public Dictionary<string, string> Status { get; } = new();
    public event Action? StatusChanged;

    public void Start()
    {
        AppServices.Modes.Applied += r => _ = Task.Run(() => ApplyForMode(r.Mode.Key));
        _ = Task.Run(() =>
        {
            LaunchEnabled();
            if (AppServices.Modes.CurrentKey is { } key) ApplyForMode(key);
        });
    }

    /// <summary>Başlat-ile-Pulse seçili ve çalışmayan araçları başlatır.</summary>
    public void LaunchEnabled()
    {
        lock (_gate)
        {
            if (_settings.Current.ThrottleStopLink.StartWithPulse && ThrottleStopControl.IsInstalled) Set("ThrottleStop", ThrottleStopControl.Launch().Message);
            if (_settings.Current.AfterburnerLink.StartWithPulse && AfterburnerControl.IsInstalled) Set("Afterburner", AfterburnerControl.Launch().Message);
            if (_settings.Current.GHelperLink.StartWithPulse && GHelperControl.IsInstalled) Set("G-Helper", GHelperControl.Launch().Message);
        }
    }

    /// <summary>Moda bağlı profilleri uygular.</summary>
    public void ApplyForMode(string modeKey)
    {
        lock (_gate)
        {
            var ts = _settings.Current.ThrottleStopLink;
            if (ts.ModeProfiles.TryGetValue(modeKey, out var tsProfile) && tsProfile > 0 && ThrottleStopControl.IsInstalled)
            {
                if (!ThrottleStopControl.IsRunning && ts.StartWithPulse) ThrottleStopControl.Launch();
                Set("ThrottleStop", ThrottleStopControl.IsRunning ? ThrottleStopControl.SetProfile(tsProfile).Message : "ThrottleStop çalışmıyor, profil uygulanamadı.");
            }

            var ab = _settings.Current.AfterburnerLink;
            if (ab.ModeProfiles.TryGetValue(modeKey, out var abProfile) && abProfile > 0 && AfterburnerControl.IsInstalled)
                Set("Afterburner", AfterburnerControl.SavedProfileCount() == 0
                    ? "Afterburner'da kayıtlı profil yok; önce Afterburner'da bir profil kaydet."
                    : AfterburnerControl.ApplyProfile(abProfile).Message);
        }
    }

    private void Set(string tool, string text)
    {
        Status[tool] = $"{DateTime.Now:HH:mm}  {text}";
        Journal.Write($"Ortak çalışma ({tool}): {text}");
        StatusChanged?.Invoke();
    }
}