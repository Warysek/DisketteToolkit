using System;
using System.Collections.Generic;

namespace AtariFloppy.Core.Formats;

/// <summary>Sektor nalezený v magnetickém toku: číslo z hlavičky (C/H/R/N), data a jestli sedí kontrolní součet dat.</summary>
public sealed record DecodedSector(int Cyl, int Head, int Sector, int SizeCode, bool Ok, byte[] Data);

/// <summary>
/// Vlastní převod magnetického toku na sektory IBM MFM (PC, Atari ST, MSX…): jednoduché PLL rozdělí mezery
/// mezi změnami na bitové buňky, pak se hledá synchronizace A1 A1 A1 (0x4489), hlavička 0xFE a data 0xFB/0xF8
/// a kontroluje CRC. Ověřeno na skutečných .scp z Greaseweazle (zdravá i poškozená HD disketa, 2 mechaniky).
/// </summary>
public static class MfmDecoder
{
    /// <summary>
    /// Nastavení PLL (gain = jak rychle se dolaďuje délka buňky, phase = kolik chyby se přenese do další mezery).
    /// První je výchozí; ostatní se zkoušejí jen na stopách, kde sektory chybí — na poškozené disketě
    /// tak přibylo 10–14 sektorů na mechaniku.
    /// </summary>
    public static readonly (double gain, double phase)[] Variants =
    {
        (0.05, 0.0), (0.05, 0.6), (0.1, 0.6), (0.02, 0.3), (0.05, 0.3), (0.1, 0.3), (0.02, 0.6), (0.02, 0.0), (0.0, 0.6),
    };

    public static List<DecodedSector> Decode(IReadOnlyList<double> fluxUs, double cellUs, double gain, double phase)
    {
        var bits = Bits(fluxUs, cellUs, gain, phase);
        var result = new List<DecodedSector>();
        (int c, int h, int r, int n)? id = null;
        ulong sr = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            sr = (sr << 1) | bits[i];
            if ((sr & 0xFFFF_FFFF_FFFF) != 0x4489_4489_4489) continue;
            int j = i + 1;                                   // první bit značky za A1 A1 A1
            if (j + 16 > bits.Length) break;
            int mark = ByteAt(bits, j);
            if (mark == 0xFE && j + 16 * 7 <= bits.Length)
            {
                var hdr = new byte[7];
                for (int k = 0; k < 7; k++) hdr[k] = (byte)ByteAt(bits, j + 16 * k);
                id = Crc(hdr) == 0 ? (hdr[1], hdr[2], hdr[3], hdr[4] & 7) : null;
            }
            else if ((mark == 0xFB || mark == 0xF8) && id is { } s)
            {
                int size = 128 << s.n;
                if (j + 16 * (size + 3) <= bits.Length)
                {
                    var dd = new byte[size + 3];
                    for (int k = 0; k < dd.Length; k++) dd[k] = (byte)ByteAt(bits, j + 16 * k);
                    var data = new byte[size];
                    Buffer.BlockCopy(dd, 1, data, 0, size);
                    result.Add(new DecodedSector(s.c, s.h, s.r, s.n, Crc(dd) == 0, data));
                }
                id = null;
            }
            sr = 0;
        }
        return result;
    }

    private static byte[] Bits(IReadOnlyList<double> flux, double cell, double gain, double phase)
    {
        var out_ = new List<byte>(flux.Count * 3);
        double p = cell, err = 0;
        foreach (double f in flux)
        {
            double x = f + err;
            int n = (int)Math.Round(x / p);
            if (n < 1) n = 1; else if (n > 6) n = 6;
            for (int k = 1; k < n; k++) out_.Add(0);
            out_.Add(1);
            double e = x - n * p;
            err = e * phase;
            p += gain * e / n;
            p = Math.Min(Math.Max(p, cell * 0.85), cell * 1.15);
        }
        return out_.ToArray();
    }

    /// <summary>Datový bajt z 16 bitů MFM (každý druhý bit je hodinový).</summary>
    private static int ByteAt(byte[] b, int i)
    {
        int v = 0;
        for (int k = 0; k < 8; k++) v = (v << 1) | b[i + 2 * k + 1];
        return v;
    }

    /// <summary>CRC-CCITT přes A1 A1 A1 + bajty; 0 = v pořádku (bajty obsahují i CRC).</summary>
    private static int Crc(byte[] bytes)
    {
        int crc = 0xCDB4;                                   // CRC po A1 A1 A1 z výchozí 0xFFFF
        foreach (byte x in bytes)
        {
            crc ^= x << 8;
            for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }
}
