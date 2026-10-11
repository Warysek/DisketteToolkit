using System;
using System.Linq;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Co je na vložené disketě — zjištěno přečtením jen prvních stop (rychlé, disketu nemění).</summary>
public sealed class DiskSummary
{
    public FsDetection Fs { get; init; } = null!;
    public string? Label { get; init; }
    public long? UsedBytes { get; init; }
    public long? FreeBytes { get; init; }
    public int? RootEntries { get; init; }
    public bool WindowsReadable { get; init; }
    public bool BootSectorExecutable { get; init; }
    public bool Bootable { get; init; }
}

public static class DiskProbe
{
    /// <summary>Přečte první dva válce (boot sektor, FAT, kořenový adresář, superbloky) a rozpozná souborový systém.</summary>
    public static DiskSummary Probe(ISectorDevice dev)
    {
        var g = dev.MediaGeometry ?? throw new OperationException(Tr.T("Disketa není připravená."));
        var disk = new byte[g.SizeBytes];
        int sectors = Math.Min(g.TotalSectors, g.SectorsPerTrack * g.Sides * 2);
        for (int s = 0; s < sectors; s += g.SectorsPerTrack)
        {
            int n = Math.Min(g.SectorsPerTrack, sectors - s);
            try
            {
                var buf = dev.ReadSectors(s, n);
                Buffer.BlockCopy(buf, 0, disk, s * g.BytesPerSector, Math.Min(buf.Length, n * g.BytesPerSector));
            }
            catch (SectorIOException) { if (s == 0) throw; }
        }
        var fs = FileSystemDetector.Detect(disk);
        var bs = new BootSector(disk);
        string? label = null; long? used = null, free = null; int? entries = null;
        bool bootable = false;
        if (fs.CanCheck && Fat12Volume.TryOpen(disk, out _) is { } vol)
        {
            label = vol.GetVolumeLabel()?.Trim() ?? (bs.VolumeLabel.Length > 0 && bs.VolumeLabel != "NO NAME" ? bs.VolumeLabel : null);
            free = vol.FreeBytes;
            used = vol.CapacityBytes - vol.FreeBytes;
            var root = vol.ListAll().Where(e => !e.Path.Contains("\\")).ToList();
            entries = root.Count;
            bootable = root.Any(e => e.Name is "IO.SYS" or "IBMBIO.COM" or "KERNEL.SYS") || (fs.Details ?? "").Contains("FreeDOS") || (fs.Details ?? "").Contains("MS-DOS");
        }
        if (fs.Kind == FsKind.Amiga) bootable = Fs.Amiga.AmigaDos.IsBootable(disk);
        return new DiskSummary
        {
            Fs = fs, Label = label, UsedBytes = used, FreeBytes = free, RootEntries = entries,
            WindowsReadable = fs.CanCheck && bs.WindowsLikelyReadable,
            BootSectorExecutable = bs.IsExecutable,
            Bootable = bootable,
        };
    }
}
