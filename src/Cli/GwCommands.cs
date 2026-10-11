using System;
using System.IO;
using System.Linq;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;

namespace AtariFloppy.Cli;

/// <summary>Příkazy pro Greaseweazle (přes gw.exe): stejné jako u USB mechaniky, jen s rozpoznáním formátu a toky.</summary>
internal static class GwCommands
{
    private static T Busy<T>(Func<T> f)
    {
        Program.Busy = true;
        try { return f(); }
        finally { Program.Busy = false; Ui.EndProgress(); }
    }

    private static GwFormat ChooseFormat(GwSession gw, string? profileId)
    {
        if (profileId != null)
        {
            var p = FormatProfiles.Find(profileId) ?? throw new ArgumentException($"Neznámý formát „{profileId}“. Seznam: dtk profiles");
            return GwFormat.ForProfile(p) ?? throw new OperationException($"{p.Title}: tento formát přes Greaseweazle zatím nepodporuji.");
        }
        var probe = Busy(() => gw.Probe(Ui.Progress, Program.Cancel.Token));
        Ui.Field("Rozpoznaný formát", probe.Summary, probe.Format != null ? ConsoleColor.White : ConsoleColor.Yellow);
        return probe.Format ?? throw new OperationException("Formát diskety se nepodařilo rozpoznat. Zkus zálohu jako magnetický tok: dtk read GW zaloha.scp, nebo zadej --profile.");
    }

    private static void ShowRead(GwSession.ReadResult r)
    {
        CheckCommand.PrintMap(r.Map);
        var st = new SurfaceTestResult { Map = r.Map, Contents = r.Image };
        st.Warnings.AddRange(r.Warnings);
        CheckCommand.PrintSurfaceSummary(st);
    }

    // ------------------------------------------------------------------ čtení

    public static int Read(string arg, string output, string? profileId)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        if (File.Exists(output) && !Ui.Confirm($"Soubor {output} už existuje. Přepsat?")) return 1;
        Ui.Title($"Čtení diskety přes {drive.Name} do {Path.GetFileName(output)}");

        if (Backends.IsFlux(output))
        {
            int tracks = Backends.FluxTracks ?? 82;
            Ui.Field("Záloha", $"magnetický tok, {tracks} stop, {Backends.Revs} otáčky na stopu — uchová i kopírovací ochrany");
            Busy(() => gw.ReadFlux(Path.GetFullPath(output), Backends.Revs, tracks, Ui.Progress, Program.Cancel.Token));
            Ui.Ok($"Uloženo: {output} ({new FileInfo(output).Length / 1024} KB)");
            Ui.Dim("  Tok (.scp/.hfe) jde zapsat zpět přes Greaseweazle, použít v Goteku (HFE) nebo převést na obraz.");
            return 0;
        }

        var fmt = ChooseFormat(gw, profileId);
        var r = Busy(() => gw.Read(fmt, Ui.Progress, Program.Cancel.Token));
        ShowRead(r);
        ImageFile.Save(r.Image, output);
        Ui.Ok($"Uloženo: {output} ({fmt})");
        return r.Map.Problems.Any() ? 4 : 0;
    }

    // ------------------------------------------------------------------ zápis

    public static int Write(string path, string arg, string? profileId, bool verify)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".stx")
            throw new OperationException("Formát STX (Pasti) program gw zapsat neumí. Hledej stejnou hru jako .IPF nebo .SCP, případně .ST/.MSA (bez ochrany).");
        if (!File.Exists(path)) throw new FileNotFoundException($"Soubor neexistuje: {path}");

        Ui.Title($"Zápis {Path.GetFileName(path)} přes {drive.Name}");
        if (Backends.IsFlux(path))
        {
            GwFormat? f = profileId != null ? GwFormat.ForProfile(FormatProfiles.Find(profileId) ?? throw new ArgumentException($"Neznámý formát „{profileId}“.")) : null;
            Ui.Field("Obraz", ext == ".ipf" ? "IPF (archivní, s ochranami) — gw potřebuje knihovnu capsimg.dll" : "magnetický tok — zapíše se včetně ochran");
            if (!Ui.Confirm("Všechna data na disketě budou přepsána. Pokračovat?")) return 1;
            var o = Busy(() => gw.WriteFile(path, f, verify, 168, Ui.Progress, Program.Cancel.Token));
            Ui.Ok($"Zapsáno stop: {o.Written.Count}" + (o.VerifyRetries.Count > 0 ? $" (opakováno kvůli ověření: {o.VerifyRetries.Count})" : ""));
            return 0;
        }

        var loaded = ImageFile.Load(path);
        if (loaded.Image == null) throw new OperationException(loaded.NotLoadedReason ?? "Obraz nejde načíst.");
        var img = loaded.Image;
        var fmt = profileId != null
            ? GwFormat.ForProfile(FormatProfiles.Find(profileId) ?? throw new ArgumentException($"Neznámý formát „{profileId}“.")) ?? throw new OperationException("Tento formát přes Greaseweazle nepodporuji.")
            : GwFormat.ForImage(img);
        Ui.Field("Obraz", img.Geometry.ToString());
        Ui.Field("Formát zápisu", fmt.ToString());
        Ui.Field("Kontrola po zápisu", verify ? "ano (Greaseweazle každou stopu přečte zpět)" : "ne");
        Ui.Dim(img.Geometry.IsHighDensity ? "  Vlož HD disketu (bez pásky)." : "  Vlož DD disketu (nebo HD s přelepeným otvorem).");
        if (!Ui.Confirm("Všechna data na disketě budou přepsána. Pokračovat?")) return 1;
        var w = Busy(() => gw.Write(img, fmt, verify, Ui.Progress, Program.Cancel.Token));
        Ui.Ok($"Zapsáno stop: {w.Written.Count}" + (verify ? ", ověřeno." : ".") +
              (w.VerifyRetries.Count > 0 ? $" U {w.VerifyRetries.Count} stop bylo potřeba zápis opakovat — disketa slábne." : ""));
        return 0;
    }

    public static int Format(string arg, FormatProfile p, string? label, bool verify)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        var verdict = FormatProfiles.Evaluate(p, drive, InsertedMedia.Unknown);
        if (!verdict.IsReady) throw new OperationException($"{p.Title}: {verdict.Reason}");
        var fmt = GwFormat.ForProfile(p)!;
        Ui.Title($"Formátování přes {drive.Name}");
        Ui.Field("Formát", p.Title);
        Ui.Field("K čemu", p.UsedFor);
        Ui.Field("Geometrie", p.Geometry.ToString());
        Ui.Dim("  " + verdict.Reason);
        if (!Ui.Confirm("Všechna data na disketě budou smazána. Pokračovat?")) return 1;
        var blank = p.Build!(p.SupportsLabel ? label : null);
        Busy(() => gw.Write(blank, fmt, verify, Ui.Progress, Program.Cancel.Token));
        Ui.Ok($"Hotovo — disketa je naformátovaná jako {p.Title}.");
        return 0;
    }

    public static int Erase(string arg)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        int tracks = Backends.FluxTracks ?? 82;
        if (!Ui.Confirm($"Magneticky smazat celou disketu ({tracks} stop) přes {drive.Name}?")) return 1;
        Busy(() => gw.Erase(tracks, Ui.Progress, Program.Cancel.Token));
        Ui.Ok("Disketa je smazaná (bez formátu — před použitím ji naformátuj).");
        return 0;
    }

    // ------------------------------------------------------------------ test a kontrola

    public static int Check(string arg, WriteTestMode mode, bool repair, string? saveImage, string? profileId)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        Ui.Title($"Test povrchu a kontrola — {drive.Name}");
        if (mode == WriteTestMode.Destructive && !Ui.Confirm("Destruktivní test SMAŽE všechna data na disketě. Pokračovat?")) return 1;
        if (mode == WriteTestMode.PreserveData && !Ui.Confirm("Test s obnovou přepíše celou disketu stejným obsahem. Před zápisem uložím zálohu diskety. Pokračovat?")) return 1;

        var fmt = ChooseFormat(gw, profileId);
        var r = Busy(() => gw.Read(fmt, Ui.Progress, Program.Cancel.Token));
        if (saveImage != null) { ImageFile.Save(r.Image, saveImage); Ui.Dim($"  Obsah uložen do {saveImage}"); }
        ShowRead(r);

        if (mode != WriteTestMode.None)
        {
            string backup = CheckCommand.SaveBackup(r.Image, drive);
            Ui.Dim($"  Záloha před zápisovým testem: {backup}");
            var content = mode == WriteTestMode.PreserveData ? r.Image : AtariFloppy.Core.App.Repair.BlankLike(r.Image, fmt.Name.StartsWith("amiga"));
            Ui.Title(mode == WriteTestMode.PreserveData ? "Zápisový test (zapisuji zpět stejný obsah s ověřením)" : "Zápisový test (nový prázdný formát s ověřením)");
            try
            {
                var w = Busy(() => gw.Write(content, fmt, true, Ui.Progress, Program.Cancel.Token));
                if (w.VerifyRetries.Count > 0) Ui.Warn($"  U {w.VerifyRetries.Count} stop se zápis musel opakovat — slabá místa: " +
                                                      string.Join(", ", w.VerifyRetries.Take(12).Select(t => $"{t.cyl}.{t.head}")));
                else Ui.Ok("  Všechny stopy zapsané a ověřené napoprvé.");
            }
            catch (OperationException e)
            {
                Ui.Error("  " + e.Message);
                Ui.Error($"  Disketa má místa, kam nejde spolehlivě zapisovat. Původní obsah je v {backup}.");
                return 4;
            }
            if (mode == WriteTestMode.Destructive) return r.Map.Problems.Any() ? 4 : 0;
        }

        var fs = FileSystemDetector.Detect(r.Image.Data);
        CheckCommand.PrintFileSystem(fs);
        if (fs.Kind == FsKind.Amiga)
        {
            var vol = AmigaDos.TryOpen(r.Image.Data);
            if (vol != null) Ui.Field("Obsah", $"souborů {vol.ListAll().Count(e => !e.IsDirectory)}, volno {vol.FreeBlocks / 2} KB");
        }
        if (!fs.CanCheck) return r.Map.Problems.Any() ? 4 : 0;

        var report = FatDoctor.Check(r.Image.Data, r.Map);
        CheckCommand.PrintReport(report);
        if (!report.HasRepairable) return report.HasProblems || r.Map.Problems.Any() ? 4 : 0;
        if (!repair) { Ui.Dim($"\n  Opravit: dtk check {arg} --repair"); return 4; }

        Ui.Title("Oprava");
        if (report.SystemAreaDamaged)
        {
            string img = CheckCommand.SaveBackup(r.Image, drive);
            Ui.Error("  Poškozená systémová oblast — opravu na disketu nezapíšu. Oprav obraz a zapiš ho na jinou disketu:");
            Ui.Warn($"    dtk check \"{img}\" --repair");
            return 4;
        }
        var repaired = (byte[])r.Image.Data.Clone();
        var done = FatDoctor.Repair(repaired, r.Map);
        foreach (var a in done.Actions) Ui.Write("   • " + a, ConsoleColor.Cyan, true);
        string bak = CheckCommand.SaveBackup(r.Image, drive);
        Ui.Dim($"  Záloha diskety před opravou: {bak}");
        if (!Ui.Confirm("Zapsat opravu na disketu? (Greaseweazle přepíše celou disketu opraveným obsahem)")) return 1;
        Busy(() => gw.Write(new DiskImage(r.Image.Geometry, repaired), fmt, true, Ui.Progress, Program.Cancel.Token));
        Ui.Ok("  Oprava zapsaná a ověřená.");
        return 0;
    }

    public static void PrintInfo(DetectedDrive d)
    {
        if (d.GwToolPath == null) { Ui.Warn("  Chybí gw.exe. " + GwTool.DownloadHint); return; }
        var tool = GwTool.Find(d.GwToolPath);
        if (tool == null) return;
        try
        {
            var lines = new GwSession(tool, d.Port).Info(Program.Cancel.Token)
                .Where(l => !l.StartsWith("***") && l.Trim().Length > 0).Take(12).ToList();
            foreach (var l in lines) Ui.Dim("    " + l);
        }
        catch (Exception e) { Ui.Warn("  gw info selhalo: " + e.Message); }
    }
}
