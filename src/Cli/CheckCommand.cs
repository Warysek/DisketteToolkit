using System;
using System.IO;
using System.Linq;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;

namespace AtariFloppy.Cli;

/// <summary>Test povrchu, kontrola a oprava souborového systému, přehled formátů.</summary>
internal static class CheckCommand
{
    // ================================================================ disketa

    public static int CheckDisk(string driveArg, WriteTestMode mode, bool repair, string? saveImage)
    {
        if (Backends.IsGwArg(driveArg)) return GwCommands.Check(driveArg, mode, repair, saveImage, null);
        var drive = Program.ResolveDrive(driveArg, Feature.Standard720);

        Ui.Title($"Test povrchu a kontrola — {drive.Id} ({drive.Name})");
        Ui.Field("Čtecí test", "celá disketa, u chyb až " + SurfaceTest.ScanRetries + " pokusů na sektor");
        Ui.Field("Zápisový test", mode switch
        {
            WriteTestMode.PreserveData => "s obnovou dat (vzorky 55/AA, pak se vrátí původní obsah)",
            WriteTestMode.Destructive => "DESTRUKTIVNÍ (5 vzorků, disketa se pak naformátuje znovu)",
            _ => "ne",
        }, mode == WriteTestMode.Destructive ? ConsoleColor.Red : ConsoleColor.Gray);
        Ui.Field("Oprava", repair ? "ano, pokud to bude bezpečné (s potvrzením)" : "ne, jen výpis");

        if (mode == WriteTestMode.Destructive && !Ui.Confirm($"Destruktivní test SMAŽE všechna data na {drive.Id}. Pokračovat?")) return 1;
        if (mode == WriteTestMode.PreserveData && !Ui.Confirm("Test s obnovou dočasně přepisuje stopy. Před zápisem uložím zálohu diskety. Pokračovat?")) return 1;

        using var dev = Backends.OpenForRead(drive, write: mode != WriteTestMode.None || repair);
        string? backupPath = null;
        SurfaceTestResult r;
        Program.Busy = true;
        try
        {
            r = SurfaceTest.Run(dev, mode, Ui.Progress, Program.Cancel.Token, beforeWrite: res =>
            {
                backupPath = SaveBackup(res.Contents, drive);
                Ui.EndProgress();
                Ui.Dim($"  Záloha před zápisovým testem: {backupPath}");
            });
        }
        finally { Program.Busy = false; }
        Ui.EndProgress();

        if (saveImage != null)
        {
            ImageFile.Save(r.Contents, saveImage);
            Ui.Dim($"  Obsah diskety uložen do {saveImage}");
        }

        PrintMap(r.Map);
        PrintSurfaceSummary(r);

        if (mode == WriteTestMode.Destructive)
            return RebuildAfterDestructive(dev, r);

        var fs = FileSystemDetector.Detect(r.Contents.Data);
        PrintFileSystem(fs);
        if (!fs.CanCheck)
        {
            Ui.Dim("  Kontrolu souborového systému umím zatím jen u FAT12 (Atari, PC, MSX). Test povrchu výše platí pro každou disketu.");
            return r.Map.Problems.Any() ? 4 : 0;
        }

        var report = FatDoctor.Check(r.Contents.Data, r.Map);
        PrintReport(report);

        if (!report.HasRepairable) return report.HasProblems || r.Map.Problems.Any() ? 4 : 0;
        if (!repair)
        {
            Ui.Dim("\n  Opravit: dtk check " + drive.Id + " --repair");
            return 4;
        }
        return RepairDisk(dev, drive, r, report, backupPath);
    }

    private static int RepairDisk(ISectorDevice dev, DetectedDrive drive, SurfaceTestResult r, FatCheckReport report, string? backupPath)
    {
        Ui.Title("Oprava");
        if (report.SystemAreaDamaged)
        {
            Ui.Error("  Opravu na této disketě neudělám: poškozená je systémová oblast (boot / FAT / adresář).");
            Ui.Error("  Zápis na slábnoucí místa by mohl zničit i to, co se ještě dá přečíst.");
            string img = backupPath ?? SaveBackup(r.Contents, drive);
            Ui.Warn($"  Obsah je uložený v {img}. Doporučený postup:");
            Ui.Warn($"    1) dtk check \"{img}\" --repair      (oprava obrazu)");
            Ui.Warn($"    2) dtk write <opravený obraz> {drive.Id} --format   (na JINOU, dobrou disketu)");
            return 4;
        }

        var plan = AtariFloppy.Core.App.Repair.PlanDisk(r.Contents, r.Map, report);
        if (plan.Refusal != null)
            throw new OperationException(plan.Refusal + " (dtk check <obraz> --repair)");
        var repaired = plan.Repaired;
        var changed = plan.ChangedSectors;
        if (changed.Count == 0) { Ui.Ok("  Není co zapsat."); return 0; }

        Ui.Write("  Oprava udělá:", ConsoleColor.Gray, true);
        foreach (var a in plan.Actions) Ui.Write("   • " + a, ConsoleColor.Cyan, true);
        Ui.Dim($"  Změní se {changed.Count} sektorů (FAT a adresáře), data souborů zůstanou.");
        backupPath ??= SaveBackup(r.Contents, drive);
        Ui.Dim($"  Záloha diskety před opravou: {backupPath}");
        if (!Ui.Confirm("Zapsat opravu na disketu?")) return 1;

        var result = new OperationResult();
        Program.Busy = true;
        try { DiskOperations.WriteSectorList(dev, repaired, changed, result, Ui.Progress, Program.Cancel.Token); }
        finally { Program.Busy = false; }
        Ui.EndProgress();
        Program.Report(result);
        if (!result.Success)
        {
            Ui.Error($"  Některé sektory se nepodařilo zapsat. Původní stav je v {backupPath} — zapiš ho na jinou disketu.");
            return 4;
        }

        var after = FatDoctor.Check(repaired, r.Map);
        if (!after.HasRepairable) Ui.Ok("  Oprava zapsaná a ověřená. Souborový systém je v pořádku" +
                                        (after.DamagedFiles.Count > 0 ? " (kromě souborů na vadných sektorech — ty už zachránit nejde)." : "."));
        else Ui.Warn("  Oprava zapsaná, ale kontrola pořád něco hlásí — spusť kontrolu znovu.");
        return 0;
    }

    private static int RebuildAfterDestructive(ISectorDevice dev, SurfaceTestResult r)
    {
        Ui.Title("Nový souborový systém");
        try
        {
            var (fresh, style, marked, sys) = AtariFloppy.Core.App.Repair.RebuildAfterDestructive(r);
            var result = new OperationResult();
            DiskOperations.WriteSectorList(dev, fresh.Data, Enumerable.Range(0, sys).Select(i => (long)i).ToList(), result);
            Program.Report(result);
            if (!result.Success) { Ui.Error("  Nový souborový systém se nepodařilo zapsat — disketa není použitelná, naformátuj ji znovu."); return 4; }
            Ui.Ok($"  Disketa naformátovaná ({Program.StyleText(style)})" + (marked > 0 ? $", {marked} vadných clusterů označeno ve FAT." : "."));
            return r.Map.Problems.Any() ? 4 : 0;
        }
        catch (OperationException e)
        {
            Ui.Error("  " + e.Message);
            return 4;
        }
    }

    // ================================================================ image

    public static int CheckImage(string path, bool repair)
    {
        var loaded = ImageFile.Load(path);
        Ui.Title("Kontrola obrazu: " + Path.GetFileName(path));
        if (loaded.Image == null) throw new OperationException(loaded.NotLoadedReason ?? "Obraz nejde načíst.");
        Ui.Field("Geometrie", loaded.Image.Geometry.ToString());

        var fs = FileSystemDetector.Detect(loaded.Image.Data);
        PrintFileSystem(fs);
        if (!fs.CanCheck)
        {
            Ui.Dim("  Kontrolu souborového systému umím zatím jen u FAT12 (Atari, PC, MSX).");
            return 0;
        }
        var report = FatDoctor.Check(loaded.Image.Data);
        PrintReport(report);
        if (!report.HasRepairable) return report.HasProblems ? 4 : 0;
        if (!repair) { Ui.Dim($"\n  Opravit: dtk check \"{path}\" --repair   (původní soubor zůstane beze změny)"); return 4; }

        var repaired = (byte[])loaded.Image.Data.Clone();
        var done = FatDoctor.Repair(repaired);
        Ui.Title("Oprava");
        foreach (var a in done.Actions) Ui.Write("   • " + a, ConsoleColor.Cyan, true);

        string output = AtariFloppy.Core.App.Repair.RepairedImagePath(path, loaded.Kind);
        if (File.Exists(output) && !Ui.Confirm($"Soubor {output} už existuje. Přepsat?")) return 1;
        ImageFile.Save(new DiskImage(loaded.Image.Geometry, repaired), output);

        var after = FatDoctor.Check(repaired);
        Ui.Ok($"  Opravený obraz: {output}" + (after.HasRepairable ? " (kontrola pořád něco hlásí)" : " — souborový systém je v pořádku."));
        Ui.Dim("  Původní soubor zůstal beze změny.");
        return 0;
    }

    // ================================================================ výpisy

    public static void PrintMap(SurfaceMap map)
    {
        var g = map.Geometry;
        Ui.Title("Mapa diskety (každý znak = jedna stopa)");
        Ui.Write("           ", ConsoleColor.DarkGray);
        for (int t = 0; t < g.Tracks; t++)
            Ui.Write(t % 10 == 0 ? (t / 10).ToString() : " ", ConsoleColor.DarkGray);
        Console.WriteLine();
        Ui.Write("           ", ConsoleColor.DarkGray);
        for (int t = 0; t < g.Tracks; t++) Ui.Write((t % 10).ToString(), ConsoleColor.DarkGray);
        Console.WriteLine();
        for (int s = 0; s < g.Sides; s++)
        {
            Ui.Write($"  strana {s}  ", ConsoleColor.DarkGray);
            for (int t = 0; t < g.Tracks; t++)
            {
                var (ch, col) = Glyph(map.Worst(t, s));
                Ui.Write(ch, col);
            }
            Console.WriteLine();
        }
        Ui.Write("  ", ConsoleColor.Gray);
        foreach (var st in new[] { SectorStatus.Ok, SectorStatus.Weak, SectorStatus.Bad, SectorStatus.WriteFailed })
        {
            var (ch, col) = Glyph(st);
            Ui.Write(ch, col);
            Ui.Write(" " + Name(st) + "   ", ConsoleColor.DarkGray);
        }
        Console.WriteLine();
    }

    private static (string, ConsoleColor) Glyph(SectorStatus s) => s switch
    {
        SectorStatus.Ok => ("█", ConsoleColor.DarkGreen),
        SectorStatus.Weak => ("█", ConsoleColor.Yellow),
        SectorStatus.Bad => ("█", ConsoleColor.Red),
        SectorStatus.WriteFailed => ("█", ConsoleColor.Magenta),
        _ => ("·", ConsoleColor.DarkGray),
    };

    private static string Name(SectorStatus s) => s switch
    {
        SectorStatus.Ok => "v pořádku",
        SectorStatus.Weak => "slabý (čte se až napodruhé)",
        SectorStatus.Bad => "nečitelný",
        SectorStatus.WriteFailed => "selhal zápis",
        _ => "netestováno",
    };

    internal static void PrintSurfaceSummary(SurfaceTestResult r)
    {
        var m = r.Map;
        Console.WriteLine();
        int ok = m.Count(SectorStatus.Ok), weak = m.Count(SectorStatus.Weak), bad = m.Count(SectorStatus.Bad), wf = m.Count(SectorStatus.WriteFailed);
        Ui.Field("Sektorů", $"{m.Geometry.TotalSectors}: v pořádku {ok}, slabých {weak}, nečitelných {bad}" + (wf > 0 ? $", selhal zápis {wf}" : ""),
                 bad + wf > 0 ? ConsoleColor.Red : weak > 0 ? ConsoleColor.Yellow : ConsoleColor.Green);
        var problems = m.Problems.ToList();
        foreach (long l in problems.Take(20))
        {
            var (t, s, n) = m.Chs(l);
            Ui.Dim($"    stopa {t,2}, strana {s}, sektor {n,2}: {Name(m[l])}" + (m.Attempts(l) > 1 && m[l] == SectorStatus.Weak ? $" ({m.Attempts(l)}. pokus)" : ""));
        }
        if (problems.Count > 20) Ui.Dim($"    … a dalších {problems.Count - 20}");
        foreach (var w in r.Warnings) Ui.Warn("  ! " + w);
        if (r.RestoreFailures.Count > 0)
            Ui.Error($"  Na {r.RestoreFailures.Count} sektorů se nepodařilo vrátit původní data — jsou v záloze.");

        string verdict = bad + wf > 0 ? "Disketa má vadná místa — na důležitá data ji už nepoužívej."
                       : weak > 0 ? "Disketa slábne — data z ní zkopíruj, na nová data ji raději nepoužívej."
                       : "Povrch je v pořádku.";
        Ui.Write("  " + verdict, bad + wf > 0 ? ConsoleColor.Red : weak > 0 ? ConsoleColor.Yellow : ConsoleColor.Green, true);
    }

    public static void PrintFileSystem(FsDetection fs)
    {
        Ui.Title("Souborový systém");
        Ui.Field("Systém", fs.Name, ConsoleColor.White);
        if (fs.Details != null) Ui.Field("Detaily", fs.Details);
        Ui.Field("K čemu", fs.UsedFor);
    }

    public static void PrintReport(FatCheckReport rep)
    {
        if (!rep.IsFat)
        {
            foreach (var i in rep.Issues) Ui.Error("  ✗ " + i.Message);
            return;
        }
        Ui.Field("Obsah", $"souborů {rep.Files}, adresářů {rep.Directories}, obsazeno {rep.UsedBytes / 1024} KB, volno {rep.FreeBytes / 1024} KB");
        Console.WriteLine();
        if (rep.Issues.Count == 0) { Ui.Ok("  ✓ Souborový systém je v pořádku."); return; }

        foreach (var i in rep.Issues.OrderByDescending(i => i.Severity))
        {
            var (mark, col) = i.Severity switch
            {
                IssueSeverity.Error => ("  ✗ ", ConsoleColor.Red),
                IssueSeverity.Warning => ("  ! ", ConsoleColor.Yellow),
                _ => ("  i ", ConsoleColor.DarkGray),
            };
            Ui.Write(mark + i.Message, col);
            if (i.Repairable) Ui.Write("  [lze opravit]", ConsoleColor.Cyan);
            Console.WriteLine();
        }
        int err = rep.Count(IssueSeverity.Error), warn = rep.Count(IssueSeverity.Warning);
        Console.WriteLine();
        if (err + warn == 0) Ui.Ok("  ✓ Souborový systém je v pořádku (jen informace).");
        else Ui.Write($"  Chyb: {err}, varování: {warn}" + (rep.HasRepairable ? $", opravitelných: {rep.Issues.Count(i => i.Repairable)}" : ""),
                      err > 0 ? ConsoleColor.Red : ConsoleColor.Yellow, true);
    }

    public static int Formats()
    {
        Ui.Title("3,5\" diskety různých počítačů — co s čím přečteš");
        Ui.Dim("  Sloupce: USB mechanika | interní mechanika s fdrawcmd.sys | Greaseweazle   (✓ ano, ? záleží, ✗ ne)");
        foreach (var f in FormatCatalog.All)
        {
            Console.WriteLine();
            Ui.Write("  " + f.System, ConsoleColor.White);
            Ui.Write(" — " + f.Format, ConsoleColor.Cyan, true);
            Ui.Write("    " + f.UsedFor, ConsoleColor.Gray, true);
            Ui.Write("    USB ", ConsoleColor.DarkGray); Mark(f.Usb);
            Ui.Write("   interní ", ConsoleColor.DarkGray); Mark(f.InternalFdraw);
            Ui.Write("   Greaseweazle ", ConsoleColor.DarkGray); Mark(f.Greaseweazle);
            if (f.Note != null) Ui.Write("   (" + f.Note + ")", ConsoleColor.DarkGray);
            Console.WriteLine();
        }
        return 0;
    }

    private static void Mark(Availability a)
    {
        var (s, c) = a switch
        {
            Availability.Available => ("✓", ConsoleColor.Green),
            Availability.Maybe => ("?", ConsoleColor.Yellow),
            _ => ("✗", ConsoleColor.DarkGray),
        };
        Ui.Write(s, c);
    }

    internal static string SaveBackup(DiskImage img, DetectedDrive drive) => AtariFloppy.Core.App.Backups.Save(img, AtariFloppy.Core.App.Backups.DriveTag(drive));
}
