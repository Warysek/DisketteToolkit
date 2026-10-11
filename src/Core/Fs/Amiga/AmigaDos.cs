using System;
using System.Collections.Generic;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs.Amiga;

/// <summary>Varianta souborového systému AmigaDOS (bajt 3 bootbloku "DOS\x").</summary>
public enum AmigaFsType
{
    Ofs = 0,
    Ffs = 1,
    OfsIntl = 2,
    FfsIntl = 3,
    OfsDirCache = 4,
    FfsDirCache = 5,
}

public sealed record AmigaEntry(string Path, string Name, bool IsDirectory, long Size, DateTime? Modified, int HeaderBlock);

/// <summary>
/// AmigaDOS OFS/FFS (ADF): vytvoření prázdné diskety a čtení souborů.
/// Bloky po 512 B, čísla big-endian, kořenový blok uprostřed disku (880 u DD, 1760 u HD).
/// </summary>
public static class AmigaDos
{
    private const int BlockSize = 512;
    private const int HashSize = 72;
    private static readonly DateTime Epoch = new(1978, 1, 1);

    public static bool IsFfs(AmigaFsType t) => ((int)t & 1) == 1;

    // ------------------------------------------------------------------ tvorba

    public static DiskImage CreateBlank(DiskGeometry g, AmigaFsType type, string? name, DateTime? now = null)
    {
        if (g.BytesPerSector != 512) throw new ArgumentException(Tr.T("Amiga používá bloky 512 B."));
        var img = new DiskImage(g);
        var d = img.Data;
        int blocks = g.TotalSectors;
        int root = blocks / 2;
        int bitmap = root + 1;
        string vol = VolumeName(name);
        var t = now ?? DateTime.Now;

        // bootblok: "DOS" + typ, kontrolní součet 0 (disketa není bootovací), ukazatel na kořen
        d[0] = (byte)'D'; d[1] = (byte)'O'; d[2] = (byte)'S'; d[3] = (byte)type;
        Be32(d, 8, root);

        // kořenový blok
        int r = root * BlockSize;
        Be32(d, r + 0, 2);                 // T_HEADER
        Be32(d, r + 12, HashSize);
        Be32(d, r + 312, 0xFFFFFFFF);      // bitmapa platná
        Be32(d, r + 316, bitmap);
        WriteDate(d, r + 420, t);          // poslední změna kořene
        d[r + 432] = (byte)vol.Length;
        Encoding.ASCII.GetBytes(vol, 0, vol.Length, d, r + 433);
        WriteDate(d, r + 472, t);          // poslední změna disku
        WriteDate(d, r + 484, t);          // vytvoření
        Be32(d, r + 508, 1);               // ST_ROOT
        Be32(d, r + 20, Checksum(d, r, 20));

        // bitmapa: 1 = volný blok; bit 0 = blok 2
        int b = bitmap * BlockSize;
        for (int i = 4; i < BlockSize; i++) d[b + i] = 0xFF;
        foreach (int used in new[] { root, bitmap }) ClearBit(d, b, used - 2);
        Be32(d, b, Checksum(d, b, 0));
        return img;
    }

    private static void ClearBit(byte[] d, int bitmapOffset, int bit)
    {
        int o = bitmapOffset + 4 + (bit / 32) * 4;
        uint v = U32(d, o);
        v &= ~(1u << (bit % 32));
        Be32(d, o, v);
    }

    // ------------------------------------------------------------------ spustitelná disketa

    /// <summary>Standardní bootblok (jako příkaz „install“) pro Kickstart 2.0 a novější; funguje i na 1.3.</summary>
    private static readonly byte[] Boot2x = Hex("43fa003e70254eaefdd84a80670c224008e9000600224eaefe6243fa00184eaeffa04a80670a20402068001670004e7570ff4e75646f732e6c69627261727900657870616e73696f6e2e6c69627261727900");
    /// <summary>Bootblok z Kickstartu 1.x (install z Workbench 1.3).</summary>
    private static readonly byte[] Boot1x = Hex("43fa00184eaeffa04a80670a20402068001670004e7570ff60fa646f732e6c69627261727900");

    private static byte[] Hex(string s)
    {
        s = s.Replace(" ", "");
        var b = new byte[s.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return b;
    }

    /// <summary>Udělá z diskety spustitelnou (bootblok s kódem + kontrolní součet), jako „install df0:“.</summary>
    public static void InstallBootblock(byte[] d, bool kickstart13 = false)
    {
        if (d.Length < 1024 || d[0] != 'D' || d[1] != 'O' || d[2] != 'S')
            throw new Drives.OperationException(Tr.T("Obraz nemá souborový systém AmigaDOS."));
        Array.Clear(d, 12, 1024 - 12);
        var code = kickstart13 ? Boot1x : Boot2x;
        Buffer.BlockCopy(code, 0, d, 12, code.Length);
        Be32(d, 4, BootChecksum(d));
    }

    /// <summary>Kontrolní součet bootbloku (1024 B, sčítání s přenosem, výsledek negovaný).</summary>
    public static uint BootChecksum(byte[] d)
    {
        uint sum = 0;
        for (int i = 0; i < 1024; i += 4)
        {
            if (i == 4) continue;
            uint v = U32(d, i);
            uint n = sum + v;
            if (n < sum) n++;
            sum = n;
        }
        return ~sum;
    }

    public static bool IsBootable(byte[] d) =>
        d.Length >= 1024 && d[0] == 'D' && d[1] == 'O' && d[2] == 'S' && d[12] != 0 && U32(d, 4) == BootChecksum(d);

    /// <summary>Kontrolní součet bloku: záporný součet všech 32bitových slov (pole součtu se počítá jako 0).</summary>
    public static uint Checksum(byte[] d, int blockOffset, int checksumOffset)
    {
        uint sum = 0;
        for (int i = 0; i < BlockSize; i += 4)
            if (i != checksumOffset) sum += U32(d, blockOffset + i);
        return (uint)-(int)sum;
    }

    public static string VolumeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (sb.Length == 30) break;
            if (c == ':' || c == '/') { sb.Append('_'); continue; }
            string n = c.ToString().Normalize(NormalizationForm.FormD);
            char ch = n.Length > 0 ? n[0] : '_';
            sb.Append(ch >= 0x20 && ch < 0x7F ? ch : '_');
        }
        return sb.Length == 0 ? "Empty" : sb.ToString();
    }

    // ------------------------------------------------------------------ čtení

    public sealed class Volume
    {
        private readonly byte[] _d;
        public AmigaFsType Type { get; }
        public int RootBlock { get; }
        public string Name { get; }
        public int Blocks { get; }

        internal Volume(byte[] d, AmigaFsType type, int root, string name)
        {
            _d = d; Type = type; RootBlock = root; Name = name; Blocks = d.Length / BlockSize;
        }

        public bool Ffs => IsFfs(Type);

        public List<AmigaEntry> ListAll()
        {
            var list = new List<AmigaEntry>();
            ListDir(RootBlock, "", list, new HashSet<int>(), 0);
            return list;
        }

        private void ListDir(int dirBlock, string parent, List<AmigaEntry> list, HashSet<int> seen, int depth)
        {
            if (depth > 32 || !seen.Add(dirBlock)) return;
            int dOff = dirBlock * BlockSize;
            var children = new List<(string name, int block, bool dir)>();
            for (int h = 0; h < HashSize; h++)
            {
                int blk = (int)U32(_d, dOff + 24 + h * 4);
                int guard = 0;
                while (blk > 1 && blk < Blocks && guard++ < 1000)
                {
                    int o = blk * BlockSize;
                    if (U32(_d, o) != 2) break;
                    int sec = (int)U32(_d, o + 508);
                    string name = Latin1(_d, o + 433, Math.Min((int)_d[o + 432], 30));
                    children.Add((name, blk, sec == 2));
                    blk = (int)U32(_d, o + 496);
                }
            }
            children.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            foreach (var (name, blk, dir) in children)
            {
                int o = blk * BlockSize;
                string path = parent.Length == 0 ? name : parent + "/" + name;
                long size = dir ? 0 : U32(_d, o + 324);
                list.Add(new AmigaEntry(path, name, dir, size, ReadDate(_d, o + 420), blk));
                if (dir) ListDir(blk, path, list, seen, depth + 1);
            }
        }

        public byte[] ReadFile(AmigaEntry e)
        {
            int o = e.HeaderBlock * BlockSize;
            long size = U32(_d, o + 324);
            var output = new List<byte>((int)Math.Min(size, 2_000_000));

            if (!Ffs)
            {
                // OFS: datové bloky mají hlavičku 24 B a řetězí se přes next_data
                int blk = (int)U32(_d, o + 16);
                int guard = 0;
                while (blk > 1 && blk < Blocks && output.Count < size && guard++ < Blocks)
                {
                    int b = blk * BlockSize;
                    int len = (int)Math.Min(U32(_d, b + 12), 488);
                    for (int k = 0; k < len && output.Count < size; k++) output.Add(_d[b + 24 + k]);
                    blk = (int)U32(_d, b + 16);
                }
                return output.ToArray();
            }

            // FFS: seznam datových bloků v hlavičce (odzadu), pokračuje v rozšiřujících blocích
            int hdr = o;
            int guard2 = 0;
            while (output.Count < size && guard2++ < 1000)
            {
                int count = (int)Math.Min(U32(_d, hdr + 8), HashSize);
                for (int i = 0; i < count && output.Count < size; i++)
                {
                    int blk = (int)U32(_d, hdr + 24 + (HashSize - 1 - i) * 4);
                    if (blk <= 1 || blk >= Blocks) break;
                    int b = blk * BlockSize;
                    int n = (int)Math.Min(BlockSize, size - output.Count);
                    for (int k = 0; k < n; k++) output.Add(_d[b + k]);
                }
                int ext = (int)U32(_d, hdr + 504);
                if (ext <= 1 || ext >= Blocks) break;
                hdr = ext * BlockSize;
            }
            return output.ToArray();
        }

        /// <summary>Počet volných bloků podle bitmapy (jen první bitmapový blok — stačí pro diskety).</summary>
        public int FreeBlocks
        {
            get
            {
                int r = RootBlock * BlockSize;
                int bm = (int)U32(_d, r + 316);
                if (bm <= 1 || bm >= Blocks) return 0;
                int free = 0;
                for (int blk = 2; blk < Blocks; blk++)
                {
                    int bit = blk - 2;
                    uint v = U32(_d, bm * BlockSize + 4 + (bit / 32) * 4);
                    if ((v & (1u << (bit % 32))) != 0) free++;
                }
                return free;
            }
        }
    }

    public static Volume? TryOpen(byte[] d)
    {
        if (d.Length < 1024 || d[0] != 'D' || d[1] != 'O' || d[2] != 'S' || d[3] > 7) return null;
        int root = d.Length / BlockSize / 2;
        int r = root * BlockSize;
        if (r + BlockSize > d.Length || U32(d, r) != 2 || U32(d, r + 508) != 1) return null;
        string name = Latin1(d, r + 433, Math.Min((int)d[r + 432], 30));
        return new Volume(d, (AmigaFsType)Math.Min((int)d[3], 5), root, name);
    }

    // ------------------------------------------------------------------ pomocné

    private static void WriteDate(byte[] d, int o, DateTime t)
    {
        var span = t - Epoch;
        Be32(d, o, (int)span.TotalDays);
        Be32(d, o + 4, t.Hour * 60 + t.Minute);
        Be32(d, o + 8, t.Second * 50);
    }

    private static DateTime? ReadDate(byte[] d, int o)
    {
        uint days = U32(d, o), mins = U32(d, o + 4), ticks = U32(d, o + 8);
        if (days == 0 || days > 60000 || mins >= 1440) return null;
        return Epoch.AddDays(days).AddMinutes(mins).AddSeconds(ticks / 50.0);
    }

    internal static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void Be32(byte[] d, int o, long v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
    private static string Latin1(byte[] d, int o, int n) => Encoding.GetEncoding(28591).GetString(d, o, Math.Max(0, n));
}
