using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.I18n;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Builders;
using AtariFloppy.Core.Fs.Amiga;

namespace AtariFloppy.Core.Drives;

/// <summary>Jaký hardware formát minimálně potřebuje.</summary>
public enum HardwareNeed
{
    /// <summary>Zvládne i USB mechanika (a interní přes Windows).</summary>
    Standard,
    /// <summary>Nestandardní MFM geometrie — interní mechanika s fdrawcmd.sys nebo Greaseweazle.</summary>
    LowLevelMfm,
    /// <summary>Jiné kódování / celostopý zápis — jen Greaseweazle.</summary>
    Flux,
    /// <summary>Potřebuje jinou mechaniku (ED 2,88 MB, 3-mode).</summary>
    SpecialDrive,
}

public enum Density { DD, HD, ED }

/// <summary>Jeden formát diskety, který jde vytvořit (nebo aspoň popsat, proč zatím ne).</summary>
public sealed class FormatProfile
{
    public string Id { get; init; } = "";
    private string _system = "", _name = "", _usedFor = "";
    private string? _why, _note;
    /// <summary>Původní (český) název systému — pro porovnávání.</summary>
    public string RawSystem => _system;
    public string System { get => Tr.T(_system); init => _system = value; }
    public string Name { get => Tr.T(_name); init => _name = value; }
    public DiskGeometry Geometry { get; init; } = DiskGeometry.Dd720;
    public Density Density { get; init; }
    public string UsedFor { get => Tr.T(_usedFor); init => _usedFor = value; }
    public HardwareNeed Need { get; init; }
    /// <summary>Proč to USB mechanika nezvládne (pro Need ≠ Standard).</summary>
    public string? WhyNotUsb { get => _why == null ? null : Tr.T(_why); init => _why = value; }
    /// <summary>Tvorba prázdného souborového systému; null = program ho zatím vytvořit neumí.</summary>
    public Func<string?, DiskImage>? Build { get; init; }
    /// <summary>FAT formát → formátuje se přes DiskOperations.Format se značením vadných clusterů.</summary>
    public FormatStyle? FatStyle { get; init; }
    public bool SupportsLabel { get; init; }
    public bool Experimental { get; init; }
    /// <summary>Přípona pro prázdný obraz (výchozí .st/.img podle systému).</summary>
    public string ImageExtension { get; init; } = ".img";
    public string? Note { get => _note == null ? null : Tr.T(_note); init => _note = value; }

    public string Title => $"{System} — {Name}";
}

public enum ProfileStatus
{
    Ready,
    NoDisk,
    WrongMedia,
    NeedsDriver,
    Planned,
    Unsupported,
}

public sealed record ProfileVerdict(FormatProfile Profile, ProfileStatus Status, string Reason)
{
    public bool IsReady => Status == ProfileStatus.Ready;
}

/// <summary>Co je právě vložené v mechanice.</summary>
public enum InsertedMedia { Unknown, None, DD, HD }

public static class FormatProfiles
{
    private static FormatProfile Fat(string id, string system, string name, DiskGeometry g, FormatStyle style, string usedFor, string? note = null) => new()
    {
        Id = id, System = system, Name = name, Geometry = g, Density = g.IsHighDensity ? Density.HD : Density.DD,
        UsedFor = usedFor, Need = HardwareNeed.Standard, FatStyle = style, SupportsLabel = true, Note = note,
        ImageExtension = system.StartsWith("Atari") ? ".st" : system == "MSX" ? ".dsk" : ".img",
        Build = label => DiskFormatter.CreateBlank(g, style, label),
    };

    private static FormatProfile Low(string id, string system, string name, DiskGeometry g, string usedFor, string why, FormatStyle? fat = null) => new()
    {
        Id = id, System = system, Name = name, Geometry = g, Density = g.IsHighDensity ? Density.HD : Density.DD,
        UsedFor = usedFor, Need = HardwareNeed.LowLevelMfm, WhyNotUsb = why, FatStyle = fat, SupportsLabel = fat != null,
        ImageExtension = system.StartsWith("Atari") ? ".st" : ".img",
        Build = fat == null ? null : label => DiskFormatter.CreateBlank(g, fat.Value, label),
    };

    private static FormatProfile Flux(string id, string system, string name, DiskGeometry g, Density den, string usedFor, string why) => new()
    {
        Id = id, System = system, Name = name, Geometry = g, Density = den, UsedFor = usedFor, Need = HardwareNeed.Flux, WhyNotUsb = why,
    };

    // i18n:raw-begin (texty se překládají až při čtení vlastností)
    public static readonly IReadOnlyList<FormatProfile> All = new List<FormatProfile>
    {
        // ---------------- Atari
        Fat("atari", "Atari ST", "TOS 720 KB", DiskGeometry.Dd720, FormatStyle.Atari,
            "Přesně jako formát z desktopu TOS. Windows ho nemusí přečíst."),
        Fat("atari-hybrid", "Atari ST", "720 KB hybrid (Atari i Windows)", DiskGeometry.Dd720, FormatStyle.Hybrid,
            "Doporučeno pro přenos mezi PC a Atari: PC boot sektor + unikátní Atari sériové číslo."),
        Fat("atari-ss", "Atari ST", "360 KB jednostranná", DiskGeometry.Ss360, FormatStyle.Atari,
            "Pro 520ST s jednostrannou mechanikou SF354. Fyzicky se naformátují obě strany, data jen na straně 0."),
        Fat("atari-hd", "Atari STE/TT/Falcon", "1,44 MB HD", DiskGeometry.Hd1440, FormatStyle.Hybrid,
            "HD diskety pro Mega STE, TT a Falcon030 (ST s HD modifikací)."),
        Low("atari-800", "Atari ST", "800 KB (80×2×10)", new DiskGeometry(80, 2, 10),
            "Rozšířený formát FastCopy / TOS 1.04+ — o 80 KB víc místa.", "10 sektorů na stopu USB mechanika neumí (jen 8, 9 nebo 18).", FormatStyle.Atari),
        Low("atari-820", "Atari ST", "820 KB (82×2×10)", new DiskGeometry(82, 2, 10),
            "Maximum běžných mechanik — hry a dema ze scény.", "82 stop a 10 sektorů USB mechanika neumí.", FormatStyle.Atari),
        Low("atari-900", "Atari ST", "900 KB (80×2×11)", new DiskGeometry(80, 2, 11),
            "Twister / HyperFormat — nejhustší formát, ne každá mechanika ho spolehlivě čte.", "11 sektorů na stopu USB mechanika neumí; i s fdrawcmd jen některé řadiče.", FormatStyle.Atari),

        // ---------------- PC
        Fat("pc", "PC (MS-DOS, Windows)", "720 KB", DiskGeometry.Dd720, FormatStyle.Pc, "Jako FORMAT A: /F:720 ve Windows."),
        Fat("pc-hd", "PC (MS-DOS, Windows)", "1,44 MB", DiskGeometry.Hd1440, FormatStyle.Pc, "Nejběžnější disketa 1990–2005."),
        Fat("pc-640", "PC", "640 KB (80×2×8)", DiskGeometry.Dd640, FormatStyle.Pc, "Starší DD formát s 8 sektory — ojediněle MS-DOS a MSX."),
        Low("pc-dmf", "PC", "1,68 MB DMF (80×2×21)", new DiskGeometry(80, 2, 21),
            "Instalační diskety Windows 95 / Office — Microsoft Distribution Format.", "21 sektorů na stopu USB mechanika neumí.", FormatStyle.Pc),
        new() { Id = "pc-ed", System = "PC", Name = "2,88 MB ED", Geometry = new DiskGeometry(80, 2, 36), Density = Density.ED,
                UsedFor = "IBM PS/2, NeXT — Extra Density.", Need = HardwareNeed.SpecialDrive,
                WhyNotUsb = "Potřebuje ED mechaniku a ED disketu (jiný magnetický materiál) — běžná HD mechanika to neumí." },

        // ---------------- MSX
        Fat("msx", "MSX", "MSX-DOS 720 KB (2DD)", DiskGeometry.Dd720, FormatStyle.Msx,
            "Počítače MSX 2 / turboR (Sony HitBit, Philips NMS, Panasonic FS-A1). Datová disketa — MSX s ní naběhne do Disk BASICu."),
        Fat("msx-ss", "MSX", "MSX-DOS 360 KB (1DD)", DiskGeometry.Ss360, FormatStyle.Msx,
            "MSX 1 s jednostrannou mechanikou (Philips VG-8235, Sony HB-F500 …)."),

        // ---------------- Apple
        new() { Id = "mac-hfs", System = "Apple Macintosh", Name = "HFS 1,44 MB", Geometry = DiskGeometry.Hd1440, Density = Density.HD,
                UsedFor = "Mac s mechanikou SuperDrive (1988–1998), System 6 až Mac OS 9.", Need = HardwareNeed.Standard, SupportsLabel = true,
                Build = label => HfsBuilder.Create(DiskGeometry.Hd1440, label) },
        new() { Id = "mac-800", System = "Apple Macintosh", Name = "HFS 800 KB (GCR)", Geometry = new DiskGeometry(80, 2, 10), Density = Density.DD,
                UsedFor = "Macy 1986–1990 a Macy bez SuperDrive.", Need = HardwareNeed.Flux, SupportsLabel = true, ImageExtension = ".dsk",
                WhyNotUsb = "Mac GCR kódování s proměnnou rychlostí otáčení — umí jen Greaseweazle. Prázdný obraz (pro emulátor nebo FloppyEmu) vytvořit jde.",
                Build = label => HfsBuilder.Create(new DiskGeometry(80, 2, 10), label) },
        Flux("mac-400", "Apple Macintosh", "400 KB (GCR, MFS)", new DiskGeometry(80, 1, 10), Density.DD,
            "Úplně první Macintosh 128K/512K.", "Mac GCR kódování — umí jen Greaseweazle."),
        Flux("apple2-800", "Apple IIgs / IIc+", "800 KB (ProDOS, GCR)", new DiskGeometry(80, 2, 10), Density.DD,
            "Apple II s 3,5\" mechanikou.", "Apple GCR kódování — umí jen Greaseweazle."),

        // ---------------- Commodore
        new() { Id = "amiga", System = "Commodore Amiga", Name = "AmigaDOS FFS 880 KB", Geometry = new DiskGeometry(80, 2, 11), Density = Density.DD,
                UsedFor = "Amiga s Workbench 2.0 a novějším (A500+, A600, A1200, A4000).", Need = HardwareNeed.Flux, SupportsLabel = true, ImageExtension = ".adf",
                WhyNotUsb = "Amiga zapisuje celou stopu jako jeden blok bez mezer mezi sektory — PC ani USB mechanika to neumí, jen Greaseweazle. Prázdný obraz .ADF (pro Gotek, emulátor nebo pozdější zápis) vytvořit jde.",
                Build = label => AmigaDos.CreateBlank(new DiskGeometry(80, 2, 11), AmigaFsType.Ffs, label) },
        new() { Id = "amiga-ofs", System = "Commodore Amiga", Name = "AmigaDOS OFS 880 KB", Geometry = new DiskGeometry(80, 2, 11), Density = Density.DD,
                UsedFor = "Amiga 500/1000/2000 s Kickstartem 1.2 / 1.3 (starý systém, čte ho každá Amiga).", Need = HardwareNeed.Flux, SupportsLabel = true, ImageExtension = ".adf",
                WhyNotUsb = "Amiga zapisuje celou stopu jako jeden blok — umí jen Greaseweazle. Prázdný obraz .ADF vytvořit jde.",
                Build = label => AmigaDos.CreateBlank(new DiskGeometry(80, 2, 11), AmigaFsType.Ofs, label) },
        new() { Id = "amiga-hd", System = "Commodore Amiga", Name = "AmigaDOS FFS 1,76 MB HD", Geometry = new DiskGeometry(80, 2, 22), Density = Density.HD,
                UsedFor = "Amiga 4000 / 1200 s HD mechanikou (Amiga HD mechanika se otáčí poloviční rychlostí).", Need = HardwareNeed.Flux, SupportsLabel = true, ImageExtension = ".adf",
                WhyNotUsb = "Celostopý zápis Amigy — umí jen Greaseweazle. Prázdný obraz .ADF vytvořit jde.",
                Build = label => AmigaDos.CreateBlank(new DiskGeometry(80, 2, 22), AmigaFsType.Ffs, label) },
        new() { Id = "amiga-crossdos", System = "Commodore Amiga", Name = "CrossDOS 720 KB (PC disketa pro Amigu)", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "Přenos souborů PC ↔ Amiga přes USB mechaniku: Amiga s Workbench 2.1+ čte PC diskety jako PC0: (CrossDOS). Amiga umí jen DD — nepoužívej 1,44 MB.",
                Need = HardwareNeed.Standard, FatStyle = FormatStyle.Pc, SupportsLabel = true,
                Build = label => DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc, label) },
        Low("c1581", "Commodore 1581", "800 KB (CBM DOS)", new DiskGeometry(80, 2, 10),
            "3,5\" mechanika pro C64 / C128.", "10 sektorů na stopu (a prohozené strany) USB mechanika neumí."),

        // ---------------- další
        new() { Id = "pcw", System = "Amstrad PCW 9512+ / 9256", Name = "CP/M 720 KB (CF2DD)", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "Datová disketa pro LocoScript a CP/M Plus.", Need = HardwareNeed.Standard,
                Build = _ => CpmBuilder.CreatePcw720() },
        new() { Id = "ql", System = "Sinclair QL", Name = "QDOS 720 KB", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "Sinclair QL s řadičem Trump Card / Miracle / Gold Card.", Need = HardwareNeed.Standard,
                Note = "USB mechanika to fyzicky zvládne, ale tvorbu souborového systému QDOS program zatím neumí." },
        Low("sam", "SAM Coupé", "800 KB (MGT)", new DiskGeometry(80, 2, 10),
            "SAM Coupé a Spectrum s rozhraním +D / DISCiPLE.", "10 sektorů na stopu USB mechanika neumí."),
        new() { Id = "acorn", System = "Acorn Archimedes / RISC OS", Name = "ADFS 800 KB (5 × 1024 B)", Geometry = new DiskGeometry(80, 2, 5, 1024),
                Density = Density.DD, UsedFor = "Acorn A3000, A5000, RiscPC.", Need = HardwareNeed.LowLevelMfm,
                WhyNotUsb = "Sektory po 1024 B USB mechanika neumí." },
        new() { Id = "pc98", System = "NEC PC-98 / Sharp X68000", Name = "1,23 MB (77×2×8 × 1024 B)", Geometry = new DiskGeometry(77, 2, 8, 1024),
                Density = Density.HD, UsedFor = "Japonské počítače.", Need = HardwareNeed.SpecialDrive,
                WhyNotUsb = "Potřebuje „3-mode“ mechaniku (360 ot./min) a sektory 1024 B." },

        // ---------------- Unix
        new() { Id = "minix", System = "MINIX (PC)", Name = "MINIX v1 720 KB", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "MINIX 1.x / 2.x na PC, starý Linux.", Need = HardwareNeed.Standard,
                Build = _ => MinixBuilder.Create(DiskGeometry.Dd720, bigEndian: false) },
        new() { Id = "minix-hd", System = "MINIX (PC)", Name = "MINIX v1 1,44 MB", Geometry = DiskGeometry.Hd1440, Density = Density.HD,
                UsedFor = "MINIX 1.x / 2.x na PC, starý Linux.", Need = HardwareNeed.Standard,
                Build = _ => MinixBuilder.Create(DiskGeometry.Hd1440, bigEndian: false) },
        new() { Id = "minix-st", System = "MINIX-ST (Atari)", Name = "MINIX v1 720 KB, 68000", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "MINIX 1.5 pro Atari ST.", Need = HardwareNeed.Standard, Experimental = true,
                Note = "Experimentální — struktura ověřená, ale na skutečném MINIX-ST zatím nevyzkoušená.",
                Build = _ => MinixBuilder.Create(DiskGeometry.Dd720, bigEndian: true) },
        new() { Id = "ext2", System = "Linux", Name = "ext2 1,44 MB", Geometry = DiskGeometry.Hd1440, Density = Density.HD,
                UsedFor = "Linuxové záchranné a zaváděcí diskety.", Need = HardwareNeed.Standard, SupportsLabel = true,
                Build = label => Ext2Builder.Create(DiskGeometry.Hd1440, label) },
        new() { Id = "ext2-dd", System = "Linux", Name = "ext2 720 KB", Geometry = DiskGeometry.Dd720, Density = Density.DD,
                UsedFor = "Linux na DD disketě.", Need = HardwareNeed.Standard, SupportsLabel = true,
                Build = label => Ext2Builder.Create(DiskGeometry.Dd720, label) },
    };
    // i18n:raw-end

    /// <summary>FAT profil pro libovolnou kombinaci stylu a geometrie (příkazová řádka: --atari --size 1440 …).</summary>
    public static FormatProfile ForFat(FormatStyle style, DiskGeometry g)
    {
        var known = All.FirstOrDefault(p => p.FatStyle == style && p.Geometry == g);
        if (known != null) return known;
        string sys = style switch { FormatStyle.Atari => "Atari ST", FormatStyle.Msx => "MSX", FormatStyle.Pc => "PC", _ => "Atari + PC" };
        return Fat($"fat-{style}-{g.Short}".ToLowerInvariant(), sys, $"FAT12 {g.SizeBytes / 1024} KB", g, style, Tr.T("Vlastní kombinace stylu a velikosti."));
    }

    public static FormatProfile? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Jde formát na této mechanice s touto disketou vytvořit? Pokud ne, proč.</summary>
    public static ProfileVerdict Evaluate(FormatProfile p, DetectedDrive drive, InsertedMedia media)
    {
        string exp = p.Experimental ? Tr.T("Experimentální.") : "";
        if (p.Need == HardwareNeed.SpecialDrive)
            return new(p, ProfileStatus.Unsupported, p.WhyNotUsb ?? Tr.T("Potřebuje speciální mechaniku."));

        // ---- Greaseweazle: umí skoro všechno, vloženou disketu ale nepozná (nemá čidlo hustoty)
        if (drive.Kind == DriveKind.Greaseweazle)
        {
            if (drive.GwToolPath == null)
                return new(p, ProfileStatus.NeedsDriver, Tr.T("Chybí program gw.exe. ") + Gw.GwTool.DownloadHint);
            if (Gw.GwFormat.ForProfile(p) == null)
                return new(p, ProfileStatus.Unsupported, Tr.T("Tento formát zatím přes Greaseweazle nepodporuji."));
            if (p.Build == null)
                return new(p, ProfileStatus.Planned, (p.Note ?? Tr.T("Prázdný souborový systém zatím neumím vytvořit")) + Tr.T(" — zálohu a zápis hotového obrazu Greaseweazle zvládne."));
            string disk = p.Density == Density.HD ? Tr.T("Vlož HD disketu (bez pásky).") : Tr.T("Vlož DD disketu (nebo HD s přelepeným otvorem).");
            return new(p, ProfileStatus.Ready, (disk + " " + exp).Trim());
        }

        // ---- USB / interní mechanika
        switch (p.Need)
        {
            case HardwareNeed.Flux:
                return new(p, ProfileStatus.Unsupported, (p.WhyNotUsb ?? "") + (drive.Kind == DriveKind.Internal ? Tr.T(" Interní mechanika ani s fdrawcmd.sys to neumí.") : ""));
            case HardwareNeed.LowLevelMfm:
                if (drive.Kind == DriveKind.Usb) return new(p, ProfileStatus.Unsupported, p.WhyNotUsb + Tr.T(" Jde to interní mechanikou s fdrawcmd.sys nebo přes Greaseweazle."));
                if (!drive.FdrawcmdInstalled)
                    return new(p, ProfileStatus.NeedsDriver, Tr.T("Interní mechanika to zvládne po instalaci ovladače fdrawcmd.sys (simonowen.com/fdrawcmd)."));
                if (p.Build == null)
                    return new(p, ProfileStatus.Planned, Tr.T("Prázdný souborový systém zatím neumím vytvořit — zálohu a zápis hotového obrazu fdrawcmd zvládne."));
                if (p.Geometry.SectorsPerTrack == 11 && p.Geometry.BytesPerSector == 512)
                    exp = Tr.T("Záleží na řadiči — některé 11 sektorů nezvládnou (program to pozná hned na první stopě).");
                break;
        }
        if (p.Build == null)
            return new(p, ProfileStatus.Planned, p.Note ?? Tr.T("Tento souborový systém program zatím neumí vytvořit."));
        var needed = p.Density == Density.HD ? Feature.Standard1440 : Feature.Standard720;
        if (drive.Get(needed) != Availability.Available)
            return new(p, ProfileStatus.Unsupported, Tr.F("Mechanika neumí {0} diskety.", (p.Density == Density.HD ? "HD (1,44 MB)" : "DD (720 KB)")));

        switch (media)
        {
            case InsertedMedia.None:
                return new(p, ProfileStatus.NoDisk, Tr.T("Vlož disketu."));
            case InsertedMedia.DD when p.Density == Density.HD:
                return new(p, ProfileStatus.WrongMedia, Tr.T("Vložená disketa je DD (nebo HD s přelepeným otvorem) — pro tento formát vlož HD disketu bez pásky."));
            case InsertedMedia.HD when p.Density == Density.DD:
                return new(p, ProfileStatus.WrongMedia, Tr.T("Vložená disketa je HD — přelep otvor neprůhlednou páskou z obou stran, nebo vlož DD disketu."));
        }
        return new(p, ProfileStatus.Ready, exp);
    }
}
