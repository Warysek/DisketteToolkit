using System;

namespace AtariFloppy.Core.Analysis;

/// <summary>Co se právě se sektorem stalo — barva políčka v mapě diskety.</summary>
public enum CellState : byte
{
    /// <summary>Operace se ho zatím nedotkla.</summary>
    Untouched,
    /// <summary>Přečteno v pořádku.</summary>
    Read,
    /// <summary>Stopa naformátovaná (ještě bez dat).</summary>
    Formatted,
    /// <summary>Zapsáno.</summary>
    Written,
    /// <summary>Zapsáno a ověřeno čtením.</summary>
    Verified,
    /// <summary>Přečteno až na opakovaný pokus.</summary>
    Weak,
    /// <summary>Nejde přečíst.</summary>
    Bad,
    /// <summary>Zápis selhal nebo data nesouhlasí.</summary>
    WriteFailed,
    /// <summary>Záměrně vynecháno (např. zápisový test stopy s vadnými sektory).</summary>
    Skipped,
}

/// <summary>Právě prováděná činnost hlavy — zvýraznění aktuální stopy.</summary>
public enum TrackPhase : byte { Reading, Writing, Formatting, Verifying, Erasing }

/// <summary>
/// Pozorovatel průběhu po stopách a sektorech — pro živou mapu diskety (jako ve FastCopy).
/// Volá se z vlákna operace; implementace si musí data předat do okna sama (thread-safe).
/// </summary>
public interface ITrackObserver
{
    /// <summary>Začíná operace nad disketou této geometrie; mapa se vymaže.</summary>
    void Begin(DiskGeometry geometry, string title);
    /// <summary>Hlava je teď na této stopě a straně (side = -1 znamená obě strany, např. formát válce).</summary>
    void Head(int track, int side, TrackPhase phase);
    /// <summary>Stav jednoho sektoru (sector = index od 0).</summary>
    void Sector(int track, int side, int sector, CellState state);
    /// <summary>Stav všech sektorů stopy, které nemají horší stav (side = -1 = obě strany).</summary>
    void Track(int track, int side, CellState state);
    void End();
}

/// <summary>
/// Pozorovatel připojený k vláknu, které operaci provádí. Diskové operace ho volají samy,
/// takže se nemusí předávat přes všechny metody:
/// <code>using (TrackActivity.Attach(mapa)) DiskOperations.ReadDisk(...);</code>
/// </summary>
public static class TrackActivity
{
    [ThreadStatic] private static ITrackObserver? _current;

    public static ITrackObserver? Current => _current;

    public static IDisposable Attach(ITrackObserver? observer)
    {
        var previous = _current;
        _current = observer;
        return new Detach(previous);
    }

    private sealed class Detach : IDisposable
    {
        private readonly ITrackObserver? _previous;
        private bool _done;
        public Detach(ITrackObserver? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current = _previous;
        }
    }

    // Pohodlné zkratky pro diskové operace (nic nedělají, když nikdo nepozoruje).
    internal static void Begin(DiskGeometry g, string title) => _current?.Begin(g, title);
    internal static void Head(int t, int s, TrackPhase p) => _current?.Head(t, s, p);
    internal static void Sector(int t, int s, int n, CellState st) => _current?.Sector(t, s, n, st);
    internal static void Track(int t, int s, CellState st) => _current?.Track(t, s, st);
    internal static void End() => _current?.End();

    public static CellState FromSurface(SectorStatus s) => s switch
    {
        SectorStatus.Ok => CellState.Read,
        SectorStatus.Weak => CellState.Weak,
        SectorStatus.Bad => CellState.Bad,
        SectorStatus.WriteFailed => CellState.WriteFailed,
        _ => CellState.Untouched,
    };

    /// <summary>Pořadí závažnosti — horší stav se lepším nepřepíše (kromě Untouched).</summary>
    public static int Severity(CellState s) => s switch
    {
        CellState.Bad => 5,
        CellState.WriteFailed => 4,
        CellState.Weak => 3,
        _ => 0,
    };
}
