using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>
/// Soubor magnetického toku SuperCard Pro (.scp), jak ho ukládá Greaseweazle. Čteme jen to, co potřebuje
/// histogram toku: časy mezi magnetickými změnami pro každou otáčku stopy a dobu otáčky.
/// Formát: hlavička „SCP“, tabulka 168 posunů stop od 0x10, stopa „TRK“ + pro každou otáčku
/// (doba otáčky, počet vzorků, posun dat); vzorky 16 bitů big-endian, 0 = přetečení (+65536).
/// </summary>
public sealed class ScpFile
{
    private readonly byte[] _b;
    private readonly uint[] _offsets = new uint[168];

    public int Revolutions { get; }
    /// <summary>Délka jednoho tiku v ns (25 ns × (rozlišení + 1)).</summary>
    public double TickNs { get; }
    /// <summary>0 = obě strany, 1 = jen strana 0, 2 = jen strana 1.</summary>
    public int Heads { get; }

    private ScpFile(byte[] b)
    {
        _b = b;
        if (b.Length < 0x10 + 168 * 4 || b[0] != 'S' || b[1] != 'C' || b[2] != 'P')
            throw new FormatException(Tr.T("Soubor není magnetický tok SuperCard Pro (.scp)."));
        Revolutions = Math.Max(1, (int)b[5]);
        if (b[9] != 0 && b[9] != 16) throw new FormatException(Tr.T("Tento .scp soubor používá nepodporovanou šířku vzorků."));
        Heads = b[10];
        TickNs = 25.0 * (b[11] + 1);
        for (int i = 0; i < 168; i++) _offsets[i] = BitConverter.ToUInt32(b, 0x10 + i * 4);
    }

    public static ScpFile Load(string path) => new(File.ReadAllBytes(path));
    public static ScpFile Parse(byte[] data) => new(data);

    private int Index(int cyl, int head)
    {
        int i = cyl * 2 + head;
        if (i < 168 && _offsets[i] != 0) return i;
        // jednostranný soubor může číslovat stopy jen podle válce
        if (Heads != 0 && cyl < 168 && _offsets[cyl] != 0 && Heads - 1 == head) return cyl;
        return -1;
    }

    public bool HasTrack(int cyl, int head) => Index(cyl, head) >= 0;

    /// <summary>Stopy v souboru jako (válec, strana).</summary>
    public IEnumerable<(int cyl, int head)> Tracks()
    {
        for (int i = 0; i < 168; i++)
            if (_offsets[i] != 0) yield return (i / 2, i % 2);
    }

    /// <summary>Doby otáček stopy v ms.</summary>
    public List<double> RevolutionMs(int cyl, int head)
    {
        var list = new List<double>();
        int i = Index(cyl, head);
        if (i < 0) return list;
        int t = (int)_offsets[i];
        for (int r = 0; r < Revolutions; r++)
        {
            int e = t + 4 + r * 12;
            if (e + 12 > _b.Length) break;
            list.Add(BitConverter.ToUInt32(_b, e) * TickNs / 1e6);
        }
        return list;
    }

    /// <summary>Časy mezi magnetickými změnami (µs) — všechny otáčky stopy za sebou.</summary>
    public List<double> FluxUs(int cyl, int head)
    {
        var list = new List<double>();
        int i = Index(cyl, head);
        if (i < 0) return list;
        int t = (int)_offsets[i];
        if (t + 4 > _b.Length || _b[t] != 'T' || _b[t + 1] != 'R' || _b[t + 2] != 'K')
            throw new FormatException(Tr.T("Poškozený .scp soubor (chybí záhlaví stopy)."));
        for (int r = 0; r < Revolutions; r++)
        {
            int e = t + 4 + r * 12;
            if (e + 12 > _b.Length) break;
            int count = (int)BitConverter.ToUInt32(_b, e + 4);
            int pos = t + (int)BitConverter.ToUInt32(_b, e + 8);
            long carry = 0;
            for (int k = 0; k < count && pos + 1 < _b.Length; k++, pos += 2)
            {
                int v = (_b[pos] << 8) | _b[pos + 1];
                if (v == 0) { carry += 65536; continue; }
                list.Add((carry + v) * TickNs / 1000.0);
                carry = 0;
            }
        }
        return list;
    }

    /// <summary>Sestaví .scp z toku v µs (pro testy a ukázku): tracks[(válec, strana)] = otáčky.</summary>
    public static byte[] Build(IDictionary<(int cyl, int head), List<double[]>> tracks, int revolutions)
    {
        var ms = new MemoryStream();
        var header = new byte[0x10 + 168 * 4];
        header[0] = (byte)'S'; header[1] = (byte)'C'; header[2] = (byte)'P';
        header[3] = 0x19; header[5] = (byte)revolutions;
        header[6] = (byte)tracks.Keys.Min(k => k.cyl * 2 + k.head);
        header[7] = (byte)tracks.Keys.Max(k => k.cyl * 2 + k.head);
        ms.Write(header, 0, header.Length);
        foreach (var kv in tracks.OrderBy(k => k.Key.cyl * 2 + k.Key.head))
        {
            int index = kv.Key.cyl * 2 + kv.Key.head;
            long start = ms.Position;
            BitConverter.GetBytes((uint)start).CopyTo(header, 0x10 + index * 4);
            var revs = kv.Value;
            var data = revs.Select(rv => rv.Select(us => (int)Math.Round(us * 1000 / 25.0)).ToArray()).ToList();
            var th = new byte[4 + revolutions * 12];
            th[0] = (byte)'T'; th[1] = (byte)'R'; th[2] = (byte)'K'; th[3] = (byte)index;
            int dataOffset = th.Length;
            for (int r = 0; r < revolutions; r++)
            {
                var d = data[Math.Min(r, data.Count - 1)];
                BitConverter.GetBytes((uint)d.Sum()).CopyTo(th, 4 + r * 12);
                BitConverter.GetBytes((uint)d.Length).CopyTo(th, 8 + r * 12);
                BitConverter.GetBytes((uint)dataOffset).CopyTo(th, 12 + r * 12);
                dataOffset += d.Length * 2;
            }
            ms.Write(th, 0, th.Length);
            for (int r = 0; r < revolutions; r++)
                foreach (int v in data[Math.Min(r, data.Count - 1)])
                {
                    ms.WriteByte((byte)(v >> 8));
                    ms.WriteByte((byte)v);
                }
        }
        var bytes = ms.ToArray();
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        return bytes;
    }
}
