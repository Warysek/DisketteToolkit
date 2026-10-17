using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Windows;

/// <summary>
/// Standardní přístup k disketě přes Windows (\\.\A:). Funguje pro USB disketovky
/// i interní mechaniky — obojí umí jen formáty, které zná ovladač Windows (720 KB, 1,44 MB).
/// </summary>
public sealed class WinFloppyDevice : ISectorDevice
{
    private readonly SafeFileHandle _h;
    private readonly string _letter;
    private bool _locked;
    private IntPtr _raw, _aligned;
    private const int BufferSize = 64 * 1024;

    public string Name => _letter + ":";
    public DiskGeometry? MediaGeometry { get; private set; }
    public MediaState MediaState { get; private set; }
    public string? MediaStateMessage { get; private set; }
    public int MediaTypeCode { get; private set; }

    public WinFloppyDevice(string letter, bool write)
    {
        _letter = letter.TrimEnd(':').ToUpperInvariant();
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS); // žádné systémové okno "Vložte disk"
        uint access = Native.GENERIC_READ | (write ? Native.GENERIC_WRITE : 0);
        _h = Native.CreateFile($@"\\.\{_letter}:", access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                               IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        if (_h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_ACCESS_DENIED)
                throw new OperationException(Tr.F("Přístup k jednotce {0}: byl odepřen. Program musí běžet jako správce (Terminál → Spustit jako správce).", _letter));
            throw new OperationException(Tr.F("Jednotku {0}: nejde otevřít: {1}", _letter, Msg(err)));
        }
        // Dovolí číst celou disketu, i když boot sektor (BPB) udává menší svazek — typické u Atari disket.
        Native.DeviceIoControl(_h, Native.FSCTL_ALLOW_EXTENDED_DASD_IO, null, 0, null, 0, out _, IntPtr.Zero);
        // Zarovnaný buffer — přímý přístup k médiu vyžaduje zarovnání na sektor.
        _raw = Marshal.AllocHGlobal(BufferSize + 4096);
        _aligned = new IntPtr((_raw.ToInt64() + 4095) & ~4095L);
        RefreshMedia();
    }

    public void RefreshMedia()
    {
        var buf = new byte[24];
        MediaGeometry = null;
        MediaTypeCode = 0;
        if (!Native.DeviceIoControl(_h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY, null, 0, buf, buf.Length, out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            MediaStateMessage = Msg(err);
            MediaState = err switch
            {
                Native.ERROR_NOT_READY or Native.ERROR_NO_MEDIA_IN_DRIVE => MediaState.NoDisk,
                Native.ERROR_UNRECOGNIZED_MEDIA => MediaState.Unrecognized,
                _ => MediaState.Error,
            };
            return;
        }
        long cyl = BitConverter.ToInt64(buf, 0);
        int type = BitConverter.ToInt32(buf, 8);
        int heads = BitConverter.ToInt32(buf, 12);
        int spt = BitConverter.ToInt32(buf, 16);
        int bps = BitConverter.ToInt32(buf, 20);
        MediaTypeCode = type;
        if (type == 0 || cyl <= 0 || cyl > 90 || heads is < 1 or > 2 || spt is < 1 or > 40 || bps != 512)
        {
            MediaState = MediaState.Unrecognized;
            MediaStateMessage = Tr.F("neznámé médium (typ {0}, {1}×{2}×{3}, {4} B)", type, cyl, heads, spt, bps);
            return;
        }
        MediaGeometry = new DiskGeometry((int)cyl, heads, spt, bps);
        MediaState = MediaState.Ready;
        MediaStateMessage = Native.MediaTypeName(type);
    }

    public void BeginExclusive()
    {
        if (_locked) return;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (Native.DeviceIoControl(_h, Native.FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero))
            {
                _locked = true;
                Native.DeviceIoControl(_h, Native.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
                return;
            }
            Thread.Sleep(300);
        }
        throw new OperationException(Tr.F("Jednotku {0}: právě používá jiný program (třeba Průzkumník s otevřenou disketou). Zavři okna s disketou a zkus to znovu.", _letter));
    }

    public void EndExclusive()
    {
        if (!_locked) return;
        // Po zápisu „zapomeň“ starý obsah — jinak Průzkumník ukazuje disketu, jaká byla před zápisem.
        Native.DeviceIoControl(_h, Native.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
        Native.DeviceIoControl(_h, Native.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
        _locked = false;
        Native.NotifyShellMediaChanged(_letter + @":\");
    }

    public byte[] ReadSectors(long lba, int count)
    {
        int bps = MediaGeometry?.BytesPerSector ?? 512;
        int len = count * bps;
        if (len > BufferSize) throw new ArgumentOutOfRangeException(nameof(count));
        int read = 0;
        for (int attempt = 0; ; attempt++)
        {
            Seek(lba * bps);
            if (Native.ReadFile(_h, _aligned, len, out read, IntPtr.Zero)) break;
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_MEDIA_CHANGED && attempt == 0) { RefreshMedia(); continue; }
            throw IoError(err, false);
        }
        if (read != len) throw new SectorIOException(Tr.F("přečteno jen {0} z {1} B", read, len));
        var result = new byte[len];
        Marshal.Copy(_aligned, result, 0, len);
        return result;
    }

    public void WriteSectors(long lba, byte[] buffer, int offset, int count)
    {
        int bps = MediaGeometry?.BytesPerSector ?? 512;
        int len = count * bps;
        if (len > BufferSize) throw new ArgumentOutOfRangeException(nameof(count));
        Marshal.Copy(buffer, offset, _aligned, len);
        int written = 0;
        for (int attempt = 0; ; attempt++)
        {
            Seek(lba * bps);
            if (Native.WriteFile(_h, _aligned, len, out written, IntPtr.Zero)) break;
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_MEDIA_CHANGED && attempt == 0) { RefreshMedia(); continue; }
            throw IoError(err, true);
        }
        if (written != len) throw new SectorIOException(Tr.F("zapsáno jen {0} z {1} B", written, len));
    }

    public void FormatCylinder(DiskGeometry target, int cylinder)
    {
        int mediaType = target.SectorsPerTrack switch
        {
            8 => Native.F3_640_512,
            9 => Native.F3_720_512,
            18 => Native.F3_1Pt44_512,
            _ => throw new OperationException(Tr.F("Windows umí formátovat jen 640 KB, 720 KB a 1,44 MB, ne {0}.", target)),
        };
        var p = new byte[20];
        BitConverter.GetBytes(mediaType).CopyTo(p, 0);
        BitConverter.GetBytes(cylinder).CopyTo(p, 4);
        BitConverter.GetBytes(cylinder).CopyTo(p, 8);
        BitConverter.GetBytes(0).CopyTo(p, 12);
        BitConverter.GetBytes(target.Sides - 1).CopyTo(p, 16);
        var bad = new byte[64];
        if (!Native.DeviceIoControl(_h, Native.IOCTL_DISK_FORMAT_TRACKS, p, p.Length, bad, bad.Length, out _, IntPtr.Zero))
        {
            var e = IoError(Marshal.GetLastWin32Error(), true);
            // Odmítnutí hned první stopy DD formátu = typicky USB disketovka, která DD diskety neumí.
            if (cylinder == 0 && !target.IsHighDensity && e is SectorIOException)
                throw new OperationException(Tr.F("Mechanika odmítla formát {0}: {1}.", target, e.Message) + " " +
                                             Tr.T("Některé USB disketovky diskety 720 KB (DD) neumějí vůbec — zkus jinou USB mechaniku, interní mechaniku nebo Greaseweazle."));
            throw e;
        }
    }

    public bool SupportsGeometry(DiskGeometry g) => StandardGeometry.Supports(g);
    public string GeometryLimits => StandardGeometry.Limits;
    public DiskGeometry FormatTargetFor(DiskGeometry image) => StandardGeometry.FormatTargetFor(image);

    private void Seek(long pos)
    {
        if (!Native.SetFilePointerEx(_h, pos, out _, 0))
            throw new SectorIOException(Tr.T("nastavení pozice selhalo: ") + Msg(Marshal.GetLastWin32Error()));
    }

    private Exception IoError(int err, bool writing)
    {
        switch (err)
        {
            case Native.ERROR_WRITE_PROTECT:
                return new OperationException(Tr.T("Disketa je chráněná proti zápisu. Vyndej ji a v rohu diskety najdi malý posuvný jezdec se čtvercovým okénkem: posuň ho tak, aby okénko bylo ZAKRYTÉ (otevřené okénko = ochrana zapnutá). U 5,25\" diskety odlep nálepku z výřezu na boku. Pak disketu vlož znovu."));
            case Native.ERROR_NOT_READY:
            case Native.ERROR_NO_MEDIA_IN_DRIVE:
                return new OperationException(Tr.T("Mechanika není připravená — je vložená disketa?"));
            case Native.ERROR_ACCESS_DENIED when writing:
                return new OperationException(Tr.T("Windows zápis odmítly (přístup odepřen). Spusť program jako správce a zavři okna, která mají disketu otevřenou."));
            default:
                return new SectorIOException(Msg(err), err);
        }
    }

    internal static string Msg(int err) => Tr.F("{0} (kód {1})", new Win32Exception(err).Message.TrimEnd('.', ' ', '\r', '\n'), err);

    public void Dispose()
    {
        EndExclusive();
        _h.Dispose();
        if (_raw != IntPtr.Zero) { Marshal.FreeHGlobal(_raw); _raw = IntPtr.Zero; }
    }
}
