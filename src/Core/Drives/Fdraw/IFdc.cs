using System;
using System.Collections.Generic;

namespace AtariFloppy.Core.Drives.Fdraw;

/// <summary>Identifikace sektoru tak, jak je zapsaná na disketě (C/H/R/N).</summary>
public readonly record struct SectorId(byte Cyl, byte Head, byte Sector, byte SizeCode)
{
    public int Length => 128 << SizeCode;
}

public enum DataRate
{
    /// <summary>DD 3,5" / 5,25" 360 KB.</summary>
    R250K,
    /// <summary>DD disketa v 360ot. mechanice (5,25" HD).</summary>
    R300K,
    /// <summary>HD 1,44 MB.</summary>
    R500K,
    /// <summary>ED 2,88 MB.</summary>
    R1M,
}

/// <summary>
/// Řadič disketové mechaniky na úrovni příkazů µPD765 / 82077 — jak ho zpřístupňuje fdrawcmd.sys.
/// Oddělené od zbytku programu kvůli testům (simulovaný řadič).
/// </summary>
public interface IFdc : IDisposable
{
    string Description { get; }
    void SetDataRate(DataRate rate);
    /// <summary>Rychlost krokování hlavy v ms (Atari standard 3 ms, bezpečné 6 ms, staré mechaniky až 12 ms).</summary>
    void SetStepRate(int milliseconds, DataRate rate);
    void Seek(int cylinder);
    /// <summary>Je v mechanice disketa? (null = řadič to neumí zjistit)</summary>
    bool? CheckDisk();
    /// <summary>Hlavičky sektorů na aktuální stopě v pořadí otáčení; prázdné = nenalezeno nic.</summary>
    List<SectorId> ScanTrack(int head);
    byte[] Read(int cyl, int head, int firstSector, int sizeCode, int count);
    void Write(int cyl, int head, int firstSector, int sizeCode, int count, byte[] data, int offset);
    void Format(int cyl, int head, int sizeCode, IList<SectorId> ids, int gap3, byte fill);
}
