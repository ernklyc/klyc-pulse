using Pulse.Core.Hardware;

namespace Pulse.App;

/// <summary>
/// "Bu bilgisayarda Windows işlemci hız sınırını gerçekten uyguluyor mu?" denemesini uygulama içinde çalıştırır:
/// Mod Koruyucu ve Isı hedefini bekletir, sonucu ayarlara yazar ve yük altı tepe hızı öğrenir (kademeler buna göre kurulur).
/// </summary>
public static class FreqCapService
{
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Deneme zaten sürüyorsa null döner.</summary>
    public static async Task<FreqCapProbeResult?> RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return null;
        try
        {
            using var keeper = AppServices.Keeper.Pause();
            using var heat = AppServices.Heat.Pause();
            var r = await Task.Run(() => FreqCapProbe.Run());
            var s = AppServices.Settings;
            s.Current.FreqCapSupported = r.Supported;
            s.Current.FreqCapNote = r.Note;
            if (r.UncappedMhz > s.Current.CpuPeakMhz) s.Current.CpuPeakMhz = r.UncappedMhz;
            s.Save();
            return r;
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
