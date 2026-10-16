using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Gw;

/// <summary>Formát diskety tak, jak mu rozumí gw (--format), případně s vlastní definicí (--diskdefs).</summary>
public sealed class GwFormat
{
    public string Name { get; init; } = "";
    /// <summary>Obsah vlastního souboru diskdefs; null = vestavěný formát gw.</summary>
    public string? DiskDefs { get; init; }
    public DiskGeometry Geometry { get; init; } = DiskGeometry.Dd720;
    /// <summary>Přípona dočasného obrazu, který gw zapíše/přečte (holá data v pořadí sektorů).</summary>
    public string ImageExt { get; init; } = ".img";
    public string Description { get; init; } = "";

    public override string ToString() => DiskDefs != null ? Tr.F("{0} (vlastní definice formátu)", Description) : Description.Length > 0 ? $"{Description} (gw: {Name})" : Name;

    /// <summary>IBM MFM formát (PC, Atari, MSX …) pro libovolnou geometrii: vestavěný, když existuje, jinak vlastní definice.</summary>
    public static GwFormat ForGeometry(DiskGeometry g, string? preferredName = null)
    {
        string? named = preferredName ?? (g.BytesPerSector != 512 ? null : (g.Tracks, g.Sides, g.SectorsPerTrack) switch
        {
            (80, 2, 9) => "atarist.720",
            (80, 1, 9) => "atarist.360",
            (80, 2, 10) => "atarist.800",
            (80, 1, 10) => "atarist.400",
            (80, 2, 11) => "atarist.880",
            (80, 1, 11) => "atarist.440",
            (80, 2, 18) => "ibm.1440",
            (80, 2, 21) => "ibm.dmf",
            (80, 2, 36) => "ibm.2880",
            _ => null,
        });
        if (named != null) return new GwFormat { Name = named, Geometry = g, Description = g.ToString() };

        int size = g.BytesPerSector switch { 128 => 128, 256 => 256, 1024 => 1024, _ => 512 };
        int rate = g.IsHighDensity ? 500 : (g.SectorsPerTrack >= 11 && size == 512 ? 261 : 250);
        var sb = new StringBuilder();
        sb.AppendLine("disk afloppy");
        sb.AppendLine($"    cyls = {g.Tracks}");
        sb.AppendLine($"    heads = {g.Sides}");
        sb.AppendLine("    tracks * ibm.mfm");
        sb.AppendLine($"        secs = {g.SectorsPerTrack}");
        sb.AppendLine($"        bps = {size}");
        sb.AppendLine($"        gap3 = {FdrawDevice.Gap3(g)}");
        sb.AppendLine($"        rate = {rate}");
        if (g.SectorsPerTrack >= 10 && size == 512 && !g.IsHighDensity) sb.AppendLine("        iam = no");
        sb.AppendLine("    end");
        sb.AppendLine("end");
        return new GwFormat { Name = "afloppy", DiskDefs = sb.ToString(), Geometry = g, Description = g.ToString() };
    }

    public static GwFormat Amiga(bool hd) => new()
    {
        Name = hd ? "amiga.amigados_hd" : "amiga.amigados", ImageExt = ".adf",
        Geometry = new DiskGeometry(80, 2, hd ? 22 : 11), Description = hd ? "Amiga 1,76 MB" : "Amiga 880 KB",
    };

    public static GwFormat Mac(bool twoSided) => new()
    {
        Name = twoSided ? "mac.800" : "mac.400", Geometry = new DiskGeometry(80, twoSided ? 2 : 1, 10),
        Description = twoSided ? "Mac 800 KB (GCR)" : "Mac 400 KB (GCR)",
    };

    public static GwFormat Commodore1581() => new()
    {
        Name = "commodore.1581", ImageExt = ".d81", Geometry = new DiskGeometry(80, 2, 10), Description = "Commodore 1581",
    };

    /// <summary>gw formát pro profil (formát pro určitý počítač).</summary>
    public static GwFormat? ForProfile(FormatProfile p) => p.Id switch
    {
        "amiga" or "amiga-ofs" => Amiga(false),
        "amiga-hd" => Amiga(true),
        "mac-800" => Mac(true),
        "mac-400" => Mac(false),
        "c1581" => Commodore1581(),
        "acorn" => new GwFormat { Name = "acorn.adfs.800", Geometry = p.Geometry, Description = "Acorn ADFS 800 KB" },
        "pc98" => new GwFormat { Name = "pc98.2hd", Geometry = p.Geometry, Description = "PC-98 1,23 MB" },
        "pc" or "amiga-crossdos" or "pcw" or "ql" or "minix" or "ext2-dd" => ForGeometry(p.Geometry, "ibm.720"),
        "pc-hd" or "mac-hfs" or "minix-hd" or "ext2" or "atari-hd" => ForGeometry(p.Geometry, "ibm.1440"),
        "msx" => ForGeometry(p.Geometry, "msx.2dd"),
        "msx-ss" => ForGeometry(p.Geometry, "msx.1dd"),
        "apple2-800" => null,
        _ => p.Need == HardwareNeed.Flux ? null : ForGeometry(p.Geometry),
    };

    /// <summary>gw formát pro obraz z disku — podle obsahu (Amiga) nebo geometrie.</summary>
    public static GwFormat ForImage(DiskImage img)
    {
        var g = img.Geometry;
        if (Fs.FileSystemDetector.Detect(img.Data).Kind == Fs.FsKind.Amiga) return Amiga(g.SectorsPerTrack >= 22);
        return ForGeometry(g);
    }
}

public sealed class GwProbe
{
    public GwFormat? Format { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>Práce s disketou přes Greaseweazle: čtení, zápis, formát, mazání, rozpoznání formátu.</summary>
public sealed class GwSession
{
    private readonly GwTool _tool;
    public string? Device { get; }
    /// <summary>Mechanika na sběrnici: A/B (PC kabel s překřížením) nebo 0–3 (Shugart).</summary>
    public string Drive { get; }
    public int Retries { get; set; } = 3;

    public GwSession(GwTool tool, string? device, string drive = "A")
    {
        _tool = tool;
        Device = device;
        Drive = drive;
    }

    private List<string> Common()
    {
        var a = new List<string>();
        if (Device != null) { a.Add("--device"); a.Add(Device); }
        a.Add("--drive"); a.Add(Drive);
        return a;
    }

    private GwOutput Run(string action, IEnumerable<string> args, int totalTracks, ProgressHandler? progress, string verb, CancellationToken ct,
                         TrackPhase? phase = null, CellState doneState = CellState.Read)
    {
        var output = new GwOutput();
        var seen = new HashSet<(int, int)>();
        var all = new List<string> { action };
        all.AddRange(args);
        int verifyFailed = 0;
        var observer = TrackActivity.Current;   // výstup gw se čte v jiném vlákně
        int code = _tool.Run(all, line =>
        {
            var t = output.Feed(line);
            if (phase != null && observer != null) ShowLine(observer, output, t, phase.Value, doneState, ref verifyFailed);
            if (t != null && progress != null)
            {
                seen.Add(t.Value);
                progress(Math.Min(seen.Count, totalTracks), totalTracks, Tr.F("{0} stopu {1}, stranu {2}", verb, t.Value.cyl, t.Value.head));
            }
        }, ct);
        if (code != 0 || output.Fatal != null)
            throw new OperationException("Greaseweazle: " + Explain(output.Fatal ?? output.Lines.LastOrDefault(l => l.Trim().Length > 0) ?? Tr.F("gw skončil s kódem {0}", code)));
        return output;
    }

    /// <summary>Průběžné kreslení mapy podle výpisu gw (po stopách; sektory doplní až souhrn na konci).</summary>
    private static void ShowLine(ITrackObserver o, GwOutput output, (int cyl, int head)? t, TrackPhase phase, CellState doneState, ref int verifyFailed)
    {
        while (verifyFailed < output.VerifyFailed.Count)
        {
            var (c, h) = output.VerifyFailed[verifyFailed++];
            o.Track(c, h, CellState.WriteFailed);
        }
        if (t == null) return;
        var (cyl, head) = t.Value;
        o.Head(cyl, head, phase);
        if (output.GaveUp.Contains((cyl, head))) { o.Track(cyl, head, CellState.Bad); return; }
        if (phase == TrackPhase.Reading)
        {
            var r = output.Reads.LastOrDefault();
            if (r == null || r.Cyl != cyl || r.Head != head) return;
            if (r.Total > 0 && r.Found == r.Total) o.Track(cyl, head, r.IsRetry ? CellState.Weak : doneState);
            else if (r.Total == 0) o.Track(cyl, head, doneState);   // tok bez dekódování
        }
        else
        {
            o.Track(cyl, head, output.VerifyRetries.Contains((cyl, head)) ? CellState.Weak : doneState);
        }
    }

    private static string Explain(string msg)
    {
        string m = msg.ToLowerInvariant();
        if (m.Contains("could not auto-probe") || m.Contains("no greaseweazle") || m.Contains("could not open"))
            return msg + Tr.T(" — Greaseweazle není připojený, nebo ho používá jiný program.");
        if (m.Contains("track 0") || m.Contains("no index"))
            return msg + Tr.T(" — mechanika nehlásí stopu 0 / indexový puls: je vložená disketa, připojené napájení a správně nastavená mechanika (A/B)?");
        if (m.Contains("write protect")) return Tr.T("Disketa je chráněná proti zápisu. Vyndej ji a v rohu diskety najdi malý posuvný jezdec se čtvercovým okénkem: posuň ho tak, aby okénko bylo ZAKRYTÉ (otevřené okénko = ochrana zapnutá). U 5,25\" diskety odlep nálepku z výřezu na boku. Pak disketu vlož znovu.");
        // firmware hlásí, že mu PC při zápisu nestihlo poslat data (vyprázdněný buffer) — gw to už samo zkoušel znovu
        if (m.Contains("flux underflow"))
            return msg + Tr.T(" — počítač nestihl Greaseweazle posílat data pro zápis přes USB. Zapoj ho přímo do portu v počítači (ne přes rozbočovač nebo přední panel), zkus kratší kabel a zavři programy, které zatěžují počítač. Propustnost ověří Diagnostika mechaniky → Test propustnosti USB.");
        return msg;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "afloppy-gw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    private static List<string> FormatArgs(GwFormat f, string dir)
    {
        var a = new List<string>();
        if (f.DiskDefs != null)
        {
            string defs = Path.Combine(dir, "afloppy.cfg");
            File.WriteAllText(defs, f.DiskDefs);
            a.Add("--diskdefs"); a.Add(defs);
        }
        a.Add("--format"); a.Add(f.Name);
        return a;
    }

    // ------------------------------------------------------------------ informace

    public List<string> Info(CancellationToken ct = default)
    {
        var lines = new List<string>();
        var args = new List<string> { "info" };
        if (Device != null) { args.Add("--device"); args.Add(Device); }
        _tool.Run(args, lines.Add, ct);
        return lines;
    }

    /// <summary>Rychlost krokování hlavy (µs) — ukládá se v Greaseweazle do jeho resetu.</summary>
    public void SetStepDelay(int microseconds, CancellationToken ct = default)
    {
        var a = new List<string> { "--step", microseconds.ToString() };
        if (Device != null) { a.Add("--device"); a.Add(Device); }
        Run("delays", a, 1, null, "", ct);
    }

    // ------------------------------------------------------------------ rozpoznání

    /// <summary>Zjistí, jak je vložená disketa naformátovaná: IBM MFM (PC/Atari/MSX…), Amiga, nebo Mac GCR.</summary>
    public GwProbe Probe(ProgressHandler? progress = null, CancellationToken ct = default)
    {
        string dir = TempDir();
        try
        {
            progress?.Invoke(0, 3, Tr.T("Zkouším formát PC / Atari / MSX (MFM)"));
            const string probeDefs = "disk probe\n    cyls = 84\n    heads = 2\n    tracks * ibm.scan\n    end\nend\n";
            File.WriteAllText(Path.Combine(dir, "probe.cfg"), probeDefs);
            var args = Common();
            args.AddRange(new[] { "--diskdefs", Path.Combine(dir, "probe.cfg"), "--format", "probe", "--tracks", "c=0,39,79-83", "--retries", "1",
                                  Path.Combine(dir, "probe.imd") });
            GwOutput? ibm = null;
            try { ibm = Run("read", args, 1, null, "", ct); } catch (OperationException) { }
            if (ibm != null)
            {
                var final = ibm.FinalReads();
                if (final.TryGetValue((0, 0), out var t0) && t0.Found > 0)
                {
                    int spt = Math.Max(t0.Total, t0.Found);
                    int heads = final.TryGetValue((0, 1), out var t01) && t01.Found > 0 ? 2 : 1;
                    // stopa za 79 se počítá, jen když její sektory mají v hlavičce opravdu tohle číslo stopy —
                    // jinak hlava dál nedojela nebo čte přeslech ze stopy 79 (disketa pak vyšla jako 81 stop)
                    var imd = ImdTracks(Path.Combine(dir, "probe.imd"));
                    int maxCyl = final.Where(kv => kv.Value.Found > 0)
                                      .Where(kv => kv.Key.cyl < 80 || imd == null || imd.TryGetValue(kv.Key, out var t) && t.CylinderIds.Count > 0 && t.CylinderIds.All(x => x == kv.Key.cyl))
                                      .Max(kv => kv.Key.cyl);
                    int tracks = maxCyl >= 79 ? maxCyl + 1 : maxCyl >= 39 ? 40 : 80;
                    int bps = ImdSectorSize(Path.Combine(dir, "probe.imd")) ?? 512;
                    // stopy za 79, které už nepatří souborovému systému (FAT podle boot sektoru), jsou zbytek
                    // dřívějšího formátu — PC disketa 1,44 MB tak vycházela jako 81 stop
                    string note = "";
                    byte[]? boot = imd != null && imd.TryGetValue((0, 0), out var t00) && t00.Sectors.TryGetValue(1, out var b0) ? b0 : null;
                    int fsTracks = TracksOfFileSystem(tracks, heads, spt, bps, boot);
                    if (fsTracks < tracks)
                    {
                        note = fsTracks == tracks - 1
                            ? Tr.F(" — stopa {0} je za koncem souborového systému, do obrazu ji nedávám", fsTracks)
                            : Tr.F(" — stopy {0}–{1} jsou za koncem souborového systému, do obrazu je nedávám", fsTracks, tracks - 1);
                        tracks = fsTracks;
                    }
                    var g = new DiskGeometry(tracks, heads, spt, bps);
                    return new GwProbe { Format = GwFormat.ForGeometry(g), Summary = $"MFM (PC / Atari / MSX …): {g}" + note };
                }
            }

            progress?.Invoke(1, 3, Tr.T("Zkouším formát Amiga"));
            var am = Common();
            am.AddRange(new[] { "--format", "amiga.amigados", "--tracks", "c=0", "--retries", "1", Path.Combine(dir, "probe.adf") });
            try
            {
                var a = Run("read", am, 1, null, "", ct).FinalReads();
                if (a.TryGetValue((0, 0), out var r) && r.Found > 0)
                    return new GwProbe { Format = GwFormat.Amiga(false), Summary = "Amiga 880 KB (AmigaDOS)" };
            }
            catch (OperationException) { }
            var amh = Common();
            amh.AddRange(new[] { "--format", "amiga.amigados_hd", "--tracks", "c=0", "--retries", "1", Path.Combine(dir, "probehd.adf") });
            try
            {
                var a = Run("read", amh, 1, null, "", ct).FinalReads();
                if (a.TryGetValue((0, 0), out var r) && r.Found > 0)
                    return new GwProbe { Format = GwFormat.Amiga(true), Summary = "Amiga 1,76 MB (AmigaDOS HD)" };
            }
            catch (OperationException) { }

            progress?.Invoke(2, 3, Tr.T("Zkouším formát Mac (GCR)"));
            var mac = Common();
            mac.AddRange(new[] { "--format", "mac.800", "--tracks", "c=0", "--retries", "1", Path.Combine(dir, "probe.img") });
            try
            {
                var m = Run("read", mac, 1, null, "", ct).FinalReads();
                if (m.TryGetValue((0, 0), out var r) && r.Found > 0)
                {
                    bool two = m.TryGetValue((0, 1), out var r1) && r1.Found > 0;
                    return new GwProbe { Format = GwFormat.Mac(two), Summary = two ? "Mac 800 KB (GCR)" : "Mac 400 KB (GCR)" };
                }
            }
            catch (OperationException) { }

            return new GwProbe { Summary = Tr.T("Formát se nepodařilo rozpoznat (nenaformátovaná disketa, chráněná hra, nebo formát, který neznám). Zkus zálohu jako tok (.scp).") };
        }
        finally { TryDelete(dir); }
    }

    /// <summary>
    /// Počet stop podle souborového systému (BPB v boot sektoru), když na disketě našel gw víc než 80 stop a BPB
    /// sedí s formátem; jinak beze změny. Zkracuje jen stopy za 79 — nikdy pod 80.
    /// </summary>
    public static int TracksOfFileSystem(int tracks, int heads, int spt, int bps, byte[]? boot)
    {
        if (tracks <= 80 || boot == null || boot.Length < 512) return tracks;
        var bs = new Fs.BootSector(boot);
        if (bs.BytesPerSector != bps || bs.SectorsPerTrack != spt || bs.Heads != heads || bs.TotalSectors <= 0 || bs.TotalSectors % (spt * heads) != 0)
            return tracks;
        int fs = bs.TotalSectors / (spt * heads);
        return fs >= 80 && fs < tracks ? fs : tracks;
    }

    /// <summary>Stopa z IMD souboru: čísla stop z hlaviček sektorů a data sektorů podle čísla sektoru.</summary>
    public sealed record ImdTrack(List<int> CylinderIds, Dictionary<int, byte[]> Sectors);

    /// <summary>
    /// Stopy IMD souboru (ImageDisk), jak ho zapisuje gw: záznam stopy = režim, válec, hlava (bit 7 = mapa válců,
    /// bit 6 = mapa hlav), počet sektorů, velikost, mapa sektorů, [mapa válců], [mapa hlav], data sektorů
    /// (0 = žádná, sudé = 1 bajt opakovaný, liché = celý sektor).
    /// </summary>
    public static Dictionary<(int cyl, int head), ImdTrack>? ImdTracks(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        int i = Array.IndexOf(b, (byte)0x1A);
        if (i < 0) return null;
        i++;
        var result = new Dictionary<(int, int), ImdTrack>();
        try
        {
            while (i + 5 <= b.Length)
            {
                int cyl = b[i + 1], head = b[i + 2], n = b[i + 3], size = 128 << Math.Min((int)b[i + 4], 6);
                var rmap = new int[n];
                for (int k = 0; k < n; k++) rmap[k] = b[i + 5 + k];
                i += 5 + n;
                var cyls = new List<int>();
                if ((head & 0x80) != 0) { for (int k = 0; k < n; k++) cyls.Add(b[i + k]); i += n; }
                else for (int k = 0; k < n; k++) cyls.Add(cyl);
                if ((head & 0x40) != 0) i += n;
                var sectors = new Dictionary<int, byte[]>();
                for (int k = 0; k < n; k++)
                {
                    int rec = b[i++];
                    if (rec == 0) continue;
                    var data = new byte[size];
                    if (rec % 2 == 0) { for (int x = 0; x < size; x++) data[x] = b[i]; i++; }
                    else { Buffer.BlockCopy(b, i, data, 0, size); i += size; }
                    sectors[rmap[k]] = data;
                }
                result[(cyl, head & 1)] = new ImdTrack(cyls, sectors);
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return null; }
        return result;
    }

    /// <summary>Velikost sektoru z prvního záznamu stopy v IMD souboru (ImageDisk).</summary>
    public static int? ImdSectorSize(string path)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        int i = Array.IndexOf(b, (byte)0x1A);
        if (i < 0 || i + 5 >= b.Length) return null;
        int code = b[i + 5];
        return code <= 6 ? 128 << code : null;
    }

    // ------------------------------------------------------------------ čtení

    public sealed class ReadResult
    {
        public DiskImage Image { get; init; } = null!;
        public SurfaceMap Map { get; init; } = null!;
        public GwOutput Output { get; init; } = null!;
        public List<string> Warnings { get; } = new();
    }

    public ReadResult Read(GwFormat f, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        string dir = TempDir();
        try
        {
            string tmp = Path.Combine(dir, "disk" + f.ImageExt);
            var args = Common();
            args.AddRange(FormatArgs(f, dir));
            args.AddRange(new[] { "--retries", Retries.ToString(), tmp });
            TrackActivity.Begin(f.Geometry, Tr.T("Čtení diskety (Greaseweazle)"));
            var output = Run("read", args, f.Geometry.TrackCount, progress, Tr.T("Čtu"), ct, TrackPhase.Reading);
            if (!File.Exists(tmp)) throw new OperationException(Tr.T("Greaseweazle nevytvořil obraz diskety."));
            var bytes = File.ReadAllBytes(tmp);
            var img = new DiskImage(f.Geometry, bytes);
            var result = new ReadResult { Image = img, Map = output.BuildMap(f.Geometry), Output = output };
            if (TrackActivity.Current != null)
                for (long l = 0; l < f.Geometry.TotalSectors; l++)
                {
                    var (c, h, n) = result.Map.Chs(l);
                    var st = TrackActivity.FromSurface(result.Map[l]);
                    if (st != CellState.Untouched) TrackActivity.Sector(c, h, n - 1, st);
                    if (result.Map.Reasons.TryGetValue(l, out var why)) TrackActivity.Reason(c, h, n - 1, why);
                }
            if (bytes.Length != f.Geometry.SizeBytes)
                result.Warnings.Add(Tr.F("gw vrátil {0} B, formát má {1} B.", bytes.Length, f.Geometry.SizeBytes));
            result.Warnings.AddRange(output.Warnings.Take(10));
            return result;
        }
        finally { TryDelete(dir); TrackActivity.End(); }
    }

    /// <summary>Záloha jako magnetický tok (.scp / .hfe / .raw) — uchová i kopírovací ochrany.</summary>
    public GwOutput ReadFlux(string outputPath, int revs, int tracks, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var args = Common();
        args.AddRange(new[] { "--revs", revs.ToString(), "--tracks", $"c=0-{tracks - 1}", outputPath });
        TrackActivity.Begin(new DiskGeometry(tracks, 2, 1, 512), Tr.T("Čtení magnetického toku"));
        try { return Run("read", args, tracks * 2, progress, Tr.T("Čtu tok"), ct, TrackPhase.Reading); }
        finally { TrackActivity.End(); }
    }

    // ------------------------------------------------------------------ zápis

    private bool _writeBandwidthOk;

    /// <summary>
    /// Před zápisem změří propustnost USB (gw bandwidth). Když gw hlásí, že nestačí, nezapisuje se vůbec:
    /// zápis by se uprostřed stopy přerušil („Flux Underflow“) a stopu poškodil — stalo se na skutečné disketě.
    /// Měří se jednou za relaci.
    /// </summary>
    private void EnsureWriteBandwidth(CancellationToken ct)
    {
        if (_writeBandwidthOk) return;
        var lines = Bandwidth(ct);
        if (lines.Any(l => l.IndexOf("BELOW REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0))
            throw new OperationException(Tr.T("Zápis jsem nezačal: USB mezi počítačem a Greaseweazle nestíhá (test propustnosti je pod minimem, které gw pro zápis potřebuje). Zápis by se uprostřed stopy přerušil a poškodil disketu. Zapoj Greaseweazle přímo do portu v počítači (zkus i USB 2.0), bez rozbočovače, krátkým kvalitním kabelem a zkus to znovu.")
                                         + "\n" + string.Join("\n", lines.Where(l => l.IndexOf("Bandwidth", StringComparison.OrdinalIgnoreCase) >= 0 || l.Contains("REQUIRED"))));
        _writeBandwidthOk = true;
    }

    public GwOutput Write(DiskImage img, GwFormat f, bool verify, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        EnsureWriteBandwidth(ct);
        string dir = TempDir();
        try
        {
            string tmp = Path.Combine(dir, "disk" + f.ImageExt);
            var data = new byte[f.Geometry.SizeBytes];
            Buffer.BlockCopy(img.Data, 0, data, 0, (int)Math.Min(data.Length, img.Data.Length));
            File.WriteAllBytes(tmp, data);
            var args = Common();
            args.AddRange(FormatArgs(f, dir));
            if (!verify) args.Add("--no-verify");
            args.Add(tmp);
            TrackActivity.Begin(f.Geometry, Tr.T("Zápis na disketu (Greaseweazle)"));
            return Run("write", args, f.Geometry.TrackCount, progress, Tr.T("Zapisuji"), ct, TrackPhase.Writing,
                       verify ? CellState.Verified : CellState.Written);
        }
        finally { TryDelete(dir); TrackActivity.End(); }
    }

    /// <summary>Zápis hotového souboru, kterému gw rozumí sám (.scp, .hfe, .ipf, .raw, .adf, .d81 …).</summary>
    public GwOutput WriteFile(string path, GwFormat? f, bool verify, int totalTracks, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        EnsureWriteBandwidth(ct);
        string dir = TempDir();
        try
        {
            var args = Common();
            if (f != null) args.AddRange(FormatArgs(f, dir));
            if (!verify) args.Add("--no-verify");
            args.Add(Path.GetFullPath(path));
            var g = f?.Geometry ?? new DiskGeometry(Math.Max(1, totalTracks / 2), 2, 1, 512);
            TrackActivity.Begin(g, Tr.T("Zápis na disketu (Greaseweazle)"));
            return Run("write", args, totalTracks, progress, Tr.T("Zapisuji"), ct, TrackPhase.Writing,
                       verify ? CellState.Verified : CellState.Written);
        }
        finally { TryDelete(dir); TrackActivity.End(); }
    }

    /// <summary>Aktualizace firmwaru Greaseweazle (gw update stáhne nejnovější firmware z GitHubu).</summary>
    public int UpdateFirmware(Action<string> onLine, CancellationToken ct = default)
    {
        var args = new List<string> { "update" };
        if (Device != null) { args.Add("--device"); args.Add(Device); }
        return _tool.Run(args, onLine, ct);
    }

    /// <summary>Verze firmwaru z „gw info“ (null = zařízení nenalezeno).</summary>
    public string? FirmwareVersion(CancellationToken ct = default)
    {
        foreach (var l in Info(ct))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*Firmware:\s*(.+)$");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    public GwOutput Erase(int tracks, ProgressHandler? progress = null, CancellationToken ct = default)
    {
        var args = Common();
        args.AddRange(new[] { "--tracks", $"c=0-{tracks - 1}" });
        TrackActivity.Begin(new DiskGeometry(tracks, 2, 1, 512), Tr.T("Mazání diskety"));
        try { return Run("erase", args, tracks * 2, progress, Tr.T("Mažu"), ct, TrackPhase.Erasing, CellState.Formatted); }
        finally { TrackActivity.End(); }
    }

    // ------------------------------------------------------------------ diagnostika mechaniky

    private static readonly System.Text.RegularExpressions.Regex PeriodRx = new(@"Period:\s*([\d.]+)\s*ms");
    private static readonly System.Text.RegularExpressions.Regex PinRx = new(@"^Pin\s+\d+\s+is\s+(Low|High)");

    /// <summary>gw rpm --nr N: doba každé otáčky v ms (souhrn za „***“ se přeskočí).</summary>
    public void Rpm(int count, Action<double> onRevolutionMs, CancellationToken ct = default)
    {
        var args = new List<string> { "rpm" };
        args.AddRange(Common());
        args.AddRange(new[] { "--nr", count.ToString() });
        var output = new GwOutput();
        bool summary = false;
        int code = _tool.Run(args, line =>
        {
            output.Feed(line);
            if (line.StartsWith("***")) summary = true;
            if (!summary && ParsePeriodMs(line) is double ms) onRevolutionMs(ms);
        }, ct);
        if (code != 0 || output.Fatal != null)
            throw new OperationException("Greaseweazle: " + Explain(output.Fatal ?? output.Lines.LastOrDefault(l => l.Trim().Length > 0) ?? Tr.F("gw skončil s kódem {0}", code)));
    }

    public static double? ParsePeriodMs(string line)
    {
        var m = PeriodRx.Match(line);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    /// <summary>Přesun hlavy na stopu (gw seek).</summary>
    public void Seek(int cyl, CancellationToken ct = default)
    {
        var a = Common();
        a.Add("--force");
        a.Add(cyl.ToString());
        Run("seek", a, 1, null, "", ct);
    }

    /// <summary>Úroveň vývodu rozhraní (gw pin get): true = vysoká (5 V), false = nízká, null = nejde přečíst.</summary>
    public bool? Pin(int pin, CancellationToken ct = default)
    {
        var args = new List<string> { "pin", "get" };
        args.AddRange(Common());
        args.Add(pin.ToString());
        bool? level = null;
        _tool.Run(args, line => level ??= ParsePin(line), ct);
        return level;
    }

    public static bool? ParsePin(string line)
    {
        var m = PinRx.Match(line.Trim());
        return m.Success ? m.Groups[1].Value == "High" : null;
    }

    /// <summary>Propustnost USB (gw bandwidth) — řádky výpisu.</summary>
    public List<string> Bandwidth(CancellationToken ct = default)
    {
        var args = new List<string> { "bandwidth" };
        if (Device != null) { args.Add("--device"); args.Add(Device); }
        var lines = new List<string>();
        var output = new GwOutput();
        int code = _tool.Run(args, l => { output.Feed(l); if (l.Trim().Length > 0) lines.Add(l.Trim()); }, ct);
        if (code != 0 || output.Fatal != null)
            throw new OperationException("Greaseweazle: " + Explain(output.Fatal ?? lines.LastOrDefault() ?? Tr.F("gw skončil s kódem {0}", code)));
        return lines;
    }

    /// <summary>Magnetický tok jedné stopy do souboru .scp (pro histogram).</summary>
    public void ReadTrackFlux(string outputPath, int cyl, int head, int revs, CancellationToken ct = default)
    {
        var args = Common();
        args.AddRange(new[] { "--revs", revs.ToString(), "--tracks", $"c={cyl}:h={head}", outputPath });
        Run("read", args, 1, null, "", ct);
    }

    /// <summary>Čištění hlavy čisticí disketou (gw clean).</summary>
    public void Clean(int cyls, int passes, CancellationToken ct = default)
    {
        var a = Common();
        a.AddRange(new[] { "--cyls", cyls.ToString(), "--passes", passes.ToString() });
        Run("clean", a, 1, null, "", ct);
    }

    /// <summary>Jedno čtení jedné stopy bez opakování: výsledek a čísla sektorů, které chyběly (podle souhrnu gw).</summary>
    public (GwTrackRead? read, List<int> failed) ReadOneTrack(GwFormat f, int cyl, int head, CancellationToken ct = default)
    {
        string dir = TempDir();
        try
        {
            var args = Common();
            args.AddRange(FormatArgs(f, dir));
            args.AddRange(new[] { "--tracks", $"c={cyl}:h={head}", "--retries", "0", Path.Combine(dir, "track" + f.ImageExt) });
            var output = Run("read", args, 1, null, "", ct);
            output.FinalReads().TryGetValue((cyl, head), out var read);
            return (read, MissingSectors(output, cyl, head));
        }
        finally { TryDelete(dir); }
    }

    /// <summary>Čísla sektorů označená v souhrnu gw jako chybějící ('X') pro danou stopu.</summary>
    public static List<int> MissingSectors(GwOutput o, int cyl, int head)
    {
        var list = new List<int>();
        int col = o.SummaryCyls.IndexOf(cyl);
        if (col < 0) return list;
        foreach (var kv in o.Summary.Where(k => k.Key.head == head).OrderBy(k => k.Key.sector))
            if (col < kv.Value.Length && kv.Value[col] == 'X') list.Add(kv.Key.sector);
        return list;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}
