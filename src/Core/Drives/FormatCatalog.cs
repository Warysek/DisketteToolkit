using System.Collections.Generic;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

/// <summary>Přehled 3,5" disketových formátů různých počítačů a čím je jde číst a zapisovat.</summary>
public sealed record FormatInfo(
    string RawSystem,
    string RawFormat,
    string RawUsedFor,
    Availability Usb,
    Availability InternalFdraw,
    Availability Greaseweazle,
    string? RawNote = null)
{
    public string System => Tr.T(RawSystem);
    public string Format => Tr.T(RawFormat);
    public string UsedFor => Tr.T(RawUsedFor);
    public string? Note => RawNote == null ? null : Tr.T(RawNote);
}

public static class FormatCatalog
{
    private const Availability Y = Availability.Available;
    private const Availability N = Availability.Unsupported;
    private const Availability M = Availability.Maybe;

    // Sloupce: USB mechanika / interní mechanika s fdrawcmd.sys / Greaseweazle — co umí HARDWARE.
    // i18n:raw-begin
    public static readonly IReadOnlyList<FormatInfo> All = new List<FormatInfo>
    {
        new("Atari ST/STE", "720 KB (80×2×9)", "Standardní oboustranná disketa TOS — programy, data, většina her.", Y, Y, Y),
        new("Atari ST", "360 KB (80×1×9)", "Jednostranné diskety prvních 520ST (mechanika SF354).", Y, Y, Y, "USB zapíše na stranu 0 oboustranné diskety"),
        new("Atari ST", "800–900 KB (10/11 sektorů, 81–83 stop)", "Rozšířené formáty z FastCopy, Twister, HyperFormat — hry a dema ze scény.", N, M, Y, "11 sektorů zvládne jen některý řadič"),
        new("Atari ST", "chráněné hry (.STX / .IPF)", "Originální komerční hry s kopírovací ochranou.", N, M, Y, "fdrawcmd jen jednodušší ochrany"),
        new("Atari STE/TT/Falcon", "1,44 MB HD (80×2×18)", "HD diskety pro Mega STE, TT a Falcon030.", Y, Y, Y),
        new("PC (MS-DOS, Windows)", "720 KB DD / 1,44 MB HD", "Nejrozšířenější formát 1987–2005.", Y, Y, Y),
        new("PC", "640 KB (80×2×8)", "Starší varianta DD, ojediněle MS-DOS a MSX.", Y, Y, Y),
        new("PC", "1,68 MB DMF / 1,72 MB XDF", "Instalační diskety Windows 95, Office (DMF) a OS/2 (XDF).", N, Y, Y),
        new("PC", "2,88 MB ED", "IBM PS/2, NeXT — potřebuje ED mechaniku.", N, N, M, "běžná HD mechanika ED disketu neumí"),
        new("MSX", "720 KB / 360 KB (MSX-DOS)", "Počítače MSX (Sony HitBit, Philips VG/NMS, Panasonic FS-A1). Souborový systém FAT12.", Y, Y, Y),
        new("Sinclair QL", "720 KB / 1,44 MB (QDOS)", "Sinclair QL s řadičem Trump Card / Miracle / Gold Card.", Y, Y, Y, "program disketu rozpozná, soubory zatím nevypíše; formát zatím neumí"),
        new("Amstrad PCW 9512+", "720 KB (CP/M)", "Textové editory Amstrad PCW (LocoScript) — novější modely s 3,5\" mechanikou.", Y, Y, Y, "program disketu naformátuje a rozpozná, soubory zatím nevypíše"),
        new("Apple Macintosh", "1,44 MB HD (HFS)", "Mac s mechanikou SuperDrive (1988–1998).", Y, Y, Y, "program disketu naformátuje a rozpozná, soubory zatím nevypíše"),
        new("Apple Macintosh", "400 / 800 KB (GCR, MFS/HFS)", "Starší Macy 1984–1990 — kódování GCR, proměnná rychlost otáčení.", N, N, Y),
        new("Apple IIgs / IIc+", "800 KB (ProDOS, GCR)", "Apple II s 3,5\" mechanikou UniDisk / AppleDisk.", N, N, Y),
        new("Commodore Amiga", "880 KB DD / 1,76 MB HD (AmigaDOS)", "Amiga 500/600/1200… — celá stopa se zapisuje jako jeden blok.", N, N, Y, "bez Greaseweazle jen obraz .ADF"),
        new("Commodore 1581", "800 KB (80×2×10, CBM DOS)", "3,5\" mechanika pro C64/C128.", N, Y, Y),
        new("Acorn Archimedes / RISC OS", "800 KB / 1,6 MB (ADFS, 1024 B sektory)", "Acorn A3000, A5000, RiscPC.", N, Y, Y),
        new("SAM Coupé", "800 KB (80×2×10)", "SAM Coupé (MGT) — nástupce ZX Spectra.", N, Y, Y),
        new("NEC PC-98, Sharp X68000, FM Towns", "1,2 / 1,23 MB (8×1024 B, 360 ot./min)", "Japonské počítače — potřebují „3-mode“ mechaniku.", M, M, Y, "jen 3-mode mechanika"),
        new("Hudební nástroje", "720 KB / 1,44 MB FAT, nebo vlastní formát", "Roland, Korg, Yamaha (často FAT12 — čitelné); samplery Akai S1000/S3000, Ensoniq (vlastní formát).", M, Y, Y),
        new("Linux / MINIX", "720 KB / 1,44 MB (ext2, MINIX)", "Záchranné a instalační diskety, MINIX i na Atari ST.", Y, Y, Y, "program naformátuje a rozpozná, soubory zatím nevypíše"),
    };
    // i18n:raw-end
}
