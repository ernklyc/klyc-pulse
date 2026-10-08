using System.Diagnostics;
using System.Text.Json;
using Pulse.Core.Diagnostics;
using Pulse.Core.Hardware;
using Pulse.Core.Platform;

namespace Pulse.Core.Modes;

public enum StepStatus { Verified, Applied, Warning, Failed, Skipped }

/// <summary>Bir adımın sonucu: ne yapıldı, geri okununca ne görüldü.</summary>
public sealed record StepResult(string Name, StepStatus Status, string Detail)
{
    public bool IsOk => Status is StepStatus.Verified or StepStatus.Applied or StepStatus.Skipped;
}

public sealed record ModeResult(ModeDefinition Mode, IReadOnlyList<StepResult> Steps)
{
    public bool Success => Steps.All(s => s.Status != StepStatus.Failed);
    public bool FullyVerified => Steps.All(s => s.Status is StepStatus.Verified or StepStatus.Skipped);
}

public sealed class ModeOptions
{
    public bool ChangeBrightness { get; set; } = true;
    /// <summary>Oyun modunda uygulanacak ekran kartı ofseti (MHz). Diğer modlarda her zaman fabrika hızı.</summary>
    public int GpuOcCore { get; set; }
    public int GpuOcMem { get; set; }
}

/// <summary>
/// Modu uygular: her adım uygula, geri oku, doğrula düzeninde çalışır.
/// Doğrulanamayan bir şeyi asla "tamam" saymaz.
/// </summary>
public sealed class ModeEngine : IDisposable
{
    private readonly AsusAcpi? _acpi = AsusAcpi.TryOpen();
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "state.json");

    public bool HasAsusDriver => _acpi is not null;

    public string? CurrentModeKey => LoadState().CurrentMode;

    public Task<ModeResult> ApplyAsync(ModeDefinition mode, ModeOptions? options = null) =>
        Task.Run(() => Apply(mode, options ?? new ModeOptions()));

    public ModeResult Apply(ModeDefinition mode, ModeOptions options)
    {
        Journal.Write($"Mod uygulanıyor: {mode.Title}");
        var steps = new List<StepResult>();

        steps.Add(ApplyAsusProfile(mode));
        steps.AddRange(ApplyPowerPlan(mode));
        // Parlaklık, yenileme hızından önce: hız değişince monitör kaydı kısa süre yeniden oluşur.
        steps.Add(options.ChangeBrightness ? ApplyBrightness(mode) : new StepResult("Parlaklık", StepStatus.Skipped, "Bu modda parlaklık değiştirilmedi."));
        steps.Add(ApplyRefresh(mode));
        steps.Add(ApplyGpuCap(mode));
        steps.Add(ApplyGpuOc(mode, options));
        steps.Add(ApplyIdlePower(mode));
        steps.AddRange(CheckConflicts());

        var state = LoadState();
        state.CurrentMode = mode.Key;
        SaveState(state);

        foreach (var s in steps) Journal.Write($"  [{s.Status}] {s.Name}: {s.Detail}");
        return new ModeResult(mode, steps);
    }

    /// <summary>Yalnızca Windows güç planı ayarlarını yeniden yazar (başka bir program bozduysa). Ekran/parlaklığa dokunmaz.</summary>
    public IReadOnlyList<StepResult> ReapplyPower(ModeDefinition mode) => ApplyPowerPlan(mode).ToList();

    /// <summary>Yeniden başlatma sonrası kaybolan ekran kartı ayarlarını (hız sınırı, hızlandırma) yeniden uygular.</summary>
    public IReadOnlyList<StepResult> ReapplyGpu(ModeDefinition mode, ModeOptions options) => [ApplyGpuCap(mode), ApplyGpuOc(mode, options)];

    /// <summary>Windows güç planındaki değerler modunkilerle uyuşuyor mu? (Başka bir araç bozmuş olabilir.)</summary>
    public static bool PowerMatches(ModeDefinition mode, bool ignoreMaxState)
    {
        var active = Powercfg.ActiveScheme();
        if (active is null) return true;
        var boost = Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode);
        var epp = Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref);
        var max = Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.MaxProcessorState);
        return (boost is null || boost == mode.Boost) && (epp is null || epp == mode.Epp) && (ignoreMaxState || max is null || max == mode.MaxState);
    }

    // ---- Adımlar ---------------------------------------------------------
    private StepResult ApplyAsusProfile(ModeDefinition mode)
    {
        const string name = "ASUS profili";
        if (_acpi is null) return new(name, StepStatus.Skipped, "ASUS'un donanım sürücüsü bulunamadı.");

        var before = _acpi.GetCpuFanRpm();
        var accepted = _acpi.SetPerformanceMode(mode.Asus);
        if (!accepted) return new(name, StepStatus.Failed, $"{mode.Asus} profilini bilgisayar reddetti.");

        // Bu model profili geri okutmaz; en azından fan hızını izleyip bilgi olarak göster.
        Thread.Sleep(700);
        var after = _acpi.GetCpuFanRpm();
        var detail = $"{mode.Asus} profili bilgisayar tarafından kabul edildi" +
                     (before is not null && after is not null ? $" (CPU fanı {before} → {after} RPM)." : ".");
        return new(name, StepStatus.Applied, detail);
    }

    private IEnumerable<StepResult> ApplyPowerPlan(ModeDefinition mode)
    {
        var active = Powercfg.ActiveScheme();
        if (active is null)
        {
            yield return new("Güç planı", StepStatus.Failed, "Etkin güç planı okunamadı.");
            yield break;
        }

        // ASUS profili değişince ASUS'un kendi servisi birkaç yüz ms sonra Windows güç ayarlarına dokunabiliyor (gerçek donanımda görüldü).
        // Bu yüzden yaz, bekle, kontrol et; tutmadıysa yeniden yaz (en çok 3 kez).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var scheme in Powercfg.AllSchemes())
            {
                Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.BoostMode, mode.Boost);
                Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, mode.MaxState);
                Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref, mode.Epp);
                // Pilde çalışırken de aynı mod geçerli olsun (yoksa fiş çekilince Windows eski ayara döner).
                Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.BoostMode, mode.Boost);
                Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.MaxProcessorState, mode.MaxState);
                Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref, mode.Epp);
            }
            Powercfg.SetActive(active);
            Thread.Sleep(attempt == 0 ? 900 : 1500);
            var ok = Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode) == mode.Boost
                  && Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref) == mode.Epp
                  && Powercfg.GetDc(active, Powercfg.SubProcessor, Powercfg.BoostMode) == mode.Boost;
            if (ok) break;
            Journal.Write($"Güç ayarları tutmadı (deneme {attempt + 1}), yeniden yazılıyor.");
        }

        yield return Verify("İşlemci ek hızı (turbo)", mode.Boost, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode), v => v == 0 ? "kapalı" : "açık");
        yield return Verify("İşlemci üst sınırı", mode.MaxState, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.MaxProcessorState), v => $"%{v}");
        yield return Verify("Hız / güç dengesi", mode.Epp, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref), v => v.ToString());
        yield return Verify("Pilde de aynı (ek hız)", mode.Boost, Powercfg.GetDc(active, Powercfg.SubProcessor, Powercfg.BoostMode), v => v == 0 ? "kapalı" : "açık");
    }

    private static StepResult ApplyRefresh(ModeDefinition mode)
    {
        const string name = "Ekran yenileme hızı";
        var supported = DisplayService.SupportedRefreshRates();
        var target = mode.RefreshHz == Modes.MaxHz && supported.Count > 0 ? supported.Max() : mode.RefreshHz;
        if (target == Modes.MaxHz)
            return new(name, StepStatus.Skipped, "Ekranın desteklediği yenileme hızları okunamadı.");
        if (supported.Count > 0 && !supported.Contains(target))
            return new(name, StepStatus.Warning, $"{target} Hz bu ekranda yok (desteklenen: {string.Join(", ", supported)} Hz).");

        DisplayService.SetRefreshRate(target);
        Thread.Sleep(900);
        return Verify(name, target, DisplayService.GetRefreshRate(), v => $"{v} Hz");
    }

    private static StepResult ApplyBrightness(ModeDefinition mode)
    {
        const string name = "Ekran parlaklığı";
        if (DisplayService.GetBrightnessWithRetry(attempts: 4) is null)
            return new(name, StepStatus.Skipped, "Bu ekranın parlaklığı yazılımla okunamıyor.");

        DisplayService.SetBrightness(mode.Brightness);
        Thread.Sleep(500);
        var now = DisplayService.GetBrightnessWithRetry();
        return now is not null && Math.Abs(now.Value - mode.Brightness) <= 3
            ? new(name, StepStatus.Verified, $"%{now} (hedef %{mode.Brightness})")
            : new(name, StepStatus.Failed, $"Hedef %{mode.Brightness}, okunan {(now is null ? "yok" : "%" + now)}.");
    }

    private static StepResult ApplyGpuOc(ModeDefinition mode, ModeOptions options)
    {
        const string name = "Ekran kartı hızlandırma";
        if (!Monitoring.Nvml.IsAvailable) return new(name, StepStatus.Skipped, "NVIDIA ekran kartı bulunamadı.");
        var wantCore = mode.Key == Modes.Game ? options.GpuOcCore : 0;
        var wantMem = mode.Key == Modes.Game ? options.GpuOcMem : 0;
        var current = Hardware.GpuOverclock.Read();
        if (current is null)
            // Uyuyan (boşta kapanmış) ekran kartı zaten fabrika hızındadır; hızlandırma istenmiyorsa yapılacak bir şey yok.
            return wantCore == 0 && wantMem == 0 ? new(name, StepStatus.Verified, "Ekran kartı uykuda, fabrika hızında.") : new(name, StepStatus.Warning, "Ekran kartı hız arayüzü okunamadı (kart uyuyor olabilir).");
        if (current.CoreMhz == wantCore && current.MemMhz == wantMem)
            return new(name, StepStatus.Verified, wantCore == 0 && wantMem == 0 ? "Fabrika hızı." : $"Çekirdek +{wantCore}, bellek +{wantMem} MHz (zaten uygulu).");
        if (!Cleanup.CleanupEngine.IsAdmin) return new(name, StepStatus.Skipped, "Hız ayarı için KLYC-Pulse'ın yönetici olarak çalışması gerekir.");
        var r = Hardware.GpuOverclock.Apply(wantCore, wantMem);
        return !r.Ok ? new(name, StepStatus.Failed, r.Message) : new(name, r.Verified ? StepStatus.Verified : StepStatus.Warning, r.Message);
    }

    private static StepResult ApplyGpuCap(ModeDefinition mode)
    {
        const string name = "Ekran kartı hız sınırı";
        if (!Monitoring.Nvml.IsAvailable) return new(name, StepStatus.Skipped, "NVIDIA ekran kartı bulunamadı.");
        if (!Cleanup.CleanupEngine.IsAdmin) return new(name, StepStatus.Skipped, "Saat sınırı için KLYC-Pulse'ın yönetici olarak çalışması gerekir.");

        if (mode.GpuCapMhz is not { } cap)
        {
            var r = Hardware.GpuClocks.Release();
            return r.Ok ? new(name, StepStatus.Verified, "Sınırsız (sürücü varsayılanı).") : new(name, StepStatus.Warning, r.Message);
        }
        var c = Hardware.GpuClocks.Cap(cap);
        return !c.Ok ? new(name, StepStatus.Failed, c.Message) : new(name, c.Verified ? StepStatus.Verified : StepStatus.Applied, c.Message);
    }

    private static StepResult ApplyIdlePower(ModeDefinition mode)
    {
        const string name = "Ekran kapanma ve uyku";
        var active = Powercfg.ActiveScheme();
        if (active is null) return new(name, StepStatus.Failed, "Etkin güç planı okunamadı.");

        var state = LoadState();
        if (mode.IdlePower)
        {
            if (state.OriginalMonitorSeconds is null)
            {
                state.OriginalMonitorSeconds = Powercfg.GetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle);
                state.OriginalStandbySeconds = Powercfg.GetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle);
                SaveState(state);
            }
            Powercfg.SetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle, 60);
            Powercfg.SetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle, 0);
            Powercfg.SetActive(active);
            var mon = Powercfg.GetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle);
            var slp = Powercfg.GetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle);
            return mon == 60 && slp == 0
                ? new(name, StepStatus.Verified, "Ekran 1 dk'da kapanır, uyku kapalı.")
                : new(name, StepStatus.Failed, $"Okunan: ekran {mon} sn, uyku {slp} sn.");
        }

        if (state.OriginalMonitorSeconds is { } m && state.OriginalStandbySeconds is { } s)
        {
            Powercfg.SetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle, m);
            Powercfg.SetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle, s);
            Powercfg.SetActive(active);
            state.OriginalMonitorSeconds = null;
            state.OriginalStandbySeconds = null;
            SaveState(state);
            var mon = Powercfg.GetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle);
            return mon == m
                ? new(name, StepStatus.Verified, "Önceki ekran ve uyku ayarları geri yüklendi.")
                : new(name, StepStatus.Failed, $"Geri yükleme doğrulanamadı (okunan ekran {mon} sn).");
        }
        return new(name, StepStatus.Skipped, "Bu modda değiştirilmedi.");
    }

    /// <summary>Modu bozabilecek diğer yazılımlar (açılışta profili geri alabilirler).</summary>
    private static IEnumerable<StepResult> CheckConflicts()
    {
        foreach (var label in ConflictService.Running())
            yield return new("Çakışma uyarısı", StepStatus.Warning,
                $"{label} çalışıyor. Kendi profilini uygulayıp Pulse'ın seçtiği modu bozabilir. Ayarlar'dan otomatik kapatılabilir.");
    }
    private static StepResult Verify(string name, int expected, int? actual, Func<int, string> format) =>
        actual == expected
            ? new(name, StepStatus.Verified, format(expected))
            : new(name, StepStatus.Failed, $"Hedef {format(expected)}, okunan {(actual is null ? "yok" : format(actual.Value))}.");

    // ---- Durum -----------------------------------------------------------
    private sealed class ModeState
    {
        public string? CurrentMode { get; set; }
        public int? OriginalMonitorSeconds { get; set; }
        public int? OriginalStandbySeconds { get; set; }
    }

    private static ModeState LoadState()
    {
        try { return JsonSerializer.Deserialize<ModeState>(File.ReadAllText(StatePath)) ?? new(); }
        catch { return new(); }
    }

    private static void SaveState(ModeState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch { /* durum yazılamazsa mod yine de uygulanmıştır */ }
    }

    public void Dispose() => _acpi?.Dispose();
}
