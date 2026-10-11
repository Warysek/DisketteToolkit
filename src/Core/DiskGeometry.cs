using System;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core;

/// <summary>
/// Fyzické uspořádání diskety: stopy × strany × sektory na stopu.
/// Pořadí dat v image (.ST) i na disku (LBA): stopa 0 strana 0, stopa 0 strana 1, stopa 1 strana 0, …
/// </summary>
public sealed record DiskGeometry(int Tracks, int Sides, int SectorsPerTrack, int BytesPerSector = 512)
{
    public static readonly DiskGeometry Dd720 = new(80, 2, 9);
    public static readonly DiskGeometry Dd640 = new(80, 2, 8);
    public static readonly DiskGeometry Ss360 = new(80, 1, 9);
    public static readonly DiskGeometry Hd1440 = new(80, 2, 18);

    public int TotalSectors => Tracks * Sides * SectorsPerTrack;
    public int TrackSize => SectorsPerTrack * BytesPerSector;
    public long SizeBytes => (long)TotalSectors * BytesPerSector;
    public int TrackCount => Tracks * Sides;

    /// <summary>HD disketa (18+ sektorů na stopu).</summary>
    public bool IsHighDensity => SectorsPerTrack >= 13;

    /// <summary>Pořadové číslo sektoru (0 = boot sektor). Sektor je zde 0-based.</summary>
    public long Lba(int track, int side, int sector = 0) => ((long)track * Sides + side) * SectorsPerTrack + sector;

    public long Offset(int track, int side, int sector = 0) => Lba(track, side, sector) * BytesPerSector;

    public override string ToString()
    {
        string sides = Sides == 1 ? Tr.T("strana") : Tr.T("strany");
        string kb = SizeBytes % 1024 == 0 ? (SizeBytes / 1024).ToString() : (SizeBytes / 1024.0).ToString("0.#");
        return Tr.F("{0} stop × {1} {2} × {3} sektorů ({4} KB)", Tracks, Sides, sides, SectorsPerTrack, kb);
    }

    public string Short => $"{Tracks}x{Sides}x{SectorsPerTrack}";

    /// <summary>Formát "80x2x9" (stopy x strany x sektory).</summary>
    public static bool TryParse(string text, out DiskGeometry geometry)
    {
        geometry = Dd720;
        var parts = text.ToLowerInvariant().Replace('×', 'x').Split('x');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int t) || !int.TryParse(parts[1], out int s) || !int.TryParse(parts[2], out int n))
            return false;
        if (t < 1 || t > 86 || s < 1 || s > 2 || n < 1 || n > 24) return false;
        geometry = new DiskGeometry(t, s, n);
        return true;
    }
}
