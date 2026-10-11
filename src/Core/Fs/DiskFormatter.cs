using System;
using System.Text;

namespace AtariFloppy.Core.Fs;

public enum FormatStyle
{
    /// <summary>Jako TOS desktop: E9 00 "NNNNNN", sériové číslo, 5 sektorů FAT, bez 55AA.</summary>
    Atari,
    /// <summary>Jako Windows/MS-DOS: EB 3C 90 "MSDOS5.0", rozšířený BPB, 55AA.</summary>
    Pc,
    /// <summary>PC boot sektor (Windows ho přečte) + náhodné Atari sériové číslo (TOS pozná výměnu disket).</summary>
    Hybrid,
    /// <summary>MSX-DOS: skok EB FE 90, BPB, Z80 kód „RET“ (datová disketa — MSX nastartuje Disk BASIC).</summary>
    Msx,
}

/// <summary>Vytváří prázdný souborový systém FAT12 (boot sektor, 2× FAT, kořenový adresář).</summary>
public static class DiskFormatter
{
    public sealed record Layout(int SectorsPerCluster, int RootEntries, int SectorsPerFat, int Media, int ReservedSectors = 1, int Fats = 2)
    {
        public int RootSectors => RootEntries * 32 / 512;
        /// <summary>Počet sektorů na začátku disku, které nesou souborový systém (boot + FAT + kořen).</summary>
        public int SystemSectors => ReservedSectors + Fats * SectorsPerFat + RootSectors;
    }

    public static Layout ComputeLayout(DiskGeometry g, FormatStyle style)
    {
        int spc, root, media;
        if (g.IsHighDensity) { spc = 1; root = 224; media = 0xF0; }
        else if (g.SectorsPerTrack == 8) { spc = 2; root = 112; media = g.Sides == 2 ? 0xFB : 0xFA; }
        else if (g.Sides == 2) { spc = 2; root = 112; media = 0xF9; }
        else { spc = 2; root = 112; media = 0xF8; }

        int total = g.TotalSectors;
        int rootSecs = root * 32 / 512;
        int spf = 1;
        while (true)
        {
            int data = total - 1 - rootSecs - 2 * spf;
            int clusters = data / spc;
            int needed = ((clusters + 2) * 3 / 2 + 511) / 512;
            if (needed <= spf) break;
            spf++;
        }
        // TOS dává u DD disket vždy 5 sektorů na FAT (i když stačí 3).
        if (style == FormatStyle.Atari && !g.IsHighDensity) spf = Math.Max(spf, 5);
        return new Layout(spc, root, spf, media);
    }

    public static DiskImage CreateBlank(DiskGeometry g, FormatStyle style, string? label = null, Random? rng = null)
    {
        rng ??= new Random();
        var img = new DiskImage(g);
        var boot = BuildBootSector(g, style, label, rng);
        Buffer.BlockCopy(boot, 0, img.Data, 0, 512);

        var layout = ComputeLayout(g, style);
        for (int f = 0; f < layout.Fats; f++)
        {
            int off = (layout.ReservedSectors + f * layout.SectorsPerFat) * 512;
            img.Data[off] = (byte)layout.Media;
            img.Data[off + 1] = 0xFF;
            img.Data[off + 2] = 0xFF;
        }

        string? lbl = NormalizeLabel(label);
        if (lbl != null)
        {
            int rootOff = (layout.ReservedSectors + layout.Fats * layout.SectorsPerFat) * 512;
            Encoding.ASCII.GetBytes(lbl, 0, 11, img.Data, rootOff);
            img.Data[rootOff + 11] = 0x08; // atribut: jmenovka svazku
            WriteDosDateTime(img.Data, rootOff + 22, DateTime.Now);
        }
        return img;
    }

    public static byte[] BuildBootSector(DiskGeometry g, FormatStyle style, string? label, Random rng)
    {
        var b = new byte[512];
        var layout = ComputeLayout(g, style);
        var serial = new byte[3];
        rng.NextBytes(serial);

        switch (style)
        {
            case FormatStyle.Atari:
                b[0] = 0xE9; b[1] = 0x00;
                for (int i = 2; i < 8; i++) b[i] = 0x4E; // "NNNNNN" jako TOS
                break;
            case FormatStyle.Pc:
                b[0] = 0xEB; b[1] = 0x3C; b[2] = 0x90;
                Encoding.ASCII.GetBytes("MSDOS5.0", 0, 8, b, 3);
                break;
            case FormatStyle.Hybrid:
                b[0] = 0xEB; b[1] = 0x3C; b[2] = 0x90;
                Encoding.ASCII.GetBytes("ATARI", 0, 5, b, 3);
                break;
            case FormatStyle.Msx:
                b[0] = 0xEB; b[1] = 0xFE; b[2] = 0x90;
                Encoding.ASCII.GetBytes("MSX_DOS ", 0, 8, b, 3);
                break;
        }
        if (style is FormatStyle.Atari or FormatStyle.Hybrid)
        {
            b[8] = serial[0]; b[9] = serial[1]; b[10] = serial[2];
        }

        W16(b, 0x0B, 512);
        b[0x0D] = (byte)layout.SectorsPerCluster;
        W16(b, 0x0E, layout.ReservedSectors);
        b[0x10] = (byte)layout.Fats;
        W16(b, 0x11, layout.RootEntries);
        W16(b, 0x13, g.TotalSectors);
        b[0x15] = (byte)layout.Media;
        W16(b, 0x16, layout.SectorsPerFat);
        W16(b, 0x18, g.SectorsPerTrack);
        W16(b, 0x1A, g.Sides);
        W16(b, 0x1C, 0);

        if (style != FormatStyle.Atari)
        {
            // Rozšířený BPB (DOS 4+) — Windows ho očekává.
            b[0x24] = 0x00;       // číslo jednotky
            b[0x26] = 0x29;       // podpis rozšířeného BPB
            var volSerial = new byte[4];
            rng.NextBytes(volSerial);
            Buffer.BlockCopy(volSerial, 0, b, 0x27, 4);
            string lbl = NormalizeLabel(label) ?? "NO NAME    ";
            Encoding.ASCII.GetBytes(lbl, 0, 11, b, 0x2B);
            Encoding.ASCII.GetBytes("FAT12   ", 0, 8, b, 0x36);
            // x86 zavaděč: "tato disketa není systémová" → INT 18h (BIOS zkusí další zařízení)
            b[0x3E] = 0xCD; b[0x3F] = 0x18;
            b[510] = 0x55; b[511] = 0xAA;
            if (style == FormatStyle.Msx) b[0x1E] = 0xC9;   // Z80 RET — disketa není systémová, MSX spustí Disk BASIC
        }

        BootSector.EnsureNotExecutable(b);
        return b;
    }

    public static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var sb = new StringBuilder();
        foreach (char c in label!.ToUpperInvariant())
        {
            if (sb.Length == 11) break;
            if (c >= 0x20 && c < 0x7F && "\"*+,./:;<=>?[\\]|".IndexOf(c) < 0) sb.Append(c);
            else if (c < 0x7F) sb.Append('_');
            else sb.Append(RemoveDiacritic(c));
        }
        return sb.ToString().PadRight(11);
    }

    private static char RemoveDiacritic(char c)
    {
        string n = c.ToString().Normalize(NormalizationForm.FormD);
        return n.Length > 0 && n[0] < 0x7F ? n[0] : '_';
    }

    private static void W16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

    internal static void WriteDosDateTime(byte[] b, int o, DateTime t)
    {
        int time = (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2);
        int date = ((Math.Max(1980, t.Year) - 1980) << 9) | (t.Month << 5) | t.Day;
        W16(b, o, time);
        W16(b, o + 2, date);
    }
}
