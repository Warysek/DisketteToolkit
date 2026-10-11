using System.Collections.Generic;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Analysis;

public enum Verdict
{
    /// <summary>Zelená: jde zapsat 1:1.</summary>
    Writable,
    /// <summary>Žlutá: 1:1 ne, ale soubory se dají přenést na standardní disketu.</summary>
    ContentOnly,
    /// <summary>Červená: nejde (ochrana / neznámý obsah).</summary>
    NotWritable,
}

public sealed class WriteAssessment
{
    public Verdict Verdict { get; init; }
    public string Summary { get; init; } = "";
    public List<string> Notes { get; } = new();
}

public static class ImageAnalysis
{
    /// <summary>Lze image zapsat přes standardní (Windows) přístup — USB nebo interní mechanika bez fdrawcmd?</summary>
    public static bool FitsStandardDrive(DiskGeometry g) => Drives.StandardGeometry.Supports(g);

    public static WriteAssessment AssessForStandardDrive(LoadedImage loaded)
    {
        if (loaded.Image == null)
        {
            var a = new WriteAssessment
            {
                Verdict = Verdict.NotWritable,
                Summary = loaded.Kind is ImageKind.Stx or ImageKind.Ipf or ImageKind.Scp or ImageKind.KryoFlux or ImageKind.Hfe
                    ? Tr.T("Formát zatím jen rozpoznaný — zápis bude možný po doplnění čtení tohoto formátu (neochráněné disky i na USB, chráněné jen přes Greaseweazle).")
                    : Tr.T("Image se nepodařilo načíst."),
            };
            if (loaded.NotLoadedReason != null) a.Notes.Add(loaded.NotLoadedReason);
            return a;
        }

        var g = loaded.Image.Geometry;
        var fat = Fat12Volume.TryOpen(loaded.Image.Data, out _);

        if (FitsStandardDrive(g))
        {
            var a = new WriteAssessment
            {
                Verdict = Verdict.Writable,
                Summary = g.IsHighDensity
                    ? Tr.T("Lze zapsat na USB i interní mechaniku — potřebuješ HD disketu (1,44 MB).")
                    : Tr.T("Lze zapsat na USB i interní mechaniku (720 KB, DD nebo přelepená HD disketa)."),
            };
            if (g.Sides == 1) a.Notes.Add(Tr.T("Jednostranný image: zapíše se na stranu 0 oboustranně naformátované diskety — Atari ho přečte i v jednostranné mechanice."));
            if (g.Tracks < 80) a.Notes.Add(Tr.F("Image má jen {0} stop, zbytek diskety zůstane nevyužitý.", g.Tracks));
            var fsKind = FileSystemDetector.Detect(loaded.Image.Data).Kind;
            if (fsKind is FsKind.OwnLoader or FsKind.Unknown)
                a.Notes.Add(Tr.T("Boot sektor nemá souborový systém (typicky hra/demo s vlastním zavaděčem). Pokud nejde o ochranu, zápis 1:1 bude fungovat."));
            return a;
        }

        if (fat != null)
        {
            long used = (long)fat.UsedClusters * fat.ClusterSize;
            bool fits = used <= 713 * 1024;
            var a = new WriteAssessment
            {
                Verdict = fits ? Verdict.ContentOnly : Verdict.NotWritable,
                Summary = fits
                    ? Tr.F("Formát {0} standardní mechanika 1:1 nezapíše. Soubory ({1} KB) se ale vejdou na standardní 720 KB disketu — přebalení přijde v další verzi.", g.Short, used / 1024)
                    : Tr.F("Formát {0} standardní mechanika nezapíše a obsah ({1} KB) se na 720 KB disketu nevejde.", g.Short, used / 1024),
            };
            a.Notes.Add(Tr.T("Zápis 1:1 umí interní mechanika s fdrawcmd.sys nebo Greaseweazle."));
            return a;
        }

        if (FileSystemDetector.Detect(loaded.Image.Data).Kind == FsKind.Amiga)
            return new WriteAssessment
            {
                Verdict = Verdict.NotWritable,
                Summary = Tr.T("Disketa pro Amigu — Amiga zapisuje celou stopu jako jeden blok, což PC ani USB mechanika neumí. Na disketu ji zapíše jen Greaseweazle; v emulátoru (WinUAE) nebo na Goteku ji můžeš použít hned. Pro přenos souborů na skutečnou Amigu přes USB použij formát CrossDOS (--profile amiga-crossdos)."),
            };

        return new WriteAssessment
        {
            Verdict = Verdict.NotWritable,
            Summary = Tr.F("Formát {0} bez čitelného souborového systému — standardní mechanika ho nezapíše (nutná interní mechanika s fdrawcmd.sys nebo Greaseweazle).", g.Short),
        };
    }
}
