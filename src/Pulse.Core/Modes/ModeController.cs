using Pulse.Core.Diagnostics;
using Pulse.Core.Settings;

namespace Pulse.Core.Modes;

/// <summary>Bir modun ekran ayarlarını geçici olarak değiştirir (null = modun kendi değeri).</summary>
public sealed record ModeOverrides(int? RefreshHz, int? Brightness, int? CpuMaxMhz = null);

/// <summary>
/// Mod uygulamanın tek giriş noktası: ana ekran, tepsi menüsü ve otomatik mod aynı denetleyiciyi kullanır.
/// Aynı anda tek işlem yürür.
/// </summary>
public sealed class ModeController : IDisposable
{
    private readonly ModeEngine _engine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SettingsStore _settings;

    public ModeController(SettingsStore settings) => _settings = settings;

    public string? CurrentKey => _engine.CurrentModeKey;

    /// <summary>Şu an uygulanmış modun gerçek tanımı (oyun profilinin geçici ayarları dahil). Henüz mod uygulanmadıysa null.</summary>
    public ModeDefinition? ActiveDefinition { get; private set; }

    /// <summary>Etkin tanım bu moda aitse onu (profil ayarlarıyla), değilse modun varsayılan tanımını verir.</summary>
    private ModeDefinition? DefinitionFor(string key) =>
        ActiveDefinition is { } a && string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase) ? a : Modes.Get(key);
    public bool HasAsusDriver => _engine.HasAsusDriver;
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Bir mod uygulandığında (nereden başlatılırsa başlatılsın) tetiklenir.</summary>
    public event Action<ModeResult>? Applied;
    public event Action<string>? Busy;

    public Task<ModeResult?> ApplyAsync(string key) => ApplyAsync(key, null);

    /// <summary>Modu uygular. İstenirse ekran yenileme hızı / parlaklık o seferlik değiştirilir (oyun profilleri için).</summary>
    public async Task<ModeResult?> ApplyAsync(string key, ModeOverrides? overrides)
    {
        var def = Modes.Get(key);
        if (def is null) return null;
        if (overrides is not null)
            def = def with { RefreshHz = overrides.RefreshHz ?? def.RefreshHz, Brightness = overrides.Brightness ?? def.Brightness, CpuMaxMhz = overrides.CpuMaxMhz ?? def.CpuMaxMhz };
        if (!await _gate.WaitAsync(0)) return null; // başka bir işlem sürüyor

        try
        {
            Busy?.Invoke(key);
            if (_settings.Current.CloseConflictingApps) ConflictService.CloseAll();
            var result = await _engine.ApplyAsync(def, new ModeOptions { ChangeBrightness = _settings.Current.ChangeBrightness, GpuOcCore = _settings.Current.GpuOcCore ?? 0, GpuOcMem = _settings.Current.GpuOcMem ?? 0 });
            ActiveDefinition = def;
            Applied?.Invoke(result);
            return result;
        }
        catch (Exception ex)
        {
            Journal.Write($"Mod uygulanamadı ({key}): {ex}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fan desteğini açar/kapatır (mod uygulanıyorsa atlanır). Sonucu döner; atlandıysa null.</summary>
    public async Task<StepResult?> SetFanBoostAsync(bool on)
    {
        if (CurrentKey is not { } key || DefinitionFor(key) is not { } def) return null;
        if (!await _gate.WaitAsync(0)) return null;
        try
        {
            var r = await Task.Run(() => _engine.SetFanBoost(def, on));
            Journal.Write($"Fan desteği {(on ? "açıldı" : "kapatıldı")}: {r.Detail}");
            return r;
        }
        catch (Exception ex) { Journal.Write("Fan desteği hatası: " + ex.Message); return null; }
        finally { _gate.Release(); }
    }

    private ModeOptions Options() => new() { ChangeBrightness = _settings.Current.ChangeBrightness, GpuOcCore = _settings.Current.GpuOcCore ?? 0, GpuOcMem = _settings.Current.GpuOcMem ?? 0 };

    /// <summary>Açılışta: ekran kartı ayarları yeniden başlatmada sıfırlandığı için seçili modun GPU adımlarını yeniden uygular.</summary>
    public async Task ReapplyGpuAsync()
    {
        if (CurrentKey is not { } key || DefinitionFor(key) is not { } def) return;
        if (!await _gate.WaitAsync(0)) return;
        try { foreach (var s in await Task.Run(() => _engine.ReapplyGpu(def, Options()))) Journal.Write($"  [açılış] {s.Name}: {s.Detail}"); }
        catch (Exception ex) { Journal.Write("GPU ayarları yeniden uygulanamadı: " + ex.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>Başka bir program güç planını bozduysa mod değerlerini geri yazar. Bozulma varsa true.</summary>
    public async Task<bool> RepairPowerAsync(bool ignoreMaxState)
    {
        if (CurrentKey is not { } key || DefinitionFor(key) is not { } def) return false;
        if (!await _gate.WaitAsync(0)) return false;
        try
        {
            if (await Task.Run(() => ModeEngine.PowerMatches(def, ignoreMaxState))) return false;
            Journal.Write($"Güç planı bozulmuş ({def.Title} modu); geri yazılıyor.");
            await Task.Run(() => _engine.ReapplyPower(def));
            return true;
        }
        catch (Exception ex) { Journal.Write("Güç planı onarılamadı: " + ex.Message); return false; }
        finally { _gate.Release(); }
    }

    public void Dispose() => _engine.Dispose();
}