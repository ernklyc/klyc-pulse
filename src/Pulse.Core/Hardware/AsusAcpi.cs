using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Hardware;

/// <summary>ASUS performans profilleri (firmware numaralandırması).</summary>
public enum AsusPerformanceMode
{
    Balanced = 0,
    Turbo = 1,
    Silent = 2,
}

/// <summary>
/// ASUS'un resmi sürücü arayüzü (ATKACPI) üzerinden donanım erişimi.
/// G-Helper ve Armoury Crate aynı kanalı kullanır. Yönetici yetkisi gerekmez.
/// </summary>
public sealed class AsusAcpi : IDisposable
{
    private const uint ControlCode = 0x0022240C;
    private const uint Dsts = 0x53545344; // "DSTS" okuma
    private const uint Devs = 0x53564544; // "DEVS" yazma

    public const uint PerformanceModeId = 0x00120075;
    public const uint CpuFanId = 0x00110013;
    public const uint GpuFanId = 0x00110014;
    public const uint BatteryLimitId = 0x00120057;
    public const uint CpuTempId = 0x00120094;
    public const uint GpuTempId = 0x00120097;
    public const uint KeyboardBrightnessId = 0x00050021;

    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();

    private AsusAcpi(SafeFileHandle handle) => _handle = handle;

    /// <summary>Sürücü varsa açar, yoksa null döner.</summary>
    public static AsusAcpi? TryOpen()
    {
        var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        return h.IsInvalid ? null : new AsusAcpi(h);
    }

    /// <summary>Ham durum değeri (bit 16 = "destekleniyor" bayrağı).</summary>
    public int GetRaw(uint deviceId)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        return BitConverter.ToInt32(Call(Dsts, args), 0);
    }

    /// <summary>Bir cihaz kimliğine değer yazar. Firmware 1 döndürürse kabul etmiştir.</summary>
    public bool Set(uint deviceId, int value)
    {
        var args = new byte[8];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        BitConverter.GetBytes(value).CopyTo(args, 4);
        return BitConverter.ToInt32(Call(Devs, args), 0) == 1;
    }

    public bool SetPerformanceMode(AsusPerformanceMode mode) => Set(PerformanceModeId, (int)mode);

    /// <summary>Fan devri (RPM). Okunamazsa null.</summary>
    public int? GetCpuFanRpm() => ToRpm(GetRaw(CpuFanId));
    public int? GetGpuFanRpm() => ToRpm(GetRaw(GpuFanId));

    /// <summary>Klavye ışığı seviyesi 0 (kapalı) ile 3 arası. Okunamazsa null.</summary>
    public int? GetKeyboardBrightness()
    {
        var level = GetRaw(KeyboardBrightnessId) & 0x7F;
        return level is >= 0 and <= 3 ? level : null;
    }

    public const uint TufRgbId = 0x00100056;
    public const uint TufRgb2Id = 0x0010005A;

    /// <summary>Tampon parametreli yazma (TUF RGB gibi). Firmware 1 döndürürse kabul etmiştir.</summary>
    public bool SetBuffer(uint deviceId, byte[] data)
    {
        var args = new byte[4 + data.Length];
        BitConverter.GetBytes(deviceId).CopyTo(args, 0);
        data.CopyTo(args, 4);
        return BitConverter.ToInt32(Call(Devs, args), 0) == 1;
    }

    public bool IsKeyboardRgbSupported() => (GetRaw(TufRgbId) & 0x10000) != 0;

    /// <summary>TUF klavye RGB: mod 0 sabit, 1 nefes, 2 renk döngüsü, 3 gökkuşağı; hız 0 yavaş, 1 normal, 2 hızlı. Geri okunamaz, yalnızca firmware kabulü bilinir.</summary>
    public bool SetKeyboardRgb(int mode, byte r, byte g, byte b, int speed)
    {
        var speedByte = speed switch { 0 => (byte)0xE1, 1 => (byte)0xEB, _ => (byte)0xF5 };
        var data = new byte[] { 0xB4, (byte)mode, r, g, b, speedByte };
        if (SetBuffer(TufRgbId, data)) return true;
        data[0] = 0xB3; SetBuffer(TufRgb2Id, data);
        data[0] = 0xB4; return SetBuffer(TufRgb2Id, data);
    }

    public bool SetKeyboardBrightness(int level) => Set(KeyboardBrightnessId, 0x80 | Math.Clamp(level, 0, 3));

    /// <summary>Pil şarj limitini yazar (%40-100). Bu modelde geri okunamaz: yalnızca firmware kabulü doğrulanabilir.</summary>
    public bool SetBatteryLimit(int percent) => Set(BatteryLimitId, Math.Clamp(percent, 40, 100));

    /// <summary>Pil şarj limiti (%). Desteklenmiyorsa null.</summary>
    public int? GetBatteryLimit()
    {
        var raw = GetRaw(BatteryLimitId);
        var v = raw & 0xFFFF;
        return (raw & 0x10000) != 0 && v is >= 20 and <= 100 ? v : null;
    }

    private static int? ToRpm(int raw)
    {
        var fan = raw & 0xFFFF;
        return fan is > 0 and <= 120 ? fan * 100 : null;
    }

    private byte[] Call(uint method, byte[] args)
    {
        var buf = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        Array.Copy(args, 0, buf, 8, args.Length);
        var output = new byte[16];
        lock (_lock)
        {
            uint returned = 0;
            DeviceIoControl(_handle, ControlCode, buf, buf.Length, output, output.Length, ref returned, IntPtr.Zero);
        }
        return output;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inLen, byte[] output, int outLen, ref uint returned, IntPtr overlapped);
}
