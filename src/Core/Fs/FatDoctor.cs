using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Fs;

public enum IssueSeverity { Info, Warning, Error }

public sealed record FsIssue(IssueSeverity Severity, string Message, bool Repairable = false);

public sealed class FatCheckReport
{
    public List<FsIssue> Issues { get; } = new();
    /// <summary>Co oprava udělala (jen při opravě).</summary>
    public List<string> Actions { get; } = new();
    /// <summary>Soubory na nečitelných sektorech (cesta → sektory LBA).</summary>
    public Dictionary<string, List<long>> DamagedFiles { get; } = new();
    /// <summary>Soubory na slabých sektorech.</summary>
    public Dictionary<string, List<long>> WeakFiles { get; } = new();

    public bool IsFat { get; set; }
    public int Files { get; set; }
    public int Directories { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
    public int BadClusters { get; set; }
    /// <summary>Poškozený boot, FAT nebo kořenový adresář.</summary>
    public bool SystemAreaDamaged { get; set; }
    /// <summary>Sektory, kde leží systémová oblast (boot + FAT + kořen).</summary>
    public int SystemSectors { get; set; }

    public bool HasProblems => Issues.Any(i => i.Severity != IssueSeverity.Info);
    public bool HasRepairable => Issues.Any(i => i.Repairable);
    public int Count(IssueSeverity s) => Issues.Count(i => i.Severity == s);
}

/// <summary>
/// Kontrola a opatrná oprava FAT12 (Atari TOS, MS-DOS/Windows, MSX-DOS) — obdoba chkdsk.
/// Oprava mění jen FAT a adresářové záznamy, nikdy data souborů: ztracené clustery ukládá jako FILEnnnn.CHK,
/// sdílené clustery kopíruje, soubory na vadných sektorech jen hlásí.
/// </summary>
public sealed class FatDoctor
{
    public static FatCheckReport Check(byte[] disk, SurfaceMap? map = null) => new FatDoctor((byte[])disk.Clone(), map, false).Run();

    /// <summary>Opraví obraz v paměti (pole se změní). Vrací, co našel a co udělal.</summary>
    public static FatCheckReport Repair(byte[] disk, SurfaceMap? map = null) => new FatDoctor(disk, map, true).Run();

    /// <summary>Sektory, které se opravou změnily.</summary>
    public static List<long> ChangedSectors(byte[] before, byte[] after)
    {
        var list = new List<long>();
        for (long s = 0; s * 512 < Math.Min(before.Length, after.Length); s++)
            for (int k = 0; k < 512; k++)
                if (before[s * 512 + k] != after[s * 512 + k]) { list.Add(s); break; }
        return list;
    }

    // ------------------------------------------------------------------ stav

    private sealed class Entry
    {
        public string Path = "";
        public int Offset;          // bajtový offset adresářového záznamu v disku
        public bool IsDir;
        public long Size;
        public int FirstCluster;
        public List<int> Chain = new();
        /// <summary>Řetězec neskončil značkou konce (neplatný odkaz, smyčka, volný cluster).</summary>
        public bool Broken;
        /// <summary>Řetězec končí clusterem označeným ve FAT jako vadný.</summary>
        public bool EndsOnBad;
    }

    private readonly byte[] _d;
    private readonly SurfaceMap? _map;
    private readonly bool _repair;
    private readonly FatCheckReport _r = new();
    private BootSector _bs = null!;
    private int _spc, _cs, _dataStart, _maxCluster, _rootOffset, _primaryFat;
    private int[] _fatOffsets = Array.Empty<int>();
    private int[] _owner = Array.Empty<int>();
    private List<int>?[] _claims = Array.Empty<List<int>?>();
    private readonly List<Entry> _entries = new();
    private readonly HashSet<int> _visitedDirs = new();

    private FatDoctor(byte[] disk, SurfaceMap? map, bool repair)
    {
        _d = disk;
        _map = map;
        _repair = repair;
    }

    private void Issue(IssueSeverity s, string msg, bool repairable = false) => _r.Issues.Add(new FsIssue(s, msg, repairable));
    private void Action(string a) => _r.Actions.Add(a);

    private int Get(int c) => FatTable.Get(_d, _primaryFat, c);
    private void Set(int c, int v) { if (_repair) FatTable.Set(_d, _primaryFat, c, v); }
    private int ClusterOffset(int c) => (_dataStart + (c - 2) * _spc) * 512;
    private bool InRange(int c) => c >= 2 && c <= _maxCluster;

    // ------------------------------------------------------------------ běh

    private FatCheckReport Run()
    {
        _bs = new BootSector(_d);
        if (!_bs.BpbLooksValid)
        {
            Issue(IssueSeverity.Error, Tr.T("Boot sektor neobsahuje platný popis FAT (BPB) — disketa nemá souborový systém FAT, nebo je boot sektor poškozený."));
            return _r;
        }
        _spc = _bs.SectorsPerCluster;
        _cs = _spc * 512;
        int rootSecs = (_bs.RootEntries * 32 + 511) / 512;
        _fatOffsets = Enumerable.Range(0, _bs.FatCount).Select(f => (_bs.ReservedSectors + f * _bs.SectorsPerFat) * 512).ToArray();
        _rootOffset = (_bs.ReservedSectors + _bs.FatCount * _bs.SectorsPerFat) * 512;
        _dataStart = _bs.ReservedSectors + _bs.FatCount * _bs.SectorsPerFat + rootSecs;
        _r.SystemSectors = _dataStart;

        int diskSectors = _d.Length / 512;
        if (_bs.TotalSectors > diskSectors)
            Issue(IssueSeverity.Warning, Tr.F("Boot sektor udává {0} sektorů, disketa/obraz jich má jen {1}. Konec souborového systému chybí.", _bs.TotalSectors, diskSectors));
        int usable = Math.Min(_bs.TotalSectors, diskSectors);
        int clusters = (usable - _dataStart) / _spc;
        if (clusters <= 0) { Issue(IssueSeverity.Error, Tr.T("Souborový systém nemá žádné datové clustery — BPB je nesmyslný.")); return _r; }
        if (clusters >= 4085) { Issue(IssueSeverity.Error, Tr.F("Disk má {0} clusterů — to už není FAT12, kontrolu neumím.", clusters)); return _r; }
        int fatCapacity = _bs.SectorsPerFat * 512 * 2 / 3 - 2;
        if (clusters > fatCapacity)
        {
            Issue(IssueSeverity.Warning, Tr.F("FAT má místo jen pro {0} clusterů, disk jich má {1} — zbytek disku se nevyužije.", fatCapacity, clusters));
            clusters = fatCapacity;
        }
        _maxCluster = clusters + 1;
        _owner = Enumerable.Repeat(-1, _maxCluster + 1).ToArray();
        _claims = new List<int>?[_maxCluster + 1];
        _r.IsFat = true;
        if (RootLooksForeign())
        {
            // zavaděče (Smart Boot Manager…) a hry mívají platný BPB, ale za ním vlastní data — „oprava“ by je zničila
            _r.IsFat = false;
            Issue(IssueSeverity.Warning, Tr.T("Kořenový adresář nevypadá jako FAT — většina záznamů jsou nesmysly. Disketa má nejspíš jen boot sektor ve formátu DOS a za ním vlastní data (zavaděč, hra). Kontrolu a opravu přeskakuji, aby se nic nepoškodilo."));
            return _r;
        }

        CheckSystemArea();
        CheckBoot();
        ChooseFat();
        WalkDirectory(RootOffsets(), "", 0, 0, 0);
        ResolveFiles();
        BuildOwners();
        CheckLostClusters();
        CheckSurface();
        Statistics();

        if (_repair && _fatOffsets.Length > 1)
        {
            int len = _bs.SectorsPerFat * 512;
            foreach (int off in _fatOffsets.Where(o => o != _primaryFat))
                Buffer.BlockCopy(_d, _primaryFat, _d, off, len);
        }
        return _r;
    }

    /// <summary>
    /// Kořenový adresář je převážně nesmysl (řídicí znaky ve jménech, clustery mimo disk, rezervované atributy) —
    /// pak to není poškozená FAT disketa, ale jiný formát s DOS boot sektorem.
    /// </summary>
    private bool RootLooksForeign()
    {
        int entries = 0, garbage = 0;
        foreach (int o in RootOffsets())
        {
            if (o + 32 > _d.Length || _d[o] == 0x00) break;
            if (_d[o] == 0xE5 || _d[o + 11] == 0x0F) continue;
            entries++;
            int cluster = _d[o + 26] | (_d[o + 27] << 8);
            bool badName = Enumerable.Range(0, 11).Any(k => _d[o + k] < 0x20 && !(k == 0 && _d[o] == 0x05));
            if (badName || (_d[o + 11] & 0xC0) != 0 || cluster > _maxCluster) garbage++;
        }
        return entries >= 3 && garbage * 2 >= entries;
    }

    private IEnumerable<int> RootOffsets()
    {
        for (int i = 0; i < _bs.RootEntries; i++) yield return _rootOffset + i * 32;
    }

    // ------------------------------------------------------------------ boot, systémová oblast

    private void CheckSystemArea()
    {
        if (_map == null) return;
        var bad = Enumerable.Range(0, Math.Min(_dataStart, _map.Geometry.TotalSectors))
                            .Where(l => _map[l] is SectorStatus.Bad or SectorStatus.Weak or SectorStatus.WriteFailed).ToList();
        if (bad.Count == 0) return;
        _r.SystemAreaDamaged = true;
        string where = string.Join(", ", bad.Take(6).Select(l =>
        {
            var (t, s, n) = _map.Chs(l);
            string part = l == 0 ? "boot" : l < _bs.ReservedSectors + _bs.FatCount * _bs.SectorsPerFat ? "FAT" : Tr.T("kořenový adresář");
            return Tr.F("{0} (stopa {1}, strana {2}, sektor {3}, {4})", part, t, s, n, Describe(_map[l]));
        }));
        Issue(IssueSeverity.Error, Tr.F("Systémová oblast je poškozená: {0}. Opravu dělej jen na obrazu, ne na této disketě.", where));
    }

    private static string Describe(SectorStatus s) => s switch
    {
        SectorStatus.Bad => Tr.T("nečitelný"),
        SectorStatus.Weak => Tr.T("slabý"),
        SectorStatus.WriteFailed => Tr.T("selhal zápis"),
        _ => s.ToString(),
    };

    private void CheckBoot()
    {
        if (_bs.IsExecutable)
            Issue(IssueSeverity.Info, Tr.T("Boot sektor je spustitelný na Atari — buď vlastní zavaděč hry/dema, nebo boot virus. Pokud má jít o obyčejnou datovou disketu, je to podezřelé."));
        if (_d.Length >= 512 * (_bs.ReservedSectors + 1))
        {
            int media = _d[_fatOffsets[0]];
            if (media != _bs.MediaDescriptor && media >= 0xF0)
                Issue(IssueSeverity.Info, Tr.F("Typ média ve FAT (0x{0:X2}) se liší od boot sektoru (0x{1:X2}). Běžné u disket z Atari, nevadí.", media, _bs.MediaDescriptor));
        }
    }

    private bool InvalidValue(int v) => v == 1 || (v > _maxCluster && v < 0xFF0) || (v >= 0xFF0 && v <= 0xFF6);

    /// <summary>První dvě položky FAT nesou typ média a značku — bez nich disketu některé systémy odmítnou.</summary>
    private void CheckFatHeader()
    {
        int v0 = Get(0), v1 = Get(1);
        if ((v0 & 0xF00) == 0xF00 && (v0 & 0xFF) >= 0xF0 && v1 >= 0xFF8) return;
        Issue(IssueSeverity.Warning, Tr.F("Začátek FAT je poškozený (0x{0:X3} 0x{1:X3} místo 0x{2:X3} 0xFFF).", v0, v1, 0xF00 | _bs.MediaDescriptor), true);
        if (_repair) { Set(0, 0xF00 | _bs.MediaDescriptor); Set(1, 0xFFF); Action(Tr.T("Obnoven začátek FAT (typ média).")); }
    }

    private void ChooseFat()
    {
        _primaryFat = _fatOffsets[0];
        if (_fatOffsets.Length < 2) { CheckFatHeader(); return; }

        int[] invalid = _fatOffsets.Select(off =>
            Enumerable.Range(2, _maxCluster - 1).Count(c => InvalidValue(FatTable.Get(_d, off, c)))).ToArray();
        int diffs = 0;
        for (int c = 2; c <= _maxCluster; c++)
            if (_fatOffsets.Skip(1).Any(off => FatTable.Get(_d, off, c) != FatTable.Get(_d, _fatOffsets[0], c))) diffs++;

        int best = Array.IndexOf(invalid, invalid.Min());
        _primaryFat = _fatOffsets[best];
        CheckFatHeader();
        if (diffs > 0)
        {
            Issue(IssueSeverity.Warning, Tr.F("Kopie FAT se liší v {0} položkách (disketa nejspíš nebyla správně dopsaná). Použiji FAT {1}.", diffs, best + 1), true);
            if (_repair) Action(Tr.F("Kopie FAT sjednoceny podle FAT {0}.", best + 1));
        }
    }

    // ------------------------------------------------------------------ adresáře a soubory

    private void WalkDirectory(IEnumerable<int> offsets, string parent, int depth, int selfCluster, int parentCluster)
    {
        if (depth > 32) { Issue(IssueSeverity.Error, Tr.F("{0}: adresáře jsou vnořené příliš hluboko (smyčka?).", parent)); return; }
        bool isRoot = depth == 0;
        bool seenDot = false, seenDotDot = false;
        var subdirs = new List<Entry>();

        foreach (int o in offsets)
        {
            byte first = _d[o];
            if (first == 0x00) break;
            if (first == 0xE5) continue;
            byte attr = _d[o + 11];
            if (attr == 0x0F) continue;                       // dlouhé jméno (VFAT)
            if ((attr & 0x08) != 0 && (attr & 0x10) == 0)     // jmenovka svazku
            {
                if (!isRoot) Issue(IssueSeverity.Info, Tr.F("{0}: jmenovka svazku uvnitř podadresáře.", parent));
                continue;
            }

            string name = DecodeName(o);
            int cluster = _d[o + 26] | (_d[o + 27] << 8);

            if (name == "." || name == "..")
            {
                if (isRoot) continue;
                int expected = name == "." ? selfCluster : parentCluster;
                if (name == ".") seenDot = true; else seenDotDot = true;
                if (cluster != expected)
                {
                    Issue(IssueSeverity.Warning, Tr.F("{0}: položka „{1}“ ukazuje na cluster {2} místo {3}.", parent, name, cluster, expected), true);
                    if (_repair) { W16(o + 26, expected); Action(Tr.F("{0}: opravena položka „{1}“.", parent, name)); }
                }
                continue;
            }

            if (Enumerable.Range(0, 11).Any(k => _d[o + k] < 0x20 && !(k == 0 && _d[o] == 0x05)))
                Issue(IssueSeverity.Warning, Tr.F("{0}: jméno obsahuje řídicí znaky (poškozený záznam?).", JoinPath(parent, name)));

            var e = new Entry
            {
                Path = JoinPath(parent, name),
                Offset = o,
                IsDir = (attr & 0x10) != 0,
                Size = (uint)BitConverter.ToInt32(_d, o + 28),
                FirstCluster = cluster,
            };
            int id = _entries.Count;
            _entries.Add(e);
            if (e.IsDir) { _r.Directories++; CheckDirectory(e, id); subdirs.Add(e); }
            else { _r.Files++; CheckFile(e, id); }
        }

        if (!isRoot && (!seenDot || !seenDotDot))
            Issue(IssueSeverity.Info, Tr.F("{0}: chybí položky „.“ a „..“ (některé programy je nevytváří, nevadí).", parent));

        foreach (var dir in subdirs)
        {
            if (dir.Chain.Count == 0 || !_visitedDirs.Add(dir.Chain[0])) continue;
            WalkDirectory(dir.Chain.SelectMany(c => Enumerable.Range(0, _cs / 32).Select(i => ClusterOffset(c) + i * 32))
                                   .Where(off => off + 32 <= _d.Length),
                          dir.Path, depth + 1, dir.Chain[0], selfCluster);
        }
    }

    private void CheckDirectory(Entry e, int id)
    {
        if (!InRange(e.FirstCluster))
        {
            Issue(IssueSeverity.Error, Tr.F("{0}\\: adresář ukazuje mimo disk (cluster {1}) — jeho obsah je ztracený.", e.Path, e.FirstCluster));
            return;
        }
        e.Chain = FollowChain(e, id);
    }

    /// <summary>1. fáze: jen ověří první cluster a projde řetězec. Délku a sdílení řeší ResolveFiles, až je znám celý disk.</summary>
    private void CheckFile(Entry e, int id)
    {
        if (e.FirstCluster == 0)
        {
            if (e.Size > 0)
            {
                Issue(IssueSeverity.Error, Tr.F("{0}: má velikost {1} B, ale žádná data.", e.Path, e.Size), true);
                if (_repair) { W32(e.Offset + 28, 0); e.Size = 0; Action(Tr.F("{0}: velikost nastavena na 0.", e.Path)); }
            }
            return;
        }
        if (!InRange(e.FirstCluster))
        {
            Issue(IssueSeverity.Error, Tr.F("{0}: první cluster ({1}) je mimo disk — data jsou ztracená.", e.Path, e.FirstCluster), true);
            if (_repair) { W16(e.Offset + 26, 0); W32(e.Offset + 28, 0); e.FirstCluster = 0; e.Size = 0; Action(Tr.F("{0}: nastaven jako prázdný soubor.", e.Path)); }
            return;
        }
        e.Chain = FollowChain(e, id);
    }

    /// <summary>
    /// 1. fáze: projde řetězec a zapíše, kdo si který cluster nárokuje. NIC nemění —
    /// o opravách se rozhoduje až v ResolveFiles, kdy je známý celý disk (jinak by oprava
    /// jednoho souboru mohla rozbít jiný, se kterým sdílí clustery).
    /// </summary>
    private List<int> FollowChain(Entry e, int id)
    {
        var list = new List<int>();
        var seen = new HashSet<int>();
        int c = e.FirstCluster;
        string what = e.IsDir ? e.Path + "\\" : e.Path;

        while (true)
        {
            if (!seen.Add(c))
            {
                Issue(IssueSeverity.Error, Tr.F("{0}: řetězec clusterů tvoří smyčku.", what), true);
                e.Broken = true;
                break;
            }
            Claim(c, id);
            list.Add(c);

            int v = Get(c);
            if (FatTable.IsEnd(v)) break;
            if (v == FatTable.BadCluster)
            {
                Issue(IssueSeverity.Error, Tr.F("{0}: používá cluster {1}, který je ve FAT označený jako vadný.", what, c));
                e.EndsOnBad = true;
                break;
            }
            if (v == FatTable.Free)
            {
                Issue(IssueSeverity.Error, Tr.F("{0}: řetězec vede do clusteru {1}, který FAT vede jako volný.", what, c), true);
                e.Broken = true;
                break;
            }
            if (!InRange(v))
            {
                Issue(IssueSeverity.Error, Tr.F("{0}: řetězec pokračuje neplatnou hodnotou 0x{1:X3}.", what, v), true);
                e.Broken = true;
                break;
            }
            c = v;
        }
        return list;
    }

    private void Claim(int c, int id)
    {
        var l = _claims[c] ??= new List<int>();
        if (!l.Contains(id)) l.Add(id);
    }

    private void Unclaim(int c, int id) => _claims[c]?.Remove(id);
    private int Claims(int c) => _claims[c]?.Count ?? 0;

    /// <summary>
    /// 2. fáze (celý disk je projitý): délky řetězců proti velikostem, sdílené clustery,
    /// a nakonec přepis odkazů ve FAT podle výsledných řetězců.
    /// </summary>
    private void ResolveFiles()
    {
        var released = new HashSet<int>();

        // a) velikost souboru proti délce řetězce
        for (int id = 0; id < _entries.Count; id++)
        {
            var e = _entries[id];
            if (e.IsDir || e.Chain.Count == 0) continue;

            if (e.Size == 0)
            {
                Issue(IssueSeverity.Warning, Tr.F("{0}: prázdný soubor má přidělené clustery ({1}).", e.Path, e.Chain.Count), true);
                if (_repair)
                {
                    released.UnionWith(e.Chain);
                    W16(e.Offset + 26, 0);
                    e.FirstCluster = 0;
                    Action(Tr.F("{0}: uvolněny clustery prázdného souboru.", e.Path));
                }
                e.Chain = new List<int>();
                continue;
            }

            int needed = (int)((e.Size + _cs - 1) / _cs);
            if (e.Chain.Count > needed)
            {
                Issue(IssueSeverity.Warning, Tr.F("{0}: řetězec má {1} clusterů, soubor potřebuje jen {2}.", e.Path, e.Chain.Count, needed), true);
                released.UnionWith(e.Chain.Skip(needed));
                e.Chain = e.Chain.Take(needed).ToList();
                e.EndsOnBad = false;
                if (_repair) Action(Tr.F("{0}: odříznuty clustery za koncem souboru (ponecháno {1}, data souboru zůstala).", e.Path, needed));
            }
            else if (e.Chain.Count < needed)
            {
                long newSize = (long)e.Chain.Count * _cs;
                Issue(IssueSeverity.Warning, Tr.F("{0}: adresář udává {1} B, ale data mají jen {2} B — konec souboru chybí.", e.Path, e.Size, newSize), true);
                if (_repair) { W32(e.Offset + 28, newSize); e.Size = newSize; Action(Tr.F("{0}: velikost upravena na {1} B.", e.Path, newSize)); }
            }
        }

        // b) nároky podle výsledných řetězců
        _claims = new List<int>?[_maxCluster + 1];
        for (int id = 0; id < _entries.Count; id++)
            foreach (int c in _entries[id].Chain) Claim(c, id);

        // c) sdílené clustery: vlastníkem zůstane adresář, jinak první nalezený soubor; ostatní soubory dostanou kopie
        var pairs = new Dictionary<(int, int), int>();
        for (int c = 2; c <= _maxCluster; c++)
        {
            if (Claims(c) < 2) continue;
            int keeper = Keeper(c);
            foreach (int other in _claims[c]!.Where(x => x != keeper))
            {
                var key = (keeper, other);
                pairs.TryGetValue(key, out int n);
                pairs[key] = n + 1;
            }
        }
        foreach (var kv in pairs)
        {
            var first = _entries[kv.Key.Item1];
            var second = _entries[kv.Key.Item2];
            Issue(IssueSeverity.Error,
                  Tr.F("{0}{1} sdílí {2} {3} s {4}{5} (cross-link) — jeden z nich má v té části cizí data.", second.Path, (second.IsDir ? "\\" : ""), kv.Value, (kv.Value == 1 ? Tr.T("cluster") : Tr.T("clusterů")), first.Path, (first.IsDir ? "\\" : "")),
                  !second.IsDir);
        }
        if (!_repair) return;

        for (int id = 0; id < _entries.Count; id++)
        {
            var e = _entries[id];
            if (e.IsDir) continue;
            int copied = 0;
            bool noSpace = false;
            for (int i = 0; i < e.Chain.Count; i++)
            {
                int c = e.Chain[i];
                if (Claims(c) > 1 && Keeper(c) != id)
                {
                    if (CloneInto(e, id, i)) copied++; else noSpace = true;
                }
            }
            if (copied > 0) Action(Tr.F("{0}: {1} sdílených clusterů zkopírováno, soubory jsou teď oddělené.", e.Path, copied));
            if (noSpace) Action(Tr.F("{0}: na disku není volné místo pro oddělení všech sdílených clusterů.", e.Path));
        }

        // d) přepsat odkazy ve FAT podle výsledných řetězců (jen clustery, které patří jedinému vlastníkovi)
        for (int id = 0; id < _entries.Count; id++)
        {
            var e = _entries[id];
            for (int i = 0; i < e.Chain.Count; i++)
            {
                int c = e.Chain[i];
                if (Claims(c) > 1) continue;
                bool last = i == e.Chain.Count - 1;
                if (last && e.EndsOnBad) continue; // vadný cluster necháme označený
                int want = last ? FatTable.EndOfChain : e.Chain[i + 1];
                int have = Get(c);
                if (last ? !FatTable.IsEnd(have) : have != want) Set(c, want);
            }
        }

        // e) uvolnit clustery za koncem souborů, pokud je nikdo jiný nepoužívá
        foreach (int c in released)
            if (Claims(c) == 0 && Get(c) != FatTable.BadCluster) Set(c, FatTable.Free);
    }

    private int Keeper(int c)
    {
        var l = _claims[c]!;
        foreach (int id in l) if (_entries[id].IsDir) return id;
        return l[0];
    }

    /// <summary>Dá souboru vlastní kopii clusteru na pozici i (data se zkopírují, řetězec se přepojí).</summary>
    private bool CloneInto(Entry e, int id, int i)
    {
        int orig = e.Chain[i];
        int copy = AllocateFree();
        if (copy == -1) return false;
        Buffer.BlockCopy(_d, ClusterOffset(orig), _d, ClusterOffset(copy), _cs);
        Set(copy, i == e.Chain.Count - 1 ? FatTable.EndOfChain : e.Chain[i + 1]);
        if (i == 0) { W16(e.Offset + 26, copy); e.FirstCluster = copy; }
        else Set(e.Chain[i - 1], copy);
        Unclaim(orig, id);
        Claim(copy, id);
        e.Chain[i] = copy;
        return true;
    }

    private int AllocateFree()
    {
        for (int c = 2; c <= _maxCluster; c++)
        {
            if (Get(c) != FatTable.Free || Claims(c) > 0) continue;
            if (_map != null && Enumerable.Range(0, _spc).Any(k => IsProblem(_dataStart + (c - 2) * _spc + k))) continue;
            if (ClusterOffset(c) + _cs > _d.Length) continue;
            Set(c, FatTable.EndOfChain);
            return c;
        }
        return -1;
    }

    private void BuildOwners()
    {
        for (int c = 2; c <= _maxCluster; c++)
            _owner[c] = Claims(c) > 0 ? _claims[c]![0] : -1;
    }

    // ------------------------------------------------------------------ ztracené clustery

    private void CheckLostClusters()
    {
        var lost = new HashSet<int>();
        for (int c = 2; c <= _maxCluster; c++)
        {
            int v = Get(c);
            if (v != FatTable.Free && v != FatTable.BadCluster && _owner[c] == -1) lost.Add(c);
        }
        if (lost.Count == 0) return;

        // Rozdělit na řetězce: začátky jsou clustery, na které nic z ostatních ztracených neukazuje.
        var pointedTo = new HashSet<int>(lost.Select(Get).Where(lost.Contains));
        var chains = new List<List<int>>();
        var done = new HashSet<int>();
        foreach (int head in lost.Where(c => !pointedTo.Contains(c)).OrderBy(c => c).Concat(lost.OrderBy(c => c)))
        {
            if (done.Contains(head)) continue;
            var chain = new List<int>();
            int c = head;
            while (lost.Contains(c) && done.Add(c))
            {
                chain.Add(c);
                c = Get(c);
            }
            chains.Add(chain);
        }

        Issue(IssueSeverity.Warning,
              Tr.F("Ztracené clustery: {0} ({1} KB) v {2} řetězcích — obsazené ve FAT, ale nepatří žádnému souboru.", lost.Count, lost.Count * _cs / 1024, chains.Count), true);
        if (!_repair) return;

        int saved = 0, freed = 0, n = NextChkNumber();
        foreach (var chain in chains)
        {
            // uzavřít řetězec: poslední cluster = konec, vnitřní odkazy už vedou správně
            Set(chain[chain.Count - 1], FatTable.EndOfChain);
            int slot = FindFreeRootSlot();
            if (slot >= 0 && n < 10000)
            {
                CreateRootEntry(slot, $"FILE{n:D4}", "CHK", chain[0], (long)chain.Count * _cs);
                foreach (int c in chain) _owner[c] = int.MaxValue;
                n++;
                saved++;
            }
            else
            {
                foreach (int c in chain) Set(c, FatTable.Free);
                freed++;
            }
        }
        if (saved > 0) Action(Tr.F("Ztracená data uložena do {0} souborů FILEnnnn.CHK v kořenovém adresáři.", saved));
        if (freed > 0) Action(Tr.F("Kořenový adresář je plný — {0} ztracených řetězců bylo uvolněno.", freed));
    }

    private int NextChkNumber()
    {
        int max = -1;
        foreach (int o in RootOffsets())
        {
            if (_d[o] == 0) break;
            string name = Encoding.ASCII.GetString(_d, o, 11);
            if (name.StartsWith("FILE") && name.EndsWith("CHK") && int.TryParse(name.Substring(4, 4), out int k)) max = Math.Max(max, k);
        }
        return max + 1;
    }

    private int FindFreeRootSlot()
    {
        int endMarker = -1;
        foreach (int o in RootOffsets())
        {
            if (_d[o] == 0xE5) return o;
            if (_d[o] == 0x00) { endMarker = o; break; }
        }
        if (endMarker >= 0)
        {
            int next = endMarker + 32;
            if (next < _rootOffset + _bs.RootEntries * 32) _d[next] = 0x00; // za novou položkou musí zůstat konec adresáře
        }
        return endMarker;
    }

    private void CreateRootEntry(int o, string name, string ext, int cluster, long size)
    {
        Array.Clear(_d, o, 32);
        Encoding.ASCII.GetBytes(name.PadRight(8), 0, 8, _d, o);
        Encoding.ASCII.GetBytes(ext.PadRight(3), 0, 3, _d, o + 8);
        _d[o + 11] = 0x20; // archiv
        DiskFormatter.WriteDosDateTime(_d, o + 22, DateTime.Now);
        W16(o + 26, cluster);
        W32(o + 28, size);
    }

    // ------------------------------------------------------------------ povrch

    private bool IsProblem(long lba) =>
        _map != null && lba < _map.Geometry.TotalSectors && _map[lba] is SectorStatus.Bad or SectorStatus.Weak or SectorStatus.WriteFailed;

    private void CheckSurface()
    {
        if (_map == null) return;
        var freeMarked = new HashSet<int>();
        foreach (long lba in _map.Problems)
        {
            if (lba < _dataStart) continue; // systémová oblast už nahlášená
            int c = (int)((lba - _dataStart) / _spc) + 2;
            if (c > _maxCluster)
            {
                Issue(IssueSeverity.Info, Tr.F("Vadný sektor {0} leží za koncem souborového systému — nevadí.", lba));
                continue;
            }
            int owner = _owner[c];
            bool weak = _map[lba] == SectorStatus.Weak;
            if (owner >= 0 && owner < _entries.Count)
            {
                var dict = weak ? _r.WeakFiles : _r.DamagedFiles;
                string path = _entries[owner].Path + (_entries[owner].IsDir ? "\\" : "");
                if (!dict.TryGetValue(path, out var l)) dict[path] = l = new List<long>();
                l.Add(lba);
            }
            else if (owner == -1 && Get(c) == FatTable.Free)
            {
                freeMarked.Add(c);
            }
        }

        foreach (var kv in _r.DamagedFiles)
            Issue(IssueSeverity.Error, Tr.F("{0}: leží na {1} nečitelných sektorech — tahle část souboru je ztracená.", kv.Key, kv.Value.Count));
        foreach (var kv in _r.WeakFiles.Where(kv => !_r.DamagedFiles.ContainsKey(kv.Key)))
            Issue(IssueSeverity.Warning, Tr.F("{0}: leží na {1} slabých sektorech — přečetl se, ale doporučuji ho zkopírovat na jinou disketu.", kv.Key, kv.Value.Count));

        if (freeMarked.Count > 0)
        {
            Issue(IssueSeverity.Warning, Tr.F("Ve volném místě je {0} vadných/slabých clusterů, které FAT ještě nezná.", freeMarked.Count), true);
            if (_repair)
            {
                foreach (int c in freeMarked) Set(c, FatTable.BadCluster);
                Action(Tr.F("{0} vadných clusterů označeno ve FAT, aby se už nepoužily.", freeMarked.Count));
            }
        }
    }

    private void Statistics()
    {
        int free = 0, bad = 0, used = 0;
        for (int c = 2; c <= _maxCluster; c++)
        {
            int v = Get(c);
            if (v == FatTable.Free) free++;
            else if (v == FatTable.BadCluster) bad++;
            else used++;
        }
        _r.FreeBytes = (long)free * _cs;
        _r.UsedBytes = (long)used * _cs;
        _r.BadClusters = bad;
        if (bad > 0) Issue(IssueSeverity.Info, Tr.F("Ve FAT je {0} clusterů označených jako vadné ({1} KB) — ty se nepoužívají.", bad, bad * _cs / 1024));
    }

    // ------------------------------------------------------------------ pomocné

    private string DecodeName(int o)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < 8; k++) sb.Append(Chr(_d[o + k], k == 0));
        string b = sb.ToString().TrimEnd();
        sb.Clear();
        for (int k = 8; k < 11; k++) sb.Append(Chr(_d[o + k], false));
        string e = sb.ToString().TrimEnd();
        return e.Length > 0 ? b + "." + e : b;
    }

    private static char Chr(byte b, bool first) => first && b == 0x05 ? 'å' : b >= 0x20 && b < 0x7F ? (char)b : '_';

    private static string JoinPath(string parent, string name) => parent.Length == 0 ? name : parent + "\\" + name;

    private void W16(int o, int v)
    {
        if (!_repair) return;
        _d[o] = (byte)v; _d[o + 1] = (byte)(v >> 8);
    }

    private void W32(int o, long v)
    {
        if (!_repair) return;
        _d[o] = (byte)v; _d[o + 1] = (byte)(v >> 8); _d[o + 2] = (byte)(v >> 16); _d[o + 3] = (byte)(v >> 24);
    }
}
