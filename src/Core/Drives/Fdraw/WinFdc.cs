using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AtariFloppy.Core.Drives.Windows;
using Microsoft.Win32.SafeHandles;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Fdraw;

/// <summary>
/// Skutečný řadič přes fdrawcmd.sys (Simon Owen, http://simonowen.com/fdrawcmd/).
/// Kódy IOCTL a struktury podle fdrawcmd.h 1.0.1.11; použití podle SAMdisku.
/// </summary>
public sealed class WinFdc : IFdc
{
    private const uint IOCTL_FDCMD_READ_DATA = 0x0022E01A;
    private const uint IOCTL_FDCMD_WRITE_DATA = 0x0022E015;
    private const uint IOCTL_FDCMD_SPECIFY = 0x0022E00C;
    private const uint IOCTL_FDCMD_RECALIBRATE = 0x0022E01C;
    private const uint IOCTL_FDCMD_SEEK = 0x0022E03C;
    private const uint IOCTL_FDCMD_FORMAT_TRACK = 0x0022E034;
    private const uint IOCTL_FDCMD_PERPENDICULAR_MODE = 0x0022E048;
    private const uint IOCTL_FD_SCAN_TRACK = 0x0022E400;
    private const uint IOCTL_FD_SET_MOTOR_TIMEOUT = 0x0022E40C;
    private const uint IOCTL_FD_SET_DATA_RATE = 0x0022E410;
    private const uint IOCTL_FD_GET_FDC_INFO = 0x0022E414;
    private const uint IOCTL_FD_SET_DISK_CHECK = 0x0022E420;
    private const uint IOCTL_FD_CHECK_DISK = 0x0022E45C;
    private const uint IOCTL_FDCMD_SENSE_DRIVE_STATUS = 0x0022E010;
    private const uint IOCTL_FD_MOTOR_ON = 0x0022E448;
    private const uint IOCTL_FD_MOTOR_OFF = 0x0022E44C;
    private const uint IOCTL_FD_TIMED_SCAN_TRACK = 0x0022E454;
    private const uint IOCTL_FD_GET_TRACK_TIME = 0x0022E460;
    private const byte FD_OPTION_MFM = 0x40;
    private const byte RW_GAP = 0x0A;

    private readonly SafeFileHandle _h;
    public string Description { get; }

    public WinFdc(int driveIndex)
    {
        _h = Native.CreateFile($@"\\.\fdraw{driveIndex}", Native.GENERIC_READ | Native.GENERIC_WRITE,
                               Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (_h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new OperationException(Tr.F("Mechaniku {0}: přes fdrawcmd.sys nejde otevřít: {1}. Je ovladač nainstalovaný a mechanika připojená přímo k základní desce?", (char)('A' + driveIndex), WinFloppyDevice.Msg(err)));
        }
        Ioctl(IOCTL_FD_SET_MOTOR_TIMEOUT, new byte[] { 3 }, null);
        Ioctl(IOCTL_FD_SET_DISK_CHECK, new byte[] { 0 }, null);   // jako SAMdisk: staré mechaniky mají nespolehlivý signál výměny diskety
        Description = ReadInfo();
    }

    private string ReadInfo()
    {
        var info = new byte[15];
        if (!Ioctl(IOCTL_FD_GET_FDC_INFO, null, info)) return Tr.T("řadič (typ neznámý)");
        string type = info[0] switch
        {
            2 => Tr.T("NEC µPD765 / kompatibilní"),
            3 => Tr.T("rozšířený řadič"),
            4 => "Intel 82077",
            5 => "Intel 82077AA",
            6 => Tr.T("Intel 82078 (44 vývodů)"),
            7 => Tr.T("Intel 82078 (64 vývodů)"),
            8 => "National Semiconductor",
            _ => Tr.T("neznámý typ"),
        };
        var speeds = new List<string>();
        if ((info[1] & 0x01) != 0) speeds.Add("250K");
        if ((info[1] & 0x02) != 0) speeds.Add("300K");
        if ((info[1] & 0x04) != 0) speeds.Add("500K");
        if ((info[1] & 0x08) != 0) speeds.Add("1M");
        return Tr.F("{0}, rychlosti {1}", type, string.Join("/", speeds));
    }

    public void SetDataRate(DataRate rate)
    {
        Ioctl(IOCTL_FDCMD_PERPENDICULAR_MODE, new byte[] { (byte)(rate == DataRate.R1M ? 0xBC : 0x00) }, null);
        byte r = rate switch { DataRate.R500K => 0, DataRate.R300K => 1, DataRate.R250K => 2, _ => 3 };
        if (!Ioctl(IOCTL_FD_SET_DATA_RATE, new[] { r }, null))
            throw new SectorIOException(Tr.T("řadič odmítl rychlost ") + FdrawDevice.RateText(rate), Marshal.GetLastWin32Error());
    }

    public void SetStepRate(int milliseconds, DataRate rate)
    {
        // SRT: krok = (16 − SRT) ms při 500 kbit/s; při 250 kbit/s dvojnásobek, při 300 kbit/s 1,67×.
        double unit = rate switch { DataRate.R250K => 2.0, DataRate.R300K => 1.667, DataRate.R1M => 0.5, _ => 1.0 };
        int srt = 16 - (int)Math.Ceiling(milliseconds / unit);
        srt = Math.Max(1, Math.Min(15, srt));
        byte hut = 0x0F, hlt = 0x0F;
        Ioctl(IOCTL_FDCMD_SPECIFY, new[] { (byte)((srt << 4) | hut), (byte)(hlt << 1) }, null);
    }

    public void Seek(int cylinder)
    {
        bool ok = cylinder == 0
            ? Ioctl(IOCTL_FDCMD_RECALIBRATE, null, null)
            : Ioctl(IOCTL_FDCMD_SEEK, new[] { (byte)cylinder, (byte)0 }, null);
        if (!ok) throw new SectorIOException(Tr.F("hlava nedojela na stopu {0}", cylinder), Marshal.GetLastWin32Error());
    }

    public bool? CheckDisk()
    {
        if (Ioctl(IOCTL_FD_CHECK_DISK, null, null)) return true;
        int err = Marshal.GetLastWin32Error();
        if (err == Native.ERROR_NOT_READY || err == Native.ERROR_NO_MEDIA_IN_DRIVE) return false;
        return null; // starší ovladač příkaz nezná
    }

    public List<SectorId> ScanTrack(int head)
    {
        var result = new byte[1 + 4 * 128];
        var list = new List<SectorId>();
        if (!Ioctl(IOCTL_FD_SCAN_TRACK, new[] { FD_OPTION_MFM, (byte)head }, result)) return list;
        int count = Math.Min((int)result[0], 128);
        for (int i = 0; i < count; i++)
        {
            int o = 1 + i * 4;
            list.Add(new SectorId(result[o], result[o + 1], result[o + 2], result[o + 3]));
        }
        return list;
    }

    private static byte[] RwParams(int cyl, int head, int sector, int size, int count) => new[]
    {
        FD_OPTION_MFM, (byte)head, (byte)cyl, (byte)head, (byte)sector, (byte)size,
        (byte)(sector + count), RW_GAP, (byte)(size == 0 ? 0x80 : 0xFF),
    };

    public byte[] Read(int cyl, int head, int firstSector, int sizeCode, int count)
    {
        var buf = new byte[count * (128 << sizeCode)];
        if (!Ioctl(IOCTL_FDCMD_READ_DATA, RwParams(cyl, head, firstSector, sizeCode, count), buf))
            throw Error(Marshal.GetLastWin32Error());
        return buf;
    }

    public void Write(int cyl, int head, int firstSector, int sizeCode, int count, byte[] data, int offset)
    {
        var buf = new byte[count * (128 << sizeCode)];
        Buffer.BlockCopy(data, offset, buf, 0, buf.Length);
        if (!Ioctl(IOCTL_FDCMD_WRITE_DATA, RwParams(cyl, head, firstSector, sizeCode, count), buf))
            throw Error(Marshal.GetLastWin32Error());
    }

    public void Format(int cyl, int head, int sizeCode, IList<SectorId> ids, int gap3, byte fill)
    {
        var p = new byte[6 + ids.Count * 4];
        p[0] = FD_OPTION_MFM;
        p[1] = (byte)head;
        p[2] = (byte)sizeCode;
        p[3] = (byte)ids.Count;
        p[4] = (byte)gap3;
        p[5] = fill;
        for (int i = 0; i < ids.Count; i++)
        {
            p[6 + i * 4] = ids[i].Cyl;
            p[7 + i * 4] = ids[i].Head;
            p[8 + i * 4] = ids[i].Sector;
            p[9 + i * 4] = ids[i].SizeCode;
        }
        if (!Ioctl(IOCTL_FDCMD_FORMAT_TRACK, p, null))
            throw Error(Marshal.GetLastWin32Error());
    }

    public int? TrackTimeUs()
    {
        var o = new byte[4];
        return Ioctl(IOCTL_FD_GET_TRACK_TIME, null, o) ? BitConverter.ToInt32(o, 0) : null;
    }

    public TimedScan? TimedScanTrack(int head)
    {
        // FD_TIMED_SCAN_RESULT (pack 1): count, firstseen, DWORD tracktime, pak {DWORD reltime; C,H,R,N} × count
        var r = new byte[6 + 8 * 128];
        if (!Ioctl(IOCTL_FD_TIMED_SCAN_TRACK, new[] { FD_OPTION_MFM, (byte)head }, r)) return null;
        int count = Math.Min((int)r[0], 128);
        var list = new List<(int, SectorId)>();
        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 8;
            list.Add((BitConverter.ToInt32(r, o), new SectorId(r[o + 4], r[o + 5], r[o + 6], r[o + 7])));
        }
        return new TimedScan(BitConverter.ToInt32(r, 2), list);
    }

    public byte? DriveStatus(int head)
    {
        var o = new byte[1];
        return Ioctl(IOCTL_FDCMD_SENSE_DRIVE_STATUS, new[] { (byte)head }, o) ? o[0] : null;
    }

    public bool ReadRaw(int cyl, int head, int sector, int sizeCode, out byte[]? data)
    {
        // Výchozí vzorek: když ho po chybě najdeme v bufferu celý, řadič žádná data nepřenesl.
        // READ_DATA je METHOD_OUT_DIRECT, takže data z chybného čtení by tam být měla (neověřeno).
        var buf = new byte[128 << sizeCode];
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(0xA5 ^ i);
        if (Ioctl(IOCTL_FDCMD_READ_DATA, RwParams(cyl, head, sector, sizeCode, 1), buf)) { data = buf; return true; }
        bool untouched = true;
        for (int i = 0; i < buf.Length && untouched; i++) untouched = buf[i] == (byte)(0xA5 ^ i);
        data = untouched ? null : buf;
        return false;
    }

    public void Motor(bool on) => Ioctl(on ? IOCTL_FD_MOTOR_ON : IOCTL_FD_MOTOR_OFF, null, null);

    private static Exception Error(int err) => err switch
    {
        Native.ERROR_WRITE_PROTECT => new OperationException(Tr.T("Disketa je chráněná proti zápisu. Vyndej ji a v rohu diskety najdi malý posuvný jezdec se čtvercovým okénkem: posuň ho tak, aby okénko bylo ZAKRYTÉ (otevřené okénko = ochrana zapnutá). U 5,25\" diskety odlep nálepku z výřezu na boku. Pak disketu vlož znovu.")),
        Native.ERROR_NOT_READY or Native.ERROR_NO_MEDIA_IN_DRIVE => new OperationException(Tr.T("Mechanika není připravená — je vložená disketa?")),
        _ => new SectorIOException(WinFloppyDevice.Msg(err), err),
    };

    private bool Ioctl(uint code, byte[]? input, byte[]? output) =>
        Native.DeviceIoControl(_h, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out _, IntPtr.Zero);

    public void Dispose() => _h.Dispose();
}
