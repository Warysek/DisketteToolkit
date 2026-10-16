using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Jedno čtení diskety: která mechanika, která disketa (podle boot sektoru) a kolik problémů na které straně.</summary>
public sealed record ReadRecord(DateTime When, string DriveId, string DiskKey, int Total, int Problems, int Side0, int Side1);

/// <summary>
/// Historie čtení (history.csv vedle nastavení). Z ní program pozná, jestli je problém v disketě, nebo
/// v mechanice: stejná disketa vadná ve dvou mechanikách = vadná disketa; s jednou mechanikou chybuje skoro
/// každá disketa = vadná mechanika; opakovaně horší jedna strana = špinavá nebo opotřebená hlava té strany.
/// Disketa se pozná podle otisku boot sektoru — kopie stejné hry proto vypadají jako stejná disketa.
/// </summary>
public sealed class ReadHistory
{
    private readonly string _path;
    public List<ReadRecord> Records { get; } = new();

    public ReadHistory(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var f = line.Split(';');
                if (f.Length < 7 || !DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) continue;
                int I(int k) => int.TryParse(f[k], out int v) ? v : 0;
                Records.Add(new ReadRecord(when, f[1], f[2], I(3), I(4), I(5), I(6)));
            }
        }
        catch { /* poškozená historie se ignoruje */ }
    }

    public void Add(ReadRecord r)
    {
        Records.Add(r);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, string.Join(";", r.When.ToString("o", CultureInfo.InvariantCulture), r.DriveId.Replace(";", ","), r.DiskKey,
                                                   r.Total, r.Problems, r.Side0, r.Side1) + Environment.NewLine);
        }
        catch { /* historie není kritická */ }
    }

    /// <summary>Otisk diskety z boot sektoru (prázdný, když boot sektor chybí nebo je celý prázdný).</summary>
    public static string DiskKey(byte[]? bootSector)
    {
        if (bootSector == null || bootSector.All(b => b == 0) || bootSector.All(b => b == 0xE5)) return "";
        using var sha = System.Security.Cryptography.SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(bootSector), 0, 8).Replace("-", "");
    }

    /// <summary>Co z historie plyne pro právě dokončené čtení. warn = důležité upozornění.</summary>
    public List<(bool warn, string text)> Analyze(ReadRecord cur)
    {
        var msgs = new List<(bool, string)>();
        string Side(int s) => s == 0 ? Tr.T("strana 0 (spodní hlava)") : Tr.T("strana 1 (horní hlava)");

        // 1) tahle disketa: chyby jen na jedné straně
        if (cur.Problems >= 3 && (cur.Side0 == 0 || cur.Side1 == 0))
            msgs.Add((false, Tr.F("Chyby jsou jen na straně {0}.", cur.Side0 > 0 ? 0 : 1)));

        // 2) mechanika: opakovaně horší jedna strana
        var mine = Records.Where(r => r.DriveId == cur.DriveId).Reverse().Take(10).Where(r => r.Problems >= 3).ToList();
        foreach (int s in new[] { 0, 1 })
        {
            int worse = mine.Count(r => (s == 0 ? r.Side0 : r.Side1) >= 3 * Math.Max(1, s == 0 ? r.Side1 : r.Side0));
            if (worse >= 3 && worse * 10 >= mine.Count * 6)
                msgs.Add((true, Tr.F("U této mechaniky bývá opakovaně horší {0} ({1} z {2} disket s chybami) — nejspíš špinavá nebo opotřebená hlava té strany. Zkus ji vyčistit.", Side(s), worse, mine.Count)));
        }

        // 3) stejná disketa v jiné mechanice
        if (cur.DiskKey.Length > 0)
            foreach (var other in Records.Where(r => r.DiskKey == cur.DiskKey && r.DriveId != cur.DriveId)
                                         .GroupBy(r => r.DriveId).Select(g => g.Last()))
            {
                if (cur.Problems > 0 && other.Problems == 0)
                    msgs.Add((true, Tr.F("Stejná disketa (nebo kopie se stejným boot sektorem) šla v mechanice {0} přečíst bez chyb — problém je spíš v téhle mechanice.", other.DriveId)));
                else if (cur.Problems == 0 && other.Problems > 0)
                    msgs.Add((false, Tr.F("V mechanice {0} měla tahle disketa chyby, tady ne — problém byl spíš v mechanice {0}.", other.DriveId)));
                else if (cur.Problems > 0 && other.Problems > 0)
                    msgs.Add((false, Tr.F("Chyby má tahle disketa i v mechanice {0} — vadná je spíš disketa.", other.DriveId)));
            }

        // 4) mechanika: chybuje skoro každá disketa
        var disks = Records.Where(r => r.DriveId == cur.DriveId).Reverse()
                           .GroupBy(r => r.DiskKey.Length > 0 ? r.DiskKey : r.When.Ticks.ToString())
                           .Select(g => g.First()).Take(6).ToList();
        int bad = disks.Count(r => r.Problems > 0);
        if (disks.Count >= 5 && bad * 10 >= disks.Count * 8)
            msgs.Add((true, Tr.F("Skoro každá disketa má v téhle mechanice chyby ({0} z {1} posledních) — zkontroluj mechaniku: vyčisti hlavu a změř otáčky (Diagnostika mechaniky).", bad, disks.Count)));
        return msgs;
    }
}
