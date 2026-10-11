using System;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>.ST = holá kopie sektorů, bez hlavičky. Geometrii je třeba odvodit z boot sektoru nebo z velikosti.</summary>
public static class StFormat
{
    public static DiskImage Load(byte[] bytes)
    {
        var g = GuessGeometry(bytes)
            ?? throw new FormatException(Tr.F("Nepodařilo se určit geometrii .ST souboru (velikost {0} B neodpovídá žádnému známému formátu).", bytes.Length));
        return new DiskImage(g, bytes);
    }

    public static byte[] Save(DiskImage image) => (byte[])image.Data.Clone();

    public static DiskGeometry? GuessGeometry(byte[] bytes)
    {
        long size = bytes.Length;
        if (size < 512 || size % 512 != 0) return null;
        long sectors = size / 512;

        // 1) Boot sektor, pokud dává smysl a sedí s velikostí souboru.
        var bs = new BootSector(bytes);
        if (bs.BpbLooksValid)
        {
            int spt = bs.SectorsPerTrack, sides = bs.Heads;
            if (sectors % (spt * sides) == 0)
            {
                long tracks = sectors / (spt * sides);
                if (tracks >= 1 && tracks <= 86) return new DiskGeometry((int)tracks, sides, spt);
            }
        }

        // 2) Podle velikosti — nejdřív běžné formáty.
        int[] spts = { 9, 10, 11, 18, 19, 20, 21, 8, 12 };
        foreach (int spt in spts)
            foreach (int sides in new[] { 2, 1 })
            {
                if (sectors % (spt * sides) != 0) continue;
                long tracks = sectors / (spt * sides);
                if (tracks >= 78 && tracks <= 86) return new DiskGeometry((int)tracks, sides, spt);
            }
        foreach (int sides in new[] { 2, 1 })
            if (sectors % (9 * sides) == 0 && sectors / (9 * sides) <= 86)
                return new DiskGeometry((int)(sectors / (9 * sides)), sides, 9);
        return null;
    }
}
