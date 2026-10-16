using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Diag;

/// <summary>Stav čidel mechaniky; null = nepodařilo se zjistit (ovladač / firmware to neumí).</summary>
public sealed class SensorReport
{
    /// <summary>Čidlo stopy 0 hlásí stopu 0, když je hlava na stopě 0 (má být true).</summary>
    public bool? Track0AtZero { get; set; }
    /// <summary>Čidlo stopy 0 hlásí stopu 0, i když hlava stojí jinde (má být false).</summary>
    public bool? Track0Elsewhere { get; set; }
    public bool? WriteProtected { get; set; }
    public bool? DiskPresent { get; set; }
    /// <summary>Index čidlo dává pulsy (jinak nejde změřit otáčka).</summary>
    public bool? IndexSeen { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>Jedno čtení celé stopy: kolik sektorů se přečetlo a které ne.</summary>
public sealed record TrackReadPass(int Found, int Total, IReadOnlyList<int> FailedSectors);

/// <summary>
/// Diagnostika a servis mechaniky (otáčky, čidla, pohyb hlavy, čištění, opakované čtení stopy, rozložení
/// stop). Umí ji interní mechanika s fdrawcmd.sys a Greaseweazle; USB disketovka ne — Windows k ní
/// takový přístup nedají.
/// </summary>
public interface IDriveDiag : IDisposable
{
    string Name { get; }
    /// <summary>Umí vypsat hlavičky sektorů s polohou na stopě (jen fdrawcmd.sys).</summary>
    bool CanLayout { get; }
    /// <summary>Měří otáčky; count = 0 → dokud se nezruší. Volá onRevolutionMs pro každou otáčku.</summary>
    void MeasureRpm(int count, Action<double> onRevolutionMs, CancellationToken ct);
    SensorReport Sensors(CancellationToken ct);
    void Seek(int cyl, CancellationToken ct);
    /// <summary>Čištění hlavy čisticí disketou: hlava jezdí cik-cak po celé dráze (jako gw clean).</summary>
    void Clean(int cyls, int passes, Action<int> onPass, CancellationToken ct);
    /// <summary>Zjistí formát vložené diskety pro opakované čtení; vrací popis.</summary>
    string PrepareTrackRead(CancellationToken ct);
    TrackReadPass ReadTrack(int cyl, int head, CancellationToken ct);
    /// <summary>Kolik stop a stran má smysl vypisovat (po PrepareTrackRead).</summary>
    (int tracks, int sides) LayoutSize { get; }
    TrackLayout Layout(int cyl, int head, int expectedCount);
    /// <summary>
    /// Test přesnosti krokování: hlava dojede na stopu zvenku (od stopy 0) nebo zevnitř (od stopy 79)
    /// a ověří se, že je opravdu na ní. Vyžaduje PrepareTrackRead.
    /// </summary>
    (bool ok, string detail) StepCheck(int cyl, int head, bool fromInside, CancellationToken ct);
    /// <summary>Propustnost USB mezi PC a Greaseweazle (výpis gw bandwidth).</summary>
    List<string> Bandwidth(CancellationToken ct);
    /// <summary>Přečte magnetický tok jedné stopy do dočasného .scp; vrací cestu.</summary>
    string ReadTrackFlux(int cyl, int head, CancellationToken ct);
}

/// <summary>Diagnostika přes fdrawcmd.sys (přímé příkazy řadiči základní desky).</summary>
public sealed class FdrawDiag : IDriveDiag
{
    private readonly FdrawDevice _dev;
    private IFdc Fdc => _dev.Fdc;

    public FdrawDiag(FdrawDevice dev, bool canLayout = true)
    {
        _dev = dev;
        CanLayout = canLayout;
    }

    public string Name => _dev.Name + " — " + _dev.Controller;
    public bool CanLayout { get; }

    public void MeasureRpm(int count, Action<double> onRevolutionMs, CancellationToken ct)
    {
        _dev.RefreshMedia();   // nastaví rychlost řadiče podle diskety
        Fdc.Motor(true);
        try
        {
            for (int i = 0; count == 0 || i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int? us = Fdc.TrackTimeUs();
                if (us is null or <= 0) us = Fdc.TimedScanTrack(0)?.TrackTimeUs;
                if (us is null or <= 0)
                    throw new OperationException(Tr.T("Ovladač otáčky nezměřil. Je vložená naformátovaná disketa? Měření potřebuje fdrawcmd.sys verze 1.0.1.10 nebo novější."));
                onRevolutionMs(us.Value / 1000.0);
            }
        }
        finally { Fdc.Motor(false); }
    }

    public SensorReport Sensors(CancellationToken ct)
    {
        var r = new SensorReport { DiskPresent = Fdc.CheckDisk() };
        if (r.DiskPresent == null) r.Notes.Add(Tr.T("Ovladač neumí zjistit vloženou disketu (starší fdrawcmd.sys)."));
        _dev.MoveHead(10);
        byte? away = Fdc.DriveStatus(0);
        try { _dev.MoveHead(0); }
        catch (SectorIOException e)
        {
            // návrat na stopu 0 (recalibrate) se řídí čidlem stopy 0 — když selže, čidlo nejspíš nehlásí
            r.Track0AtZero = false;
            r.Notes.Add(e.Message);
        }
        byte? zero = r.Track0AtZero == false ? null : Fdc.DriveStatus(0);
        if (away != null)
        {
            r.Track0Elsewhere = (away.Value & St3.Track0) != 0;
            r.WriteProtected = (away.Value & St3.WriteProtect) != 0;
        }
        if (zero != null) r.Track0AtZero = (zero.Value & St3.Track0) != 0;
        if (away == null && zero == null && r.Track0AtZero == null) r.Notes.Add(Tr.T("Řadič nevrátil stav mechaniky (ST3)."));
        else r.Notes.Add(Tr.F("ST3 na stopě 10: 0x{0:X2}, na stopě 0: 0x{1:X2}", away ?? 0, zero ?? 0));
        if (r.DiskPresent == true)
        {
            try
            {
                Fdc.Motor(true);
                r.IndexSeen = (Fdc.TrackTimeUs() ?? Fdc.TimedScanTrack(0)?.TrackTimeUs) > 0;
            }
            finally { Fdc.Motor(false); }
            if (r.IndexSeen == false) r.Notes.Add(Tr.T("Index nejde ověřit — ovladač neměří otáčku (u nenaformátované diskety je to běžné)."));
        }
        else r.Notes.Add(Tr.T("Index čidlo se ověřuje jen s vloženou disketou."));
        return r;
    }

    public void Seek(int cyl, CancellationToken ct) => _dev.MoveHead(cyl);

    public void Clean(int cyls, int passes, Action<int> onPass, CancellationToken ct)
    {
        int step = Math.Max(cyls / 8, 2);
        Fdc.Motor(true);
        try
        {
            for (int p = 0; p < passes; p++)
            {
                onPass(p);
                for (int c = 0; c < cyls; c += step)
                {
                    ct.ThrowIfCancellationRequested();
                    _dev.MoveHead(Math.Min(c + step - 1, cyls - 1));
                    Thread.Sleep(100);
                    _dev.MoveHead(c);
                    Thread.Sleep(100);
                }
            }
            _dev.MoveHead(0);
        }
        finally { Fdc.Motor(false); }
    }

    public string PrepareTrackRead(CancellationToken ct)
    {
        _dev.RefreshMedia();
        if (_dev.MediaGeometry == null) throw new OperationException(_dev.MediaStateMessage ?? Tr.T("Disketa není rozpoznaná."));
        return _dev.MediaStateMessage ?? _dev.MediaGeometry.ToString();
    }

    public (int tracks, int sides) LayoutSize => (_dev.MediaGeometry?.Tracks ?? 80, _dev.MediaGeometry?.Sides ?? 2);

    public TrackReadPass ReadTrack(int cyl, int head, CancellationToken ct)
    {
        var g = _dev.MediaGeometry ?? throw new OperationException(Tr.T("Disketa není rozpoznaná."));
        int size = g.BytesPerSector switch { 128 => 0, 256 => 1, 1024 => 3, _ => 2 };
        _dev.MoveHead(cyl);
        var failed = new List<int>();
        for (int s = 0; s < g.SectorsPerTrack; s++)
        {
            ct.ThrowIfCancellationRequested();
            int id = _dev.FirstSectorId + s;
            try { Fdc.Read(cyl, head, id, size, 1); }
            catch (SectorIOException) { failed.Add(id); }
        }
        return new TrackReadPass(g.SectorsPerTrack - failed.Count, g.SectorsPerTrack, failed);
    }

    public TrackLayout Layout(int cyl, int head, int expectedCount)
    {
        _dev.MoveHead(cyl);
        var timed = Fdc.TimedScanTrack(head);
        var list = timed != null
            ? timed.Sectors.Select(s => ((int?)s.Us, s.Id)).ToList()
            : Fdc.ScanTrack(head).Select(id => ((int?)null, id)).ToList();
        return new TrackLayout(cyl, head, timed?.TrackTimeUs, list, expectedCount);
    }

    public (bool ok, string detail) StepCheck(int cyl, int head, bool fromInside, CancellationToken ct)
    {
        _dev.MoveHead(0);
        if (fromInside) _dev.MoveHead(79);
        _dev.MoveHead(cyl);
        var ids = Fdc.ScanTrack(head);
        if (ids.Count == 0) return (false, Tr.T("na stopě nejsou čitelné hlavičky sektorů"));
        var cyls = ids.Select(i => (int)i.Cyl).Distinct().OrderBy(x => x).ToList();
        bool ok = cyls.Count == 1 && cyls[0] == cyl;
        return (ok, Tr.F("hlavičky sektorů uvádějí stopu {0}", string.Join(", ", cyls)));
    }

    public List<string> Bandwidth(CancellationToken ct) =>
        throw new OperationException(Tr.T("Test propustnosti USB je jen pro Greaseweazle."));

    public string ReadTrackFlux(int cyl, int head, CancellationToken ct) =>
        throw new OperationException(Tr.T("Magnetický tok umí číst jen Greaseweazle — řadič na základní desce vrací jen hotové sektory."));

    public void Dispose() => _dev.Dispose();
}

/// <summary>Diagnostika přes Greaseweazle (program gw: rpm, seek, pin get, clean, read).</summary>
public sealed class GwDiag : IDriveDiag
{
    private readonly GwSession _s;
    private GwFormat? _format;

    public GwDiag(GwSession s, string name)
    {
        _s = s;
        Name = name;
    }

    public string Name { get; }
    public bool CanLayout => false;

    public void MeasureRpm(int count, Action<double> onRevolutionMs, CancellationToken ct) =>
        _s.Rpm(count == 0 ? 100000 : count, onRevolutionMs, ct);

    public SensorReport Sensors(CancellationToken ct)
    {
        var r = new SensorReport();
        _s.Seek(10, ct);
        bool? away = _s.Pin(26, ct);
        try
        {
            _s.Seek(0, ct);
            bool? zero = _s.Pin(26, ct);
            r.Track0AtZero = zero == null ? null : !zero.Value;   // vývod 26 /TRK0: nízká úroveň = stopa 0
        }
        catch (OperationException e)
        {
            // gw po každém přesunu na stopu 0 kontroluje signál TRK0 a bez něj skončí chybou
            r.Track0AtZero = false;
            r.Notes.Add(e.Message);
        }
        r.Track0Elsewhere = away == null ? null : !away.Value;
        bool? wp = _s.Pin(28, ct);
        r.WriteProtected = wp == null ? null : !wp.Value;   // /WRPROT: nízká = chráněno
        bool? chg = _s.Pin(34, ct);
        r.DiskPresent = chg;   // /DSKCHG po přesunu hlavy s vloženou disketou zhasne (vysoká úroveň)
        if (chg != null) r.Notes.Add(Tr.T("Vložená disketa se pozná podle vývodu 34 — u PC mechanik je to „výměna diskety“, u jiných „připraveno“; výsledek ber orientačně."));
        if (away == null || wp == null || chg == null)
            r.Notes.Add(Tr.T("Firmware Greaseweazle některý vývod číst neumí — v tom případě se stav neukáže."));
        try
        {
            var stats = new RpmStats();
            _s.Rpm(1, stats.Add, ct);
            r.IndexSeen = stats.Count > 0;
        }
        catch (OperationException e)
        {
            r.IndexSeen = false;
            r.Notes.Add(Tr.T("Otáčku nejde změřit (index): ") + e.Message);
        }
        return r;
    }

    public void Seek(int cyl, CancellationToken ct) => _s.Seek(cyl, ct);

    public void Clean(int cyls, int passes, Action<int> onPass, CancellationToken ct)
    {
        onPass(0);
        _s.Clean(cyls, passes, ct);
    }

    public string PrepareTrackRead(CancellationToken ct)
    {
        var probe = _s.Probe(null, ct);
        _format = probe.Format ?? throw new OperationException(probe.Summary);
        return probe.Summary;
    }

    public (int tracks, int sides) LayoutSize => (_format?.Geometry.Tracks ?? 80, _format?.Geometry.Sides ?? 2);

    public TrackReadPass ReadTrack(int cyl, int head, CancellationToken ct)
    {
        var f = _format ?? throw new OperationException(Tr.T("Formát diskety není zjištěný."));
        var (read, failed) = _s.ReadOneTrack(f, cyl, head, ct);
        int total = read?.Total > 0 ? read.Total : f.Geometry.SectorsPerTrack;
        // souhrn gw čísluje sektory od 0 — pro výpis je číslujeme od 1 jako na PC / Atari
        return new TrackReadPass(read?.Found ?? 0, total, failed.Select(x => x + 1).ToList());
    }

    public TrackLayout Layout(int cyl, int head, int expectedCount) =>
        throw new OperationException(Tr.T("Výpis rozložení stopy zatím umí jen interní mechanika s fdrawcmd.sys."));

    public (bool ok, string detail) StepCheck(int cyl, int head, bool fromInside, CancellationToken ct)
    {
        var f = _format ?? throw new OperationException(Tr.T("Formát diskety není zjištěný."));
        _s.Seek(fromInside ? 79 : 0, ct);
        var (read, _) = _s.ReadOneTrack(f, cyl, head, ct);
        int found = read?.Found ?? 0, total = read?.Total > 0 ? read.Total : f.Geometry.SectorsPerTrack;
        return (found == total, Tr.F("přečteno {0} z {1} sektorů", found, total));
    }

    public List<string> Bandwidth(CancellationToken ct) => _s.Bandwidth(ct);

    public string ReadTrackFlux(int cyl, int head, CancellationToken ct)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dtk-track-{cyl}-{head}-{Guid.NewGuid():N}.scp");
        _s.ReadTrackFlux(path, cyl, head, 3, ct);
        return path;
    }

    public void Dispose() { }
}
