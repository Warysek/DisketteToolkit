using System;
using System.Threading;

namespace AtariFloppy.Core.Analysis;

/// <summary>
/// Data pro mapu diskety: stav každého sektoru a poloha hlavy. Plní se z vlákna operace
/// (přes <see cref="ITrackObserver"/>), okno si ho čte při kreslení — proto zámek a číslo verze.
/// </summary>
public sealed class SectorMapModel : ITrackObserver
{
    private readonly object _sync = new();
    private CellState[] _cells = Array.Empty<CellState>();
    private readonly System.Collections.Generic.Dictionary<int, string> _reasons = new();
    private int _version;

    public DiskGeometry? Geometry { get; private set; }
    public string Title { get; private set; } = "";
    public int HeadTrack { get; private set; } = -1;
    public int HeadSide { get; private set; } = -1;
    public TrackPhase HeadPhase { get; private set; }
    public bool Running { get; private set; }

    /// <summary>Mění se při každé změně — okno podle něj pozná, že má překreslit.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Kopie stavu pro kreslení (bez držení zámku během kreslení).</summary>
    public CellState[] Snapshot()
    {
        lock (_sync) return (CellState[])_cells.Clone();
    }

    public CellState Get(int track, int side, int sector)
    {
        lock (_sync)
        {
            int i = Index(track, side, sector);
            return i >= 0 ? _cells[i] : CellState.Untouched;
        }
    }

    /// <summary>Důvod chyby sektoru (null = žádný není znám).</summary>
    public string? ReasonFor(int track, int side, int sector)
    {
        lock (_sync)
        {
            int i = Index(track, side, sector);
            return i >= 0 && _reasons.TryGetValue(i, out var r) ? r : null;
        }
    }

    public int Count(CellState s)
    {
        lock (_sync)
        {
            int n = 0;
            foreach (var c in _cells) if (c == s) n++;
            return n;
        }
    }

    /// <summary>Zobrazí prázdnou mapu pro danou geometrii (např. vybraný formát nebo otevřený obraz).</summary>
    public void Reset(DiskGeometry? g, string title = "")
    {
        lock (_sync)
        {
            Geometry = g;
            _cells = g == null ? Array.Empty<CellState>() : new CellState[g.TotalSectors];
            _reasons.Clear();
            Title = title;
            HeadTrack = HeadSide = -1;
            Running = false;
        }
        Bump();
    }

    /// <summary>Zobrazí výsledek dřívějšího testu povrchu.</summary>
    public void Load(SurfaceMap map, string title = "")
    {
        lock (_sync)
        {
            Geometry = map.Geometry;
            _cells = new CellState[map.Geometry.TotalSectors];
            for (long l = 0; l < _cells.Length; l++) _cells[l] = TrackActivity.FromSurface(map[l]);
            _reasons.Clear();
            foreach (var kv in map.Reasons) _reasons[(int)kv.Key] = kv.Value;
            Title = title;
            HeadTrack = HeadSide = -1;
            Running = false;
        }
        Bump();
    }

    // ------------------------------------------------------------------ ITrackObserver

    public void Begin(DiskGeometry geometry, string title)
    {
        lock (_sync)
        {
            Geometry = geometry;
            _cells = new CellState[geometry.TotalSectors];
            _reasons.Clear();
            Title = title;
            HeadTrack = HeadSide = -1;
            Running = true;
        }
        Bump();
    }

    public void Head(int track, int side, TrackPhase phase)
    {
        lock (_sync) { HeadTrack = track; HeadSide = side; HeadPhase = phase; }
        Bump();
    }

    public void Sector(int track, int side, int sector, CellState state)
    {
        lock (_sync)
        {
            int i = Index(track, side, sector);
            if (i >= 0) _cells[i] = state;
        }
        Bump();
    }

    public void Track(int track, int side, CellState state)
    {
        lock (_sync)
        {
            var g = Geometry;
            if (g == null || track < 0 || track >= g.Tracks) return;
            int s0 = side < 0 ? 0 : side, s1 = side < 0 ? g.Sides - 1 : side;
            for (int s = s0; s <= s1 && s < g.Sides; s++)
                for (int n = 0; n < g.SectorsPerTrack; n++)
                {
                    int i = Index(track, s, n);
                    if (TrackActivity.Severity(_cells[i]) <= TrackActivity.Severity(state)) _cells[i] = state;
                }
        }
        Bump();
    }

    public void Reason(int track, int side, int sector, string text)
    {
        lock (_sync)
        {
            int i = Index(track, side, sector);
            if (i >= 0) _reasons[i] = text;
        }
    }

    public void End()
    {
        lock (_sync) { Running = false; HeadTrack = HeadSide = -1; }
        Bump();
    }

    private int Index(int track, int side, int sector)
    {
        var g = Geometry;
        if (g == null || track < 0 || track >= g.Tracks || side < 0 || side >= g.Sides || sector < 0 || sector >= g.SectorsPerTrack) return -1;
        return (int)g.Lba(track, side, sector);
    }

    private void Bump() => Interlocked.Increment(ref _version);
}
