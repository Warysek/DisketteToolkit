using System;
using System.Collections.Generic;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs;

public sealed record FatEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime? Modified,
    int FirstCluster,
    byte Attributes)
{
    public bool IsHidden => (Attributes & 0x02) != 0;
    public bool IsSystem => (Attributes & 0x04) != 0;
    public bool IsReadOnly => (Attributes & 0x01) != 0;
}

/// <summary>Čtení souborového systému FAT12 uvnitř image (Atari TOS i MS-DOS).</summary>
public sealed class Fat12Volume
{
    private readonly byte[] _disk;
    private readonly BootSector _bs;
    private readonly int _fatOffset;
    private readonly int _rootOffset;
    private readonly int _dataOffset;
    private readonly int _clusterSize;
    private readonly int _clusterCount;

    public BootSector BootSector => _bs;
    public int ClusterSize => _clusterSize;
    public int ClusterCount => _clusterCount;

    private Fat12Volume(byte[] disk, BootSector bs)
    {
        _disk = disk;
        _bs = bs;
        _fatOffset = bs.ReservedSectors * 512;
        _rootOffset = (bs.ReservedSectors + bs.FatCount * bs.SectorsPerFat) * 512;
        _dataOffset = _rootOffset + bs.RootEntries * 32;
        _clusterSize = bs.SectorsPerCluster * 512;
        int totalBytes = Math.Min(bs.TotalSectors * 512, disk.Length);
        _clusterCount = Math.Max(0, (totalBytes - _dataOffset) / _clusterSize);
    }

    public static Fat12Volume? TryOpen(byte[] disk, out string? error)
    {
        error = null;
        if (disk.Length < 512) { error = Tr.T("Image je příliš malý."); return null; }
        var bs = new BootSector(disk);
        if (!bs.BpbLooksValid)
        {
            error = Tr.T("Boot sektor neobsahuje platné údaje o souborovém systému (disketa může být chráněná, nestandardní nebo nenaformátovaná).");
            return null;
        }
        var v = new Fat12Volume(disk, bs);
        if (v._dataOffset >= disk.Length) { error = Tr.T("Souborový systém přesahuje velikost image."); return null; }
        return v;
    }

    /// <summary>Další cluster v řetězci (12bitová FAT). ≥ 0xFF8 = konec souboru.</summary>
    public int NextCluster(int cluster)
    {
        int o = _fatOffset + cluster * 3 / 2;
        if (o + 1 >= _disk.Length) return 0xFFF;
        int v = _disk[o] | (_disk[o + 1] << 8);
        return (cluster & 1) != 0 ? v >> 4 : v & 0xFFF;
    }

    public bool IsClusterUsed(int cluster) => NextCluster(cluster) != 0;

    public int UsedClusters
    {
        get
        {
            int n = 0;
            for (int c = 2; c < _clusterCount + 2; c++) if (IsClusterUsed(c)) n++;
            return n;
        }
    }

    public long FreeBytes => (long)(_clusterCount - UsedClusters) * _clusterSize;
    public long CapacityBytes => (long)_clusterCount * _clusterSize;

    public List<FatEntry> ListAll()
    {
        var result = new List<FatEntry>();
        var visited = new HashSet<int>();
        ReadDirectory(ReadRootDirectory(), "", result, visited, 0);
        return result;
    }

    public string? GetVolumeLabel()
    {
        var root = ReadRootDirectory();
        for (int i = 0; i + 32 <= root.Length; i += 32)
        {
            if (root[i] == 0) break;
            if (root[i] == 0xE5) continue;
            if ((root[i + 11] & 0x08) != 0 && root[i + 11] != 0x0F)
                return Encoding.ASCII.GetString(root, i, 11).TrimEnd();
        }
        return null;
    }

    private byte[] ReadRootDirectory()
    {
        int len = Math.Min(_bs.RootEntries * 32, _disk.Length - _rootOffset);
        var buf = new byte[Math.Max(0, len)];
        Buffer.BlockCopy(_disk, _rootOffset, buf, 0, buf.Length);
        return buf;
    }

    private void ReadDirectory(byte[] dir, string parent, List<FatEntry> result, HashSet<int> visited, int depth)
    {
        if (depth > 16) return;
        for (int i = 0; i + 32 <= dir.Length; i += 32)
        {
            byte first = dir[i];
            if (first == 0x00) break;
            if (first == 0xE5) continue;
            byte attr = dir[i + 11];
            if (attr == 0x0F) continue;            // dlouhé jméno (VFAT)
            if ((attr & 0x08) != 0) continue;      // jmenovka svazku
            string name = DecodeName(dir, i);
            if (name == "." || name == "..") continue;

            int cluster = dir[i + 26] | (dir[i + 27] << 8);
            long size = (uint)BitConverter.ToInt32(dir, i + 28);
            bool isDir = (attr & 0x10) != 0;
            string path = parent.Length == 0 ? name : parent + "\\" + name;
            result.Add(new FatEntry(path, name, isDir, isDir ? 0 : size, DecodeDateTime(dir, i + 22), cluster, attr));

            if (isDir && cluster >= 2 && visited.Add(cluster))
                ReadDirectory(ReadChain(cluster, -1), path, result, visited, depth + 1);
        }
    }

    public byte[] ReadFile(FatEntry entry) => ReadChain(entry.FirstCluster, entry.Size);

    /// <summary>Přečte řetězec clusterů. maxBytes &lt; 0 = celý řetězec.</summary>
    private byte[] ReadChain(int cluster, long maxBytes)
    {
        var output = new List<byte>();
        var seen = new HashSet<int>();
        while (cluster >= 2 && cluster < 0xFF0 && seen.Add(cluster))
        {
            int off = _dataOffset + (cluster - 2) * _clusterSize;
            if (off + _clusterSize > _disk.Length) break;
            for (int k = 0; k < _clusterSize; k++) output.Add(_disk[off + k]);
            if (maxBytes >= 0 && output.Count >= maxBytes) break;
            cluster = NextCluster(cluster);
        }
        if (maxBytes >= 0 && output.Count > maxBytes) output.RemoveRange((int)maxBytes, output.Count - (int)maxBytes);
        return output.ToArray();
    }

    private static string DecodeName(byte[] d, int i)
    {
        string baseName = Latin(d, i, 8).TrimEnd();
        string ext = Latin(d, i + 8, 3).TrimEnd();
        if (d[i] == 0x05) baseName = "å" + baseName.Substring(1);
        return ext.Length > 0 ? baseName + "." + ext : baseName;
    }

    private static string Latin(byte[] d, int o, int n)
    {
        var sb = new StringBuilder(n);
        for (int k = 0; k < n; k++)
        {
            byte c = d[o + k];
            sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '_');
        }
        return sb.ToString();
    }

    private static DateTime? DecodeDateTime(byte[] d, int o)
    {
        int time = d[o] | (d[o + 1] << 8);
        int date = d[o + 2] | (d[o + 3] << 8);
        if (date == 0) return null;
        int y = 1980 + (date >> 9), m = (date >> 5) & 15, day = date & 31;
        int h = time >> 11, min = (time >> 5) & 63, s = (time & 31) * 2;
        try { return new DateTime(y, m, day, Math.Min(h, 23), Math.Min(min, 59), Math.Min(s, 59)); }
        catch { return null; }
    }
}
