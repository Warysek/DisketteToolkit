using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Fdraw;

namespace AtariFloppy.Tests;

/// <summary>Simulovaný řadič µPD765 s disketou — pro testy fdrawcmd vrstvy bez hardwaru.</summary>
internal sealed class FakeFdc : IFdc
{
    public sealed class Sector
    {
        public SectorId Id;
        public byte[] Data = Array.Empty<byte>();
        public bool CrcError;
        /// <summary>Sektor leží přes fyzickou vadu stopy — co se do něj zapíše, nejde přečíst.</summary>
        public bool Damaged;
    }

    /// <summary>Fyzické vady povrchu: (stopa, strana) → úsek stopy v bajtech od indexu.</summary>
    public Dictionary<(int cyl, int head), (int from, int to)> Defects = new();

    public Dictionary<(int cyl, int head), List<Sector>> Tracks = new();
    public DataRate DiskRate = DataRate.R250K;
    public bool DiskPresent = true;
    public bool Supports11Sectors = true;
    public int PhysicalTracks = 84;
    public DataRate CurrentRate = DataRate.R250K;
    public int Cyl;
    public int StepRateMs;
    public int Seeks;

    public string Description => "simulovaný řadič";

    public static FakeFdc WithImage(DiskImage img, DataRate rate = DataRate.R250K, int firstId = 1)
    {
        var f = new FakeFdc { DiskRate = rate };
        var g = img.Geometry;
        int size = g.BytesPerSector switch { 128 => 0, 256 => 1, 1024 => 3, _ => 2 };
        for (int c = 0; c < g.Tracks; c++)
            for (int h = 0; h < g.Sides; h++)
            {
                var list = new List<Sector>();
                for (int s = 0; s < g.SectorsPerTrack; s++)
                {
                    var data = new byte[g.BytesPerSector];
                    Buffer.BlockCopy(img.Data, (int)g.Offset(c, h, s), data, 0, data.Length);
                    list.Add(new Sector { Id = new SectorId((byte)c, (byte)h, (byte)(firstId + s), (byte)size), Data = data });
                }
                f.Tracks[(c, h)] = list;
            }
        return f;
    }

    public void SetDataRate(DataRate rate) => CurrentRate = rate;
    public void SetStepRate(int milliseconds, DataRate rate) => StepRateMs = milliseconds;

    public void Seek(int cylinder)
    {
        if (cylinder >= PhysicalTracks) throw new SectorIOException("hlava nedojela", 0);
        Cyl = cylinder;
        Seeks++;
    }

    public bool? CheckDisk() => DiskPresent;

    private List<Sector>? Track(int head) =>
        DiskPresent && CurrentRate == DiskRate && Tracks.TryGetValue((Cyl, head), out var t) ? t : null;

    public List<SectorId> ScanTrack(int head) => Track(head)?.Select(s => s.Id).ToList() ?? new List<SectorId>();

    public byte[] Read(int cyl, int head, int firstSector, int sizeCode, int count)
    {
        if (cyl != Cyl) throw new SectorIOException("špatná stopa", 27);
        var t = Track(head) ?? throw new SectorIOException("sektor nenalezen", 27);
        var output = new List<byte>();
        for (int s = firstSector; s < firstSector + count; s++)
        {
            var sec = t.FirstOrDefault(x => x.Id.Sector == s && x.Id.Cyl == cyl) ?? throw new SectorIOException("sektor nenalezen", 27);
            if (sec.CrcError) throw new SectorIOException("CRC chyba", 23);
            output.AddRange(sec.Data);
        }
        return output.ToArray();
    }

    public void Write(int cyl, int head, int firstSector, int sizeCode, int count, byte[] data, int offset)
    {
        var t = Track(head) ?? throw new SectorIOException("sektor nenalezen", 27);
        for (int i = 0; i < count; i++)
        {
            var sec = t.FirstOrDefault(x => x.Id.Sector == firstSector + i) ?? throw new SectorIOException("sektor nenalezen", 27);
            Buffer.BlockCopy(data, offset + i * sec.Data.Length, sec.Data, 0, sec.Data.Length);
            sec.CrcError = sec.Damaged;
        }
    }

    public List<(int cyl, int head, int gap, int count)> FormatLog = new();

    public void Format(int cyl, int head, int sizeCode, IList<SectorId> ids, int gap3, byte fill)
    {
        if (cyl != Cyl) throw new SectorIOException("špatná stopa", 27);
        if (ids.Count == 11 && sizeCode == 2 && !Supports11Sectors) throw new SectorIOException("řadič 11 sektorů nezvládne", 27);
        DiskRate = CurrentRate;
        var sectors = ids.Select(id => new Sector { Id = id, Data = Enumerable.Repeat(fill, 128 << sizeCode).ToArray() }).ToList();
        if (Defects.TryGetValue((cyl, head), out var d))
        {
            // poloha na stopě: úvod 146 B, pak každý sektor = 62 B režie (ID, synchronizace) + data + mezera
            int pitch = 62 + (128 << sizeCode) + gap3;
            for (int i = 0; i < sectors.Count; i++)
            {
                int start = 146 + i * pitch, end = start + 62 + (128 << sizeCode);
                sectors[i].Damaged = start < d.to && d.from < end;
                sectors[i].CrcError = sectors[i].Damaged;
            }
        }
        Tracks[(cyl, head)] = sectors;
        FormatLog.Add((cyl, head, gap3, ids.Count));
    }

    public void Dispose() { }
}
