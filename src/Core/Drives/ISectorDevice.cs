using System;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

/// <summary>Chyba čtení/zápisu konkrétních sektorů.</summary>
public sealed class SectorIOException : Exception
{
    public int Win32Error { get; }
    public SectorIOException(string message, int win32Error = 0) : base(message) { Win32Error = win32Error; }
}

/// <summary>Stav média v mechanice.</summary>
public enum MediaState
{
    Ready,
    NoDisk,
    /// <summary>Disketa je vložená, ale mechanika ji nerozpoznala (nenaformátovaná / nestandardní formát).</summary>
    Unrecognized,
    WriteProtected,
    Error,
}

/// <summary>
/// Sektorový přístup k disketě. Implementace pro Windows (USB / interní přes \\.\A:),
/// později fdrawcmd a Greaseweazle. V testech se nahrazuje pamětí.
/// </summary>
public interface ISectorDevice : IDisposable
{
    string Name { get; }

    /// <summary>Geometrie vložené diskety podle mechaniky; null když není rozpoznaná.</summary>
    DiskGeometry? MediaGeometry { get; }
    MediaState MediaState { get; }
    string? MediaStateMessage { get; }

    /// <summary>Znovu zjistí vloženou disketu (po formátu / výměně).</summary>
    void RefreshMedia();

    /// <summary>Zamkne jednotku pro výhradní přístup (nutné pro zápis a formát).</summary>
    void BeginExclusive();
    void EndExclusive();

    byte[] ReadSectors(long lba, int count);
    void WriteSectors(long lba, byte[] buffer, int offset, int count);

    /// <summary>Nízkoúrovňově naformátuje jednu stopu (obě strany) na danou geometrii.</summary>
    void FormatCylinder(DiskGeometry target, int cylinder);

    /// <summary>Umí zařízení s touto geometrií pracovat (číst, zapisovat, formátovat)?</summary>
    bool SupportsGeometry(DiskGeometry g);

    /// <summary>Proč zařízení geometrii nepodporuje (pro chybovou hlášku).</summary>
    string GeometryLimits { get; }

    /// <summary>Na jakou fyzickou geometrii naformátovat disketu, aby na ni šel zapsat daný image.</summary>
    DiskGeometry FormatTargetFor(DiskGeometry image);
}

/// <summary>Chování standardních mechanik přes Windows (USB i interní bez fdrawcmd).</summary>
public static class StandardGeometry
{
    public static string Limits => Tr.T("Standardní mechanika přes Windows umí jen 8, 9 nebo 18 sektorů po 512 B na stopu a nejvýš 80 stop.");

    public static bool Supports(DiskGeometry g) =>
        g.BytesPerSector == 512 && (g.SectorsPerTrack is 8 or 9 or 18) && g.Tracks <= 80 && g.Sides is 1 or 2;

    public static DiskGeometry FormatTargetFor(DiskGeometry image) =>
        image.IsHighDensity ? DiskGeometry.Hd1440 : image.SectorsPerTrack == 8 ? DiskGeometry.Dd640 : DiskGeometry.Dd720;
}
