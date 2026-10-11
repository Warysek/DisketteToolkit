using AtariFloppy.Core.I18n;
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AtariFloppy.Core.Drives.Windows;

internal static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_DISK_GET_MEDIA_TYPES = 0x00070C00;
    public const uint IOCTL_DISK_FORMAT_TRACKS = 0x0007C018;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

    public const int ERROR_WRITE_PROTECT = 19;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_CRC = 23;
    public const int ERROR_SECTOR_NOT_FOUND = 27;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_UNRECOGNIZED_MEDIA = 1785;
    public const int ERROR_MEDIA_CHANGED = 1110;
    public const int ERROR_NO_MEDIA_IN_DRIVE = 1112;

    public const uint DRIVE_REMOVABLE = 2;
    public const int BusTypeUsb = 7;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    /// <summary>Řekne Průzkumníku, že se obsah diskety změnil (jako vyjmutí a vložení diskety).</summary>
    public static void NotifyShellMediaChanged(string root)
    {
        const int SHCNE_MEDIAINSERTED = 0x20, SHCNE_MEDIAREMOVED = 0x40, SHCNE_UPDATEDIR = 0x1000;
        const uint SHCNF_PATHW = 0x0005, SHCNF_FLUSHNOWAIT = 0x3000;
        try
        {
            SHChangeNotify(SHCNE_MEDIAREMOVED, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, root, IntPtr.Zero);
            SHChangeNotify(SHCNE_MEDIAINSERTED, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, root, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, root, IntPtr.Zero);
        }
        catch { /* mimo Windows nebo bez shellu — nevadí */ }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int toRead, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int toWrite, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

    [DllImport("kernel32.dll")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveType(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDevice(string device, char[] target, int max);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetErrorMode(uint mode);
    public const uint SEM_FAILCRITICALERRORS = 1;

    /// <summary>Formáty médií podle winioctl.h (MEDIA_TYPE).</summary>
    public static string MediaTypeName(int t) => t switch
    {
        1 => "5,25\" 1,2 MB",
        2 => "3,5\" 1,44 MB",
        3 => "3,5\" 2,88 MB",
        4 => "3,5\" 20,8 MB",
        5 => "3,5\" 720 KB",
        6 => "5,25\" 360 KB",
        7 => "5,25\" 320 KB",
        8 => "5,25\" 320 KB (1024 B)",
        9 => "5,25\" 180 KB",
        10 => "5,25\" 160 KB",
        14 => "3,5\" 640 KB",
        15 => "5,25\" 640 KB",
        16 => "5,25\" 720 KB",
        17 => "3,5\" 1,2 MB",
        18 => "3,5\" 1,23 MB (1024 B)",
        19 => "5,25\" 1,23 MB (1024 B)",
        _ => Tr.F("typ {0}", t),
    };

    public static bool IsFloppyMediaType(int t) => t is >= 1 and <= 10 or >= 14 and <= 19;

    public const int F3_1Pt44_512 = 2;
    public const int F3_720_512 = 5;
    public const int F3_640_512 = 14;
}
