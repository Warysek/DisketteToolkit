using System.Collections.Generic;
using System.Linq;

namespace AtariFloppy.Core.Drives;

/// <summary>
/// Odvození schopností mechaniky ze seznamu formátů od Windows.
/// Pozor: Windows (hlavně u USB) vrací formáty pro PRÁVĚ VLOŽENOU disketu —
/// s DD disketou jen 640/720 KB, s HD jen 1,2/1,44 MB. Každá 3,5" mechanika
/// ale umí DD i HD, proto rozhoduje typ mechaniky, ne vložená disketa.
/// </summary>
public static class MediaSupport
{
    // MEDIA_TYPE z winioctl.h: 3,5" formáty
    private static readonly int[] ThreeAndHalf = { 2, 3, 4, 5, 14, 17, 18 };

    public static bool Is35(IReadOnlyCollection<int> media) => media.Count == 0 || media.Any(t => ThreeAndHalf.Contains(t));
    public static bool Supports720(IReadOnlyCollection<int> media) => Is35(media) || media.Contains(5);
    public static bool Supports1440(IReadOnlyCollection<int> media) => Is35(media) || media.Contains(2);

    /// <summary>
    /// Hustota VLOŽENÉ diskety: USB mechanika nabízí formáty podle čidla hustoty (otvor v disketě).
    /// DD → 640/720 KB, HD → 1,2/1,44 MB. Když jsou obě skupiny nebo nic, nevíme.
    /// </summary>
    public static InsertedMedia Density(IReadOnlyCollection<int> media)
    {
        bool dd = media.Contains(5) || media.Contains(14);
        bool hd = media.Contains(2) || media.Contains(17) || media.Contains(3);
        if (dd && !hd) return InsertedMedia.DD;
        if (hd && !dd) return InsertedMedia.HD;
        return InsertedMedia.Unknown;
    }
}
