using System;
using static AtariFloppy.Core.Fs.Builders.Bytes;

namespace AtariFloppy.Core.Fs.Builders;

/// <summary>
/// Prázdný souborový systém MINIX v1 (bloky 1 KB, jména 14 znaků — magic 0x137F), jako MINIX 1.x/1.5.
/// PC verze je little-endian; Atari ST (MINIX-ST, 68000) ukládá všechna čísla big-endian.
/// </summary>
public static class MinixBuilder
{
    public static DiskImage Create(DiskGeometry g, bool bigEndian, DateTime? now = null)
    {
        var img = new DiskImage(g);
        var d = img.Data;
        bool be = bigEndian;
        int blocks = (int)(g.SizeBytes / 1024);
        int inodes = RoundUp(blocks / 3, 32);            // 32 inodů na blok
        int imapBlocks = (inodes + 1 + 8191) / 8192;
        int zmapBlocks = (blocks + 8191) / 8192;
        int inodeBlocks = inodes * 32 / 1024;
        int firstData = 2 + imapBlocks + zmapBlocks + inodeBlocks;
        uint time = (uint)(((now ?? DateTime.Now).ToUniversalTime() - new DateTime(1970, 1, 1)).TotalSeconds);

        // superblok (blok 1)
        int sb = 1024;
        W16(d, sb + 0, inodes, be);
        W16(d, sb + 2, blocks, be);
        W16(d, sb + 4, imapBlocks, be);
        W16(d, sb + 6, zmapBlocks, be);
        W16(d, sb + 8, firstData, be);
        W16(d, sb + 10, 0, be);                          // log2(zóna/blok)
        W32(d, sb + 12, (7 + 512 + 512L * 512) * 1024, be);
        W16(d, sb + 16, 0x137F, be);
        W16(d, sb + 18, 1, be);                          // stav: čistý

        // bitmapa inodů: bit 0 rezervovaný, bit 1 = kořen, bity za posledním inodem obsazené
        int imap = 2 * 1024;
        for (int bit = 0; bit < imapBlocks * 8192; bit++)
            if (bit <= 1 || bit > inodes) SetBit(d, imap, bit, be);

        // bitmapa zón: bit 0 rezervovaný, bit 1 = první datová zóna (kořenový adresář), zbytek za koncem disku
        int zmap = (2 + imapBlocks) * 1024;
        int dataZones = blocks - firstData;
        for (int bit = 0; bit < zmapBlocks * 8192; bit++)
            if (bit <= 1 || bit > dataZones) SetBit(d, zmap, bit, be);

        // inode 1 = kořenový adresář
        int ino = (2 + imapBlocks + zmapBlocks) * 1024;
        W16(d, ino + 0, 0x41ED, be);                    // adresář, rwxr-xr-x
        W16(d, ino + 2, 0, be);
        W32(d, ino + 4, 32, be);                         // 2 položky po 16 B
        W32(d, ino + 8, time, be);
        d[ino + 12] = 0;                                 // gid
        d[ino + 13] = 2;                                 // odkazy
        W16(d, ino + 14, firstData, be);

        // obsah kořene: "." a ".."
        int dir = firstData * 1024;
        W16(d, dir, 1, be); d[dir + 2] = (byte)'.';
        W16(d, dir + 16, 1, be); d[dir + 18] = (byte)'.'; d[dir + 19] = (byte)'.';
        return img;
    }

    /// <summary>MINIX bitmapy jsou pole 16bitových slov — na 68000 se bajty ve slově prohodí.</summary>
    private static void SetBit(byte[] d, int offset, int bit, bool be)
    {
        int word = bit / 16, b = bit % 16;
        int byteInWord = b / 8;
        if (be) byteInWord ^= 1;
        d[offset + word * 2 + byteInWord] |= (byte)(1 << (b % 8));
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;
}
