using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Diag;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>
/// Stav záchrany vadné diskety: soubor „obraz.st.rescue.txt“ vedle obrazu. Missing = sektory, které se zatím
/// nepřečetly bez chyby; Voted = z nich ty, které jsou v obrazu poskládané hlasováním z vadných čtení
/// (nejpravděpodobnější obsah, neověřený). Další záchrana (i v jiné mechanice) čte jen Missing.
/// </summary>
public sealed class RescueState
{
    public DiskGeometry Geometry { get; }
    public SortedSet<long> Missing { get; } = new();
    public SortedSet<long> Voted { get; } = new();
    public List<string> History { get; } = new();

    public RescueState(DiskGeometry g) => Geometry = g;

    public static string SidecarPath(string image) => image + ".rescue.txt";

    public static RescueState? Load(string image, DiskGeometry g)
    {
        string side = SidecarPath(image);
        if (!File.Exists(side)) return null;
        var st = new RescueState(g);
        foreach (var raw in File.ReadAllLines(side))
        {
            string line = raw.Trim();
            static IEnumerable<long> Nums(string s) =>
                s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out long v) ? v : -1).Where(v => v >= 0);
            if (line.StartsWith("missing:")) foreach (long l in Nums(line.Substring(8))) st.Missing.Add(l);
            else if (line.StartsWith("voted:")) foreach (long l in Nums(line.Substring(6))) st.Voted.Add(l);
            else if (line.StartsWith("#")) st.History.Add(line.Substring(1).Trim());
        }
        return st;
    }

    public void Save(string image)
    {
        var lines = new List<string>
        {
            "# Diskette Toolkit — záchrana vadné diskety / floppy rescue. Do not edit the lines below.",
            "geometry: " + Geometry,
            "missing: " + string.Join(",", Missing),
            "voted: " + string.Join(",", Voted),
        };
        lines.AddRange(History.Select(h => "# " + h));
        File.WriteAllLines(SidecarPath(image), lines);
    }
}

public sealed class RescueResult
{
    public int Recovered { get; set; }
    public int NewlyVoted { get; set; }
    public int UncertainBits { get; set; }
    public bool VoteUnavailable { get; set; }
}

public static class Rescue
{
    /// <summary>
    /// Opakované čtení chybějících sektorů. Úspěšné čtení sektor doplní a vyřadí ze seznamu. Když je vote
    /// a mechanika je přes fdrawcmd.sys, sbírají se i data z chybných čtení; po všech pokusech se z nich
    /// (aspoň 3 kopie) hlasováním po bitech poskládá nejpravděpodobnější obsah.
    /// </summary>
    public static RescueResult RetryMissing(ISectorDevice dev, DiskImage img, RescueState st, int tries, bool vote,
                                            ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var g = img.Geometry;
        var result = new RescueResult();
        var fd = dev as FdrawDevice;
        if (vote && fd == null) result.VoteUnavailable = true;
        var todo = st.Missing.ToList();
        int done = 0;
        foreach (long lba in todo)
        {
            ct.ThrowIfCancellationRequested();
            int t = (int)(lba / g.SectorsPerTrack / g.Sides), s = (int)(lba / g.SectorsPerTrack % g.Sides), n = (int)(lba % g.SectorsPerTrack);
            progress?.Invoke(done++, todo.Count, Tr.F("Zachraňuji stopu {0}, stranu {1}, sektor {2}", t, s, n + 1));
            TrackActivity.Head(t, s, TrackPhase.Reading);
            var copies = new List<byte[]>();
            bool ok = false;
            for (int i = 0; i < tries && !ok; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    byte[]? data;
                    if (fd != null && vote)
                    {
                        ok = fd.ReadSectorRaw(lba, out data);
                        if (!ok) { if (data != null) copies.Add(data); continue; }
                    }
                    else data = dev.ReadSectors(lba, 1);
                    Buffer.BlockCopy(data!, 0, img.Data, (int)(lba * g.BytesPerSector), g.BytesPerSector);
                    ok = true;
                }
                catch (SectorIOException) { }
            }
            if (ok)
            {
                st.Missing.Remove(lba);
                st.Voted.Remove(lba);
                result.Recovered++;
                TrackActivity.Sector(t, s, n, CellState.Read);
                TrackActivity.Reason(t, s, n, Tr.T("zachráněno opakovaným čtením"));
            }
            else if (copies.Count >= 3)
            {
                var (data, uncertain) = MediaChecks.Vote(copies);
                Buffer.BlockCopy(data, 0, img.Data, (int)(lba * g.BytesPerSector), g.BytesPerSector);
                st.Voted.Add(lba);
                result.NewlyVoted++;
                result.UncertainBits += uncertain;
                TrackActivity.Sector(t, s, n, CellState.Weak);
                TrackActivity.Reason(t, s, n, Tr.F("poskládáno hlasováním z {0} vadných čtení, nejistých bitů {1} — neověřeno", copies.Count, uncertain));
            }
            else
            {
                TrackActivity.Sector(t, s, n, CellState.Bad);
                TrackActivity.Reason(t, s, n, copies.Count > 0 ? Tr.F("nepřečteno; vadných kopií jen {0} (na skládání jsou potřeba aspoň 3)", copies.Count)
                                                               : Tr.T("nepřečteno ani po všech pokusech"));
            }
        }
        progress?.Invoke(todo.Count, todo.Count, Tr.T("Hotovo"));
        return result;
    }

    /// <summary>Zobrazí v mapě stav záchrany: přečtené, chybějící a poskládané sektory.</summary>
    public static void ShowState(RescueState st, string title)
    {
        var g = st.Geometry;
        TrackActivity.Begin(g, title);
        for (long l = 0; l < g.TotalSectors; l++)
        {
            int t = (int)(l / g.SectorsPerTrack / g.Sides), s = (int)(l / g.SectorsPerTrack % g.Sides), n = (int)(l % g.SectorsPerTrack);
            TrackActivity.Sector(t, s, n, st.Voted.Contains(l) ? CellState.Weak : st.Missing.Contains(l) ? CellState.Bad : CellState.Read);
        }
    }
}
