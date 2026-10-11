using System;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs;

/// <summary>
/// Boot sektor diskety (prvních 512 B). Atari TOS i MS-DOS používají stejný BPB
/// (BIOS Parameter Block) na offsetu 0x0B, liší se zbytek.
/// </summary>
public sealed class BootSector
{
    private readonly byte[] _b = new byte[512];

    public BootSector(byte[] source, int offset = 0)
    {
        Buffer.BlockCopy(source, offset, _b, 0, Math.Min(512, source.Length - offset));
    }

    public byte[] Bytes => (byte[])_b.Clone();

    private int U16(int o) => _b[o] | (_b[o + 1] << 8);

    public int BytesPerSector => U16(0x0B);
    public int SectorsPerCluster => _b[0x0D];
    public int ReservedSectors => U16(0x0E);
    public int FatCount => _b[0x10];
    public int RootEntries => U16(0x11);
    public int TotalSectors => U16(0x13);
    public int MediaDescriptor => _b[0x15];
    public int SectorsPerFat => U16(0x16);
    public int SectorsPerTrack => U16(0x18);
    public int Heads => U16(0x1A);
    public int HiddenSectors => U16(0x1C);

    /// <summary>Atari "sériové číslo" (bajty 8–10), podle něj TOS pozná výměnu diskety.</summary>
    public int AtariSerial => _b[8] | (_b[9] << 8) | (_b[10] << 16);

    public string OemText
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 2; i < 11; i++)
                sb.Append(_b[i] >= 0x20 && _b[i] < 0x7F ? (char)_b[i] : '.');
            return sb.ToString();
        }
    }

    public bool HasPcSignature => _b[510] == 0x55 && _b[511] == 0xAA;
    public bool HasPcJump => _b[0] == 0xE9 || (_b[0] == 0xEB && _b[2] == 0x90);
    public bool HasExtendedBpb => _b[0x26] == 0x29;

    public string VolumeLabel => HasExtendedBpb ? Encoding.ASCII.GetString(_b, 0x2B, 11).TrimEnd() : "";

    /// <summary>Součet 256 slov (big-endian). Hodnota 0x1234 = Atari boot sektor je spustitelný.</summary>
    public int AtariChecksum => ComputeChecksum(_b);

    public bool IsExecutable => AtariChecksum == 0x1234;

    public static int ComputeChecksum(byte[] b, int offset = 0)
    {
        int sum = 0;
        for (int i = 0; i < 512; i += 2)
            sum = (sum + ((b[offset + i] << 8) | b[offset + i + 1])) & 0xFFFF;
        return sum;
    }

    /// <summary>Zajistí, aby boot sektor NEBYL spustitelný (součet ≠ 0x1234). Upraví bajt, který nic neznamená.</summary>
    public static void EnsureNotExecutable(byte[] b, int adjustOffset = 0x1FC)
    {
        if (ComputeChecksum(b) == 0x1234) b[adjustOffset]++;
    }

    /// <summary>Upraví poslední slovo tak, aby byl boot sektor spustitelný na Atari.</summary>
    public static void MakeExecutable(byte[] b)
    {
        b[510] = 0; b[511] = 0;
        int sum = ComputeChecksum(b);
        int fix = (0x1234 - sum) & 0xFFFF;
        b[510] = (byte)(fix >> 8);
        b[511] = (byte)fix;
    }

    public bool BpbLooksValid =>
        BytesPerSector == 512 &&
        SectorsPerCluster is 1 or 2 or 4 or 8 &&
        ReservedSectors >= 1 && ReservedSectors < 16 &&
        FatCount is 1 or 2 &&
        RootEntries > 0 && RootEntries % 16 == 0 && RootEntries <= 512 &&
        TotalSectors > 0 &&
        SectorsPerFat > 0 && SectorsPerFat < 32 &&
        SectorsPerTrack is >= 1 and <= 24 &&
        Heads is 1 or 2;

    /// <summary>Styl boot sektoru — kdo disketu naformátoval.</summary>
    public string DescribeStyle()
    {
        if (!BpbLooksValid) return Tr.T("neznámý / bez platného BPB (nestandardní nebo chráněná disketa)");
        bool atariOem = _b[0] == 0xE9 && _b[1] == 0x00 && _b[2] == 0x4E;
        if (atariOem) return "Atari TOS";
        if (_b[0] == 0x60) return Tr.T("Atari (68000 skok BRA)");
        if (HasPcJump && HasPcSignature && OemText.Substring(1).StartsWith("ATARI")) return Tr.T("hybrid (PC boot sektor + Atari sériové číslo) — čte Atari i Windows");
        if (HasPcJump && HasPcSignature) return Tr.T("PC (MS-DOS / Windows) — čte Atari i Windows");
        if (HasPcJump) return Tr.T("PC-kompatibilní");
        return Tr.T("Atari / jiný");
    }

    /// <summary>Co by na disketu řekly Windows.</summary>
    public bool WindowsLikelyReadable => BpbLooksValid && HasPcJump && HasPcSignature;
}
