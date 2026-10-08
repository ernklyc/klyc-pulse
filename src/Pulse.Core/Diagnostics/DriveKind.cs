using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Diagnostics;

public enum DriveKind { Unknown, Ssd, Hdd }

/// <summary>
/// Bir yolun bulunduğu diskin SSD mi HDD mi olduğunu söyler (yönetici gerekmez): Windows'un "arama cezası" (seek penalty) özelliği.
/// HDD'de oyun yükleme ve açık dünya akışında takılma yapar; SSD'ye taşımak çözer. Ağ ve bilinmeyen sürücülerde Unknown.
/// </summary>
public static class DriveKindDetector
{
    public static DriveKind Detect(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return DriveKind.Unknown;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':') return DriveKind.Unknown;

            using var h = CreateFile(@"\\.\" + root.Substring(0, 2), 0, 3 /* FILE_SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid) return DriveKind.Unknown;

            // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty (7), QueryType = PropertyStandardQuery (0) }
            var query = new byte[12];
            BitConverter.GetBytes(7).CopyTo(query, 0);
            var output = new byte[16];
            if (!DeviceIoControl(h, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 9)
                return DriveKind.Unknown;
            return output[8] != 0 ? DriveKind.Hdd : DriveKind.Ssd;   // DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty
        }
        catch { return DriveKind.Unknown; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}
