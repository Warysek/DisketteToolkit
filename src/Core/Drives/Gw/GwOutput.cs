using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AtariFloppy.Core.Analysis;

namespace AtariFloppy.Core.Drives.Gw;

/// <summary>Jedna přečtená stopa podle výpisu gw („T12.1: IBM MFM (9/9 sectors) from …“).</summary>
public sealed record GwTrackRead(int Cyl, int Head, string Codec, int Found, int Total, bool IsRetry);

/// <summary>
/// Rozbor textového výstupu nástroje gw (Greaseweazle Host Tools): průběh po stopách,
/// souhrnná mapa sektorů na konci čtení, chyby ověření a fatální chyby.
/// </summary>
public sealed class GwOutput
{
    private static readonly Regex TrackRx = new(@"^T(\d+)\.(\d+):\s+(.+?)\s+\((\d+)/(\d+) sectors\)(.*)$", RegexOptions.Compiled);
    private static readonly Regex EmptyRx = new(@"^T(\d+)\.(\d+):\s+(.+?)\s+Empty\b", RegexOptions.Compiled);
    private static readonly Regex GiveUpRx = new(@"^T(\d+)\.(\d+): Giving up: (\d+) sectors missing", RegexOptions.Compiled);
    private static readonly Regex WriteRx = new(@"^T(\d+)\.(\d+): Writing Track(?: \(Verify Failure: Retry #(\d+)\))?", RegexOptions.Compiled);
    private static readonly Regex EraseRx = new(@"^T(\d+)\.(\d+): Erasing Track", RegexOptions.Compiled);
    private static readonly Regex SkipRx = new(@"^T(\d+)\.(\d+): WARNING: (.*)$", RegexOptions.Compiled);
    private static readonly Regex SummaryRowRx = new(@"^(\d)\.\s*(\d+): (.*)$", RegexOptions.Compiled);
    private static readonly Regex VerifyFailRx = new(@"Failed to verify Track (\d+)\.(\d+)", RegexOptions.Compiled);

    public List<GwTrackRead> Reads { get; } = new();
    public HashSet<(int cyl, int head)> RetriedTracks { get; } = new();
    public HashSet<(int cyl, int head)> GaveUp { get; } = new();
    public HashSet<(int cyl, int head)> Written { get; } = new();
    public HashSet<(int cyl, int head)> VerifyRetries { get; } = new();
    public List<(int cyl, int head)> VerifyFailed { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Souhrnná mapa: (strana, sektor) → znaky po stopách ('.' dobrý, 'X' chybí, ' ' neexistuje).</summary>
    public Dictionary<(int head, int sector), string> Summary { get; } = new();
    public List<int> SummaryCyls { get; } = new();
    public string? Fatal { get; private set; }
    public int? FoundSectors { get; private set; }
    public int? TotalSectors { get; private set; }
    public List<string> Lines { get; } = new();

    private bool _expectFatal;
    private bool _inSummary;
    private string _tensRow = "";

    /// <summary>Zpracuje jeden řádek; vrací (stopa, strana), pokud řádek hlásí práci na stopě (pro průběh).</summary>
    public (int cyl, int head)? Feed(string line)
    {
        Lines.Add(line);
        if (_expectFatal)
        {
            Fatal = (Fatal == null ? "" : Fatal + " ") + line.Trim();
            _expectFatal = false;
            return null;
        }
        if (line.StartsWith("** FATAL ERROR")) { _expectFatal = true; return null; }
        if (line.StartsWith("Command Failed:")) { Fatal = line.Substring(15).Trim(); return null; }

        Match m;
        if ((m = VerifyFailRx.Match(line)).Success)
        {
            VerifyFailed.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            Fatal ??= line.Trim();
            return null;
        }
        if ((m = GiveUpRx.Match(line)).Success)
        {
            var k = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            GaveUp.Add(k);
            return k;
        }
        if ((m = SkipRx.Match(line)).Success)
        {
            Warnings.Add(line);
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        if ((m = TrackRx.Match(line)).Success)
        {
            var r = new GwTrackRead(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value,
                                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), m.Groups[6].Value.Contains("Retry #"));
            Reads.Add(r);
            if (r.IsRetry) RetriedTracks.Add((r.Cyl, r.Head));
            return (r.Cyl, r.Head);
        }
        if ((m = EmptyRx.Match(line)).Success)
        {
            var r = new GwTrackRead(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value, 0, 0, false);
            Reads.Add(r);
            return (r.Cyl, r.Head);
        }
        if ((m = WriteRx.Match(line)).Success)
        {
            var k = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            Written.Add(k);
            if (m.Groups[3].Success) VerifyRetries.Add(k);
            return k;
        }
        if ((m = EraseRx.Match(line)).Success)
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));

        if (line.StartsWith("Cyl-> ")) { _inSummary = true; _tensRow = line.Substring(6); return null; }
        if (_inSummary && line.StartsWith("H. S: "))
        {
            SummaryCyls.Clear();
            // číslice jednotek; desítky jsou v předchozím řádku — stačí nám pořadí sloupců
            string digits = line.Substring(6);
            int tens = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                char t = i < _tensRow.Length ? _tensRow[i] : ' ';
                if (t != ' ') tens = t - '0';
                SummaryCyls.Add(tens * 10 + (digits[i] - '0'));
            }
            return null;
        }
        if (_inSummary && (m = SummaryRowRx.Match(line)).Success)
        {
            Summary[(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))] = m.Groups[3].Value;
            return null;
        }
        var found = Regex.Match(line, @"^Found (\d+) sectors of (\d+)");
        if (found.Success)
        {
            FoundSectors = int.Parse(found.Groups[1].Value);
            TotalSectors = int.Parse(found.Groups[2].Value);
            _inSummary = false;
        }
        return null;
    }

    /// <summary>Poslední (konečný) výsledek čtení každé stopy.</summary>
    public Dictionary<(int cyl, int head), GwTrackRead> FinalReads()
    {
        var d = new Dictionary<(int, int), GwTrackRead>();
        foreach (var r in Reads) d[(r.Cyl, r.Head)] = r;
        return d;
    }

    /// <summary>Mapa povrchu ze souhrnu gw. Stopy, které se přečetly až na opakování, jsou „slabé“.</summary>
    public SurfaceMap BuildMap(DiskGeometry g, int firstCylinder = 0)
    {
        var map = new SurfaceMap(g);
        var final = FinalReads();
        for (int c = 0; c < g.Tracks; c++)
            for (int h = 0; h < g.Sides; h++)
                for (int s = 0; s < g.SectorsPerTrack; s++)
                {
                    long lba = g.Lba(c, h, s);
                    int cyl = firstCylinder + c;
                    SectorStatus st;
                    int col = SummaryCyls.IndexOf(cyl);
                    if (Summary.TryGetValue((h, s), out var row) && col >= 0 && col < row.Length)
                        st = row[col] == '.' ? SectorStatus.Ok : row[col] == 'X' ? SectorStatus.Bad : SectorStatus.NotTested;
                    else if (final.TryGetValue((cyl, h), out var r))
                        st = r.Total > 0 && r.Found == r.Total ? SectorStatus.Ok : r.Found == 0 ? SectorStatus.Bad : SectorStatus.NotTested;
                    else st = SectorStatus.NotTested;
                    if (st == SectorStatus.Ok && RetriedTracks.Contains((cyl, h))) st = SectorStatus.Weak;
                    map[lba] = st;
                    if (st == SectorStatus.Bad) map.Reasons[lba] = I18n.Tr.T("Greaseweazle sektor nenašel ani nedekódoval (chybí, nebo je poškozený)");
                    else if (st == SectorStatus.Weak) map.Reasons[lba] = I18n.Tr.T("stopa se přečetla celá až na opakovaný pokus");
                }
        return map;
    }
}
