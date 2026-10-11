using System;
using System.Text;
using static AtariFloppy.Core.Fs.Builders.Bytes;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs.Builders;

/// <summary>
/// Prázdný svazek Mac HFS (Inside Macintosh: Files). Rozložení odpovídá nástroji hformat (hfsutils):
///  sektory 0–1 zaváděcí bloky (prázdné), 2 = Master Directory Block, 3 = bitmapa,
///  od 4 alokační bloky: B-strom rozsahů (extents), B-strom katalogu (kořenová složka + thread),
///  předposlední sektor = záložní MDB.
/// </summary>
public static class HfsBuilder
{
    public static DiskImage Create(DiskGeometry g, string? volumeName, DateTime? now = null)
    {
        int sectors = g.TotalSectors;
        if (sectors < 1600) throw new ArgumentException(Tr.T("HFS potřebuje aspoň 800 KB."));
        var img = new DiskImage(g);
        var d = img.Data;
        string name = MacName(volumeName);
        uint date = MacDate(now ?? DateTime.Now);

        // Alokační blok = 512 B (1,44 MB), bitmapa od sektoru 3, alokační bloky od sektoru 3 + velikost bitmapy.
        int alBlkSiz = 512 * Math.Max(1, (sectors - 6) / 65535 + 1);
        int secPerBlk = alBlkSiz / 512;
        int vbmSectors = 1;
        int alBlSt = 3 + vbmSectors;
        int nmAlBlks = (sectors - alBlSt - 2) / secPerBlk;
        while ((nmAlBlks + 4095) / 4096 > vbmSectors)
        {
            vbmSectors++;
            alBlSt = 3 + vbmSectors;
            nmAlBlks = (sectors - alBlSt - 2) / secPerBlk;
        }
        int treeBlocks = Math.Max(4, nmAlBlks / 128);   // jako hformat: 2874 / 128 = 22
        int treeBytes = treeBlocks * alBlkSiz;
        int nodeSize = 512;
        int nodes = treeBytes / nodeSize;
        int xtStart = 0, ctStart = treeBlocks;
        int used = 2 * treeBlocks;

        // --- MDB
        var m = new byte[162];
        Be16(m, 0, 0x4244);
        Be32(m, 2, date);
        Be32(m, 6, date);
        Be16(m, 10, 0x0100);          // svazek řádně odpojen
        Be16(m, 12, 0);               // souborů v kořeni
        Be16(m, 14, 3);               // bitmapa
        Be16(m, 16, used);            // kde začít hledat volné místo
        Be16(m, 18, nmAlBlks);
        Be32(m, 20, alBlkSiz);
        Be32(m, 24, 4 * alBlkSiz);    // clump
        Be16(m, 28, alBlSt);
        Be32(m, 30, 16);              // další volné ID
        Be16(m, 34, nmAlBlks - used);
        m[36] = (byte)name.Length;
        Encoding.GetEncoding(28591).GetBytes(name, 0, name.Length, m, 37);
        Be32(m, 74, treeBytes);       // clump extents
        Be32(m, 78, treeBytes);       // clump katalog
        Be32(m, 130, treeBytes);      // velikost souboru extents
        Be16(m, 134, xtStart); Be16(m, 136, treeBlocks);
        Be32(m, 146, treeBytes);      // velikost katalogu
        Be16(m, 150, ctStart); Be16(m, 152, treeBlocks);
        Buffer.BlockCopy(m, 0, d, 2 * 512, m.Length);
        Buffer.BlockCopy(m, 0, d, (sectors - 2) * 512, m.Length);

        // --- bitmapa obsazených alokačních bloků
        for (int i = 0; i < used; i++) d[3 * 512 + i / 8] |= (byte)(0x80 >> (i % 8));

        // --- B-strom rozsahů: jen hlavička, prázdný
        int xtOff = (alBlSt + xtStart * secPerBlk) * 512;
        WriteHeaderNode(d, xtOff, nodeSize, depth: 0, root: 0, leafRecords: 0, firstLeaf: 0, lastLeaf: 0,
                        maxKeyLen: 7, totalNodes: nodes, freeNodes: nodes - 1, usedNodes: 1);

        // --- B-strom katalogu: hlavička + jeden list se složkou a threadem
        int ctOff = (alBlSt + ctStart * secPerBlk) * 512;
        WriteHeaderNode(d, ctOff, nodeSize, depth: 1, root: 1, leafRecords: 2, firstLeaf: 1, lastLeaf: 1,
                        maxKeyLen: 37, totalNodes: nodes, freeNodes: nodes - 2, usedNodes: 2);
        WriteRootLeaf(d, ctOff + nodeSize, nodeSize, name, date);
        return img;
    }

    private static void WriteHeaderNode(byte[] d, int o, int nodeSize, int depth, int root, int leafRecords, int firstLeaf, int lastLeaf,
                                        int maxKeyLen, int totalNodes, int freeNodes, int usedNodes)
    {
        // popisovač uzlu
        d[o + 8] = 1;                  // typ: hlavička
        Be16(d, o + 10, 3);            // 3 záznamy
        // záznam hlavičky
        int h = o + 14;
        Be16(d, h + 0, depth);
        Be32(d, h + 2, root);
        Be32(d, h + 6, leafRecords);
        Be32(d, h + 10, firstLeaf);
        Be32(d, h + 14, lastLeaf);
        Be16(d, h + 18, nodeSize);
        Be16(d, h + 20, maxKeyLen);
        Be32(d, h + 22, totalNodes);
        Be32(d, h + 26, freeNodes);
        // mapa uzlů od 0xF8
        for (int i = 0; i < usedNodes; i++) d[o + 0xF8 + i / 8] |= (byte)(0x80 >> (i % 8));
        // odkazy na záznamy od konce uzlu: hlavička 0x0E, uživatelská data 0x78, mapa 0xF8, volné 0x1F8
        Be16(d, o + nodeSize - 2, 0x0E);
        Be16(d, o + nodeSize - 4, 0x78);
        Be16(d, o + nodeSize - 6, 0xF8);
        Be16(d, o + nodeSize - 8, 0x1F8);
    }

    private static void WriteRootLeaf(byte[] d, int o, int nodeSize, string name, uint date)
    {
        d[o + 8] = 0xFF;               // list
        d[o + 9] = 1;                  // výška
        Be16(d, o + 10, 2);
        var latin = Encoding.GetEncoding(28591);

        // záznam 1: klíč (rodič 1, jméno svazku) → složka ID 2
        int p = o + 14;
        int keyLen = 6 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        d[p] = (byte)keyLen;
        Be32(d, p + 2, 1);
        d[p + 6] = (byte)name.Length;
        latin.GetBytes(name, 0, name.Length, d, p + 7);
        int rec = p + 1 + keyLen;
        d[rec] = 1;                    // cdrDirRec
        Be32(d, rec + 6, 2);           // ID složky
        Be32(d, rec + 10, date);
        Be32(d, rec + 14, date);
        int rec1End = rec + 70;

        // záznam 2: thread složky 2 → rodič 1 + jméno
        int q = rec1End;
        d[q] = 7;
        Be32(d, q + 2, 2);
        int th = q + 8;
        d[th] = 3;                     // cdrThdRec
        Be32(d, th + 10, 1);
        d[th + 14] = (byte)name.Length;
        latin.GetBytes(name, 0, name.Length, d, th + 15);
        int end = th + 46;

        Be16(d, o + nodeSize - 2, p - o);
        Be16(d, o + nodeSize - 4, q - o);
        Be16(d, o + nodeSize - 6, end - o);
    }

    /// <summary>Jméno svazku Macu: 1–27 znaků, bez dvojtečky, bez diakritiky mimo Latin-1.</summary>
    public static string MacName(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (sb.Length == 27) break;
            char ch = c == ':' ? '-' : c;
            string n = ch.ToString().Normalize(NormalizationForm.FormD);
            ch = n.Length > 0 ? n[0] : '_';
            sb.Append(ch >= 0x20 && ch < 0x7F ? ch : '_');
        }
        return sb.Length == 0 ? "Untitled" : sb.ToString();
    }

    private static uint MacDate(DateTime t) => (uint)(t - new DateTime(1904, 1, 1)).TotalSeconds;
}
