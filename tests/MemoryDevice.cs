using System;
using System.Collections.Generic;
using AtariFloppy.Core;
using AtariFloppy.Core.Drives;

namespace AtariFloppy.Tests;

/// <summary>Simulovaná mechanika v paměti: chová se jako USB disketovka (umí jen 720 KB a 1,44 MB).</summary>
internal sealed class MemoryDevice : ISectorDevice
{
    public byte[] Disk;
    public DiskGeometry? Formatted;          // null = nenaformátovaná
    public bool Present = true;
    public bool WriteProtected;
    public bool Locked;
    public HashSet<long> UnreadableSectors = new();   // trvale vadné
    public HashSet<long> SilentlyCorrupt = new();     // zápis "projde", ale uloží špatná data
    public Dictionary<long, int> FlakySectors = new(); // selže N-krát, pak se přečte
    public int FormatCalls;

    public MemoryDevice(DiskImage? content = null)
    {
        Disk = new byte[DiskGeometry.Hd1440.SizeBytes];
        if (content != null)
        {
            Buffer.BlockCopy(content.Data, 0, Disk, 0, content.Data.Length);
            Formatted = content.Geometry.IsHighDensity ? DiskGeometry.Hd1440 : DiskGeometry.Dd720;
        }
    }

    public string Name => "MEM:";
    public DiskGeometry? MediaGeometry => Present ? Formatted : null;
    public MediaState MediaState => !Present ? MediaState.NoDisk : Formatted == null ? MediaState.Unrecognized : MediaState.Ready;
    public string? MediaStateMessage => MediaState.ToString();
    public void RefreshMedia() { }
    public void BeginExclusive() => Locked = true;
    public void EndExclusive() => Locked = false;

    public byte[] ReadSectors(long lba, int count)
    {
        Check();
        for (long l = lba; l < lba + count; l++)
        {
            if (UnreadableSectors.Contains(l)) throw new SectorIOException("CRC chyba", 23);
            if (FlakySectors.TryGetValue(l, out int n) && n > 0) { FlakySectors[l] = n - 1; throw new SectorIOException("dočasná chyba", 23); }
        }
        var r = new byte[count * 512];
        Buffer.BlockCopy(Disk, (int)(lba * 512), r, 0, r.Length);
        return r;
    }

    public void WriteSectors(long lba, byte[] buffer, int offset, int count)
    {
        Check();
        if (WriteProtected) throw new OperationException("Disketa je chráněná proti zápisu.");
        if (!Locked) throw new InvalidOperationException("Zápis bez zamčení jednotky!");
        Buffer.BlockCopy(buffer, offset, Disk, (int)(lba * 512), count * 512);
        for (long l = lba; l < lba + count; l++)
            if (SilentlyCorrupt.Contains(l)) Disk[l * 512 + 7] ^= 0xFF;
    }

    public void FormatCylinder(DiskGeometry target, int cylinder)
    {
        if (!Present) throw new OperationException("V mechanice není disketa.");
        if (WriteProtected) throw new OperationException("Disketa je chráněná proti zápisu.");
        if (!Locked) throw new InvalidOperationException("Formát bez zamčení jednotky!");
        FormatCalls++;
        long start = target.Offset(cylinder, 0);
        for (long i = start; i < start + target.TrackSize * target.Sides; i++) Disk[i] = 0xF6;
        Formatted = target;
    }

    private void Check()
    {
        if (!Present) throw new OperationException("V mechanice není disketa.");
        if (Formatted == null) throw new SectorIOException("nerozpoznané médium", 1785);
    }

    public bool SupportsGeometry(DiskGeometry g) => StandardGeometry.Supports(g);
    public string GeometryLimits => StandardGeometry.Limits;
    public DiskGeometry FormatTargetFor(DiskGeometry image) => StandardGeometry.FormatTargetFor(image);

    public void Dispose() { }
}
