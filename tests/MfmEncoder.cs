using System;
using System.Collections.Generic;

namespace AtariFloppy.Tests;

/// <summary>Zakóduje sektory IBM MFM do toku (mezery v µs) — k testům vlastního dekodéru MfmDecoder.</summary>
internal static class MfmEncoder
{
    public static List<double> Encode(IEnumerable<(int c, int h, int r, byte[] data)> sectors, double cellUs, bool corruptData = false)
    {
        var bytes = new List<(byte b, bool sync)>();
        void Gap(int n, byte v) { for (int i = 0; i < n; i++) bytes.Add((v, false)); }
        void Sync() { Gap(12, 0); for (int i = 0; i < 3; i++) bytes.Add((0xA1, true)); }
        void Field(byte[] f) { var all = new byte[f.Length + 2]; Buffer.BlockCopy(f, 0, all, 0, f.Length); int c = Crc(all, f.Length); all[f.Length] = (byte)(c >> 8); all[f.Length + 1] = (byte)c; foreach (byte x in all) bytes.Add((x, false)); }
        Gap(80, 0x4E);
        foreach (var (c, h, r, data) in sectors)
        {
            Sync(); Field(new byte[] { 0xFE, (byte)c, (byte)h, (byte)r, 2 });
            Gap(22, 0x4E);
            Sync();
            var f = new byte[data.Length + 1]; f[0] = 0xFB; Buffer.BlockCopy(data, 0, f, 1, data.Length);
            Field(f);
            if (corruptData) { var last = bytes[bytes.Count - 100]; bytes[bytes.Count - 100] = ((byte)(last.b ^ 0x10), false); }
            Gap(84, 0x4E);
        }
        // MFM bity → mezery
        var flux = new List<double>();
        int prev = 0, run = 0;
        foreach (var (b, sync) in bytes)
            for (int k = 7; k >= 0; k--)
            {
                int d = (b >> k) & 1;
                int clk = d == 0 && prev == 0 ? 1 : 0;
                if (sync && b == 0xA1 && k == 2) clk = 0;    // chybějící hodinový bit = 0x4489
                foreach (int bit in new[] { clk, d })
                {
                    run++;
                    if (bit == 1) { flux.Add(run * cellUs); run = 0; }
                }
                prev = d;
            }
        return flux;
    }

    private static int Crc(byte[] bytes, int count)
    {
        int crc = 0xCDB4;                                   // CRC po A1 A1 A1 z výchozí 0xFFFF
        for (int i = 0; i < count; i++)
        {
            crc ^= bytes[i] << 8;
            for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }
}
