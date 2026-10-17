using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Co se o chybějícím sektoru v toku zjistilo — podle toho, jestli má smysl další čtení.</summary>
public enum MissingKind
{
    /// <summary>Hlavička i data se našla, jen nesedí kontrolní součet — další čtení může pomoct.</summary>
    Weak,
    /// <summary>Našla se jen hlavička, data za ní ne.</summary>
    HeaderOnly,
    /// <summary>Signál na stopě je, ale sektor se nenašel vůbec (poškozený záznam nebo jiné seřízení hlavy).</summary>
    Unreadable,
    /// <summary>Na stopě chybí magnetický záznam (dlouhá místa bez změn) — nevrátí ho žádná mechanika.</summary>
    NoSignal,
}

/// <summary>Výsledek záchrany sektorů z jednoho nebo více souborů toku.</summary>
public sealed class FluxRescueResult
{
    public DiskGeometry Geometry { get; init; } = DiskGeometry.Hd1440;
    /// <summary>Platné sektory: (stopa, strana, sektor 1…) → data a odkud jsou.</summary>
    public Dictionary<(int c, int h, int r), (byte[] data, int file, int variant)> Sectors { get; } = new();
    /// <summary>Kolik sektorů dal který soubor jako první (index souboru → počet).</summary>
    public int[] FromFile { get; init; } = Array.Empty<int>();
    /// <summary>Kolik sektorů přibylo až jiným nastavením převodu.</summary>
    public int FromVariants { get; set; }
    /// <summary>Chybějící sektory a co se o nich zjistilo.</summary>
    public Dictionary<(int c, int h, int r), MissingKind> Missing { get; } = new();

    /// <summary>
    /// Rada, jestli má smysl číst dál: (upozornění?, text). hopeless = zbylé sektory už další mechanika
    /// nejspíš nevrátí (nejsou slabé, a buď nemají signál, nebo je nepřečetly už aspoň dvě mechaniky).
    /// </summary>
    public (List<(bool warn, string text)> lines, bool hopeless) Advice(int drives)
    {
        var lines = new List<(bool, string)>();
        int weak = Missing.Count(m => m.Value == MissingKind.Weak);
        int header = Missing.Count(m => m.Value == MissingKind.HeaderOnly);
        int unread = Missing.Count(m => m.Value == MissingKind.Unreadable);
        int none = Missing.Count(m => m.Value == MissingKind.NoSignal);
        if (weak > 0) lines.Add((false, Tr.F("{0} sektorů má čitelnou hlavičku i data, jen nesedí kontrolní součet — tyhle může zachránit další čtení (víc otáček, jiná mechanika).", weak)));
        if (header > 0) lines.Add((false, Tr.F("U {0} sektorů se našla jen hlavička, data ne — šance je malá.", header)));
        if (unread > 0)
            lines.Add((false, drives >= 2
                ? Tr.F("{0} sektorů nepřečetla ani jedna z {1} mechanik, i když tam signál je — šance je malá.", unread, drives)
                : Tr.F("{0} sektorů se nepřečetlo, i když tam signál je — zkus tok z jiné mechaniky (jinak seřízená hlava je může přečíst).", unread)));
        if (none > 0) lines.Add((false, Tr.F("{0} sektorů leží na místech bez magnetického záznamu — ty už nevrátí žádná mechanika.", none)));
        bool hopeless = Missing.Count > 0 && weak == 0 && (unread == 0 || drives >= 2);
        lines.Add(hopeless
            ? (true, Tr.T("Další mechanika už nejspíš nepomůže — v těchto místech je disketa nezachranitelná."))
            : (false, Tr.T("Má smysl uložit tok téže diskety z další mechaniky a spustit záchranu znovu se stejným obrazem — doplní se jen chybějící sektory.")));
        return (lines, hopeless);
    }

    public IEnumerable<long> MissingLbas()
    {
        var g = Geometry;
        for (int c = 0; c < g.Tracks; c++)
            for (int h = 0; h < g.Sides; h++)
                for (int r = 1; r <= g.SectorsPerTrack; r++)
                    if (!Sectors.ContainsKey((c, h, r))) yield return g.Lba(c, h, r - 1);
    }

    /// <summary>Zapíše nalezené sektory do obrazu; jen do těch, které jsou v „only“ (null = všechny).</summary>
    public int FillImage(DiskImage img, ISet<long>? only = null)
    {
        var g = img.Geometry;
        int n = 0;
        foreach (var kv in Sectors)
        {
            var (c, h, r) = kv.Key;
            if (c >= g.Tracks || h >= g.Sides || r < 1 || r > g.SectorsPerTrack) continue;
            long lba = g.Lba(c, h, r - 1);
            if (only != null && !only.Contains(lba)) continue;
            Buffer.BlockCopy(kv.Value.data, 0, img.Data, (int)(lba * g.BytesPerSector), g.BytesPerSector);
            n++;
        }
        return n;
    }
}

/// <summary>
/// Záchrana sektorů z uložených toků (.scp z Greaseweazle), bez diskety v mechanice. Každý soubor se převede
/// vlastním dekodérem MFM; pro každý sektor se vezme první čtení s platným CRC (z kterékoli otáčky, kteréhokoli
/// souboru). Stopy, kde něco chybí, se zkusí převést i s jinými nastaveními PLL. Toky ze dvou mechanik se tak
/// doplňují. Umí jen IBM MFM s 512bajtovými sektory (PC, Atari ST, MSX…).
/// </summary>
public static class FluxRescue
{
    public static FluxRescueResult Run(IList<string> scpPaths, DiskGeometry? geometry = null,
                                       ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var files = scpPaths.Select(p => (path: p, scp: ScpFile.Load(p))).ToList();
        var fromFile = new int[files.Count];
        var found = new Dictionary<(int c, int h, int r), (byte[] data, int file, int variant)>();
        var cells = new Dictionary<(int file, int c, int h), double>();
        var seen = new Dictionary<(int c, int h, int r), MissingKind>();     // nejlepší neplatný nález sektoru
        var signal = new Dictionary<(int c, int h), bool>();                 // aspoň jeden soubor má na stopě záznam
        void Seen(DecodedSector s)
        {
            if (s.Ok) return;
            var kind = s.Data.Length > 0 ? MissingKind.Weak : MissingKind.HeaderOnly;
            var key = (s.Cyl, s.Head, s.Sector);
            if (!seen.TryGetValue(key, out var k) || kind < k) seen[key] = kind;
        }
        int total = files.Sum(f => f.scp.Tracks().Count()), done = 0;

        // 1. průchod: výchozí nastavení na všech stopách všech souborů
        for (int fi = 0; fi < files.Count; fi++)
            foreach (var (c, h) in files[fi].scp.Tracks())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(done++, total, Tr.F("Převádím {0}: stopa {1}, strana {2}", Path.GetFileName(files[fi].path), c, h));
                var flux = files[fi].scp.FluxUs(c, h);
                double? cell = CellUs(flux);
                if (cell == null) continue;
                cells[(fi, c, h)] = cell.Value;
                if (DropoutShare(flux, cell.Value) < 0.05) signal[(c, h)] = true;
                foreach (var s in MfmDecoder.Decode(flux, cell.Value, MfmDecoder.Variants[0].gain, MfmDecoder.Variants[0].phase))
                    if (!s.Ok) Seen(s);
                    else if (s.SizeCode == 2 && !found.ContainsKey((s.Cyl, s.Head, s.Sector)))
                    {
                        found[(s.Cyl, s.Head, s.Sector)] = (s.Data, fi, 0);
                        fromFile[fi]++;
                    }
            }
        var g = geometry ?? Guess(found.Keys)
            ?? throw new OperationException(Tr.T("V toku jsem nenašel sektory PC / Atari (MFM, 512 bajtů). Jiné formáty (Amiga, Mac, Commodore) převede jen gw."));
        var result = new FluxRescueResult { Geometry = g, FromFile = fromFile };
        foreach (var kv in found) result.Sectors[kv.Key] = kv.Value;

        TrackActivity.Begin(g, Tr.T("Záchrana sektorů z toku"));
        for (int c = 0; c < g.Tracks; c++)
            for (int h = 0; h < g.Sides; h++)
                for (int r = 1; r <= g.SectorsPerTrack; r++)
                    TrackActivity.Sector(c, h, r - 1, found.ContainsKey((c, h, r)) ? CellState.Read : CellState.Bad);

        // 2. průchod: stopy s chybějícími sektory zkusit s dalšími nastaveními PLL
        bool Incomplete(int c, int h) => Enumerable.Range(1, g.SectorsPerTrack).Any(r => !result.Sectors.ContainsKey((c, h, r)));
        var retry = cells.Keys.Where(k => k.c < g.Tracks && k.h < g.Sides && Incomplete(k.c, k.h)).ToList();
        done = 0;
        foreach (var (fi, c, h) in retry)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done++, retry.Count, Tr.F("Zkouším jiné nastavení převodu: {0}, stopa {1}, strana {2}", Path.GetFileName(files[fi].path), c, h));
            TrackActivity.Head(c, h, TrackPhase.Reading);
            var flux = files[fi].scp.FluxUs(c, h);
            for (int v = 1; v < MfmDecoder.Variants.Length && Incomplete(c, h); v++)
                foreach (var s in MfmDecoder.Decode(flux, cells[(fi, c, h)], MfmDecoder.Variants[v].gain, MfmDecoder.Variants[v].phase))
                    if (!s.Ok) Seen(s);
                    else if (s.SizeCode == 2 && s.Cyl == c && s.Head == h && !result.Sectors.ContainsKey((c, h, s.Sector)))
                    {
                        result.Sectors[(c, h, s.Sector)] = (s.Data, fi, v);
                        result.FromVariants++;
                        if (s.Sector >= 1 && s.Sector <= g.SectorsPerTrack)
                        {
                            TrackActivity.Sector(c, h, s.Sector - 1, CellState.Read);
                            TrackActivity.Reason(c, h, s.Sector - 1, Tr.T("přečteno až s jiným nastavením převodu toku"));
                        }
                    }
        }
        foreach (var kv in result.Sectors.Where(kv => kv.Value.file > 0 && kv.Key.r >= 1 && kv.Key.r <= g.SectorsPerTrack && kv.Key.c < g.Tracks && kv.Key.h < g.Sides))
            TrackActivity.Reason(kv.Key.c, kv.Key.h, kv.Key.r - 1, Tr.F("přečteno ze souboru {0}", Path.GetFileName(files[kv.Value.file].path)));
        for (int c = 0; c < g.Tracks; c++)
            for (int h = 0; h < g.Sides; h++)
                for (int r = 1; r <= g.SectorsPerTrack; r++)
                    if (!result.Sectors.ContainsKey((c, h, r)))
                    {
                        var kind = seen.TryGetValue((c, h, r), out var k) ? k : signal.ContainsKey((c, h)) ? MissingKind.Unreadable : MissingKind.NoSignal;
                        result.Missing[(c, h, r)] = kind;
                        TrackActivity.Reason(c, h, r - 1, kind switch
                        {
                            MissingKind.Weak => Tr.T("hlavička i data jsou, jen nesedí kontrolní součet — další čtení může pomoct"),
                            MissingKind.HeaderOnly => Tr.T("našla se jen hlavička sektoru, data ne"),
                            MissingKind.Unreadable => Tr.T("sektor se nenašel, i když na stopě signál je"),
                            _ => Tr.T("na stopě chybí magnetický záznam — nezachranitelné"),
                        });
                    }
        progress?.Invoke(1, 1, Tr.T("Hotovo"));
        return result;
    }

    /// <summary>
    /// Podíl času stopy v mezerách delších než 6 buněk (MFM jich má nejvýš 4): místa bez magnetického záznamu.
    /// Zdravé stopy na skutečných disketách 0–0,4 %, stopa se sloupnutou vrstvou 30 %.
    /// </summary>
    public static double DropoutShare(IReadOnlyList<double> flux, double cellUs)
    {
        double total = 0, gaps = 0;
        foreach (double f in flux) { total += f; if (f > 6 * cellUs) gaps += f; }
        return total > 0 ? gaps / total : 1;
    }

    /// <summary>
    /// Jmenovitá délka bitové buňky v µs podle první špičky histogramu: HD 1 µs, DD 2 µs. Jmenovitá, ne změřená —
    /// na poškozené stopě je špička posunutá a převod s ní dopadl hůř (ověřeno na skutečné disketě).
    /// </summary>
    internal static double? CellUs(IReadOnlyList<double> flux)
    {
        var h = FluxHistogram.Build(flux);
        if (h.Peaks.Count == 0) return null;
        double first = h.Peaks[0];
        return first > 1.4 && first < 2.6 ? 1.0 : first > 3.2 && first < 4.8 ? 2.0 : null;
    }

    /// <summary>Formát podle nalezených sektorů: nejčastější nejvyšší číslo sektoru, poslední stopa, strany.</summary>
    internal static DiskGeometry? Guess(IEnumerable<(int c, int h, int r)> keys)
    {
        var list = keys.Where(k => k.c < 90 && k.h < 2 && k.r >= 1 && k.r <= 40).ToList();
        if (list.Count == 0) return null;
        int spt = list.GroupBy(k => (k.c, k.h)).Select(t => t.Max(k => k.r)).GroupBy(m => m).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key).First().Key;
        int tracks = list.Max(k => k.c) + 1;
        if (tracks >= 70 && tracks < 80) tracks = 80;       // poslední stopy mohou být úplně nečitelné
        int sides = list.Any(k => k.h == 1) ? 2 : 1;
        return new DiskGeometry(tracks, sides, spt);
    }
}
