using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;

namespace AtariFloppy.Cli;

internal static class Program
{
    public static string Version => AtariFloppy.Core.App.AppPaths.Version + " (testovací)";
    internal static CancellationTokenSource Cancel = new();
    internal static volatile bool Busy;

    public static void ResetCancel()
    {
        if (Cancel.IsCancellationRequested) Cancel = new CancellationTokenSource();
    }

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch { /* přesměrovaný výstup */ }

        Console.CancelKeyPress += (_, e) =>
        {
            if (!Busy) return; // mimo operaci Ctrl+C program normálně ukončí
            e.Cancel = true;   // uprostřed zápisu nechceme skončit se zamčenou jednotkou
            Cancel.Cancel();
            Ui.Warn("\nPřerušuji…");
        };

        bool pause = Ui.OwnsConsole();
        int code;
        try
        {
            code = args.Length == 0 ? Menu.Run() : Run(args);
        }
        catch (OperationCanceledException)
        {
            Ui.EndProgress();
            Ui.Warn("Operace byla přerušena.");
            code = 2;
        }
        catch (Exception e) when (e is OperationException or FormatException or FileNotFoundException or NotSupportedException or ArgumentException)
        {
            Ui.EndProgress();
            Ui.Error(e.Message);
            code = 1;
        }
        catch (Exception e)
        {
            Ui.EndProgress();
            Ui.Error("Neočekávaná chyba: " + e);
            code = 3;
        }

        if (pause && args.Length > 0)
        {
            Console.WriteLine();
            Ui.Dim("Stiskni libovolnou klávesu…");
            Console.ReadKey(true);
        }
        return code;
    }

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        Ui.AssumeYes = a.Flag("yes", "y");
        Backends.ForceFdraw = a.Flag("fdraw");
        if (a.Opt("step-rate") is string sr) Backends.StepRateMs = int.TryParse(sr, out int ms) && ms is >= 1 and <= 30 ? ms : throw new ArgumentException("--step-rate: zadej 1–30 ms (Atari standard 3, bezpečně 6).");
        if (a.Opt("gw-drive") is string gd) Backends.GwDrive = gd.ToUpperInvariant();
        if (a.Opt("gw") is string gp) Backends.GwPath = gp;
        if (a.Opt("revs") is string rv && int.TryParse(rv, out int revs)) Backends.Revs = Math.Max(1, Math.Min(10, revs));
        if (a.Opt("tracks") is string tr && int.TryParse(tr, out int trk)) Backends.FluxTracks = Math.Max(40, Math.Min(86, trk));
        switch (a.Command)
        {
            case "detect": case "mechaniky": return Detect();
            case "info": return Info(a.Pos(0, "soubor"), a.Flag("all"));
            case "read": case "cti":
                if (Backends.IsGwArg(a.Pos(0, "mechanika"))) return GwCommands.Read(a.Pos(0, "mechanika"), a.Pos(1, "výstupní soubor"), a.Opt("profile"));
                return Read(a.Pos(0, "mechanika"), a.Pos(1, "výstupní soubor"), ParseSides(a.Opt("sides")));
            case "write": case "zapis":
                if (Backends.IsGwArg(a.Pos(1, "mechanika"))) return GwCommands.Write(a.Pos(0, "soubor"), a.Pos(1, "mechanika"), a.Opt("profile"), !a.Flag("no-verify"));
                return Write(a.Pos(0, "soubor"), a.Pos(1, "mechanika"), a.Flag("format"), !a.Flag("no-verify"));
            case "erase": case "smaz": return GwCommands.Erase(a.Pos(0, "Greaseweazle (GW)"));
            case "format":
                {
                    string? pid = a.Opt("profile");
                    if (pid != null)
                    {
                        var prof = FormatProfiles.Find(pid) ?? throw new ArgumentException($"Neznámý formát „{pid}“. Seznam: dtk profiles");
                        return ProfilesCommand.Format(a.Pos(0, "mechanika"), prof, a.Flag("quick"), a.Opt("label"), !a.Flag("no-verify"));
                    }
                    return Format(a.Pos(0, "mechanika"), ParseStyle(a), ParseSize(a), a.Flag("quick"), a.Opt("label"), !a.Flag("no-verify"));
                }
            case "profiles": case "profily": return ProfilesCommand.Profiles(a.PosOrNull(0));
            case "check": case "kontrola":
                {
                    string target = a.Pos(0, "mechanika nebo soubor");
                    if (File.Exists(target)) return CheckCommand.CheckImage(target, a.Flag("repair", "oprav"));
                    if (Backends.IsGwArg(target)) return GwCommands.Check(target, ParseWriteTest(a.Opt("write-test")), a.Flag("repair", "oprav"), a.Opt("save"), a.Opt("profile"));
                    return CheckCommand.CheckDisk(target, ParseWriteTest(a.Opt("write-test")), a.Flag("repair", "oprav"), a.Opt("save"));
                }
            case "formats": case "formaty": return CheckCommand.Formats();
            case "convert": case "preved": return Convert(a.Pos(0, "vstupní soubor"), a.Pos(1, "výstupní soubor"));
            case "new": case "novy":
                {
                    string file = a.Pos(0, "soubor");
                    string? pid = a.Opt("profile") ?? (Path.GetExtension(file).Equals(".adf", StringComparison.OrdinalIgnoreCase) && a.Opt("geometry") == null ? "amiga" : null);
                    if (pid != null)
                        return NewFromProfile(file, FormatProfiles.Find(pid) ?? throw new ArgumentException($"Neznámý formát „{pid}“. Seznam: dtk profiles"), a.Opt("label"));
                    return New(file, ParseStyle(a), a.Opt("geometry") ?? "80x2x9", a.Opt("label"));
                }
            case "extract": case "rozbal": return Extract(a.Pos(0, "soubor"), a.Pos(1, "cílová složka"));
            case "install": case "instaluj":
                return a.Pos(0, "gw nebo fdrawcmd").ToLowerInvariant() switch
                {
                    "gw" or "greaseweazle" => ToolsCommand.InstallGw(),
                    "fdrawcmd" or "fdraw" => ToolsCommand.InstallFdrawcmd(),
                    var x => throw new ArgumentException($"install {x}: umím „install gw“ nebo „install fdrawcmd“."),
                };
            case "boot": case "spousteci":
                return BootCommand.Run(a.Pos(0, "systém (freedos, msdos, amiga, amiga13, atari)"), a.Pos(1, "mechanika nebo soubor"),
                                       a.Opt("size"), a.Opt("label"), a.Opt("prg"), a.Flag("no-tools"), a.Opt("diskcopy"));
            case "firmware": return ToolsCommand.Firmware(a.PosOrNull(0) ?? "GW");
            case "help": case "napoveda": case "-h": case "--help": case "/?": Help(); return 0;
            default:
                Ui.Error($"Neznámý příkaz „{a.Command}“.");
                Help();
                return 1;
        }
    }

    // ================================================================ příkazy

    public static int Detect()
    {
        Ui.Title("Připojené mechaniky");
        var log = new List<string>();
        var drives = DriveDetector.DetectAll(log);
        if (drives.Count == 0)
        {
            Ui.Warn("Nenašel jsem žádnou disketovou mechaniku ani Greaseweazle.");
            Ui.Dim("USB disketovka se ve Windows ukazuje jako jednotka A: nebo B: — zkontroluj ji v Průzkumníku.");
        }
        foreach (var d in drives)
        {
            Console.WriteLine();
            Ui.Write($" {d.Id,-6}", ConsoleColor.White);
            Ui.Write(d.KindText, ConsoleColor.Cyan);
            Ui.Write($" — {d.Name}", ConsoleColor.White, true);
            if (!string.IsNullOrEmpty(d.Details)) Ui.Field("Detaily", d.Details!);
            if (d.MediaTypes.Count > 0) Ui.Field("Formáty pro vloženou", string.Join(", ", d.MediaTypes));
            if (d.CanUseStandardAccess) Ui.Field("Disketa", d.MediaText);
            Console.WriteLine();
            Ui.Write("  Co umí mechanika:", ConsoleColor.Gray, true);
            foreach (var kv in d.Features.OrderBy(k => k.Key)) Ui.AvailabilityLine(kv.Key, kv.Value);
            ProfilesCommand.PrintOperations(d);

            if (d.Kind == DriveKind.Internal && !d.FdrawcmdInstalled)
                Ui.Dim("\n  Tip: po instalaci ovladače fdrawcmd.sys (simonowen.com/fdrawcmd) půjdou i nestandardní formáty.");
            if (d.Kind == DriveKind.Internal && d.FdrawcmdInstalled)
            {
                try { using var f = Backends.OpenFdraw(d); }
                catch (Exception e) { Ui.Warn("  fdrawcmd.sys: " + e.Message); }
            }
            if (d.Kind == DriveKind.Greaseweazle) { Console.WriteLine(); GwCommands.PrintInfo(d); }
        }
        if (log.Count > 0)
        {
            Console.WriteLine();
            foreach (var l in log) Ui.Dim("  " + l);
        }
        if (drives.Any(d => d.CanUseStandardAccess))
            Ui.Dim("\n  Formáty všech systémů pro vloženou disketu: dtk profiles A:");
        return 0;
    }

    public static int Info(string path, bool all)
    {
        var loaded = ImageFile.Load(path);
        Ui.Title("Image: " + Path.GetFileName(path));
        Ui.Field("Formát", ImageFile.Describe(loaded.Kind));
        Ui.Field("Velikost souboru", $"{loaded.FileSize:N0} B".Replace(',', ' '));
        if (loaded.Image == null)
        {
            Ui.Warn("  " + loaded.NotLoadedReason);
            return 0;
        }

        var img = loaded.Image;
        Ui.Field("Geometrie", img.Geometry.ToString());

        var fs = FileSystemDetector.Detect(img.Data);
        Ui.Field("Systém", fs.Name + (fs.Details != null ? " — " + fs.Details : ""), ConsoleColor.White);
        Ui.Field("K čemu", fs.UsedFor);
        if (fs.CanCheck || fs.Kind is FsKind.OwnLoader or FsKind.Unknown) PrintBootSector(img.BootSector);
        var fat = fs.CanCheck ? Fat12Volume.TryOpen(img.Data, out _) : null;
        if (fat != null)
        {
            var check = FatDoctor.Check(img.Data);
            Ui.Field("Souborový systém", $"FAT12, {fat.CapacityBytes / 1024} KB, volno {fat.FreeBytes / 1024} KB");
            int problems = check.Issues.Count(i => i.Severity != IssueSeverity.Info);
            Ui.Field("Kontrola FAT", problems == 0 ? "v pořádku" : $"{problems} problémů — podrobnosti: dtk check \"{Path.GetFileName(path)}\"",
                     problems == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        }

        var assessment = ImageAnalysis.AssessForStandardDrive(loaded);
        var color = assessment.Verdict switch
        {
            Verdict.Writable => ConsoleColor.Green,
            Verdict.ContentOnly => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
        string head = assessment.Verdict switch
        {
            Verdict.Writable => "ZELENÁ",
            Verdict.ContentOnly => "ŽLUTÁ",
            _ => "ČERVENÁ",
        };
        Console.WriteLine();
        Ui.Write("  Zápis přes USB / interní mechaniku: ", ConsoleColor.Gray);
        Ui.Write(head, color, true);
        Ui.Write("  " + assessment.Summary, color, true);
        foreach (var n in assessment.Notes) Ui.Dim("  • " + n);

        var amigaVol = fs.Kind == FsKind.Amiga ? AmigaDos.TryOpen(img.Data) : null;
        if (amigaVol != null)
        {
            var entries = amigaVol.ListAll();
            Ui.Field("Volno", $"{amigaVol.FreeBlocks / 2} KB z {amigaVol.Blocks / 2} KB");
            Ui.Title($"Obsah (souborů: {entries.Count(e => !e.IsDirectory)})");
            int limit = all ? int.MaxValue : 60, shown = 0;
            foreach (var e in entries)
            {
                if (shown++ >= limit) { Ui.Dim($"  … a dalších {entries.Count - limit} (celý výpis: info <soubor> --all)"); break; }
                string size = e.IsDirectory ? "<DIR>" : e.Size.ToString("N0").Replace(',', ' ');
                Ui.Write("  " + (e.Path + (e.IsDirectory ? "/" : "")).PadRight(40), e.IsDirectory ? ConsoleColor.Cyan : ConsoleColor.Gray);
                Ui.Write($"{size,10}  {e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""}", ConsoleColor.DarkGray, true);
            }
            if (entries.Count == 0) Ui.Dim("  (prázdná disketa)");
        }

        if (fat != null)
        {
            var entries = fat.ListAll();
            Ui.Title($"Obsah (souborů: {entries.Count(e => !e.IsDirectory)})");
            int limit = all ? int.MaxValue : 60, shown = 0;
            foreach (var e in entries)
            {
                if (shown++ >= limit) { Ui.Dim($"  … a dalších {entries.Count - limit} (celý výpis: info <soubor> --all)"); break; }
                string size = e.IsDirectory ? "<DIR>" : e.Size.ToString("N0").Replace(',', ' ');
                string date = e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
                Ui.Write("  " + (e.Path + (e.IsDirectory ? "\\" : "")).PadRight(40), e.IsDirectory ? ConsoleColor.Cyan : ConsoleColor.Gray);
                Ui.Write($"{size,10}  {date}", ConsoleColor.DarkGray, true);
            }
            if (entries.Count == 0) Ui.Dim("  (prázdná disketa)");
        }
        return 0;
    }

    private static void PrintBootSector(BootSector bs)
    {
        Ui.Field("Boot sektor", bs.DescribeStyle());
        if (bs.BpbLooksValid)
            Ui.Field("BPB", $"{bs.TotalSectors} sektorů, {bs.SectorsPerTrack} na stopu, {bs.Heads} strany, {bs.SectorsPerCluster * 512} B cluster, média 0x{bs.MediaDescriptor:X2}");
        Ui.Field("Spustitelný (Atari)", bs.IsExecutable ? "ANO — boot sektor obsahuje program (hra, demo, nebo boot virus)" : "ne",
                 bs.IsExecutable ? ConsoleColor.Yellow : ConsoleColor.Gray);
        Ui.Field("Sériové číslo (Atari)", $"{bs.AtariSerial:X6}");
        Ui.Field("Windows ji přečtou", bs.WindowsLikelyReadable ? "pravděpodobně ano" : "spíš ne (chybí PC signatura boot sektoru)");
    }

    public static int Read(string driveArg, string output, SideMode sides)
    {
        if (Backends.IsGwArg(driveArg)) return GwCommands.Read(driveArg, output, null);
        var drive = ResolveDrive(driveArg, Feature.Standard720);
        if (File.Exists(output) && !Ui.Confirm($"Soubor {output} už existuje. Přepsat?")) return 1;

        Ui.Title($"Čtení diskety z {drive.Id} do {Path.GetFileName(output)}");
        using var dev = Backends.OpenForRead(drive, write: false);
        var result = new OperationResult();
        DiskImage img;
        Busy = true;
        try { img = DiskOperations.ReadDisk(dev, sides, result, Ui.Progress, Cancel.Token); }
        finally { Busy = false; }
        Ui.EndProgress();
        ImageFile.Save(img, output);
        Report(result);
        Ui.Ok($"Uloženo: {output} ({img.Geometry})");
        return result.Success ? 0 : 4;
    }

    public static int Write(string path, string driveArg, bool format, bool verify)
    {
        if (Backends.IsGwArg(driveArg)) return GwCommands.Write(path, driveArg, null, verify);
        var loaded = ImageFile.Load(path);
        if (loaded.Image == null) throw new OperationException(loaded.NotLoadedReason ?? "Image nejde načíst.");
        var img = loaded.Image;
        var feature = img.Geometry.IsHighDensity ? Feature.Standard1440 : Feature.Standard720;
        var drive = ResolveDrive(driveArg, feature);

        var assessment = ImageAnalysis.AssessForStandardDrive(loaded);
        if (assessment.Verdict != Verdict.Writable && !(drive.FdrawcmdInstalled && !ImageAnalysis.FitsStandardDrive(img.Geometry) &&
                                                          FileSystemDetector.Detect(img.Data).Kind != FsKind.Amiga))
            throw new OperationException(assessment.Summary);

        Ui.Title($"Zápis {Path.GetFileName(path)} na {drive.Id}");
        Ui.Field("Image", img.Geometry.ToString());
        Ui.Field("Formátovat předem", format ? "ano" : "ne");
        Ui.Field("Kontrola po zápisu", verify ? "ano" : "ne");
        foreach (var n in assessment.Notes) Ui.Dim("  • " + n);
        if (!Ui.Confirm($"Všechna data na disketě {drive.Id} budou přepsána. Pokračovat?")) return 1;

        using var dev = Backends.OpenForGeometry(drive, img.Geometry);
        if (dev is AtariFloppy.Core.Drives.Fdraw.FdrawDevice && !format && dev.MediaGeometry != img.Geometry)
        {
            Ui.Dim("  Disketa má jiný formát než obraz — naformátuji ji na formát obrazu.");
            format = true;
        }
        var result = new OperationResult();
        Busy = true;
        try { DiskOperations.WriteImage(dev, img, format, verify, result, Ui.Progress, Cancel.Token); }
        finally { Busy = false; }
        Ui.EndProgress();
        Report(result);
        if (result.Success) Ui.Ok("Zápis dokončen" + (verify ? ", kontrola v pořádku." : "."));
        else Ui.Error("Zápis dokončen s chybami — disketa nejspíš není v pořádku.");
        return result.Success ? 0 : 4;
    }

    public static int Format(string driveArg, FormatStyle style, DiskGeometry g, bool quick, string? label, bool verify) =>
        ProfilesCommand.Format(driveArg, FormatProfiles.ForFat(style, g), quick, label, verify);

    public static int Convert(string input, string output)
    {
        var loaded = ImageFile.Load(input);
        if (loaded.Image == null) throw new OperationException(loaded.NotLoadedReason ?? "Image nejde načíst.");
        if (File.Exists(output) && !Ui.Confirm($"Soubor {output} už existuje. Přepsat?")) return 1;
        ImageFile.Save(loaded.Image, output);
        long size = new FileInfo(output).Length;
        Ui.Ok($"Převedeno: {Path.GetFileName(input)} ({ImageFile.Describe(loaded.Kind)}) → {Path.GetFileName(output)}, {size:N0} B".Replace(',', ' '));
        return 0;
    }

    public static int New(string path, FormatStyle style, string geometry, string? label)
    {
        if (!DiskGeometry.TryParse(geometry, out var g))
            throw new ArgumentException($"Neplatná geometrie „{geometry}“ — zadej ve tvaru stopy x strany x sektory, třeba 80x2x9.");
        if (File.Exists(path) && !Ui.Confirm($"Soubor {path} už existuje. Přepsat?")) return 1;
        var img = DiskFormatter.CreateBlank(g, style, label);
        ImageFile.Save(img, path);
        Ui.Ok($"Vytvořen prázdný image {Path.GetFileName(path)}: {g}, styl {StyleText(style)}.");
        return 0;
    }

    public static int NewFromProfile(string path, FormatProfile p, string? label)
    {
        if (p.Build == null) throw new OperationException($"{p.Title}: {p.Note ?? "tento formát program zatím neumí vytvořit."}");
        if (Path.GetExtension(path).Length == 0) path += p.ImageExtension;
        if (File.Exists(path) && !Ui.Confirm($"Soubor {path} už existuje. Přepsat?")) return 1;
        var img = p.Build(p.SupportsLabel ? label : null);
        ImageFile.Save(img, path);
        Ui.Ok($"Vytvořen prázdný obraz {Path.GetFileName(path)}: {p.Title} ({img.Geometry}).");
        if (p.Need != HardwareNeed.Standard)
            Ui.Dim("  Na disketu ho zapíše jen " + (p.Need == HardwareNeed.Flux ? "Greaseweazle" : "interní mechanika s fdrawcmd.sys nebo Greaseweazle") +
                   "; v emulátoru nebo na Goteku ho můžeš použít hned.");
        return 0;
    }

    public static int Extract(string path, string folder)
    {
        var loaded = ImageFile.Load(path);
        if (loaded.Image == null) throw new OperationException(loaded.NotLoadedReason ?? "Image nejde načíst.");
        int files = AtariFloppy.Core.App.Extractor.Extract(loaded.Image, folder);
        Ui.Ok($"Rozbaleno souborů: {files} → {Path.GetFullPath(folder)}");
        return 0;
    }

    // ================================================================ pomocné

    internal static DetectedDrive ResolveDrive(string arg, Feature needed)
    {
        string a = arg.Trim().TrimEnd(':', '\\').ToUpperInvariant();
        bool wantsGw = a is "GW" or "GREASEWEAZLE" || a.StartsWith("COM");
        var drives = DriveDetector.DetectAll(includeGreaseweazle: wantsGw);

        if (wantsGw)
        {
            var gw = drives.FirstOrDefault(d => d.Kind == DriveKind.Greaseweazle && (a.StartsWith("COM") ? d.Port == a : true));
            if (gw == null) throw new OperationException("Greaseweazle jsem nenašel. Je připojený k USB? (Ve Správci zařízení se ukazuje jako sériový port COM.)");
            return gw;
        }

        if (a.Length != 1 || a[0] < 'A' || a[0] > 'Z')
            throw new ArgumentException($"„{arg}“ není platná mechanika. Zadej písmeno jednotky, třeba A:");

        var drive = drives.FirstOrDefault(d => d.Letter == a);
        if (drive == null)
            throw new OperationException($"Jednotka {a}: není disketová mechanika (nebo není připojená). Spusť „dtk detect“.");

        var av = drive.Get(needed);
        if (av != Availability.Available)
            throw new OperationException($"{drive.Id} ({drive.KindText}): {Capabilities.Describe(needed)} — {Capabilities.Describe(av)}.");
        return drive;
    }

    internal static void Report(OperationResult r)
    {
        foreach (var n in r.Notes) Ui.Dim("  • " + n);
        foreach (var w in r.Warnings) Ui.Warn("  ! " + w);
        if (r.BadSectors.Count > 0)
        {
            Ui.Error($"  Vadné sektory ({r.BadSectors.Count}):");
            foreach (var b in r.BadSectors.Take(30)) Ui.Error("    " + b);
            if (r.BadSectors.Count > 30) Ui.Error($"    … a dalších {r.BadSectors.Count - 30}");
            Ui.Dim("  Vadné sektory jsou v image vyplněné nulami.");
        }
        if (r.VerifyErrors.Count > 0)
        {
            Ui.Error($"  Kontrola po zápisu našla chyby ({r.VerifyErrors.Count}):");
            foreach (var b in r.VerifyErrors.Take(30)) Ui.Error("    " + b);
        }
    }

    public static string StyleText(FormatStyle s) => s switch
    {
        FormatStyle.Atari => "Atari (jako TOS)",
        FormatStyle.Pc => "PC (jako Windows)",
        _ => "hybrid (čte Atari i Windows)",
    };

    private static FormatStyle ParseStyle(Args a) =>
        a.Flag("atari") ? FormatStyle.Atari : a.Flag("pc") ? FormatStyle.Pc : FormatStyle.Hybrid;

    private static DiskGeometry ParseSize(Args a)
    {
        if (a.Flag("hd")) return DiskGeometry.Hd1440;
        return a.Opt("size") switch
        {
            null or "720" => DiskGeometry.Dd720,
            "640" => DiskGeometry.Dd640,
            "1440" or "1.44" or "1,44" => DiskGeometry.Hd1440,
            var x => throw new ArgumentException($"--size {x}: umím 640, 720 nebo 1440."),
        };
    }

    private static WriteTestMode ParseWriteTest(string? s) => s?.ToLowerInvariant() switch
    {
        null => WriteTestMode.None,
        "obnova" or "preserve" => WriteTestMode.PreserveData,
        "prazdna" or "prázdná" or "destructive" => WriteTestMode.Destructive,
        var x => throw new ArgumentException($"--write-test {x}: použij „obnova“ (data zůstanou) nebo „prazdna“ (smaže disketu)."),
    };

    private static SideMode ParseSides(string? s) => s switch
    {
        null or "auto" => SideMode.Auto,
        "1" => SideMode.Single,
        "2" => SideMode.Double,
        _ => throw new ArgumentException("--sides musí být auto, 1 nebo 2."),
    };

    public static void Help()
    {
        Ui.Title($"Diskette Toolkit {Version} — diskety a obrazy pro Atari ST a další 3,5\" systémy");
        Console.WriteLine(@"
Bez parametrů se spustí jednoduché menu.

  dtk detect                         zjistí mechaniky a co umí
  dtk info <image> [--all]           popis image, verdikt zápisu, výpis souborů
  dtk read <A:> <soubor.st|.msa|.dim> [--sides auto|1|2]
                                         načte disketu do image
  dtk write <image> <A:> [--format] [--no-verify]
                                         zapíše image na disketu
  dtk format <A:> [--hybrid|--atari|--pc] [--size 640|720|1440] [--quick] [--label JMENO] [--no-verify]
                                         naformátuje disketu (výchozí: hybrid, 720 KB)
  dtk format <A:> --profile <id> [--label JMENO]
                                         formát jiného systému: msx, mac-hfs, pcw, minix, ext2 …
  dtk profiles [A:]                  všechny formáty; s mechanikou ukáže, co jde s vloženou disketou
  dtk check <A:> [--write-test obnova|prazdna] [--repair] [--save obraz.st]
                                         test povrchu + mapa + kontrola souborového systému
  dtk check <obraz> [--repair]       kontrola obrazu, oprava do nového souboru
  dtk formats                        přehled 3,5"" formátů různých počítačů
  dtk convert <vstup> <výstup>       převod mezi .ST, .MSA, .DIM, .IMG a .ADF
  dtk new <soubor> [--geometry 80x2x9] [--hybrid|--atari|--pc] [--label JMENO]
                                         vytvoří prázdný image
  dtk new <soubor> --profile <id>    prázdný obraz libovolného systému (amiga, amiga-ofs, mac-800, msx …)
  dtk extract <image> <složka>       rozbalí soubory z image

  dtk read GW <soubor.st|.adf|.scp|.hfe> [--profile <id>]
                                         záloha přes Greaseweazle (formát pozná sám; .scp/.hfe = tok i s ochranami)
  dtk write <obraz> GW [--profile <id>]   zápis přes Greaseweazle (.st .msa .adf .img .d81 .scp .hfe .ipf)
  dtk check GW [--repair]            test povrchu a kontrola přes Greaseweazle
  dtk erase GW                       magnetické smazání diskety
  dtk boot freedos A:                spouštěcí disketa FreeDOS (soubory se jednou stáhnou z freedos.org / ibiblio)
  dtk boot freedos A: --no-tools      jen DOS bez nástrojů (FORMAT, FDISK, SYS, EDIT … se jinak přidají)
  dtk boot msdos A:                  spouštěcí disketa MS-DOS z Windows (jako dřív „Vytvořit spouštěcí disketu“)
  dtk boot msdos A: --diskcopy C:\stare\diskcopy.dll   diskcopy.dll ze starších Windows
  dtk boot amiga hra.adf             spustitelná Amiga disketa (install); amiga13 = pro Kickstart 1.3
  dtk boot atari A: --prg HRA.PRG    Atari disketa, která po zapnutí sama spustí program (složka AUTO)
                                         místo A: jde zadat soubor (.img/.st/.adf) nebo GW; --size 720|1440
  dtk install gw                     stáhne / aktualizuje Greaseweazle Host Tools (gw.exe) z GitHubu
  dtk firmware GW                    aktualizuje firmware v Greaseweazle
  dtk install fdrawcmd               stáhne a spustí instalátor ovladače fdrawcmd.sys (simonowen.com)

  --yes            neptat se na potvrzení
  --fdraw          u interní mechaniky vždy použít fdrawcmd.sys
  --step-rate N    rychlost krokování hlavy v ms (fdrawcmd / Greaseweazle; Atari 3, bezpečně 6)
  --gw-drive A|B|0|1   mechanika na Greaseweazle (A = PC kabel, výchozí)
  --gw <cesta>     kde je gw.exe (jinak se hledá vedle programu a ve složce greaseweazle)
  --revs N / --tracks N   otáček a stop při záloze toku (výchozí 3 a 82)

Styly formátu:
  hybrid  PC boot sektor + unikátní Atari sériové číslo — čte Windows i Atari (doporučeno)
  atari   přesně jako formát z TOS desktopu (Windows ho nemusí přečíst)
  pc      přesně jako Windows (na Atari funguje od TOS 1.04)");
    }
}

/// <summary>Jednoduchý rozbor parametrů: pozice + --volby.</summary>
internal sealed class Args
{
    private readonly List<string> _pos = new();
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; }

    public Args(string[] argv)
    {
        Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : "help";
        string[] valued = { "sides", "label", "geometry", "size", "write-test", "save", "profile", "step-rate", "gw-drive", "gw", "revs", "tracks" };
        for (int i = 1; i < argv.Length; i++)
        {
            string s = argv[i];
            if (s.StartsWith("--") || (s.StartsWith("-") && s.Length == 2 && !char.IsDigit(s[1])))
            {
                string key = s.TrimStart('-');
                int eq = key.IndexOf('=');
                if (eq > 0) { _opts[key.Substring(0, eq)] = key.Substring(eq + 1); continue; }
                if (valued.Contains(key.ToLowerInvariant()) && i + 1 < argv.Length) _opts[key] = argv[++i];
                else _opts[key] = null;
            }
            else _pos.Add(s);
        }
    }

    public string Pos(int i, string what) =>
        i < _pos.Count ? _pos[i] : throw new ArgumentException($"Chybí parametr: {what}. Nápověda: dtk help");

    public string? PosOrNull(int i) => i < _pos.Count ? _pos[i] : null;

    public bool Flag(params string[] names) => names.Any(n => _opts.ContainsKey(n));
    public string? Opt(string name) => _opts.TryGetValue(name, out var v) ? v : null;
}
