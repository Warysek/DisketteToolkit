using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Boot;

/// <summary>Druhy spouštěcích disket, které program umí vytvořit.</summary>
public enum BootSystem
{
    /// <summary>FreeDOS (svobodný DOS, GPL) — soubory se stáhnou z oficiálního repozitáře FreeDOS.</summary>
    FreeDos,
    /// <summary>MS-DOS z Windows — obraz, který Windows ukládají v diskcopy.dll („Vytvořit spouštěcí disketu MS-DOS“).</summary>
    MsDosFromWindows,
    /// <summary>Amiga — disketa s bootblokem (jako příkaz „install“), Kickstart 2.0+ (FFS).</summary>
    AmigaFfs,
    /// <summary>Amiga — bootblok pro Kickstart 1.x (OFS).</summary>
    AmigaOfs,
    /// <summary>Atari ST — program ze složky AUTO se po zapnutí spustí sám.</summary>
    AtariAuto,
}

public sealed class BootOptions
{
    public BootSystem System { get; set; }
    public DiskGeometry Geometry { get; set; } = DiskGeometry.Hd1440;
    public string? Label { get; set; }
    /// <summary>Atari: program (.PRG / .TOS / .TTP), který se má spouštět ze složky AUTO.</summary>
    public byte[]? AtariProgram { get; set; }
    public string? AtariProgramName { get; set; }
    /// <summary>FreeDOS: přidat základní nástroje (FORMAT, FDISK, SYS, EDIT, MEM, XCOPY…) jako na staré spouštěcí disketě Windows.</summary>
    public bool FreeDosTools { get; set; } = true;
}

/// <summary>Tvorba spouštěcích disket různých systémů.</summary>
public static class BootDisks
{
    public static string Describe(BootSystem s) => s switch
    {
        BootSystem.FreeDos => Tr.T("FreeDOS — spouštěcí disketa DOS pro PC"),
        BootSystem.MsDosFromWindows => Tr.T("MS-DOS z Windows (jako „Vytvořit spouštěcí disketu MS-DOS“)"),
        BootSystem.AmigaFfs => Tr.T("Amiga — spustitelná disketa, Kickstart 2.0+ (FFS)"),
        BootSystem.AmigaOfs => Tr.T("Amiga — spustitelná disketa, Kickstart 1.2/1.3 (OFS)"),
        BootSystem.AtariAuto => Tr.T("Atari ST — program ze složky AUTO se spustí sám"),
        _ => s.ToString(),
    };

    /// <summary>Jak se z diskety spouštěly různé počítače (pro nápovědu v okně).</summary>
    public static (string system, string text)[] OtherSystems => new[]
    {
        ("PC — MS-DOS, Windows 9x, FreeDOS", Tr.T("BIOS spustí zavaděč z prvního sektoru diskety a ten načte systémové soubory (IO.SYS, MSDOS.SYS a COMMAND.COM, u FreeDOS KERNEL.SYS). Obyčejně naformátovaná disketa je nemá — proto Windows uměly „spouštěcí disketu MS-DOS“ a DOS příkaz SYS.")),
        (Tr.T("PC — Linux, instalace, diagnostika"), Tr.T("Hotové obrazy spouštěcích disket se zapisovaly sektor po sektoru programem RawWrite (nebo dd). Totéž dělá „Hotový obraz (jako RawWrite)“.")),
        ("Atari ST", Tr.T("TOS je v paměti ROM, počítač naběhne vždy — s jakoukoli disketou i bez ní. Programy ze složky AUTO se spustí samy a na desktopu se otevře disketa. Hry a dema měly navíc „spustitelný“ boot sektor (kontrolní součet 0x1234) s vlastním zavaděčem; ten se nevyrábí, ale při kopírování obrazu program ho zachová.")),
        ("Commodore Amiga", Tr.T("Kickstart v ROM (A500, A600, A1200) spustí disketu jen s platným bootblokem — jinak ukazuje ruku s disketou. Příkaz „install“ ho zapíše; taková disketa naběhne do příkazového řádku AmigaDOS. Plný Workbench potřebuje systémové soubory Commodore/Hyperion, ty se nepřibalují. Amiga diskety zapíše jen Greaseweazle.")),
        ("Apple Macintosh", Tr.T("Potřebuje boot bloky a složku System (System + Finder) od Applu — vytvořit se nedají, jen zapsat hotový obraz (1,44 MB přes USB, 400/800 KB jen Greaseweazle). Apple kdysi zdarma uvolnil System 6.0.8 a 7.0.1.")),
        ("MSX", Tr.T("Každá disketa naběhne do Disk BASICu; soubor AUTOEXEC.BAS se spustí sám. MSX-DOS potřebuje MSXDOS.SYS a COMMAND.COM — jen z hotového obrazu.")),
        ("Amstrad PCW", Tr.T("Startuje z diskety s CP/M (soubor J*.EMS) nebo LocoScriptem — z originálních disket Amstradu, tedy z hotového obrazu.")),
        ("Acorn Archimedes / RISC OS", Tr.T("RISC OS je v ROM; aplikace !Boot na disketě se spustí sama (podobně jako AUTO na Atari).")),
        ("Commodore 64 / 1581", Tr.T("Z diskety se nebootuje — program se nahraje ručně příkazem LOAD.")),
    };

    /// <summary>Vytvoří obraz spouštěcí diskety. FreeDOS si při prvním použití stáhne soubory.</summary>
    public static DiskImage Build(BootOptions o, DownloadProgress? progress = null, CancellationToken ct = default)
    {
        switch (o.System)
        {
            case BootSystem.FreeDos:
                {
                    var (kernel, command, version) = FreeDosFiles.Get(progress, ct);
                    var tools = o.FreeDosTools ? FreeDosFiles.GetTools(progress, ct) : null;
                    return FreeDos(o.Geometry, kernel, command, o.Label, version, tools);
                }
            case BootSystem.MsDosFromWindows:
                return WindowsMsDos.Load();
            case BootSystem.AmigaFfs:
            case BootSystem.AmigaOfs:
                {
                    var g = o.Geometry.SectorsPerTrack >= 22 ? new DiskGeometry(80, 2, 22) : new DiskGeometry(80, 2, 11);
                    var img = AmigaDos.CreateBlank(g, o.System == BootSystem.AmigaOfs ? AmigaFsType.Ofs : AmigaFsType.Ffs, o.Label);
                    AmigaDos.InstallBootblock(img.Data, kickstart13: o.System == BootSystem.AmigaOfs);
                    return img;
                }
            case BootSystem.AtariAuto:
                return AtariAuto(o.Geometry, o.AtariProgram ?? throw new OperationException(Tr.T("Vyber program pro složku AUTO.")),
                                 o.AtariProgramName ?? "PROGRAM.PRG", o.Label);
            default:
                throw new ArgumentOutOfRangeException(nameof(o));
        }
    }

    // ------------------------------------------------------------------ FreeDOS

    /// <summary>Zavaděč FreeDOS pro FAT12 (boot.asm z FDOS/kernel, GPL) — kód od 0x3E, BPB je z diskety.</summary>
    public static byte[] FreeDosBootSector
    {
        get
        {
            using var s = typeof(BootDisks).Assembly.GetManifestResourceStream("AtariFloppy.Core.Boot.freedos-fat12.bin")
                          ?? throw new InvalidOperationException("freedos-fat12.bin");
            var b = new byte[512];
            int n = 0;
            while (n < 512) { int r = s.Read(b, n, 512 - n); if (r <= 0) break; n += r; }
            return b;
        }
    }

    public static DiskImage FreeDos(DiskGeometry g, byte[] kernel, byte[] command, string? label, string? version = null,
                                    System.Collections.Generic.IDictionary<string, byte[]>? tools = null)
    {
        if (kernel.Length > 128 * 1024) throw new OperationException(Tr.T("KERNEL.SYS je větší než 128 KB — zavaděč FreeDOS ho nenačte."));
        var img = DiskFormatter.CreateBlank(g, FormatStyle.Pc, label ?? "FREEDOS");
        var d = img.Data;
        var boot = FreeDosBootSector;
        // skok + kód zavaděče; BPB (0x03–0x3D) zůstává z formátu
        Buffer.BlockCopy(boot, 0, d, 0, 3);
        Buffer.BlockCopy(boot, 0x3E, d, 0x3E, 512 - 0x3E);
        Encoding.ASCII.GetBytes("FRDOS5.1").CopyTo(d, 3);
        d[0x24] = 0x00; // disketa A:
        var w = new Fat12Writer(d);
        var t = DateTime.Now;
        w.AddFile("KERNEL.SYS", kernel, t, 0x27);    // jen pro čtení, skrytý, systémový — jako SYS
        w.AddFile("COMMAND.COM", command, t);
        var names = new System.Collections.Generic.List<string>();
        if (tools != null)
            foreach (var kv in tools.OrderBy(k => k.Key))
            {
                if (w.FreeBytes < kv.Value.Length + 4096) break;   // na 720 KB se vejde jen část
                w.AddFile(kv.Key, kv.Value, t);
                names.Add(Path.GetFileNameWithoutExtension(kv.Key));
            }
        w.AddFile("AUTOEXEC.BAT", Encoding.ASCII.GetBytes(
            "@ECHO OFF\r\nPROMPT $P$G\r\nPATH A:\\\r\nECHO.\r\nECHO FreeDOS" + (version != null ? " " + version : "") +
            " - Diskette Toolkit\r\n" + (names.Count > 0 ? "ECHO Nastroje / Tools: " + string.Join(" ", names) + "\r\n" : "") + "ECHO.\r\n"), t);
        return img;
    }

    // ------------------------------------------------------------------ Atari

    public static DiskImage AtariAuto(DiskGeometry g, byte[] program, string programName, string? label)
    {
        if (program.Length < 28 || program[0] != 0x60 || program[1] != 0x1A)
            throw new OperationException(Tr.T("Soubor není program pro Atari (chybí hlavička 0x601A)."));
        var img = DiskFormatter.CreateBlank(g, FormatStyle.Atari, label);
        var w = new Fat12Writer(img.Data);
        string name = Path.GetFileName(programName);
        string ext = Path.GetExtension(name).ToUpperInvariant();
        if (ext is not (".PRG" or ".TOS" or ".TTP" or ".APP")) name = Path.GetFileNameWithoutExtension(name) + ".PRG";
        w.AddFile("AUTO\\" + name, program);
        return img;
    }
}

/// <summary>
/// Soubory FreeDOS (KERNEL.SYS a COMMAND.COM) z oficiálního repozitáře FreeDOS 1.3 (ibiblio.org, GPL).
/// Stáhnou se jednou a uloží do složky freedos vedle programu.
/// </summary>
public static class FreeDosFiles
{
    public static string[] KernelUrls { get; set; } =
    {
        "https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/kernel.zip",
        "http://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/kernel.zip",
    };
    public static string[] FreecomUrls { get; set; } =
    {
        "https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/freecom.zip",
        "http://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/freecom.zip",
    };

    public static string CacheDir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("AFLOPPY_FREEDOS");
            return !string.IsNullOrEmpty(env) ? env! : AppPaths.ToolDir("freedos");
        }
    }

    public static bool Cached => File.Exists(Path.Combine(CacheDir, "KERNEL.SYS")) && File.Exists(Path.Combine(CacheDir, "COMMAND.COM"));

    public static (byte[] kernel, byte[] command, string? version) Get(DownloadProgress? progress, CancellationToken ct)
    {
        string dir = CacheDir;
        string k = Path.Combine(dir, "KERNEL.SYS"), c = Path.Combine(dir, "COMMAND.COM"), v = Path.Combine(dir, "VERSION.TXT");
        if (!File.Exists(k) || !File.Exists(c))
        {
            Directory.CreateDirectory(dir);
            var kernel = Extract(DownloadAny(KernelUrls, Path.Combine(dir, "kernel.zip"), progress, ct), FindKernel, "KERNEL.SYS");
            var command = Extract(DownloadAny(FreecomUrls, Path.Combine(dir, "freecom.zip"), progress, ct), FindCommand, "COMMAND.COM");
            File.WriteAllBytes(k, kernel.data);
            File.WriteAllBytes(c, command.data);
            File.WriteAllText(v, "1.3");
            foreach (var z in new[] { "kernel.zip", "freecom.zip" }) try { File.Delete(Path.Combine(dir, z)); } catch { }
        }
        return (File.ReadAllBytes(k), File.ReadAllBytes(c), File.Exists(v) ? File.ReadAllText(v).Trim() : null);
    }

    /// <summary>Základní nástroje jako na spouštěcí disketě Windows 98: (balík, soubor). SYS je v balíku kernel.</summary>
    public static readonly (string package, string file)[] Tools =
    {
        ("kernel", "SYS.COM"), ("format", "FORMAT.EXE"), ("fdisk", "FDISK.EXE"), ("edit", "EDIT.EXE"), ("mem", "MEM.EXE"),
        ("xcopy", "XCOPY.EXE"), ("attrib", "ATTRIB.COM"), ("deltree", "DELTREE.COM"), ("more", "MORE.EXE"), ("choice", "CHOICE.EXE"),
    };

    public static string BaseUrl { get; set; } = "https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/";

    /// <summary>Nástroje FreeDOS (stáhnou se jednou do podsložky tools). Co se nepodaří, vynechá.</summary>
    public static System.Collections.Generic.Dictionary<string, byte[]> GetTools(DownloadProgress? progress, CancellationToken ct)
    {
        string dir = Path.Combine(CacheDir, "tools");
        Directory.CreateDirectory(dir);
        var result = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pkg, file) in Tools)
        {
            ct.ThrowIfCancellationRequested();
            string baseName = Path.GetFileNameWithoutExtension(file);
            string? cached = Directory.GetFiles(dir, baseName + ".*").FirstOrDefault();
            if (cached == null)
            {
                string zip = Path.Combine(dir, pkg + ".zip");
                try
                {
                    DownloadAny(new[] { BaseUrl + pkg + ".zip", BaseUrl.Replace("https://", "http://") + pkg + ".zip" }, zip, progress, ct);
                    using var a = ZipFile.OpenRead(zip);
                    var e = a.Entries.Where(x => x.FullName.Replace('\\', '/').ToUpperInvariant().StartsWith("BIN/") &&
                                                 Path.GetFileNameWithoutExtension(x.Name).Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                                                 (x.Name.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".COM", StringComparison.OrdinalIgnoreCase)))
                                     .OrderBy(x => x.FullName.Length).FirstOrDefault();
                    if (e != null)
                    {
                        cached = Path.Combine(dir, e.Name.ToUpperInvariant());
                        using var src = e.Open();
                        using var dst = File.Create(cached);
                        src.CopyTo(dst);
                    }
                }
                catch (OperationException) { /* nástroj se nepodařilo stáhnout — disketa bude i bez něj */ }
                finally { try { File.Delete(zip); } catch { } }
            }
            if (cached != null) result[Path.GetFileName(cached).ToUpperInvariant()] = File.ReadAllBytes(cached);
        }
        return result;
    }

    private static string DownloadAny(string[] urls, string target, DownloadProgress? progress, CancellationToken ct)
    {
        OperationException? last = null;
        foreach (var u in urls)
        {
            try { Http.Download(u, target, progress, ct); return target; }
            catch (OperationException e) { last = e; }
        }
        throw last ?? new OperationException(Tr.T("FreeDOS se nepodařilo stáhnout."));
    }

    private static (byte[] data, string name) Extract(string zip, Func<ZipArchive, ZipArchiveEntry?> pick, string what)
    {
        using var a = ZipFile.OpenRead(zip);
        var e = pick(a) ?? throw new OperationException(Tr.F("Ve staženém balíku FreeDOS chybí {0}.", what));
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return (ms.ToArray(), e.Name);
    }

    /// <summary>Jádro: přednostně verze pro 8086 (běží na každém PC), jinak 386.</summary>
    internal static ZipArchiveEntry? FindKernel(ZipArchive a)
    {
        var sys = a.Entries.Where(e => e.FullName.Replace('\\', '/').ToUpperInvariant().Contains("BIN/") &&
                                       e.Name.ToUpperInvariant().StartsWith("KERN") && e.Name.ToUpperInvariant().EndsWith(".SYS"))
                           .ToList();
        return sys.FirstOrDefault(e => e.Name.ToUpperInvariant().Contains("86") && !e.Name.Contains("386"))
               ?? sys.FirstOrDefault(e => e.Name.Equals("KERNEL.SYS", StringComparison.OrdinalIgnoreCase))
               ?? sys.FirstOrDefault();
    }

    internal static ZipArchiveEntry? FindCommand(ZipArchive a) =>
        a.Entries.Where(e => e.Name.Equals("COMMAND.COM", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(e => e.FullName.ToUpperInvariant().StartsWith("BIN/") ? 0 : 1).ThenBy(e => e.FullName.Length)
                 .FirstOrDefault();
}
