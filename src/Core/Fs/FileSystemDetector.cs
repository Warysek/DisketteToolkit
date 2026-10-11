using System;
using System.Linq;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs;

public enum FsKind
{
    Empty,
    FatAtari,
    FatPc,
    FatHybrid,
    FatMsx,
    MacHfs,
    MacHfsPlus,
    MacMfs,
    Amiga,
    Minix,
    Ext2,
    SinclairQl,
    CpmAmstrad,
    OwnLoader,
    Unknown,
}

/// <summary>Co je na disketě za souborový systém a k čemu se používal.</summary>
public sealed record FsDetection(FsKind Kind, string Name, string UsedFor, string? Details = null)
{
    /// <summary>Umí program souborový systém zkontrolovat a opravit?</summary>
    public bool CanCheck => Kind is FsKind.FatAtari or FsKind.FatPc or FsKind.FatHybrid or FsKind.FatMsx;
}

public static class FileSystemDetector
{
    public static FsDetection Detect(byte[] d)
    {
        if (d.Length < 2048) return new(FsKind.Unknown, Tr.T("neznámý"), Tr.T("Obraz je příliš malý."));

        if (IsUniform(d))
            return new(FsKind.Empty, Tr.T("prázdná / nenaformátovaná"), Tr.T("Celá disketa obsahuje stejný bajt (0x") + d[0].ToString("X2") + Tr.T(") — nic na ní není."));

        // --- Amiga: "DOS" + typ na začátku bootbloku
        if (d[0] == 'D' && d[1] == 'O' && d[2] == 'S' && d[3] <= 7)
        {
            string[] variants = { "OFS", "FFS", "OFS INTL", "FFS INTL", "OFS DirCache", "FFS DirCache", "OFS LongNames", "FFS LongNames" };
            string? vol = AmigaVolumeName(d);
            return new(FsKind.Amiga, $"AmigaDOS ({variants[d[3]]})",
                Tr.T("Commodore Amiga (AmigaOS/Workbench), 880 KB DD nebo 1,76 MB HD. OFS je původní systém z AmigaOS 1.x, FFS rychlejší od AmigaOS 2.0. Diskety se v PC ani USB mechanice nedají přečíst (celá stopa je jeden blok) — jen image .ADF nebo Greaseweazle."),
                string.Join(", ", new[] { vol != null ? Tr.F("jméno svazku „{0}“", vol) : null,
                                          Amiga.AmigaDos.IsBootable(d) ? Tr.T("spustitelná (bootblok)") : null }.Where(x => x != null)) is { Length: > 0 } det ? det : null);
        }

        // --- Sinclair QL
        if (Ascii(d, 0, 3) == "QL5" && (d[3] == 'A' || d[3] == 'B'))
            return new(FsKind.SinclairQl, "Sinclair QL (QDOS)",
                Tr.T("Sinclair QL (1984) s disketovým rozhraním (Trump Card, Miracle, Gold Card). QL5A = 720 KB, QL5B = 1,44 MB."),
                Tr.F("jméno svazku „{0}“", Ascii(d, 4, 10).Trim()));

        // --- Amstrad PCW / CPC / Spectrum +3 (CP/M) — popis disku v sektoru 0
        if (Builders.CpmBuilder.HasAmstradSpec(d) && !new BootSector(d).BpbLooksValid)
            return new(FsKind.CpmAmstrad, "CP/M (Amstrad PCW)",
                Tr.T("Amstrad PCW 8256/8512/9512/9256 — textové editory s LocoScriptem a CP/M Plus. 3,5\" 720 KB diskety používají PCW 9512+ a 9256; stejný popis disku má i Amstrad CPC a ZX Spectrum +3 (ty ale na 3\" disketách)."),
                d[0] == 3 ? Tr.T("oboustranná 720 KB (CF2DD)") : null);

        // --- Superbloky na offsetu 1024: Mac, Minix, ext2
        int sig = (d[1024] << 8) | d[1025];
        if (sig == 0x4244)
            return new(FsKind.MacHfs, "Mac HFS",
                Tr.T("Apple Macintosh — HFS (Hierarchical File System), Mac OS od roku 1985 do Mac OS 9. 1,44 MB HD disketu přečte i PC/USB mechanika; 400/800 KB Mac diskety ne (kódování GCR, jen Greaseweazle)."),
                MacVolumeName(d));
        if (sig == 0x482B || sig == 0x4858)
            return new(FsKind.MacHfsPlus, "Mac HFS+",
                Tr.T("Apple Macintosh — HFS+ (Mac OS 8.1 a novější). Na disketách vzácný."));
        if (sig == 0xD2D7)
            return new(FsKind.MacMfs, "Mac MFS",
                Tr.T("Nejstarší Macintosh (1984–1985) — MFS, plochý systém bez složek, 400 KB diskety (GCR)."),
                MacVolumeName(d));

        int minixLe = d[1024 + 16] | (d[1024 + 17] << 8);
        int minixBe = (d[1024 + 16] << 8) | d[1024 + 17];
        if (IsMinix(minixLe) || IsMinix(minixBe))
            return new(FsKind.Minix, "MINIX",
                Tr.T("MINIX — výukový Unix Andrewa Tanenbauma (1987). Běžel na PC i na Atari ST; jeho souborový systém používal i úplně první Linux."),
                IsMinix(minixBe) && !IsMinix(minixLe) ? Tr.T("varianta big-endian (Atari ST / 68000)") : Tr.T("varianta little-endian (PC)"));

        if (d[1024 + 56] == 0x53 && d[1024 + 57] == 0xEF)
        {
            string name = Ascii(d, 1024 + 120, 16).TrimEnd('\0', ' ');
            return new(FsKind.Ext2, "Linux ext2",
                Tr.T("Linux — zaváděcí a záchranné diskety z 90. let (Slackware boot/root, tomsrtbt)."),
                name.Length > 0 ? Tr.F("jméno svazku „{0}“", name) : null);
        }

        // --- FAT12
        var bs = new BootSector(d);
        if (bs.BpbLooksValid)
        {
            string? label = Fat12Volume.TryOpen(d, out _)?.GetVolumeLabel();
            string? details = label != null ? Tr.F("jmenovka „{0}“", label) : null;
            string? system = SystemFiles(d);
            if (system != null) details = details == null ? system : details + ", " + system;

            if (IsMsx(d))
                return new(FsKind.FatMsx, "MSX-DOS (FAT12)",
                    Tr.T("MSX — domácí počítače Sony, Philips, Panasonic a dalších (1983–1990), rozšířené v Japonsku, Nizozemsku, Španělsku, Brazílii a SSSR. Souborový systém je FAT12, takže disketu přečtou i Windows a Atari."), details);
            if (d[0] == 0xEB && d[2] == 0x90 && Ascii(d, 3, 5) == "ATARI")
                return new(FsKind.FatHybrid, "FAT12 hybrid (Atari + PC)",
                    Tr.T("Naformátováno tímto programem: PC boot sektor (čtou Windows) a unikátní Atari sériové číslo (TOS pozná výměnu disket)."), details);
            if ((d[0] == 0xE9 && d[1] == 0x00 && d[2] == 0x4E) || d[0] == 0x60 || !bs.HasPcSignature)
                return new(FsKind.FatAtari, "Atari TOS (FAT12)",
                    Tr.T("Atari ST/STE/TT/Falcon (TOS/GEMDOS). Varianta FAT12 z MS-DOSu — Atari čte i PC diskety, Windows ale Atari disketu někdy odmítnou."), details);
            return new(FsKind.FatPc, "MS-DOS / Windows (FAT12)",
                Tr.T("PC s MS-DOS, Windows 3.x až 11, OS/2. Nejrozšířenější disketový formát — čte ho i Atari, MSX, Amiga (CrossDOS) a Mac (PC Exchange)."), details);
        }

        if (bs.IsExecutable)
            return new(FsKind.OwnLoader, Tr.T("vlastní zavaděč (Atari)"),
                Tr.T("Disketa bez souborového systému se spustitelným boot sektorem — typicky hra nebo demo, které si čte stopy samo. Kontrolovat se dá jen povrch."));

        return new(FsKind.Unknown, Tr.T("neznámý"),
            Tr.T("Souborový systém jsem nepoznal. Může jít o hru s vlastním formátem, CP/M (Amstrad PCW), disketu pro hudební nástroj nebo poškozený boot sektor. Kontrolovat se dá jen povrch."));
    }

    private static bool IsMinix(int m) => m is 0x137F or 0x138F or 0x2468 or 0x2478;

    private static bool IsUniform(byte[] d)
    {
        byte b = d[0];
        for (int i = 0; i < d.Length; i++) if (d[i] != b) return false;
        return true;
    }

    private static bool IsMsx(byte[] d)
    {
        if (d[0] == 0xEB && d[1] == 0xFE && d[2] == 0x90) return true;
        string boot = Encoding.ASCII.GetString(d, 0, 512);
        return boot.Contains("MSX") && (boot.Contains("MSXDOS") || boot.Contains("MSX-DOS") || Ascii(d, 3, 8).Contains("MSX"));
    }

    private static string? SystemFiles(byte[] d)
    {
        string boot = Encoding.ASCII.GetString(d, 0, 512);
        if (boot.Contains("IO      SYS") || boot.Contains("IBMBIO  COM") || boot.Contains("MSDOS   SYS"))
            return Tr.T("systémová (bootovací) disketa MS-DOS");
        if (boot.Contains("NTLDR") || boot.Contains("BOOTMGR")) return Tr.T("zaváděcí disketa Windows NT");
        if (boot.Contains("KERNEL  SYS")) return Tr.T("systémová disketa FreeDOS");
        return null;
    }

    private static string? AmigaVolumeName(byte[] d)
    {
        int rootBlock = d.Length / 512 / 2; // 880 u DD, 1760 u HD
        int o = rootBlock * 512 + 432;
        if (o + 31 > d.Length) return null;
        int len = d[o];
        if (len == 0 || len > 30) return null;
        return Ascii(d, o + 1, len);
    }

    private static string? MacVolumeName(byte[] d)
    {
        int len = d[1024 + 0x24];
        if (len == 0 || len > 27) return null;
        return Tr.F("jméno svazku „{0}“", Latin(d, 1024 + 0x25, len));
    }

    private static string Ascii(byte[] d, int o, int n) =>
        o + n > d.Length ? "" : new string(Enumerable.Range(o, n).Select(i => d[i] >= 0x20 && d[i] < 0x7F ? (char)d[i] : ' ').ToArray());

    private static string Latin(byte[] d, int o, int n) =>
        new string(Enumerable.Range(o, n).Select(i => d[i] >= 0x20 && d[i] < 0x7F ? (char)d[i] : '_').ToArray());
}
