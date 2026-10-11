using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs;

/// <summary>
/// Zápis souborů a složek do FAT12 obrazu (Atari i MS-DOS): jména 8.3, obě kopie FAT,
/// kořenový adresář i podsložky. Používá se pro spouštěcí diskety (KERNEL.SYS, AUTO\PROGRAM.PRG).
/// </summary>
public sealed class Fat12Writer
{
    private readonly byte[] _d;
    private readonly int _fatOffset, _fatBytes, _fats, _rootOffset, _rootEntries, _dataOffset, _clusterSize, _clusters;

    public Fat12Writer(byte[] disk)
    {
        var bs = new BootSector(disk);
        if (!bs.BpbLooksValid) throw new OperationException(Tr.T("Obraz nemá platný souborový systém FAT12."));
        _d = disk;
        _fatOffset = bs.ReservedSectors * 512;
        _fatBytes = bs.SectorsPerFat * 512;
        _fats = bs.FatCount;
        _rootOffset = _fatOffset + _fats * _fatBytes;
        _rootEntries = bs.RootEntries;
        _dataOffset = _rootOffset + _rootEntries * 32;
        _clusterSize = bs.SectorsPerCluster * 512;
        int total = Math.Min(bs.TotalSectors * 512, disk.Length);
        _clusters = (total - _dataOffset) / _clusterSize;
    }

    public long FreeBytes => (long)Enumerable.Range(2, _clusters).Count(c => GetFat(c) == 0) * _clusterSize;

    /// <summary>Zapíše soubor. Cesta s „\“ nebo „/“ — chybějící složky se vytvoří (AUTO\HRA.PRG).</summary>
    public void AddFile(string path, byte[] data, DateTime? modified = null, byte attributes = 0x20)
    {
        var parts = path.Split('\\', '/').Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) throw new ArgumentException(path);
        var t = modified ?? DateTime.Now;
        int dirCluster = 0; // 0 = kořen
        for (int i = 0; i < parts.Length - 1; i++) dirCluster = EnsureDirectory(dirCluster, parts[i], t);

        var name = ShortName(parts[parts.Length - 1]);
        if (FindEntry(dirCluster, name) >= 0) throw new OperationException(Tr.F("Soubor {0} už na disketě je.", path));
        int clusters = (data.Length + _clusterSize - 1) / _clusterSize;
        var chain = Allocate(clusters);
        for (int i = 0; i < chain.Count; i++)
        {
            int off = DataOffset(chain[i]);
            int n = Math.Min(_clusterSize, data.Length - i * _clusterSize);
            Array.Clear(_d, off, _clusterSize);
            Buffer.BlockCopy(data, i * _clusterSize, _d, off, n);
        }
        int e = NewEntry(dirCluster, t);
        WriteEntry(e, name, attributes, chain.Count > 0 ? chain[0] : 0, data.Length, t);
    }

    /// <summary>Vytvoří složku (když už existuje, nic se nestane); vrací její první cluster.</summary>
    public int CreateDirectory(string path, DateTime? modified = null)
    {
        var t = modified ?? DateTime.Now;
        int dir = 0;
        foreach (var p in path.Split('\\', '/').Where(p => p.Length > 0)) dir = EnsureDirectory(dir, p, t);
        return dir;
    }

    // ------------------------------------------------------------------ adresáře

    private int EnsureDirectory(int parent, string name, DateTime t)
    {
        var sn = ShortName(name);
        int existing = FindEntry(parent, sn);
        if (existing >= 0)
        {
            if ((_d[existing + 11] & 0x10) == 0) throw new OperationException(Tr.F("{0} už na disketě je jako soubor.", name));
            return _d[existing + 26] | (_d[existing + 27] << 8);
        }
        int c = Allocate(1)[0];
        int off = DataOffset(c);
        Array.Clear(_d, off, _clusterSize);
        WriteEntry(off, Encoding.ASCII.GetBytes(".          "), 0x10, c, 0, t);
        WriteEntry(off + 32, Encoding.ASCII.GetBytes("..         "), 0x10, parent, 0, t);
        int e = NewEntry(parent, t);
        WriteEntry(e, sn, 0x10, c, 0, t);
        return c;
    }

    /// <summary>Všechny 32bajtové položky adresáře (offsety v obrazu).</summary>
    private IEnumerable<int> Slots(int dirCluster)
    {
        if (dirCluster == 0)
        {
            for (int i = 0; i < _rootEntries; i++) yield return _rootOffset + i * 32;
            yield break;
        }
        var seen = new HashSet<int>();
        for (int c = dirCluster; c >= 2 && c < 0xFF0 && seen.Add(c); c = GetFat(c))
        {
            int off = DataOffset(c);
            for (int i = 0; i < _clusterSize; i += 32) yield return off + i;
        }
    }

    private int FindEntry(int dirCluster, byte[] name)
    {
        foreach (int e in Slots(dirCluster))
        {
            if (_d[e] == 0) break;
            if (_d[e] == 0xE5 || _d[e + 11] == 0x0F || (_d[e + 11] & 0x08) != 0) continue;
            bool same = true;
            for (int k = 0; k < 11 && same; k++) same = _d[e + k] == name[k];
            if (same) return e;
        }
        return -1;
    }

    private int NewEntry(int dirCluster, DateTime t)
    {
        foreach (int e in Slots(dirCluster))
            if (_d[e] == 0 || _d[e] == 0xE5) return e;
        if (dirCluster == 0) throw new OperationException(Tr.T("Kořenový adresář diskety je plný."));
        // podsložka: přidáme další cluster
        int last = dirCluster;
        while (GetFat(last) is int n && n >= 2 && n < 0xFF0) last = n;
        int c = Allocate(1)[0];
        SetFat(last, c);
        Array.Clear(_d, DataOffset(c), _clusterSize);
        return DataOffset(c);
    }

    private void WriteEntry(int e, byte[] name, byte attr, int cluster, int size, DateTime t)
    {
        Array.Clear(_d, e, 32);
        Buffer.BlockCopy(name, 0, _d, e, 11);
        _d[e + 11] = attr;
        int time = (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2);
        int date = (Math.Max(0, t.Year - 1980) << 9) | (t.Month << 5) | t.Day;
        _d[e + 22] = (byte)time; _d[e + 23] = (byte)(time >> 8);
        _d[e + 24] = (byte)date; _d[e + 25] = (byte)(date >> 8);
        _d[e + 26] = (byte)cluster; _d[e + 27] = (byte)(cluster >> 8);
        BitConverter.GetBytes(size).CopyTo(_d, e + 28);
    }

    /// <summary>Jméno ve tvaru 8.3 velkými písmeny; neplatné znaky nahradí „_“.</summary>
    public static byte[] ShortName(string name)
    {
        string n = name.Trim().ToUpperInvariant();
        int dot = n.LastIndexOf('.');
        string b = dot > 0 ? n.Substring(0, dot) : n, x = dot > 0 ? n.Substring(dot + 1) : "";
        string Clean(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD))
            {
                if (sb.Length == max) break;
                if (c > 0x7F && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c >= 0x21 && c < 0x7F && "\"*+,./:;<=>?[\\]|".IndexOf(c) < 0 ? c : '_');
            }
            return sb.ToString();
        }
        b = Clean(b, 8); x = Clean(x, 3);
        if (b.Length == 0) b = "_";
        return Encoding.ASCII.GetBytes(b.PadRight(8) + x.PadRight(3));
    }

    // ------------------------------------------------------------------ FAT

    private int DataOffset(int cluster) => _dataOffset + (cluster - 2) * _clusterSize;

    private int GetFat(int c) => FatTable.Get(_d, _fatOffset, c);

    private void SetFat(int c, int v)
    {
        for (int f = 0; f < _fats; f++) FatTable.Set(_d, _fatOffset + f * _fatBytes, c, v);
    }

    private List<int> Allocate(int count)
    {
        var list = new List<int>();
        for (int c = 2; c < _clusters + 2 && list.Count < count; c++)
            if (GetFat(c) == 0) list.Add(c);
        if (list.Count < count) throw new OperationException(Tr.T("Na disketě není dost místa."));
        for (int i = 0; i < list.Count; i++) SetFat(list[i], i == list.Count - 1 ? FatTable.EndOfChain : list[i + 1]);
        return list;
    }
}
