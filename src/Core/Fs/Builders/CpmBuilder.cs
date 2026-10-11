using System;

namespace AtariFloppy.Core.Fs.Builders;

/// <summary>
/// Prázdná datová disketa Amstrad PCW 9512+/9256 (CP/M Plus / LocoScript), formát CF2DD 720 KB:
/// 80 stop × 2 strany × 9 sektorů, 1 rezervovaná stopa, bloky 2 KB, 256 položek adresáře.
/// Sektor 0 nese 16bajtový popis disku (Amstrad „disc specification“), vše ostatní je 0xE5 (prázdný adresář CP/M).
/// </summary>
public static class CpmBuilder
{
    public static readonly byte[] PcwCf2ddSpec =
    {
        0x03,       // typ: PCW oboustranná DD
        0x81,       // strany střídavě, dvojitá hustota stop
        0x50,       // 80 stop na stranu
        0x09,       // 9 sektorů na stopu
        0x02,       // sektor 512 B (128 << 2)
        0x01,       // 1 rezervovaná stopa
        0x04,       // blok 2 KB (128 << 4)
        0x04,       // adresář zabírá 4 bloky
        0x2A,       // mezera čtení/zápisu
        0x52,       // mezera formátu
        0, 0, 0, 0, 0,
        0x00,       // kontrolní bajt (0 = nezaváděcí disketa)
    };

    public static DiskImage CreatePcw720()
    {
        var img = new DiskImage(DiskGeometry.Dd720);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = 0xE5;
        Buffer.BlockCopy(PcwCf2ddSpec, 0, img.Data, 0, PcwCf2ddSpec.Length);
        return img;
    }

    /// <summary>Je v sektoru 0 platný popis disku Amstrad (PCW / CPC / Spectrum +3)?</summary>
    public static bool HasAmstradSpec(byte[] d) =>
        d.Length >= 16 && d[0] <= 3 && (d[1] & 0x7C) == 0 && d[2] is 40 or 80 && d[3] is >= 8 and <= 10 && d[4] == 2 && d[6] is >= 2 and <= 5;
}
