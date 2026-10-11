using System;
using System.Collections.Generic;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>
/// .MSA (Magic Shadow Archiver): hlavička 10 B (big-endian) + stopy, každá volitelně komprimovaná RLE.
///   0x0E0F, sektorů na stopu, strany-1, první stopa, poslední stopa
///   pro každou stopu/stranu: délka (u16) + data. Délka == velikost stopy → nekomprimováno.
///   RLE: E5 &lt;bajt&gt; &lt;počet u16&gt;. Samotný bajt E5 v datech se musí zakódovat jako běh.
/// </summary>
public static class MsaFormat
{
    public const int Magic = 0x0E0F;

    public static bool IsMsa(byte[] b) => b.Length >= 10 && ((b[0] << 8) | b[1]) == Magic;

    public static DiskImage Load(byte[] f)
    {
        if (!IsMsa(f)) throw new FormatException(Tr.T("Soubor nemá hlavičku MSA."));
        int spt = Be16(f, 2), sides = Be16(f, 4) + 1, start = Be16(f, 6), end = Be16(f, 8);
        if (spt < 1 || spt > 24 || sides < 1 || sides > 2 || end < start || end > 85)
            throw new FormatException(Tr.F("Neplatná hlavička MSA (sektory {0}, strany {1}, stopy {2}–{3}).", spt, sides, start, end));

        var g = new DiskGeometry(end + 1, sides, spt);
        var img = new DiskImage(g);
        int trackSize = g.TrackSize;
        int pos = 10;
        for (int t = start; t <= end; t++)
            for (int s = 0; s < sides; s++)
            {
                if (pos + 2 > f.Length) throw new FormatException(Tr.F("MSA soubor je useknutý (stopa {0}, strana {1}).", t, s));
                int len = Be16(f, pos); pos += 2;
                if (pos + len > f.Length) throw new FormatException(Tr.F("MSA soubor je useknutý (stopa {0}, strana {1}).", t, s));
                var track = new byte[trackSize];
                if (len == trackSize)
                    Buffer.BlockCopy(f, pos, track, 0, trackSize);
                else
                    DecodeRle(f, pos, len, track, t, s);
                img.WriteTrack(t, s, track);
                pos += len;
            }
        return img;
    }

    private static void DecodeRle(byte[] f, int pos, int len, byte[] track, int t, int s)
    {
        int end = pos + len, o = 0;
        while (pos < end && o < track.Length)
        {
            byte b = f[pos++];
            if (b != 0xE5) { track[o++] = b; continue; }
            if (pos + 3 > end) throw new FormatException(Tr.F("Poškozená RLE data v MSA (stopa {0}, strana {1}).", t, s));
            byte v = f[pos++];
            int n = Be16(f, pos); pos += 2;
            if (o + n > track.Length) throw new FormatException(Tr.F("RLE běh přesahuje stopu (stopa {0}, strana {1}).", t, s));
            for (int k = 0; k < n; k++) track[o++] = v;
        }
        if (o != track.Length) throw new FormatException(Tr.F("Stopa v MSA má po rozbalení špatnou délku (stopa {0}, strana {1}).", t, s));
    }

    public static byte[] Save(DiskImage img)
    {
        var g = img.Geometry;
        var output = new List<byte>((int)g.SizeBytes / 2);
        W16(output, Magic);
        W16(output, g.SectorsPerTrack);
        W16(output, g.Sides - 1);
        W16(output, 0);
        W16(output, g.Tracks - 1);
        for (int t = 0; t < g.Tracks; t++)
            for (int s = 0; s < g.Sides; s++)
            {
                var track = img.ReadTrack(t, s);
                var packed = EncodeRle(track);
                if (packed.Count < track.Length)
                {
                    W16(output, packed.Count);
                    output.AddRange(packed);
                }
                else
                {
                    W16(output, track.Length);
                    output.AddRange(track);
                }
            }
        return output.ToArray();
    }

    private static List<byte> EncodeRle(byte[] track)
    {
        var o = new List<byte>(track.Length);
        int i = 0;
        while (i < track.Length)
        {
            byte v = track[i];
            int run = 1;
            while (i + run < track.Length && track[i + run] == v && run < 0xFFFF) run++;
            if (run >= 4 || v == 0xE5)
            {
                o.Add(0xE5); o.Add(v); o.Add((byte)(run >> 8)); o.Add((byte)run);
                i += run;
            }
            else
            {
                o.Add(v);
                i++;
            }
        }
        return o;
    }

    private static int Be16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static void W16(List<byte> o, int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
}
