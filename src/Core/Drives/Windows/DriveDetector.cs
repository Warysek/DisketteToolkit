using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Windows;

/// <summary>Najde všechny disketové mechaniky a Greaseweazle a určí, co která umí.</summary>
public static class DriveDetector
{
    public static List<DetectedDrive> DetectAll(List<string>? log = null, bool includeGreaseweazle = true)
    {
        var result = new List<DetectedDrive>();
        try { result.AddRange(DetectFloppyLetters(log)); }
        catch (Exception e) { log?.Add(Tr.T("Hledání disketových jednotek selhalo: ") + e.Message); }
        if (includeGreaseweazle)
        try { result.AddRange(Greaseweazle.Detect(log)); }
        catch (Exception e) { log?.Add(Tr.T("Hledání Greaseweazle selhalo: ") + e.Message); }
        foreach (var d in result) d.ComputeFeatures();
        return result;
    }

    public static IEnumerable<DetectedDrive> DetectFloppyLetters(List<string>? log)
    {
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS);
        uint mask = Native.GetLogicalDrives();
        bool fdraw = FdrawCmd.IsInstalled(out string? fdrawVersion);

        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            string letter = ((char)('A' + i)).ToString();
            if (Native.GetDriveType(letter + ":\\") != Native.DRIVE_REMOVABLE) continue;

            // Dotazy na vlastnosti mechaniky nepotřebují práva ke čtení ani vloženou disketu.
            using var h = Native.CreateFile($@"\\.\{letter}:", 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                                            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { log?.Add(Tr.F("{0}: nejde otevřít ({1})", letter, WinFloppyDevice.Msg(Marshal.GetLastWin32Error()))); continue; }

            var media = GetMediaTypes(h);
            string dosDevice = QueryDosDevice(letter + ":");
            bool isFloppy = media.Any(Native.IsFloppyMediaType) || dosDevice.IndexOf("Floppy", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isFloppy) continue; // USB flashka, čtečka karet…

            var (bus, vendor, product, revision) = GetStorageDescriptor(h);
            var kind = bus == Native.BusTypeUsb ? DriveKind.Usb : DriveKind.Internal;
            string name = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (name.Length == 0) name = kind == DriveKind.Usb ? Tr.T("USB disketová mechanika") : Tr.T("Disketová mechanika");

            bool driveFdraw = fdraw && kind == DriveKind.Internal && FdrawCmd.CanOpenDrive(letter);
            var d = new DetectedDrive
            {
                Kind = kind,
                Letter = letter,
                Name = name,
                Details = string.Join(", ", new[]
                {
                    string.IsNullOrWhiteSpace(revision) ? null : "firmware " + revision,
                    string.IsNullOrEmpty(dosDevice) ? null : dosDevice,
                    driveFdraw ? "fdrawcmd " + fdrawVersion : null,
                }.Where(s => s != null)),
                FdrawcmdInstalled = driveFdraw,
                MediaTypes = media.Select(Native.MediaTypeName).Distinct().ToList(),
                Supports720 = MediaSupport.Supports720(media),
                Reports720 = media.Contains(5),
                Supports1440 = MediaSupport.Supports1440(media),
            };
            d.Media = MediaSupport.Density(media);
            try
            {
                using var dev = new WinFloppyDevice(letter, write: false);
                d.MediaState = dev.MediaState;
                d.FormattedAs = dev.MediaGeometry;
                if (dev.MediaState == MediaState.NoDisk) d.Media = InsertedMedia.None;
                else if (d.Media == InsertedMedia.Unknown && dev.MediaGeometry != null)
                    d.Media = dev.MediaGeometry.IsHighDensity ? InsertedMedia.HD : InsertedMedia.DD;
            }
            catch (Exception e) { log?.Add(Tr.F("{0}: stav diskety nejde zjistit ({1})", letter, e.Message)); }

            yield return d;
        }
    }

    private static List<int> GetMediaTypes(SafeFileHandle h)
    {
        var list = new List<int>();
        var buf = new byte[24 * 32];
        if (!Native.DeviceIoControl(h, Native.IOCTL_DISK_GET_MEDIA_TYPES, null, 0, buf, buf.Length, out int ret, IntPtr.Zero))
            return list;
        for (int o = 0; o + 24 <= ret; o += 24)
            list.Add(BitConverter.ToInt32(buf, o + 8));
        return list;
    }

    private static (int bus, string vendor, string product, string revision) GetStorageDescriptor(SafeFileHandle h)
    {
        var query = new byte[12]; // PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0)
        var buf = new byte[1024];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, buf, buf.Length, out int ret, IntPtr.Zero) || ret < 32)
            return (-1, "", "", "");
        int vendorOff = BitConverter.ToInt32(buf, 12);
        int productOff = BitConverter.ToInt32(buf, 16);
        int revOff = BitConverter.ToInt32(buf, 20);
        int bus = BitConverter.ToInt32(buf, 28);
        return (bus, CStr(buf, vendorOff, ret), CStr(buf, productOff, ret), CStr(buf, revOff, ret));
    }

    private static string CStr(byte[] b, int off, int max)
    {
        if (off <= 0 || off >= max) return "";
        int end = off;
        while (end < max && b[end] != 0) end++;
        return Regex.Replace(Encoding.ASCII.GetString(b, off, end - off), @"\s+", " ").Trim();
    }

    private static string QueryDosDevice(string name)
    {
        var buf = new char[1024];
        uint n = Native.QueryDosDevice(name, buf, buf.Length);
        return n == 0 ? "" : new string(buf, 0, (int)n).Split('\0')[0];
    }
}

/// <summary>Detekce ovladače fdrawcmd.sys (Simon Owen) — nízkoúrovňový přístup k řadiči základní desky.</summary>
public static class FdrawCmd
{
    // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x888, METHOD_BUFFERED, FILE_READ_DATA)
    private const uint IOCTL_FDRAWCMD_GET_VERSION = 0x0022E220;

    public static bool IsInstalled(out string? version)
    {
        version = null;
        using var h = Native.CreateFile(@"\\.\fdrawcmd", Native.GENERIC_READ | Native.GENERIC_WRITE, 0,
                                        IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;
        var buf = new byte[4];
        if (Native.DeviceIoControl(h, IOCTL_FDRAWCMD_GET_VERSION, null, 0, buf, 4, out int ret, IntPtr.Zero) && ret == 4)
        {
            uint v = BitConverter.ToUInt32(buf, 0);
            version = $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
        }
        else version = Tr.T("(verze neznámá)");
        return true;
    }

    public static bool CanOpenDrive(string letter)
    {
        int index = char.ToUpperInvariant(letter[0]) - 'A';
        if (index is < 0 or > 3) return false;
        using var h = Native.CreateFile($@"\\.\fdraw{index}", Native.GENERIC_READ | Native.GENERIC_WRITE, 0,
                                        IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        return !h.IsInvalid;
    }
}
