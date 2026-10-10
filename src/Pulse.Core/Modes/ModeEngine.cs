using Pulse.Core.Localization;
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
        steps.Add(options.ChangeBrightness ? ApplyBrightness(mode) : new StepResult(Loc.T("Parlaklık"), StepStatus.Skipped, Loc.T("Bu modda parlaklık değiştirilmedi.")));
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
        var name = Loc.T("ASUS profili");
        if (_acpi is null) return new(name, StepStatus.Skipped, Loc.T("ASUS'un donanım sürücüsü bulunamadı."));

        var before = _acpi.GetCpuFanRpm();
        var accepted = _acpi.SetPerformanceMode(mode.Asus);
        if (!accepted) return new(name, StepStatus.Failed, Loc.F("{0} profilini bilgisayar reddetti.", mode.Asus));

        // Bu model profili geri okutmaz; en azından fan hızını izleyip bilgi olarak göster.
        Thread.Sleep(700);
        var after = _acpi.GetCpuFanRpm();
        var detail = Loc.F("{0} profili bilgisayar tarafından kabul edildi", mode.Asus) +
                     (before is not null && after is not null ? Loc.F(" (CPU fanı {0} → {1} RPM).", before, after) : ".");
        return new(name, StepStatus.Applied, detail);
    }

    private IEnumerable<StepResult> ApplyPowerPlan(ModeDefinition mode)
    {
        var active = Powercfg.ActiveScheme();
        if (active is null)
        {
            yield return new(Loc.T("Güç planı"), StepStatus.Failed, Loc.T("Etkin güç planı okunamadı."));
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
                // Her mod "frekans sınırı yok"tan (ya da oyun profilinin seçtiği sınırdan) başlar; sıcaklık sınırı gerekirse üstüne koyar (eski sınır kalıntısı kalmasın).
                Powercfg.SetFrequencyCap(scheme, mode.CpuMaxMhz ?? 0);
                // Sistem soğutma ilkesi (varsa): Oyun/Günlük = önce fan, Sessiz/Boşta = önce yavaşlama (sessizlik). Ayar yoksa komut zararsızca yok sayılır.
                Powercfg.SetAc(scheme, Powercfg.SubProcessor, Powercfg.SystemCoolingPolicy, mode.ActiveCooling ? 1 : 0);
                Powercfg.SetDc(scheme, Powercfg.SubProcessor, Powercfg.SystemCoolingPolicy, mode.ActiveCooling ? 1 : 0);
            }
            Powercfg.SetActive(active);
            Thread.Sleep(attempt == 0 ? 900 : 1500);
            var ok = Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode) == mode.Boost
                  && Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref) == mode.Epp
                  && Powercfg.GetDc(active, Powercfg.SubProcessor, Powercfg.BoostMode) == mode.Boost;
            if (ok) break;
            Journal.Write($"Güç ayarları tutmadı (deneme {attempt + 1}), yeniden yazılıyor.");
        }

        yield return Verify(Loc.T("İşlemci ek hızı (turbo)"), mode.Boost, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.BoostMode), v => v == 0 ? Loc.T("kapalı") : Loc.T("açık"));
        yield return Verify(Loc.T("İşlemci üst sınırı"), mode.MaxState, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.MaxProcessorState), v => $"%{v}");
        yield return Verify(Loc.T("Hız / güç dengesi"), mode.Epp, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.EnergyPerformancePref), v => v.ToString());
        yield return Verify(Loc.T("Pilde de aynı (ek hız)"), mode.Boost, Powercfg.GetDc(active, Powercfg.SubProcessor, Powercfg.BoostMode), v => v == 0 ? Loc.T("kapalı") : Loc.T("açık"));
        yield return Verify(Loc.T("İşlemci en yüksek hızı"), mode.CpuMaxMhz ?? 0, Powercfg.GetAc(active, Powercfg.SubProcessor, Powercfg.MaxFrequency), v => v == 0 ? Loc.T("sınırsız") : $"{v} MHz");
    }

    /// <summary>
    /// Fan desteği: ASUS Turbo profili fanı tam hıza çıkarır (fan eğrisi bu modelde yazılamaz, profil değiştirilebilir). Kapatınca modun kendi profili geri yazılır.
    /// Fan devrinin gerçekten artıp artmadığı ölçülür. ASUS sürücüsü yoksa Skipped.
    /// </summary>
    public StepResult SetFanBoost(ModeDefinition current, bool on)
    {
        var name = Loc.T("Fan desteği");
        if (_acpi is null) return new(name, StepStatus.Skipped, Loc.T("ASUS fan profili bu bilgisayarda yok."));
        var before = _acpi.GetCpuFanRpm();
        var ok = _acpi.SetPerformanceMode(on ? AsusPerformanceMode.Turbo : current.Asus);
        if (!ok) return new(name, StepStatus.Failed, Loc.T("Fan profili bilgisayar tarafından reddedildi."));
        Thread.Sleep(2500);
        var after = _acpi.GetCpuFanRpm();
        var rpm = before is not null && after is not null ? Loc.F(" (CPU fanı {0} → {1} RPM)", before, after) : "";
        if (on && before is { } b && after is { } a && a < b * 1.1)
            return new(name, StepStatus.Warning, Loc.F("Turbo profili kabul edildi ama fan devri artmadı{0}; fan zaten tam hızda olabilir.", rpm));
        return new(name, StepStatus.Applied, (on ? Loc.T("Fan tam hıza alındı") : Loc.F("{0} profiline dönüldü", current.Asus)) + rpm + ".");
    }

    /// <summary>İstenen hızı çözer: MaxHz ise ekranın desteklediği en yüksek; liste boşsa MaxHz (belirlenemedi) döner.</summary>
    public static int ResolveRefreshTarget(int requestedHz, IReadOnlyList<int> supported) =>
        requestedHz == Modes.MaxHz && supported.Count > 0 ? supported.Max() : requestedHz;

    private static StepResult ApplyRefresh(ModeDefinition mode)
    {
        var name = Loc.T("Ekran yenileme hızı");
        var supported = DisplayService.SupportedRefreshRates();
        var target = ResolveRefreshTarget(mode.RefreshHz, supported);
        if (target == Modes.MaxHz)
            return new(name, StepStatus.Skipped, Loc.T("Ekranın desteklediği yenileme hızları okunamadı."));
        if (supported.Count > 0 && !supported.Contains(target))
            return new(name, StepStatus.Warning, Loc.F("{0} Hz bu ekranda yok (desteklenen: {1} Hz).", target, string.Join(", ", supported)));

        DisplayService.SetRefreshRate(target);
        Thread.Sleep(900);
        return Verify(name, target, DisplayService.GetRefreshRate(), v => $"{v} Hz");
    }

    private static StepResult ApplyBrightness(ModeDefinition mode)
    {
        var name = Loc.T("Ekran parlaklığı");
        if (DisplayService.GetBrightnessWithRetry(attempts: 4) is null)
            return new(name, StepStatus.Skipped, Loc.T("Bu ekranın parlaklığı yazılımla okunamıyor."));

        DisplayService.SetBrightness(mode.Brightness);
        Thread.Sleep(500);
        var now = DisplayService.GetBrightnessWithRetry();
        return now is not null && Math.Abs(now.Value - mode.Brightness) <= 3
            ? new(name, StepStatus.Verified, Loc.F("%{0} (hedef %{1})", now, mode.Brightness))
            : new(name, StepStatus.Failed, Loc.F("Hedef %{0}, okunan {1}.", mode.Brightness, now is null ? Loc.T("yok") : Loc.F("%{0}", now)));
    }

    private static StepResult ApplyGpuOc(ModeDefinition mode, ModeOptions options)
    {
        var name = Loc.T("Ekran kartı hızlandırma");
        if (!Monitoring.Nvml.IsAvailable) return new(name, StepStatus.Skipped, Loc.T("NVIDIA ekran kartı bulunamadı."));
        var wantCore = mode.Key == Modes.Game ? options.GpuOcCore : 0;
        var wantMem = mode.Key == Modes.Game ? options.GpuOcMem : 0;
        var current = Hardware.GpuOverclock.Read();
        if (current is null)
            // Uyuyan (boşta kapanmış) ekran kartı zaten fabrika hızındadır; hızlandırma istenmiyorsa yapılacak bir şey yok.
            return wantCore == 0 && wantMem == 0 ? new(name, StepStatus.Verified, Loc.T("Ekran kartı uykuda, fabrika hızında.")) : new(name, StepStatus.Warning, Loc.T("Ekran kartı hız arayüzü okunamadı (kart uyuyor olabilir)."));
        if (current.CoreMhz == wantCore && current.MemMhz == wantMem)
            return new(name, StepStatus.Verified, wantCore == 0 && wantMem == 0 ? Loc.T("Fabrika hızı.") : Loc.F("Çekirdek +{0}, bellek +{1} MHz (zaten uygulu).", wantCore, wantMem));
        if (!Cleanup.CleanupEngine.IsAdmin) return new(name, StepStatus.Skipped, Loc.T("Hız ayarı için KLYC-Pulse'ın yönetici olarak çalışması gerekir."));
        var r = Hardware.GpuOverclock.Apply(wantCore, wantMem);
        return !r.Ok ? new(name, StepStatus.Failed, r.Message) : new(name, r.Verified ? StepStatus.Verified : StepStatus.Warning, r.Message);
    }

    private static StepResult ApplyGpuCap(ModeDefinition mode)
    {
        var name = Loc.T("Ekran kartı hız sınırı");
        if (!Monitoring.Nvml.IsAvailable) return new(name, StepStatus.Skipped, Loc.T("NVIDIA ekran kartı bulunamadı."));
        if (!Cleanup.CleanupEngine.IsAdmin) return new(name, StepStatus.Skipped, Loc.T("Saat sınırı için KLYC-Pulse'ın yönetici olarak çalışması gerekir."));

        if (mode.GpuCapMhz is not { } cap)
        {
            var r = Hardware.GpuClocks.Release();
            return r.Ok ? new(name, StepStatus.Verified, Loc.T("Sınırsız (sürücü varsayılanı).")) : new(name, StepStatus.Warning, r.Message);
        }
        var c = Hardware.GpuClocks.Cap(cap);
        return !c.Ok ? new(name, StepStatus.Failed, c.Message) : new(name, c.Verified ? StepStatus.Verified : StepStatus.Applied, c.Message);
    }

    /// <summary>Boşta modunda (fişteyken) ekranın kapanma ve uykuya geçme süresi (30 dk, kullanıcının isteği). Eskiden 1 dk ekran / uyku kapalıydı; bilgisayar başındayken ekran kararıyordu.</summary>
    private const int IdleScreenSeconds = 1800;

    private static StepResult ApplyIdlePower(ModeDefinition mode)
    {
        var name = Loc.T("Ekran kapanma ve uyku");
        var active = Powercfg.ActiveScheme();
        if (active is null) return new(name, StepStatus.Failed, Loc.T("Etkin güç planı okunamadı."));

        var state = LoadState();
        if (mode.IdlePower)
        {
            if (state.OriginalMonitorSeconds is null)
            {
                state.OriginalMonitorSeconds = Powercfg.GetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle);
                state.OriginalStandbySeconds = Powercfg.GetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle);
                SaveState(state);
            }
            Powercfg.SetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle, IdleScreenSeconds);
            Powercfg.SetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle, IdleScreenSeconds);
            Powercfg.SetActive(active);
            var mon = Powercfg.GetAc(active, Powercfg.SubVideo, Powercfg.VideoIdle);
            var slp = Powercfg.GetAc(active, Powercfg.SubSleep, Powercfg.StandbyIdle);
            return mon == IdleScreenSeconds && slp == IdleScreenSeconds
                ? new(name, StepStatus.Verified, Loc.F("Ekran ve uyku {0} dk sonra devreye girer.", IdleScreenSeconds / 60))
                : new(name, StepStatus.Failed, Loc.F("Okunan: ekran {0} sn, uyku {1} sn.", mon, slp));
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
                ? new(name, StepStatus.Verified, Loc.T("Önceki ekran ve uyku ayarları geri yüklendi."))
                : new(name, StepStatus.Failed, Loc.F("Geri yükleme doğrulanamadı (okunan ekran {0} sn).", mon));
        }
        return new(name, StepStatus.Skipped, Loc.T("Bu modda değiştirilmedi."));
    }

    /// <summary>Modu bozabilecek diğer yazılımlar (açılışta profili geri alabilirler).</summary>
    private static IEnumerable<StepResult> CheckConflicts()
    {
        foreach (var label in ConflictService.Running())
            yield return new(Loc.T("Çakışma uyarısı"), StepStatus.Warning,
                Loc.F("{0} çalışıyor. Kendi profilini uygulayıp Pulse'ın seçtiği modu bozabilir. Ayarlar'dan otomatik kapatılabilir.", label));
    }
    private static StepResult Verify(string name, int expected, int? actual, Func<int, string> format) =>
        actual == expected
            ? new(name, StepStatus.Verified, format(expected))
            : new(name, StepStatus.Failed, Loc.F("Hedef {0}, okunan {1}.", format(expected), actual is null ? Loc.T("yok") : format(actual.Value)));

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
