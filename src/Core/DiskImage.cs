using System;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core;

/// <summary>Obsah diskety v paměti — sektory v pořadí .ST souboru.</summary>
public sealed class DiskImage
{
    public DiskGeometry Geometry { get; }
    public byte[] Data { get; }

    public DiskImage(DiskGeometry geometry, byte[]? data = null)
    {
        Geometry = geometry;
        Data = new byte[geometry.SizeBytes];
        if (data != null)
            Buffer.BlockCopy(data, 0, Data, 0, (int)Math.Min(data.Length, Data.Length));
    }

    public byte[] ReadTrack(int track, int side)
    {
        var buf = new byte[Geometry.TrackSize];
        Buffer.BlockCopy(Data, (int)Geometry.Offset(track, side), buf, 0, buf.Length);
        return buf;
    }

    public void WriteTrack(int track, int side, byte[] buffer)
    {
        if (buffer.Length != Geometry.TrackSize) throw new ArgumentException(Tr.T("Špatná délka stopy."));
        Buffer.BlockCopy(buffer, 0, Data, (int)Geometry.Offset(track, side), buffer.Length);
    }

    public BootSector BootSector => new(Data);
}
