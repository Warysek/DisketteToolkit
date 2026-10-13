using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AtariFloppy.Core;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui;

/// <summary>
/// Ukázkový režim (DisketteToolkit.exe --demo): simulované mechaniky s disketou v paměti.
/// Slouží k vyzkoušení okna bez hardwaru — nic se nezapisuje na skutečnou disketu.
/// </summary>
internal static class DemoDrives
{
    private static readonly Dictionary<string, SimDevice.Disk> Disks = new();

    public static List<DetectedDrive> Detect()
    {
        var list = new List<DetectedDrive>();
        foreach (var (letter, kind, fdraw, name) in new[]
                 {
                     ("A", DriveKind.Usb, false, "Sony USB-FDU (" + Tr.T("ukázka") + ")"),
                     ("B", DriveKind.Internal, true, "TEAC FD-235HF (" + Tr.T("ukázka") + ")"),
                 })
        {
            var disk = DiskFor(letter);
            var d = new DetectedDrive
            {
                Kind = kind, Letter = letter, Name = name, FdrawcmdInstalled = fdraw,
                Supports720 = true, Supports1440 = true,
                Details = Tr.T("simulovaná mechanika — nic se nezapisuje na skutečnou disketu"),
                MediaTypes = new List<string> { "3,5\" 720 KB", "3,5\" 640 KB" },
            };
            d.Media = disk.Present ? (disk.Hd ? InsertedMedia.HD : InsertedMedia.DD) : InsertedMedia.None;
            d.MediaState = !disk.Present ? MediaState.NoDisk : disk.FormattedAs == null ? MediaState.Unrecognized : MediaState.Ready;
            d.FormattedAs = disk.FormattedAs != null && StandardGeometry.Supports(disk.FormattedAs) ? disk.FormattedAs : null;
            if (d.MediaState == MediaState.Ready && d.FormattedAs == null) d.MediaState = MediaState.Unrecognized;
            d.ComputeFeatures();
            list.Add(d);
        }
        var tool = AtariFloppy.Core.Drives.Gw.GwTool.Find();
        var gw = new DetectedDrive { Kind = DriveKind.Greaseweazle, Port = "COM5", Name = "Greaseweazle F7 (" + Tr.T("ukázka") + ")", GwToolPath = tool?.Path };
        gw.ComputeFeatures();
        list.Add(gw);
        return list;
    }

    public static ISectorDevice Open(DetectedDrive d, bool write, DiskGeometry? g) =>
        new SimDevice(DiskFor(d.Letter ?? "A"), d.Kind == DriveKind.Internal && d.FdrawcmdInstalled, d.Id + " (" + Tr.T("ukázka") + ")");

    /// <summary>Diagnostika v ukázce: simulovaný řadič s disketou Atari (9 sektorů, 82 stop, chráněná stopa 79, slábnoucí stopa 22).</summary>
    public static AtariFloppy.Core.Drives.Diag.IDriveDiag OpenDiag(DetectedDrive d)
    {
        bool gw = d.Kind == DriveKind.Greaseweazle;
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(new DemoFdc(), d.Id + " (" + Tr.T("ukázka") + ")");
        return new AtariFloppy.Core.Drives.Diag.FdrawDiag(dev, canLayout: !gw);
    }

    private static SimDevice.Disk DiskFor(string letter)
    {
        lock (Disks)
        {
            if (Disks.TryGetValue(letter, out var d)) return d;
            d = letter == "A" ? AtariDemoDisk() : new SimDevice.Disk { Present = true, Hd = false };
            Disks[letter] = d;
            return d;
        }
    }

    /// <summary>Atari disketa 720 KB s několika soubory, jedním slábnoucím a dvěma vadnými sektory.</summary>
    private static SimDevice.Disk AtariDemoDisk()
    {
        var g = DiskGeometry.Dd720;
        var img = DiskFormatter.CreateBlank(g, FormatStyle.Atari, "DEMO", new Random(42));
        var rnd = new Random(7);
        var layout = DiskFormatter.ComputeLayout(g, FormatStyle.Atari);
        int rootOffset = (layout.ReservedSectors + layout.Fats * layout.SectorsPerFat) * 512;
        int dataStart = layout.SystemSectors * 512, cs = layout.SectorsPerCluster * 512;
        int cluster = 2, slot = 1; // slot 0 = jmenovka
        foreach (var (name, size) in new[] { ("DESKTOP.INF", 540), ("NEOCHROM.PRG", 62_310), ("PICTURE1.PI1", 32_034), ("PICTURE2.PI1", 32_034),
                                             ("STOS.PRG", 118_422), ("GAME.DAT", 204_800), ("README.TXT", 3_120), ("HISCORE.DAT", 1_024) })
        {
            int clusters = (size + cs - 1) / cs;
            for (int i = 0; i < clusters; i++)
            {
                int next = i == clusters - 1 ? FatTable.EndOfChain : cluster + i + 1;
                for (int f = 0; f < layout.Fats; f++)
                    FatTable.Set(img.Data, (layout.ReservedSectors + f * layout.SectorsPerFat) * 512, cluster + i, next);
            }
            var content = new byte[size];
            rnd.NextBytes(content);
            Buffer.BlockCopy(content, 0, img.Data, dataStart + (cluster - 2) * cs, size);
            int e = rootOffset + slot * 32;
            var parts = name.Split('.');
            var n83 = Encoding.ASCII.GetBytes(parts[0].PadRight(8) + parts[1].PadRight(3));
            Buffer.BlockCopy(n83, 0, img.Data, e, 11);
            img.Data[e + 11] = 0x20;
            img.Data[e + 22] = 0x00; img.Data[e + 23] = 0x60;             // 12:00
            img.Data[e + 24] = 0x3B; img.Data[e + 25] = 0x13;             // 1989-09-27
            img.Data[e + 26] = (byte)cluster; img.Data[e + 27] = (byte)(cluster >> 8);
            BitConverter.GetBytes(size).CopyTo(img.Data, e + 28);
            cluster += clusters;
            slot++;
        }
        var disk = new SimDevice.Disk { Present = true, Hd = false, FormattedAs = g };
        Buffer.BlockCopy(img.Data, 0, disk.Data, 0, img.Data.Length);
        disk.Unreadable.Add(g.Lba(57, 1, 2));
        disk.Unreadable.Add(g.Lba(57, 1, 3));
        disk.Flaky[g.Lba(22, 0, 5)] = 2;
        return disk;
    }
}

/// <summary>Simulovaná mechanika: data v paměti, přiměřeně pomalá, s vadnými místy.</summary>
internal sealed class SimDevice : ISectorDevice
{
    internal sealed class Disk
    {
        public bool Present;
        public bool Hd;
        public DiskGeometry? FormattedAs;
        public readonly byte[] Data = new byte[84 * 2 * 21 * 512];
        public readonly HashSet<long> Unreadable = new();
        public readonly Dictionary<long, int> Flaky = new();
    }

    private readonly Disk _disk;
    private readonly bool _lowLevel;

    public SimDevice(Disk disk, bool lowLevel, string name)
    {
        _disk = disk;
        _lowLevel = lowLevel;
        Name = name;
    }

    public string Name { get; }
    public DiskGeometry? MediaGeometry => _disk.Present ? _disk.FormattedAs : null;
    public MediaState MediaState => !_disk.Present ? MediaState.NoDisk : _disk.FormattedAs == null ? MediaState.Unrecognized : MediaState.Ready;
    public string? MediaStateMessage => MediaState == MediaState.Ready ? _disk.FormattedAs!.ToString() : MediaState.ToString();
    public void RefreshMedia() { }
    public void BeginExclusive() { }
    public void EndExclusive() { }
    public void Dispose() { }

    public bool SupportsGeometry(DiskGeometry g) => _lowLevel
        ? g.BytesPerSector == 512 && g.Tracks <= 84 && (g.SectorsPerTrack <= 11 || g.SectorsPerTrack == 18)
        : StandardGeometry.Supports(g);

    public string GeometryLimits => _lowLevel ? Tr.T("fdrawcmd.sys: 512 B sektory, nejvýš 11 (DD) nebo 18 (HD) na stopu, do 84 stop.") : StandardGeometry.Limits;
    public DiskGeometry FormatTargetFor(DiskGeometry image) => _lowLevel ? image : StandardGeometry.FormatTargetFor(image);

    private DiskGeometry G => _disk.FormattedAs ?? throw new SectorIOException(Tr.T("disketa není naformátovaná"));

    public byte[] ReadSectors(long lba, int count)
    {
        var g = G;
        Thread.Sleep(Math.Max(3, 30 * count / g.SectorsPerTrack));
        for (long l = lba; l < lba + count; l++)
        {
            if (_disk.Unreadable.Contains(l)) { Thread.Sleep(25); throw new SectorIOException(Tr.T("CRC chyba")); }
            if (_disk.Flaky.TryGetValue(l, out int n) && n > 0) { _disk.Flaky[l] = n - 1; Thread.Sleep(25); throw new SectorIOException(Tr.T("CRC chyba")); }
        }
        var buf = new byte[count * 512];
        Buffer.BlockCopy(_disk.Data, (int)lba * 512, buf, 0, buf.Length);
        return buf;
    }

    public void WriteSectors(long lba, byte[] buffer, int offset, int count)
    {
        var g = G;
        Thread.Sleep(Math.Max(3, 40 * count / g.SectorsPerTrack));
        for (long l = lba; l < lba + count; l++)
            if (_disk.Unreadable.Contains(l)) throw new SectorIOException(Tr.T("zápis selhal"));
        Buffer.BlockCopy(buffer, offset, _disk.Data, (int)lba * 512, count * 512);
    }

    public void FormatCylinder(DiskGeometry target, int cylinder)
    {
        if (!SupportsGeometry(target)) throw new SectorIOException(GeometryLimits);
        if (target.IsHighDensity != _disk.Hd) throw new SectorIOException(Tr.T("řadič odmítl rychlost ") + (target.IsHighDensity ? "500 kbit/s" : "250 kbit/s"));
        Thread.Sleep(45);
        if (cylinder == 0) { _disk.FormattedAs = target; _disk.Flaky.Clear(); }
        int len = target.Sides * target.SectorsPerTrack * 512;
        for (int i = 0; i < len; i++) _disk.Data[cylinder * len + i] = 0xE5;
    }
}

/// <summary>Simulovaný řadič pro ukázku diagnostiky: otáčky kolem 300,6, slabá stopa 22, nestandardní stopa 79.</summary>
internal sealed class DemoFdc : AtariFloppy.Core.Drives.Fdraw.IFdc
{
    private readonly Random _rnd = new(3);
    private int _cyl;
    public string Description => Tr.T("simulovaný řadič");
    public void SetDataRate(AtariFloppy.Core.Drives.Fdraw.DataRate rate) { }
    public void SetStepRate(int milliseconds, AtariFloppy.Core.Drives.Fdraw.DataRate rate) { }
    public void Seek(int cylinder) { Thread.Sleep(Math.Abs(cylinder - _cyl) * 3 + 5); _cyl = cylinder; }
    public bool? CheckDisk() => true;

    private List<AtariFloppy.Core.Drives.Fdraw.SectorId> Ids(int head)
    {
        var list = new List<AtariFloppy.Core.Drives.Fdraw.SectorId>();
        if (_cyl > 81) return list;
        for (int s = 1; s <= 9; s++) list.Add(new((byte)_cyl, (byte)head, (byte)s, 2));
        if (_cyl == 79 && head == 0) { list.Insert(4, new(79, 0, 5, 2)); list.Add(new(66, 0, 1, 3)); }   // „ochrana“: duplicitní sektor a cizí hlavička
        return list;
    }

    public List<AtariFloppy.Core.Drives.Fdraw.SectorId> ScanTrack(int head) => Ids(head);

    public byte[] Read(int cyl, int head, int firstSector, int sizeCode, int count)
    {
        Thread.Sleep(22 * count);
        if (cyl == 22 && head == 0 && firstSector == 6 && _rnd.Next(3) == 0) throw new SectorIOException(Tr.T("CRC chyba"));
        if (cyl == 22 && head == 0 && firstSector == 2 && _rnd.Next(5) == 0) throw new SectorIOException(Tr.T("CRC chyba"));
        return new byte[count * (128 << sizeCode)];
    }

    public void Write(int cyl, int head, int firstSector, int sizeCode, int count, byte[] data, int offset) => Thread.Sleep(30 * count);
    public void Format(int cyl, int head, int sizeCode, IList<AtariFloppy.Core.Drives.Fdraw.SectorId> ids, int gap3, byte fill) => Thread.Sleep(200);

    public int? TrackTimeUs()
    {
        Thread.Sleep(200);
        return (int)(60_000_000 / (300.6 + (_rnd.NextDouble() - 0.5) * 0.3));
    }

    public AtariFloppy.Core.Drives.Fdraw.TimedScan? TimedScanTrack(int head)
    {
        Thread.Sleep(30);
        var ids = Ids(head);
        int start = 3_100 + _cyl * 2_000 % 20_000;
        return new(199_600, ids.Select((id, i) => (start + i * 21_500, id)).ToList());
    }

    public byte? DriveStatus(int head) =>
        (byte)(AtariFloppy.Core.Drives.Fdraw.St3.Ready | AtariFloppy.Core.Drives.Fdraw.St3.TwoSide | (_cyl == 0 ? AtariFloppy.Core.Drives.Fdraw.St3.Track0 : 0));

    public void Motor(bool on) { }
    public void Dispose() { }
}
