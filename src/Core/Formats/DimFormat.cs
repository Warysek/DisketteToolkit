using System;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>
/// .DIM — vlastní formát programu FastCopy Pro. 32B hlavička + sektory v pořadí .ST.
///   0x00 "BB" (0x42 0x42), 0x02 1 = geometrie zjištěna automaticky, 0x03 0 = všechny sektory / 1 = jen obsazené,
///   0x06 strany-1, 0x08 sektorů na stopu, 0x0A první stopa, 0x0C poslední stopa, 0x0D 0 = DD / 1 = HD,
///   0x0E kopie BPB (18 B).
/// </summary>
public static class DimFormat
{
    public const int HeaderSize = 32;

    public static bool IsDim(byte[] b) => b.Length > HeaderSize && b[0] == 0x42 && b[1] == 0x42;

    public static DiskImage Load(byte[] f)
    {
        if (!IsDim(f)) throw new FormatException(Tr.T("Soubor nemá hlavičku DIM."));
        if (f[3] != 0)
            throw new FormatException(Tr.T("Tento DIM obsahuje jen obsazené sektory (volba FastCopy „Get Sectors: used“). Takový DIM zatím neumím otevřít."));
        if (f[0x0A] != 0)
            throw new FormatException(Tr.T("DIM nezačíná stopou 0 — takový obraz zatím neumím otevřít."));

        int sides = f[0x06] + 1, spt = f[0x08], tracks = f[0x0C] + 1;
        var data = new byte[f.Length - HeaderSize];
        Buffer.BlockCopy(f, HeaderSize, data, 0, data.Length);

        DiskGeometry g;
        if (sides is 1 or 2 && spt is >= 1 and <= 24 && tracks is >= 1 and <= 86)
            g = new DiskGeometry(tracks, sides, spt);
        else
            g = StFormat.GuessGeometry(data) ?? throw new FormatException(Tr.T("Nepodařilo se určit geometrii DIM obrazu."));

        if (data.Length < g.SizeBytes)
            throw new FormatException(Tr.F("DIM je kratší ({0} B), než udává hlavička ({1} B).", data.Length, g.SizeBytes));
        return new DiskImage(g, data);
    }

    public static byte[] Save(DiskImage img)
    {
        var g = img.Geometry;
        var f = new byte[HeaderSize + img.Data.Length];
        f[0] = f[1] = 0x42;
        f[2] = 1;
        f[3] = 0;
        f[6] = (byte)(g.Sides - 1);
        f[8] = (byte)g.SectorsPerTrack;
        f[0x0A] = 0;
        f[0x0C] = (byte)(g.Tracks - 1);
        f[0x0D] = (byte)(g.IsHighDensity ? 1 : 0);
        Buffer.BlockCopy(img.Data, 0x0B, f, 0x0E, 18);
        Buffer.BlockCopy(img.Data, 0, f, HeaderSize, img.Data.Length);
        return f;
    }
}
