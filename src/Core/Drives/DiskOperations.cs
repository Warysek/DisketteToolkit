using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

/// <summary>Chyba, kterou má smysl ukázat uživateli tak, jak je.</summary>
public sealed class OperationException : Exception
{
    public OperationException(string message) : base(message) { }
}

public sealed record BadSector(int Track, int Side, int Sector, string Error)
{
    public override string ToString() => Tr.F("stopa {0}, strana {1}, sektor {2}: {3}", Track, Side, Sector, Error);
}

public sealed class OperationResult
{
    public List<BadSector> BadSectors { get; } = new();
    public List<BadSector> VerifyErrors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Success => BadSectors.Count == 0 && VerifyErrors.Count == 0;
}

public enum SideMode { Auto, Single, Double }

/// <summary>Stav průběhu: hotovo / celkem stop (stopa × strana) a popis.</summary>
public delegate void ProgressHandler(int done, int total, string text);

/// <summary>Čtení, zápis, formát a kontrola — nezávisle na typu mechaniky.</summary>
public static class DiskOperations
{
    public const int Retries = 3;

    // ------------------------------------------------------------------ čtení

    public static DiskImage ReadDisk(ISectorDevice dev, SideMode sideMode, OperationResult result,
                                     ProgressHandler? progress = null, CancellationToken ct = default)
    {
        dev.RefreshMedia();
        var media = RequireMedia(dev);

        // Boot sektor napoví, jestli je disketa jednostranná a jestli má standardní formát.
        BootSector? bs = null;
        try { bs = new BootSector(ReadWithRetry(dev, 0, 1)); }
        catch (SectorIOException e) { result.Warnings.Add(Tr.T("Boot sektor nejde přečíst: ") + e.Message); }

        int sides = media.Sides;
        if (sideMode == SideMode.Single) sides = 1;
        else if (sideMode == SideMode.Auto && bs != null && bs.BpbLooksValid && bs.Heads == 1 && bs.SectorsPerTrack == media.SectorsPerTrack)
        {
            sides = 1;
            result.Notes.Add(Tr.T("Boot sektor udává jednostrannou disketu — čtu jen stranu 0 (image 360 KB)."));
        }
        sides = Math.Min(sides, media.Sides);

        if (bs != null && bs.BpbLooksValid)
        {
            if (bs.SectorsPerTrack != media.SectorsPerTrack)
                result.Warnings.Add(Tr.F("Boot sektor udává {0} sektorů na stopu, mechanika čte {1}. Disketa má nestandardní formát a image bude neúplný — takovou disketu přečte jen interní mechanika s fdrawcmd.sys nebo Greaseweazle.", bs.SectorsPerTrack, media.SectorsPerTrack));
            else if (bs.TotalSectors > media.Tracks * sides * media.SectorsPerTrack)
                result.Warnings.Add(Tr.F("Boot sektor udává {0} sektorů, ale mechanika přečte jen {1} stop. Disketa má stopy navíc (81+) a ty v image chybět budou.", bs.TotalSectors, media.Tracks));
        }
        else if (bs != null)
        {
            result.Notes.Add(Tr.T("Boot sektor nemá platný souborový systém (vlastní zavaděč hry/dema, nebo ochrana). Čtu celou disketu."));
        }

        var g = new DiskGeometry(media.Tracks, sides, media.SectorsPerTrack, media.BytesPerSector);
        var img = new DiskImage(g);
        int total = g.TrackCount, done = 0;

        TrackActivity.Begin(g, Tr.T("Čtení diskety"));
        try
        {
            for (int t = 0; t < g.Tracks; t++)
                for (int s = 0; s < g.Sides; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(done, total, Tr.F("Čtu stopu {0}, stranu {1}", t, s));
                    TrackActivity.Head(t, s, TrackPhase.Reading);
                    img.WriteTrack(t, s, ReadTrack(dev, media, t, s, result));
                    TrackActivity.Track(t, s, CellState.Read);
                    done++;
                }
        }
        finally { TrackActivity.End(); }
        progress?.Invoke(total, total, Tr.T("Hotovo"));
        return img;
    }

    private static byte[] ReadTrack(ISectorDevice dev, DiskGeometry media, int t, int s, OperationResult result)
    {
        long lba = media.Lba(t, s);
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            try { return dev.ReadSectors(lba, media.SectorsPerTrack); }
            catch (SectorIOException) { }
        }
        // Celá stopa nejde — po sektorech, ať víme, které přesně jsou vadné.
        var track = new byte[media.TrackSize];
        for (int n = 0; n < media.SectorsPerTrack; n++)
        {
            try
            {
                var sec = ReadWithRetry(dev, lba + n, 1);
                Buffer.BlockCopy(sec, 0, track, n * media.BytesPerSector, media.BytesPerSector);
            }
            catch (SectorIOException e)
            {
                result.BadSectors.Add(new BadSector(t, s, n + 1, e.Message));
                TrackActivity.Sector(t, s, n, CellState.Bad);
            }
        }
        return track;
    }

    private static byte[] ReadWithRetry(ISectorDevice dev, long lba, int count)
    {
        SectorIOException? last = null;
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            try { return dev.ReadSectors(lba, count); }
            catch (SectorIOException e) { last = e; }
        }
        throw last!;
    }

    // ------------------------------------------------------------------ zápis

    public static void WriteImage(ISectorDevice dev, DiskImage img, bool format, bool verify, OperationResult result,
                                  ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var g = img.Geometry;
        if (!dev.SupportsGeometry(g))
            throw new OperationException(Tr.F("Image má formát {0} — ten tahle mechanika zapsat neumí. {1}", g, dev.GeometryLimits));

        dev.RefreshMedia();
        if (dev.MediaState == MediaState.NoDisk) throw new OperationException(Tr.T("V mechanice není disketa."));

        var target = dev.FormatTargetFor(g);
        TrackActivity.Begin(g, format ? Tr.T("Formát a zápis") : Tr.T("Zápis na disketu"));
        dev.BeginExclusive();
        try
        {
            if (format)
            {
                LowLevelFormat(dev, target, result, progress, ct, Tr.T("Formátuji"));
                dev.RefreshMedia();
            }

            var media = RequireMedia(dev, format ? null :
                Tr.T("Použij volbu formátování před zápisem (v menu „a“, v příkazu --format)."));

            if (media.SectorsPerTrack != g.SectorsPerTrack)
                throw new OperationException(
                    Tr.F("Image má {0} sektorů na stopu, vložená disketa je naformátovaná na {1}. ", g.SectorsPerTrack, media.SectorsPerTrack) +
                    (g.IsHighDensity ? Tr.T("Vlož HD disketu (bez přelepeného otvoru).") : Tr.T("Vlož DD disketu nebo přelep otvor HD diskety")) +
                    Tr.T(", případně zapiš s formátováním."));
            if (media.Sides < g.Sides || media.Tracks < g.Tracks)
                throw new OperationException(Tr.F("Disketa ({0}) je menší než image ({1}).", media, g));

            int total = g.TrackCount * (verify ? 2 : 1), done = 0;
            for (int t = 0; t < g.Tracks; t++)
                for (int s = 0; s < g.Sides; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(done++, total, Tr.F("Zapisuji stopu {0}, stranu {1}", t, s));
                    TrackActivity.Head(t, s, TrackPhase.Writing);
                    WriteTrack(dev, media, t, s, img.ReadTrack(t, s), result);
                    TrackActivity.Track(t, s, CellState.Written);
                }

            if (verify)
                for (int t = 0; t < g.Tracks; t++)
                    for (int s = 0; s < g.Sides; s++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Invoke(done++, total, Tr.F("Kontroluji stopu {0}, stranu {1}", t, s));
                        TrackActivity.Head(t, s, TrackPhase.Verifying);
                        VerifyTrack(dev, media, t, s, img.ReadTrack(t, s), result);
                        TrackActivity.Track(t, s, CellState.Verified);
                    }
            progress?.Invoke(total, total, Tr.T("Hotovo"));
        }
        finally
        {
            dev.EndExclusive();
            TrackActivity.End();
        }
    }

    private static void WriteTrack(ISectorDevice dev, DiskGeometry media, int t, int s, byte[] data, OperationResult result)
    {
        long lba = media.Lba(t, s);
        for (int attempt = 0; attempt < Retries; attempt++)
        {
            try { dev.WriteSectors(lba, data, 0, media.SectorsPerTrack); return; }
            catch (SectorIOException) { }
        }
        for (int n = 0; n < media.SectorsPerTrack; n++)
        {
            SectorIOException? last = null;
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                try { dev.WriteSectors(lba + n, data, n * media.BytesPerSector, 1); last = null; break; }
                catch (SectorIOException e) { last = e; }
            }
            if (last != null)
            {
                result.BadSectors.Add(new BadSector(t, s, n + 1, last.Message));
                TrackActivity.Sector(t, s, n, CellState.WriteFailed);
            }
        }
    }

    private static void VerifyTrack(ISectorDevice dev, DiskGeometry media, int t, int s, byte[] expected, OperationResult result)
    {
        var tmp = new OperationResult();
        var actual = ReadTrack(dev, media, t, s, tmp);
        foreach (var b in tmp.BadSectors) result.VerifyErrors.Add(b with { Error = Tr.T("nejde přečíst: ") + b.Error });
        for (int n = 0; n < media.SectorsPerTrack; n++)
        {
            if (tmp.BadSectors.Exists(b => b.Sector == n + 1)) continue;
            int o = n * media.BytesPerSector;
            for (int k = 0; k < media.BytesPerSector; k++)
                if (actual[o + k] != expected[o + k])
                {
                    result.VerifyErrors.Add(new BadSector(t, s, n + 1, Tr.T("zapsaná data nesouhlasí")));
                    TrackActivity.Sector(t, s, n, CellState.WriteFailed);
                    break;
                }
        }
    }

    // ------------------------------------------------------------------ formát

    /// <param name="shiftSectors">Opravný formát (experimentální, jen fdrawcmd.sys): vadné místo na stopě překryje sektorem navíc.</param>
    public static void Format(ISectorDevice dev, DiskGeometry g, FormatStyle style, string? label, bool quick, bool verify,
                              OperationResult result, ProgressHandler? progress = null, CancellationToken ct = default,
                              bool shiftSectors = false)
    {
        var fdraw = dev as Fdraw.FdrawDevice;
        if (shiftSectors && (quick || !verify)) shiftSectors = false;
        if (shiftSectors && (fdraw == null || !Fdraw.FdrawDevice.CanShiftSectors(g)))
        {
            result.Notes.Add(fdraw == null
                ? Tr.T("Opravný formát umí jen interní mechanika s fdrawcmd.sys — formátuji běžně.")
                : Tr.T("Opravný formát u tohoto formátu nejde (na stopě není místo na sektor navíc) — formátuji běžně."));
            shiftSectors = false;
        }
        int shifted = 0;
        if (!dev.SupportsGeometry(g) || g.Sides != 2)
            throw new OperationException(Tr.F("Formát {0} tahle mechanika neumí. {1}", g, dev.GeometryLimits));

        dev.RefreshMedia();
        if (dev.MediaState == MediaState.NoDisk) throw new OperationException(Tr.T("V mechanice není disketa."));

        TrackActivity.Begin(g, quick ? Tr.T("Rychlý formát") : Tr.T("Formát"));
        dev.BeginExclusive();
        try
        {
            if (!quick)
            {
                LowLevelFormat(dev, g, result, progress, ct, Tr.T("Formátuji"));
                dev.RefreshMedia();
            }
            var media = RequireMedia(dev, quick ? Tr.T("Rychlý formát jde jen na už naformátovanou disketu — použij úplný formát.") : null);
            if (media.SectorsPerTrack != g.SectorsPerTrack)
                throw new OperationException(Tr.F("Disketa je naformátovaná na {0} sektorů na stopu, chtěl jsi {1}. Rychlý formát velikost nezmění — použij úplný formát.", media.SectorsPerTrack, g.SectorsPerTrack));

            var layout = DiskFormatter.ComputeLayout(g, style);
            var bad = new List<BadSector>();

            // Kontrola povrchu: najdi vadné sektory a označ jejich clustery ve FAT jako vadné.
            if (verify && !quick)
            {
                int total = g.TrackCount, done = 0;
                var scan = new OperationResult();
                for (int t = 0; t < g.Tracks; t++)
                    for (int s = 0; s < g.Sides; s++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Invoke(done++, total, Tr.F("Kontroluji povrch: stopa {0}, strana {1}", t, s));
                        TrackActivity.Head(t, s, TrackPhase.Verifying);
                        int before = scan.BadSectors.Count;
                        ReadTrack(dev, media, t, s, scan);
                        if (shiftSectors && scan.BadSectors.Count > before)
                        {
                            progress?.Invoke(done, total, Tr.F("Opravný formát: stopa {0}, strana {1}", t, s));
                            TrackActivity.Head(t, s, TrackPhase.Formatting);
                            int pos = fdraw!.ShiftSectors(g, t, s);
                            if (pos >= 0)
                            {
                                scan.BadSectors.RemoveRange(before, scan.BadSectors.Count - before);
                                shifted++;
                                result.Notes.Add(Tr.F("Stopa {0}, strana {1}: vadné místo překryté sektorem navíc (pozice {2}).", t, s, pos + 1));
                            }
                        }
                        TrackActivity.Track(t, s, CellState.Verified);
                    }
                bad = scan.BadSectors;
                if (shifted > 0)
                    result.Warnings.Add(Tr.F("Opravný formát (experimentální) obešel vadná místa na {0} stopách. Disketa je normálně čitelná, ale vada se může časem rozšířit — na archiv ji nepoužívej.", shifted));
            }
            var blank = BuildFilesystem(g, style, label, bad.Select(b => g.Lba(b.Track, b.Side, b.Sector - 1)), out int marked);
            result.BadSectors.AddRange(bad);
            if (bad.Count > 0)
                result.Warnings.Add(Tr.F("Nalezeno {0} vadných sektorů — označil jsem {1} clusterů ve FAT jako vadné, aby je Atari ani Windows nepoužily.", bad.Count, marked));

            progress?.Invoke(0, 1, Tr.T("Zapisuji souborový systém"));
            TrackActivity.Head(0, 0, TrackPhase.Writing);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    dev.WriteSectors(0, blank.Data, 0, layout.SystemSectors);
                    for (int l = 0; l < layout.SystemSectors; l++)
                        TrackActivity.Sector(l / g.SectorsPerTrack / g.Sides, l / g.SectorsPerTrack % g.Sides, l % g.SectorsPerTrack, CellState.Written);
                    break;
                }
                catch (SectorIOException e) when (attempt < Retries - 1) { _ = e; }
                catch (SectorIOException e) { throw new OperationException(Tr.T("Nepodařilo se zapsat boot sektor a FAT: ") + e.Message); }
            }
            progress?.Invoke(1, 1, Tr.T("Hotovo"));
        }
        finally
        {
            dev.EndExclusive();
            TrackActivity.End();
        }
    }

    private static void LowLevelFormat(ISectorDevice dev, DiskGeometry g, OperationResult result, ProgressHandler? progress,
                                       CancellationToken ct, string verb)
    {
        for (int c = 0; c < g.Tracks; c++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(c, g.Tracks, Tr.F("{0} stopu {1}", verb, c));
            TrackActivity.Head(c, -1, TrackPhase.Formatting);
            try { dev.FormatCylinder(g, c); TrackActivity.Track(c, -1, CellState.Formatted); }
            catch (SectorIOException e)
            {
                TrackActivity.Track(c, -1, CellState.WriteFailed);
                if (c == 0)
                    throw new OperationException(
                        Tr.F("Formátování na {0} KB selhalo hned na první stopě: {1}\n", g.SizeBytes / 1024, e.Message) +
                        (g.IsHighDensity
                            ? Tr.T("Zkontroluj, že je vložená HD disketa a otvor není přelepený.")
                            : Tr.T("Zkontroluj, že je vložená DD disketa, nebo že má HD disketa otvor přelepený neprůhlednou páskou z obou stran.")));
                throw new OperationException(Tr.F("Formátování selhalo na stopě {0}: {1}. Disketa je nejspíš poškozená.", c, e.Message));
            }
        }
        progress?.Invoke(g.Tracks, g.Tracks, Tr.T("Formát hotový"));
    }

    private static void MarkBadCluster(byte[] disk, DiskFormatter.Layout layout, int cluster)
    {
        for (int f = 0; f < layout.Fats; f++)
            FatTable.Set(disk, (layout.ReservedSectors + f * layout.SectorsPerFat) * 512, cluster, FatTable.BadCluster);
    }

    /// <summary>
    /// Prázdný souborový systém pro danou disketu s označenými vadnými clustery.
    /// Vadný sektor v systémové oblasti (boot, FAT, kořenový adresář) = disketa je nepoužitelná.
    /// </summary>
    public static DiskImage BuildFilesystem(DiskGeometry g, FormatStyle style, string? label, IEnumerable<long> badLbas, out int markedClusters)
    {
        var blank = DiskFormatter.CreateBlank(g, style, label);
        var layout = DiskFormatter.ComputeLayout(g, style);
        var clusters = new HashSet<int>();
        foreach (long lba in badLbas)
        {
            if (lba < layout.SystemSectors)
            {
                var (t, s, n) = (lba / g.SectorsPerTrack / g.Sides, lba / g.SectorsPerTrack % g.Sides, lba % g.SectorsPerTrack + 1);
                throw new OperationException(Tr.F("Disketa má vadný sektor v systémové oblasti (stopa {0}, strana {1}, sektor {2}). Taková disketa se nedá použít.", t, s, n));
            }
            clusters.Add((int)((lba - layout.SystemSectors) / layout.SectorsPerCluster) + 2);
        }
        foreach (int c in clusters) MarkBadCluster(blank.Data, layout, c);
        markedClusters = clusters.Count;
        return blank;
    }

    /// <summary>Zapíše vybrané sektory z obrazu na disketu a každý hned ověří (pro opravy souborového systému).</summary>
    public static void WriteSectorList(ISectorDevice dev, byte[] disk, IReadOnlyCollection<long> lbas, OperationResult result,
                                       ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var media = RequireMedia(dev);
        int bps = media.BytesPerSector, done = 0;
        TrackActivity.Begin(media, Tr.T("Zápis opravených sektorů"));
        dev.BeginExclusive();
        try
        {
            foreach (long lba in lbas.OrderBy(x => x))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(done++, lbas.Count, Tr.F("Zapisuji sektor {0}", lba));
                var (t, s, n) = ((int)(lba / media.SectorsPerTrack / media.Sides), (int)(lba / media.SectorsPerTrack % media.Sides), (int)(lba % media.SectorsPerTrack) + 1);
                bool ok = false;
                string err = "";
                for (int attempt = 0; attempt < Retries && !ok; attempt++)
                {
                    try
                    {
                        dev.WriteSectors(lba, disk, (int)(lba * bps), 1);
                        var back = dev.ReadSectors(lba, 1);
                        ok = true;
                        for (int k = 0; k < bps; k++) if (back[k] != disk[lba * bps + k]) { ok = false; err = Tr.T("zapsaná data nesouhlasí"); break; }
                    }
                    catch (SectorIOException e) { err = e.Message; }
                }
                TrackActivity.Head(t, s, TrackPhase.Writing);
                TrackActivity.Sector(t, s, n - 1, ok ? CellState.Verified : CellState.WriteFailed);
                if (!ok) result.VerifyErrors.Add(new BadSector(t, s, n, err));
            }
            progress?.Invoke(lbas.Count, lbas.Count, Tr.T("Hotovo"));
        }
        finally
        {
            dev.EndExclusive();
            TrackActivity.End();
        }
    }

    private static DiskGeometry RequireMedia(ISectorDevice dev, string? hint = null)
    {
        switch (dev.MediaState)
        {
            case MediaState.Ready when dev.MediaGeometry != null:
                return dev.MediaGeometry;
            case MediaState.NoDisk:
                throw new OperationException(Tr.T("V mechanice není disketa."));
            case MediaState.Unrecognized:
                throw new OperationException(Tr.T("Mechanika disketu nerozpoznala — buď není naformátovaná, nebo má formát, který tahle mechanika neumí (10/11 sektorů, extra stopy, ochrana).")
                                             + (hint != null ? "\n" + hint : ""));
            default:
                throw new OperationException(Tr.T("Disketa není připravená: ") + (dev.MediaStateMessage ?? dev.MediaState.ToString()));
        }
    }
}
