using System;
using System.Text;
using static AtariFloppy.Core.Fs.Builders.Bytes;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs.Builders;

/// <summary>
/// Prázdný Linux ext2 (revize 1, bez rozšíření, bloky 1 KB, jedna skupina) s adresářem lost+found —
/// čitelný jakýmkoli Linuxem od 90. let.
/// </summary>
public static class Ext2Builder
{
    public static DiskImage Create(DiskGeometry g, string? label, DateTime? now = null, Random? rng = null)
    {
        var img = new DiskImage(g);
        var d = img.Data;
        rng ??= new Random();
        int blocks = (int)(g.SizeBytes / 1024);
        if (blocks > 8193) throw new ArgumentException(Tr.T("ext2 builder umí jen diskety (jedna skupina bloků)."));
        int inodes = Math.Max(16, blocks / 8 / 8 * 8);                 // 1440 bloků → 184 inodů jako mke2fs
        int inodeTableBlocks = (inodes * 128 + 1023) / 1024;
        const int gdBlock = 2, bbBlock = 3, ibBlock = 4, itBlock = 5;
        int rootBlock = itBlock + inodeTableBlocks;
        int lfBlock = rootBlock + 1;
        int usedBlocks = lfBlock;                                       // bloky 1..lfBlock obsazené
        int freeBlocks = blocks - 1 - usedBlocks;
        int freeInodes = inodes - 11;
        uint t = (uint)(((now ?? DateTime.Now).ToUniversalTime() - new DateTime(1970, 1, 1)).TotalSeconds);

        // --- superblok (offset 1024)
        int s = 1024;
        Le32(d, s + 0, inodes);
        Le32(d, s + 4, blocks);
        Le32(d, s + 8, blocks / 20);            // 5 % rezerva pro roota
        Le32(d, s + 12, freeBlocks);
        Le32(d, s + 16, freeInodes);
        Le32(d, s + 20, 1);                     // první datový blok
        Le32(d, s + 24, 0);                     // 1 KB bloky
        Le32(d, s + 28, 0);
        Le32(d, s + 32, 8192);
        Le32(d, s + 36, 8192);
        Le32(d, s + 40, inodes);
        Le32(d, s + 48, t);                     // čas zápisu
        Le16(d, s + 52, 0);
        Le16(d, s + 54, 0xFFFF);                // max. počet připojení (-1)
        Le16(d, s + 56, 0xEF53);
        Le16(d, s + 58, 1);                     // čistý
        Le16(d, s + 60, 1);                     // při chybě pokračovat
        Le32(d, s + 64, t);                     // poslední kontrola
        Le32(d, s + 76, 1);                     // revize 1
        Le32(d, s + 84, 11);                    // první běžný inode
        Le16(d, s + 88, 128);                   // velikost inodu
        var uuid = new byte[16];
        rng.NextBytes(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x40); uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        Buffer.BlockCopy(uuid, 0, d, s + 104, 16);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var lb = Encoding.ASCII.GetBytes(label!.Trim());
            Buffer.BlockCopy(lb, 0, d, s + 120, Math.Min(16, lb.Length));
        }

        // --- popisovač skupiny
        int gd = gdBlock * 1024;
        Le32(d, gd + 0, bbBlock);
        Le32(d, gd + 4, ibBlock);
        Le32(d, gd + 8, itBlock);
        Le16(d, gd + 12, freeBlocks);
        Le16(d, gd + 14, freeInodes);
        Le16(d, gd + 16, 2);                    // adresáře: kořen + lost+found

        // --- bitmapa bloků: bit i = blok (1 + i); bity za koncem disku obsazené
        int bb = bbBlock * 1024;
        for (int bit = 0; bit < 8192; bit++)
            if (bit < usedBlocks || bit >= blocks - 1) d[bb + bit / 8] |= (byte)(1 << (bit % 8));

        // --- bitmapa inodů: inody 1–11 obsazené; bity za posledním inodem obsazené
        int ib = ibBlock * 1024;
        for (int bit = 0; bit < 8192; bit++)
            if (bit < 11 || bit >= inodes) d[ib + bit / 8] |= (byte)(1 << (bit % 8));

        // --- inody
        int it = itBlock * 1024;
        WriteDirInode(d, it + (2 - 1) * 128, 0x41ED, 3, rootBlock, t);   // kořen: ., .., lost+found/..
        WriteDirInode(d, it + (11 - 1) * 128, 0x41C0, 2, lfBlock, t);    // lost+found (rwx------)

        // --- kořenový adresář
        int r = rootBlock * 1024;
        DirEntry(d, r, 2, 12, ".");
        DirEntry(d, r + 12, 2, 12, "..");
        DirEntry(d, r + 24, 11, 1000, "lost+found");
        // --- lost+found
        int l = lfBlock * 1024;
        DirEntry(d, l, 11, 12, ".");
        DirEntry(d, l + 12, 2, 1012, "..");
        return img;
    }

    private static void WriteDirInode(byte[] d, int o, int mode, int links, int block, uint t)
    {
        Le16(d, o + 0, mode);
        Le32(d, o + 4, 1024);                   // velikost
        Le32(d, o + 8, t); Le32(d, o + 12, t); Le32(d, o + 16, t);
        Le16(d, o + 26, links);
        Le32(d, o + 28, 2);                     // sektory po 512 B
        Le32(d, o + 40, block);
    }

    private static void DirEntry(byte[] d, int o, int inode, int recLen, string name)
    {
        Le32(d, o, inode);
        Le16(d, o + 4, recLen);
        Le16(d, o + 6, name.Length);            // bez rozšíření filetype je délka jména 16bitová
        Encoding.ASCII.GetBytes(name, 0, name.Length, d, o + 8);
    }
}
