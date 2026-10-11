using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Analysis;

public enum SectorStatus : byte
{
    NotTested,
    Ok,
    /// <summary>Přečetl se až na opakovaný pokus — disketa tu slábne.</summary>
    Weak,
    /// <summary>Nejde přečíst.</summary>
    Bad,
    /// <summary>Zápisový test: zápis selhal nebo se data přečetla jinak.</summary>
    WriteFailed,
}

/// <summary>Stav každého sektoru diskety.</summary>
public sealed class SurfaceMap
{
    public DiskGeometry Geometry { get; }
    private readonly SectorStatus[] _status;
    private readonly byte[] _attempts;

    public SurfaceMap(DiskGeometry g)
    {
        Geometry = g;
        _status = new SectorStatus[g.TotalSectors];
        _attempts = new byte[g.TotalSectors];
    }

    public SectorStatus this[long lba]
    {
        get => _status[lba];
        set => _status[lba] = value;
    }

    public int Attempts(long lba) => _attempts[lba];
    internal void SetAttempts(long lba, int n) => _attempts[lba] = (byte)Math.Min(255, n);

    public SectorStatus Get(int track, int side, int sector) => _status[Geometry.Lba(track, side, sector)];

    /// <summary>Nejhorší stav na stopě (pro mapu).</summary>
    public SectorStatus Worst(int track, int side)
    {
        var worst = SectorStatus.NotTested;
        for (int n = 0; n < Geometry.SectorsPerTrack; n++)
        {
            var s = Get(track, side, n);
            if (Rank(s) > Rank(worst)) worst = s;
        }
        return worst;
    }

    private static int Rank(SectorStatus s) => s switch
    {
        SectorStatus.NotTested => 0,
        SectorStatus.Ok => 1,
        SectorStatus.Weak => 2,
        SectorStatus.WriteFailed => 3,
        SectorStatus.Bad => 4,
        _ => 0,
    };

    public int Count(SectorStatus s) => _status.Count(x => x == s);

    public IEnumerable<long> Where(Func<SectorStatus, bool> pred)
    {
        for (long i = 0; i < _status.Length; i++)
            if (pred(_status[i])) yield return i;
    }

    /// <summary>Sektory, na které se nedá spolehnout (vadné, slabé, selhal zápis).</summary>
    public IEnumerable<long> Problems => Where(s => s is SectorStatus.Bad or SectorStatus.Weak or SectorStatus.WriteFailed);

    public (int track, int side, int sector) Chs(long lba)
    {
        int spt = Geometry.SectorsPerTrack;
        long trackIndex = lba / spt;
        return ((int)(trackIndex / Geometry.Sides), (int)(trackIndex % Geometry.Sides), (int)(lba % spt) + 1);
    }
}

public enum WriteTestMode
{
    /// <summary>Jen čtení — nic se nemění.</summary>
    None,
    /// <summary>Zapíše vzorky a vrátí původní obsah (data zůstanou).</summary>
    PreserveData,
    /// <summary>Zapíše vzorky na celou disketu — všechna data zmizí.</summary>
    Destructive,
}

public sealed class SurfaceTestResult
{
    public SurfaceMap Map { get; init; } = null!;
    /// <summary>Obsah diskety přečtený při testu (vadné sektory jsou nuly). U destruktivního testu stav PŘED testem.</summary>
    public DiskImage Contents { get; init; } = null!;
    public List<string> Warnings { get; } = new();
    /// <summary>Sektory, které se při testu s obnovou nepodařilo vrátit (data jsou v záloze).</summary>
    public List<long> RestoreFailures { get; } = new();
    public int TracksSkippedForWrite { get; set; }
}

/// <summary>Test povrchu diskety.</summary>
public static class SurfaceTest
{
    public const int ScanRetries = 5;

    private static readonly byte[][] Patterns = BuildPatterns();

    private static byte[][] BuildPatterns()
    {
        var rnd = new byte[256];
        new Random(0x5A7A).NextBytes(rnd);
        return new[] { new byte[] { 0x55, 0xAA }, new byte[] { 0xAA, 0x55 }, new byte[] { 0x00 }, new byte[] { 0xFF }, rnd };
    }

    /// <summary>
    /// Čtecí test celé diskety, volitelně i zápisový (ten si jednotku zamkne sám).
    /// beforeWrite po čtení, před prvním zápisem (záloha).
    /// </summary>
    public static SurfaceTestResult Run(ISectorDevice dev, WriteTestMode mode, ProgressHandler? progress = null,
                                        CancellationToken ct = default,
                                        Action<SurfaceTestResult>? beforeWrite = null)
    {
        dev.RefreshMedia();
        var media = dev.MediaGeometry ?? throw new OperationException(dev.MediaState switch
        {
            MediaState.NoDisk => Tr.T("V mechanice není disketa."),
            MediaState.Unrecognized => Tr.T("Mechanika disketu nerozpoznala — buď není naformátovaná, nebo má formát, který tahle mechanika neumí. Test povrchu jde u takové diskety udělat jen s formátováním (dtk format)."),
            _ => Tr.T("Disketa není připravená: ") + dev.MediaStateMessage,
        });

        var map = new SurfaceMap(media);
        var contents = new DiskImage(media);
        var result = new SurfaceTestResult { Map = map, Contents = contents };
        int passes = mode == WriteTestMode.None ? 1 : 2;
        int total = media.TrackCount * passes, done = 0;
        TrackActivity.Begin(media, mode == WriteTestMode.None ? Tr.T("Test povrchu (čtení)") : Tr.T("Test povrchu (čtení a zápis)"));
        try { return RunInner(dev, mode, media, map, contents, result, total, done, progress, ct, beforeWrite); }
        finally { TrackActivity.End(); }
    }

    private static void ShowTrack(SurfaceMap map, DiskGeometry media, int t, int s, bool afterWrite)
    {
        if (TrackActivity.Current == null) return;
        long first = media.Lba(t, s);
        for (int n = 0; n < media.SectorsPerTrack; n++)
        {
            var st = TrackActivity.FromSurface(map[first + n]);
            if (afterWrite && st == CellState.Read) st = CellState.Verified;
            TrackActivity.Sector(t, s, n, st);
        }
    }

    private static SurfaceTestResult RunInner(ISectorDevice dev, WriteTestMode mode, DiskGeometry media, SurfaceMap map, DiskImage contents,
                                              SurfaceTestResult result, int total, int done, ProgressHandler? progress, CancellationToken ct,
                                              Action<SurfaceTestResult>? beforeWrite)
    {

        // 1) Čtení — zároveň získáme obsah (záloha, kontrola souborového systému).
        for (int t = 0; t < media.Tracks; t++)
            for (int s = 0; s < media.Sides; s++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(done++, total, Tr.F("Čtu stopu {0}, stranu {1}", t, s));
                TrackActivity.Head(t, s, TrackPhase.Reading);
                contents.WriteTrack(t, s, ScanTrack(dev, media, map, t, s));
                ShowTrack(map, media, t, s, false);
            }

        if (mode == WriteTestMode.None)
        {
            progress?.Invoke(total, total, Tr.T("Hotovo"));
            return result;
        }

        // 2) Zápis — předtím dostane volající šanci uložit zálohu přečteného obsahu.
        beforeWrite?.Invoke(result);
        dev.BeginExclusive();
        try
        {
            for (int t = 0; t < media.Tracks; t++)
                for (int s = 0; s < media.Sides; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(done++, total, Tr.F("Zápisový test: stopa {0}, strana {1}", t, s));
                    long first = media.Lba(t, s);
                    bool trackHasBad = Enumerable.Range(0, media.SectorsPerTrack).Any(n => map[first + n] == SectorStatus.Bad);
                    if (mode == WriteTestMode.PreserveData && trackHasBad)
                    {
                        // Nečitelné sektory nemáme čím obnovit a zápisem bychom přepsali i zbytek stopy — stopu vynecháme.
                        result.TracksSkippedForWrite++;
                        TrackActivity.Track(t, s, CellState.Skipped);
                        continue;
                    }
                    TrackActivity.Head(t, s, TrackPhase.Writing);
                    WriteTestTrack(dev, media, map, t, s, mode == WriteTestMode.PreserveData ? contents.ReadTrack(t, s) : null, result);
                    ShowTrack(map, media, t, s, true);
                }
        }
        finally
        {
            dev.EndExclusive();
        }
        if (result.TracksSkippedForWrite > 0)
            result.Warnings.Add(Tr.F("Zápisem jsem netestoval {0} stop s nečitelnými sektory (test s obnovou by jinak přepsal i čitelná data).", result.TracksSkippedForWrite));
        progress?.Invoke(total, total, Tr.T("Hotovo"));
        return result;
    }

    private static byte[] ScanTrack(ISectorDevice dev, DiskGeometry media, SurfaceMap map, int t, int s)
    {
        long first = media.Lba(t, s);
        int bps = media.BytesPerSector;
        try
        {
            var data = dev.ReadSectors(first, media.SectorsPerTrack);
            for (int n = 0; n < media.SectorsPerTrack; n++) { map[first + n] = SectorStatus.Ok; map.SetAttempts(first + n, 1); }
            return data;
        }
        catch (SectorIOException) { }

        var track = new byte[media.TrackSize];
        for (int n = 0; n < media.SectorsPerTrack; n++)
        {
            long lba = first + n;
            int attempt = 0;
            bool ok = false;
            while (attempt < ScanRetries && !ok)
            {
                attempt++;
                try
                {
                    var sec = dev.ReadSectors(lba, 1);
                    Buffer.BlockCopy(sec, 0, track, n * bps, bps);
                    ok = true;
                }
                catch (SectorIOException) { }
            }
            map.SetAttempts(lba, attempt);
            map[lba] = !ok ? SectorStatus.Bad : attempt == 1 ? SectorStatus.Ok : SectorStatus.Weak;
        }
        // Stopa jako celek selhala, ale každý sektor se nakonec přečetl hned → i tak je stopa podezřelá.
        if (Enumerable.Range(0, media.SectorsPerTrack).All(n => map[first + n] == SectorStatus.Ok))
            for (int n = 0; n < media.SectorsPerTrack; n++) map[first + n] = SectorStatus.Weak;
        return track;
    }

    private static void WriteTestTrack(ISectorDevice dev, DiskGeometry media, SurfaceMap map, int t, int s, byte[]? original, SurfaceTestResult result)
    {
        long first = media.Lba(t, s);
        int spt = media.SectorsPerTrack, bps = media.BytesPerSector;
        var failed = new bool[spt];
        int patterns = original != null ? 2 : Patterns.Length; // s obnovou stačí 2 vzorky (55AA / AA55)

        for (int p = 0; p < patterns; p++)
        {
            var pat = Fill(Patterns[p], media.TrackSize);
            WritePerSectorOnFailure(dev, first, pat, spt, bps, failed);
            CompareBack(dev, first, pat, spt, bps, failed);
        }

        if (original != null)
        {
            var restoreFailed = new bool[spt];
            WritePerSectorOnFailure(dev, first, original, spt, bps, restoreFailed);
            CompareBack(dev, first, original, spt, bps, restoreFailed);
            for (int n = 0; n < spt; n++)
                if (restoreFailed[n]) { failed[n] = true; result.RestoreFailures.Add(first + n); }
        }

        for (int n = 0; n < spt; n++)
            if (failed[n]) map[first + n] = SectorStatus.WriteFailed;
    }

    private static void WritePerSectorOnFailure(ISectorDevice dev, long first, byte[] data, int spt, int bps, bool[] failed)
    {
        try { dev.WriteSectors(first, data, 0, spt); return; }
        catch (SectorIOException) { }
        for (int n = 0; n < spt; n++)
        {
            try { dev.WriteSectors(first + n, data, n * bps, 1); }
            catch (SectorIOException) { failed[n] = true; }
        }
    }

    private static void CompareBack(ISectorDevice dev, long first, byte[] expected, int spt, int bps, bool[] failed)
    {
        byte[]? back = null;
        try { back = dev.ReadSectors(first, spt); } catch (SectorIOException) { }
        for (int n = 0; n < spt; n++)
        {
            byte[]? sec = null;
            if (back != null) { sec = new byte[bps]; Buffer.BlockCopy(back, n * bps, sec, 0, bps); }
            else
            {
                try { sec = dev.ReadSectors(first + n, 1); } catch (SectorIOException) { }
            }
            if (sec == null) { failed[n] = true; continue; }
            for (int k = 0; k < bps; k++)
                if (sec[k] != expected[n * bps + k]) { failed[n] = true; break; }
        }
    }

    private static byte[] Fill(byte[] pattern, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = pattern[i % pattern.Length];
        return b;
    }
}
