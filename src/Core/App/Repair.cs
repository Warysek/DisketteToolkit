using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Plán opravy souborového systému na disketě — co se zapíše, nebo proč to neuděláme.</summary>
public sealed class DiskRepairPlan
{
    /// <summary>Když není null, opravu na disketu zapsat nejde (poškozená systémová oblast, zápis na vadná místa).</summary>
    public string? Refusal { get; init; }
    public byte[] Repaired { get; init; } = Array.Empty<byte>();
    public List<long> ChangedSectors { get; init; } = new();
    public List<string> Actions { get; init; } = new();
}

public static class Repair
{
    /// <summary>
    /// Bezpečná oprava diskety: jen když je systémová oblast v pořádku a měnit se budou jen zdravé sektory.
    /// Jinak vrátí důvod — správný postup je pak opravit obraz a zapsat ho na jinou disketu.
    /// </summary>
    public static DiskRepairPlan PlanDisk(DiskImage contents, SurfaceMap map, FatCheckReport report)
    {
        if (report.SystemAreaDamaged)
            return new DiskRepairPlan
            {
                Refusal = Tr.T("Opravu na této disketě neudělám: poškozená je systémová oblast (boot / FAT / adresář). Zápis na slábnoucí místa by mohl zničit i to, co se ještě dá přečíst. Ulož obraz, oprav ho a zapiš na jinou, dobrou disketu."),
            };
        var repaired = (byte[])contents.Data.Clone();
        var done = FatDoctor.Repair(repaired, map);
        var changed = FatDoctor.ChangedSectors(contents.Data, repaired);
        if (changed.Any(l => map[l] != SectorStatus.Ok))
            return new DiskRepairPlan
            {
                Refusal = Tr.T("Oprava by musela zapisovat na vadné/slabé sektory — neudělám ji na disketě. Oprav raději obraz a zapiš ho na jinou disketu."),
            };
        return new DiskRepairPlan { Repaired = repaired, ChangedSectors = changed, Actions = done.Actions.ToList() };
    }

    /// <summary>Opravený obraz uloží vedle původního jako „…-opraveno“; původní soubor se nemění.</summary>
    public static string RepairedImagePath(string path, ImageKind kind)
    {
        string ext = Path.GetExtension(path);
        if (kind == ImageKind.Dim || string.IsNullOrEmpty(ext)) ext = ".st";
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                            Path.GetFileNameWithoutExtension(path) + Tr.T("-opraveno") + ext);
    }

    /// <summary>Po destruktivním testu: nový souborový systém s označenými vadnými clustery.</summary>
    public static (DiskImage image, FormatStyle style, int marked, int systemSectors) RebuildAfterDestructive(SurfaceTestResult r)
    {
        var g = r.Map.Geometry;
        var oldBoot = r.Contents.BootSector;
        var style = oldBoot.BpbLooksValid && r.Contents.Data[0] == 0xE9 && r.Contents.Data[2] == 0x4E ? FormatStyle.Atari : FormatStyle.Hybrid;
        var fresh = DiskOperations.BuildFilesystem(g, style, null, r.Map.Problems, out int marked);
        var layout = DiskFormatter.ComputeLayout(g, style);
        return (fresh, style, marked, layout.SystemSectors);
    }

    /// <summary>Prázdný obsah stejného formátu (zápisový test přes Greaseweazle).</summary>
    public static DiskImage BlankLike(DiskImage current, bool amiga)
    {
        var g = current.Geometry;
        if (amiga) return AmigaDos.CreateBlank(g, AmigaFsType.Ffs, null);
        if (g.BytesPerSector == 512 && g.Sides == 2)
            return DiskFormatter.CreateBlank(g, current.Data[0] == 0xE9 ? FormatStyle.Atari : FormatStyle.Hybrid, null);
        return new DiskImage(g);
    }
}

public static class Backups
{
    /// <summary>Uloží zálohu diskety (zaloha-A-20260929-120000.st) do složky (null = aktuální složka).</summary>
    public static string Save(DiskImage img, string driveTag, string? folder = null)
    {
        string path = SuggestPath(img, driveTag, folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ImageFile.Save(img, path);
        return path;
    }

    /// <summary>Navržený název zálohy: zaloha-A-20260929-120000.st (Amiga .adf).</summary>
    public static string SuggestPath(DiskImage? img, string driveTag, string? folder = null)
    {
        string ext = img != null && FileSystemDetector.Detect(img.Data).Kind == FsKind.Amiga ? ".adf"
                   : img == null || img.Geometry.BytesPerSector == 512 ? ".st" : ".img";
        string name = Tr.T("zaloha") + $"-{driveTag}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
        return Path.GetFullPath(Path.Combine(folder ?? Directory.GetCurrentDirectory(), name));
    }

    public static string DriveTag(DetectedDrive d) => d.Letter ?? "GW";
}
