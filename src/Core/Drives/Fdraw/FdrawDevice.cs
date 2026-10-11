using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Fdraw;

/// <summary>
/// Disketa přes fdrawcmd.sys — přímé příkazy řadiči základní desky. Umí nestandardní geometrie
/// (Atari 10/11 sektorů, 81–84 stop, DMF 21 sektorů, sektory 128–1024 B), vlastní rychlost krokování
/// a sám pozná, jak je disketa naformátovaná (čtením hlaviček sektorů).
/// </summary>
public sealed class FdrawDevice : ISectorDevice
{
    public const int MaxTracks = 84;
    private readonly IFdc _fdc;
    private DataRate _rate = DataRate.R250K;
    private int _currentCyl = -1;
    private int _firstSectorId = 1;
    private int _stepRateMs;

    public string Name { get; }
    public DiskGeometry? MediaGeometry { get; private set; }
    public MediaState MediaState { get; private set; }
    public string? MediaStateMessage { get; private set; }
    public DataRate Rate => _rate;
    /// <summary>Číslo prvního sektoru na stopě (1 u PC/Atari, 0 u Acornu …).</summary>
    public int FirstSectorId => _firstSectorId;

    public FdrawDevice(IFdc fdc, string name, int stepRateMs = 6)
    {
        _fdc = fdc;
        Name = name;
        _stepRateMs = stepRateMs;
        _fdc.SetStepRate(stepRateMs, _rate);
        RefreshMedia();
    }

    /// <summary>Rychlost krokování hlavy v ms (3 = Atari standard, vyšší = bezpečnější pro staré mechaniky).</summary>
    public int StepRateMs
    {
        get => _stepRateMs;
        set { _stepRateMs = value; _fdc.SetStepRate(value, _rate); }
    }

    public string Controller => _fdc.Description;

    // ------------------------------------------------------------------ rozpoznání diskety

    public void RefreshMedia()
    {
        MediaGeometry = null;
        _currentCyl = -1;
        if (_fdc.CheckDisk() == false)
        {
            MediaState = MediaState.NoDisk;
            MediaStateMessage = Tr.T("v mechanice není disketa");
            return;
        }

        foreach (var rate in new[] { DataRate.R250K, DataRate.R500K, DataRate.R300K })
        {
            SetRate(rate);
            SeekTo(0);
            var ids = _fdc.ScanTrack(0);
            if (ids.Count == 0) continue;

            // sektor navíc z opravného formátu (SpareSectorId) do geometrie nepočítáme
            var sectors = ids.Where(i => i.Sector != SpareSectorId).Select(i => i.Sector).Distinct().OrderBy(x => x).ToList();
            if (sectors.Count == 0) continue;
            int size = ids.GroupBy(i => i.SizeCode).OrderByDescending(g => g.Count()).First().Key;
            _firstSectorId = sectors[0];
            int spt = sectors.Count;

            int heads = _fdc.ScanTrack(1).Count > 0 ? 2 : 1;
            int tracks = DetectTracks();
            SeekTo(0);

            MediaGeometry = new DiskGeometry(tracks, heads, spt, 128 << size);
            MediaState = MediaState.Ready;
            MediaStateMessage = Tr.F("{0} při {1}", MediaGeometry, RateText(rate)) + (_firstSectorId != 1 ? $", sektory od {_firstSectorId}" : "");
            return;
        }
        MediaState = MediaState.Unrecognized;
        MediaStateMessage = Tr.T("na disketě nejsou žádné MFM sektory — je nenaformátovaná, nebo je pro Amigu / Mac (GCR), což řadič PC neumí");
        SeekTo(0);
    }

    private int DetectTracks()
    {
        // Běžné diskety mají 80 stop; Atari hry a dema často 81–83.
        if (!TrackHasSectors(79))
            return TrackHasSectors(39) ? 40 : 80;
        int tracks = 80;
        for (int c = 80; c < MaxTracks; c++)
        {
            if (!TrackHasSectors(c)) break;
            tracks = c + 1;
        }
        return tracks;
    }

    private bool TrackHasSectors(int cyl)
    {
        try
        {
            SeekTo(cyl);
            var ids = _fdc.ScanTrack(0);
            return ids.Count > 0 && ids.Any(i => i.Cyl == cyl);
        }
        catch (SectorIOException) { return false; }
    }

    public static string RateText(DataRate r) => r switch
    {
        DataRate.R250K => "DD 250 kbit/s",
        DataRate.R300K => "300 kbit/s",
        DataRate.R500K => "HD 500 kbit/s",
        _ => "ED 1 Mbit/s",
    };

    private void SetRate(DataRate r)
    {
        if (r == _rate) return;
        _rate = r;
        _fdc.SetDataRate(r);
        _fdc.SetStepRate(_stepRateMs, r);
    }

    private void SeekTo(int cyl)
    {
        if (cyl == _currentCyl) return;
        _fdc.Seek(cyl);
        _currentCyl = cyl;
    }

    // ------------------------------------------------------------------ sektory

    public void BeginExclusive() { }
    public void EndExclusive() { }

    public byte[] ReadSectors(long lba, int count)
    {
        var g = MediaGeometry ?? throw new OperationException(Tr.T("Disketa není rozpoznaná."));
        var result = new byte[count * g.BytesPerSector];
        int done = 0;
        foreach (var (cyl, head, idx, n) in Split(g, lba, count))
        {
            SeekTo(cyl);
            var data = _fdc.Read(cyl, head, _firstSectorId + idx, SizeCode(g.BytesPerSector), n);
            Buffer.BlockCopy(data, 0, result, done * g.BytesPerSector, n * g.BytesPerSector);
            done += n;
        }
        return result;
    }

    public void WriteSectors(long lba, byte[] buffer, int offset, int count)
    {
        var g = MediaGeometry ?? throw new OperationException(Tr.T("Disketa není rozpoznaná."));
        int done = 0;
        foreach (var (cyl, head, idx, n) in Split(g, lba, count))
        {
            SeekTo(cyl);
            _fdc.Write(cyl, head, _firstSectorId + idx, SizeCode(g.BytesPerSector), n, buffer, offset + done * g.BytesPerSector);
            done += n;
        }
    }

    /// <summary>Rozdělí souvislý úsek sektorů podle stop.</summary>
    private static IEnumerable<(int cyl, int head, int idx, int n)> Split(DiskGeometry g, long lba, int count)
    {
        while (count > 0)
        {
            long track = lba / g.SectorsPerTrack;
            int idx = (int)(lba % g.SectorsPerTrack);
            int n = Math.Min(count, g.SectorsPerTrack - idx);
            yield return ((int)(track / g.Sides), (int)(track % g.Sides), idx, n);
            lba += n;
            count -= n;
        }
    }

    public void FormatCylinder(DiskGeometry target, int cylinder)
    {
        if (!SupportsGeometry(target)) throw new OperationException(Tr.F("Formát {0} přes fdrawcmd.sys nejde. {1}", target, GeometryLimits));
        SetRate(target.IsHighDensity ? DataRate.R500K : DataRate.R250K);
        int size = SizeCode(target.BytesPerSector);
        int gap = Gap3(target);
        SeekTo(cylinder);
        for (int h = 0; h < target.Sides; h++)
        {
            var ids = Enumerable.Range(0, target.SectorsPerTrack)
                                .Select(i => new SectorId((byte)cylinder, (byte)h, (byte)(1 + i), (byte)size)).ToList();
            _fdc.Format(cylinder, h, size, ids, gap, 0xE5);
        }
        _firstSectorId = 1;
        // Po formátu stopy 0 platí nová geometrie, i když zbytek ještě není hotový.
        MediaGeometry = target;
        MediaState = MediaState.Ready;
    }

    // ------------------------------------------------------------------ opravný formát (experimentální)

    /// <summary>Číslo sektoru navíc, který opravný formát položí přes vadné místo. Žádný systém ho nečte.</summary>
    public const byte SpareSectorId = 0xF0;

    /// <summary>Vejde se na stopu ještě jeden sektor? (DD do 9, HD do 18 sektorů po 512 B; 10/11 sektorů ne.)</summary>
    public static bool CanShiftSectors(DiskGeometry g) =>
        g.BytesPerSector == 512 && g.SectorsPerTrack <= (g.IsHighDensity ? 18 : 9);

    /// <summary>
    /// Opravný formát jedné stopy: přeformátuje ji se sektorem navíc (SpareSectorId) a zkouší ho postupně
    /// na každé pozici, dokud všechny skutečné sektory nejdou zapsat a dvakrát bez chyby přečíst. Vadné místo
    /// tak padne do sektoru, který nikdo nečte. Vrátí pozici sektoru navíc, nebo -1 — pak stopu vrátí
    /// do běžného formátu (vadné sektory se označí ve FAT jako dřív).
    /// </summary>
    public int ShiftSectors(DiskGeometry g, int cyl, int head)
    {
        if (!CanShiftSectors(g)) return -1;
        SetRate(g.IsHighDensity ? DataRate.R500K : DataRate.R250K);
        SeekTo(cyl);
        int spt = g.SectorsPerTrack;
        int gap = Gap3(new DiskGeometry(g.Tracks, g.Sides, spt + 1, 512));
        var pattern = new byte[spt * 512];
        new Random(cyl * 2 + head).NextBytes(pattern);
        var fill = Enumerable.Repeat((byte)0xE5, spt * 512).ToArray();

        for (int pos = 0; pos <= spt; pos++)
        {
            var ids = Enumerable.Range(1, spt).Select(r => new SectorId((byte)cyl, (byte)head, (byte)r, 2)).ToList();
            ids.Insert(pos, new SectorId((byte)cyl, (byte)head, SpareSectorId, 2));
            try
            {
                _fdc.Format(cyl, head, 2, ids, gap, 0xE5);
                _fdc.Write(cyl, head, 1, 2, spt, pattern, 0);
                if (!_fdc.Read(cyl, head, 1, 2, spt).SequenceEqual(pattern) || !_fdc.Read(cyl, head, 1, 2, spt).SequenceEqual(pattern))
                    continue;
                _fdc.Write(cyl, head, 1, 2, spt, fill, 0);   // jako po běžném formátu
                return pos;
            }
            catch (SectorIOException) { }
        }
        var standard = Enumerable.Range(1, spt).Select(r => new SectorId((byte)cyl, (byte)head, (byte)r, 2)).ToList();
        _fdc.Format(cyl, head, 2, standard, Gap3(g), 0xE5);
        return -1;
    }

    /// <summary>Mezera za sektorem při formátu — převzato z definic Greaseweazle / standardu PC.</summary>
    public static int Gap3(DiskGeometry g)
    {
        if (g.BytesPerSector == 512)
            switch (g.SectorsPerTrack)
            {
                case 8: return 0x50;
                case 9: return 84;
                case 10: return 30;
                case 11: return 3;
                case 18: return 0x6C;
                case 19: return 0x30;
                case 20: return 0x18;
                case 21: return 0x0C;
            }
        int capacity = g.IsHighDensity ? 12500 : 6250;
        int gap = (capacity - 146 - g.SectorsPerTrack * (g.BytesPerSector + 62)) / Math.Max(1, g.SectorsPerTrack);
        return Math.Max(3, Math.Min(0x6C, gap));
    }

    private static int SizeCode(int bps) => bps switch { 128 => 0, 256 => 1, 512 => 2, 1024 => 3, 2048 => 4, _ => 2 };

    // ------------------------------------------------------------------ možnosti

    public static string Limits => Tr.T("Interní mechanika s fdrawcmd.sys: MFM, až 84 stop, DD až 11 sektorů po 512 B (11 zvládne jen některý řadič), HD až 21 sektorů, sektory 128–1024 B. Amigu a Mac GCR řadič PC neumí.");

    public string GeometryLimits => Limits;

    public bool SupportsGeometry(DiskGeometry g) =>
        g.Tracks is >= 1 and <= MaxTracks &&
        g.Sides is 1 or 2 &&
        g.BytesPerSector is 128 or 256 or 512 or 1024 &&
        (long)g.SectorsPerTrack * g.BytesPerSector <= (g.IsHighDensity ? 21 * 512 : 11 * 512);

    public DiskGeometry FormatTargetFor(DiskGeometry image) => image;

    public void Dispose() => _fdc.Dispose();
}
