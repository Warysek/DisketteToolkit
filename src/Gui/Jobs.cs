using System;
using System.IO;
using System.Linq;
using System.Threading;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui;

/// <summary>Co úloha potřebuje od okna: výpis, dotazy, průběh, zrušení.</summary>
internal interface IJobHost
{
    void Log(string text, LogKind kind = LogKind.Info);
    /// <summary>Dotaz Ano/Ne — volá se z vlákna úlohy, okno ho zobrazí ve svém vlákně.</summary>
    bool Confirm(string text, bool danger = false);
    /// <summary>Zeptá se, kam uložit soubor (null = uživatel zrušil).</summary>
    string? AskSavePath(string title, string suggestedPath);
    void Progress(int done, int total, string text);
    CancellationToken Token { get; }
    DeviceFactory Factory { get; }
    AppSettings Settings { get; }
}

internal enum LogKind { Info, Dim, Ok, Warn, Error, Title }

/// <summary>
/// Operace s disketou pro okno — stejná logika jako příkazová řádka (bezpečnostní kontroly, zálohy),
/// jen místo výpisu do konzole mluví přes <see cref="IJobHost"/>. Běží ve vlákně na pozadí.
/// </summary>
internal sealed class Jobs
{
    private readonly IJobHost _h;
    public Jobs(IJobHost host) => _h = host;

    private void Title(string t) => _h.Log(t, LogKind.Title);
    private void Info(string t) => _h.Log(t);
    private void Dim(string t) => _h.Log(t, LogKind.Dim);
    private void Ok(string t) => _h.Log(t, LogKind.Ok);
    private void Warn(string t) => _h.Log(t, LogKind.Warn);
    private void Err(string t) => _h.Log(t, LogKind.Error);
    private CancellationToken Ct => _h.Token;
    private ProgressHandler P => _h.Progress;

    // ================================================================ čtení

    public void ReadDisk(DetectedDrive d, string output, SideMode sides)
    {
        if (d.Kind == DriveKind.Greaseweazle) { GwRead(d, output); return; }
        Title(Tr.F("Záloha diskety z {0} do {1}", d.Id, Path.GetFileName(output)));
        using var dev = _h.Factory.OpenForRead(d, write: false);
        var result = new OperationResult();
        var img = DiskOperations.ReadDisk(dev, sides, result, P, Ct);
        ImageFile.Save(img, output);
        Report(result);
        Ok(Tr.F("Uloženo: {0} ({1})", output, img.Geometry));
    }

    private void GwRead(DetectedDrive d, string output)
    {
        var gw = _h.Factory.OpenGw(d, Ct);
        Title(Tr.F("Čtení diskety přes {0} do {1}", d.Name, Path.GetFileName(output)));
        if (DeviceFactory.IsFlux(output))
        {
            int tracks = _h.Settings.Drive.FluxTracks ?? 82;
            Info(Tr.F("Záloha jako magnetický tok, {0} stop, {1} otáčky na stopu — uchová i kopírovací ochrany.", tracks, _h.Settings.Drive.Revs));
            gw.ReadFlux(Path.GetFullPath(output), _h.Settings.Drive.Revs, tracks, P, Ct);
            Ok(Tr.F("Uloženo: {0} ({1} KB)", output, new FileInfo(output).Length / 1024));
            return;
        }
        var fmt = GwProbe(gw, null);
        var r = gw.Read(fmt, P, Ct);
        ShowSurface(r.Map, r.Warnings);
        ImageFile.Save(r.Image, output);
        Ok(Tr.F("Uloženo: {0} ({1})", output, fmt));
    }

    private GwFormat GwProbe(GwSession gw, FormatProfile? profile)
    {
        if (profile != null)
            return GwFormat.ForProfile(profile) ?? throw new OperationException(Tr.F("{0}: tento formát přes Greaseweazle zatím nepodporuji.", profile.Title));
        _h.Progress(0, 1, Tr.T("Rozpoznávám formát diskety…"));
        var probe = gw.Probe(P, Ct);
        _h.Log(Tr.F("Rozpoznaný formát: {0}", probe.Summary), probe.Format != null ? LogKind.Info : LogKind.Warn);
        return probe.Format ?? throw new OperationException(Tr.T("Formát diskety se nepodařilo rozpoznat. Zkus zálohu jako magnetický tok (.scp)."));
    }

    // ================================================================ zápis

    public void WriteImage(DetectedDrive d, string path, bool format, bool verify)
    {
        if (d.Kind == DriveKind.Greaseweazle) { GwWrite(d, path, verify); return; }
        var loaded = ImageFile.Load(path);
        var img = loaded.Image ?? throw new OperationException(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."));
        Title(Tr.F("Zápis {0} na {1}", Path.GetFileName(path), d.Id));
        WriteImageData(d, img, format, verify);
    }

    /// <summary>Zápis obrazu z paměti (hotový obraz nebo vytvořená spouštěcí disketa).</summary>
    private bool WriteImageData(DetectedDrive d, DiskImage img, bool format, bool verify)
    {
        Info(Tr.F("Obraz: {0}", img.Geometry));
        if (d.Kind == DriveKind.Greaseweazle)
        {
            var gw = _h.Factory.OpenGw(d, Ct);
            var fmt = GwFormat.ForImage(img);
            Info(Tr.F("Formát zápisu: {0}", fmt));
            var w = gw.Write(img, fmt, verify, P, Ct);
            Ok(Tr.F("Zapsáno stop: {0}", w.Written.Count) + (verify ? Tr.T(", ověřeno.") : "."));
            return true;
        }
        if (FileSystemDetector.Detect(img.Data).Kind == FsKind.Amiga)
            throw new OperationException(Tr.T("Amiga disketu umí zapsat jen Greaseweazle — PC ani USB mechanika neumí celostopý zápis Amigy. Ulož ji jako obraz .ADF (Gotek, emulátor)."));
        using var dev = _h.Factory.OpenForGeometry(d, img.Geometry);
        if (dev is FdrawDevice && !format && dev.MediaGeometry != img.Geometry)
        {
            Dim(Tr.T("Disketa má jiný formát než obraz — naformátuji ji na formát obrazu."));
            format = true;
        }
        var result = new OperationResult();
        DiskOperations.WriteImage(dev, img, format, verify, result, P, Ct);
        Report(result);
        if (result.Success) Ok(verify ? Tr.T("Zápis dokončen, kontrola v pořádku.") : Tr.T("Zápis dokončen."));
        else Err(Tr.T("Zápis dokončen s chybami — disketa nejspíš není v pořádku."));
        return result.Success;
    }

    private void GwWrite(DetectedDrive d, string path, bool verify)
    {
        var gw = _h.Factory.OpenGw(d, Ct);
        Title(Tr.F("Zápis {0} přes {1}", Path.GetFileName(path), d.Name));
        if (DeviceFactory.IsFlux(path))
        {
            var o = gw.WriteFile(path, null, verify, 168, P, Ct);
            Ok(Tr.F("Zapsáno stop: {0}", o.Written.Count));
            return;
        }
        var loaded = ImageFile.Load(path);
        var img = loaded.Image ?? throw new OperationException(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."));
        var fmt = GwFormat.ForImage(img);
        Info(Tr.F("Formát zápisu: {0}", fmt));
        var w = gw.Write(img, fmt, verify, P, Ct);
        Ok(Tr.F("Zapsáno stop: {0}", w.Written.Count) + (verify ? Tr.T(", ověřeno.") : "."));
        if (w.VerifyRetries.Count > 0) Warn(Tr.F("U {0} stop bylo potřeba zápis opakovat — disketa slábne.", w.VerifyRetries.Count));
    }

    // ================================================================ spouštěcí disketa

    /// <summary>Vytvoří spouštěcí disketu; uloží ji jako obraz, nebo rovnou zapíše na disketu.</summary>
    public void MakeBootDisk(AtariFloppy.Core.Boot.BootOptions o, DetectedDrive? d, string? savePath, bool verify)
    {
        Title(Tr.T("Spouštěcí disketa") + " — " + AtariFloppy.Core.Boot.BootDisks.Describe(o.System));
        if (o.System == AtariFloppy.Core.Boot.BootSystem.FreeDos && !AtariFloppy.Core.Boot.FreeDosFiles.Cached)
        {
            Info(Tr.T("Stahuji FreeDOS z oficiálního repozitáře (jen poprvé, pak se použije uložená kopie)…"));
            _h.Progress(0, 0, Tr.T("Stahuji FreeDOS…"));
        }
        var img = AtariFloppy.Core.Boot.BootDisks.Build(o, DownloadProgress, Ct);
        var fs = FileSystemDetector.Detect(img.Data);
        Info(fs.Name + (fs.Details != null ? " — " + fs.Details : ""));
        if (savePath != null)
        {
            ImageFile.Save(img, savePath);
            Ok(Tr.F("Uloženo: {0} ({1})", savePath, img.Geometry));
        }
        if (d != null)
        {
            if (WriteImageData(d, img, format: d.Kind != DriveKind.Greaseweazle, verify)) Ok(Tr.T("Spouštěcí disketa je hotová."));
            else Err(Tr.T("Disketa má vadná místa — spouštěcí disketu zapiš raději na jinou."));
        }
    }

    // ================================================================ formát

    public void Format(DetectedDrive d, FormatProfile p, bool quick, string? label, bool verify)
    {
        var verdict = FormatProfiles.Evaluate(p, d, d.Kind == DriveKind.Greaseweazle ? InsertedMedia.Unknown : d.Media);
        if (!verdict.IsReady) throw new OperationException($"{p.Title}: {verdict.Reason}");
        Title(Tr.F("Formátování {0} — {1}", d.Id, p.Title));

        if (d.Kind == DriveKind.Greaseweazle)
        {
            var gw = _h.Factory.OpenGw(d, Ct);
            var blankGw = p.Build!(p.SupportsLabel ? label : null);
            gw.Write(blankGw, GwFormat.ForProfile(p)!, verify, P, Ct);
            Ok(Tr.F("Hotovo — disketa je naformátovaná jako {0}.", p.Title));
            return;
        }

        bool fatDs = p.FatStyle != null && p.Geometry.Sides == 2;
        if (quick && !fatDs) throw new OperationException(Tr.T("Rychlý formát umím jen u oboustranných FAT disket (Atari, PC, MSX)."));
        using var dev = _h.Factory.OpenForGeometry(d, p.Geometry);
        var result = new OperationResult();
        if (fatDs)
            DiskOperations.Format(dev, p.Geometry, p.FatStyle!.Value, label, quick, verify, result, P, Ct);
        else
            DiskOperations.WriteImage(dev, p.Build!(p.SupportsLabel ? label : null), format: true, verify: verify, result, P, Ct);
        Report(result);
        if (result.Success) Ok(Tr.F("Hotovo — disketa je naformátovaná jako {0}.", p.Title));
        else Err(Tr.T("Formátování skončilo s chybami — disketa má vadná místa."));
    }

    public void Erase(DetectedDrive d)
    {
        var gw = _h.Factory.OpenGw(d, Ct);
        int tracks = _h.Settings.Drive.FluxTracks ?? 82;
        Title(Tr.F("Mazání diskety přes {0}", d.Name));
        gw.Erase(tracks, P, Ct);
        Ok(Tr.T("Disketa je smazaná (bez formátu — před použitím ji naformátuj)."));
    }

    // ================================================================ test a kontrola

    /// <summary>Záloha obsahu diskety — jednou, do zvoleného souboru (když ho uživatel nezadal předem, zeptá se).</summary>
    private sealed class BackupState
    {
        public string? Path;
        public bool Saved;
    }

    private string? EnsureBackup(DiskImage img, DetectedDrive d, BackupState b, string why)
    {
        if (b.Saved) return b.Path;
        b.Path ??= _h.AskSavePath(why, Backups.SuggestPath(img, Backups.DriveTag(d), _h.Settings.BackupFolder));
        if (b.Path == null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(b.Path))!);
        ImageFile.Save(img, b.Path);
        b.Saved = true;
        Ok(Tr.F("Záloha diskety uložena: {0}", b.Path));
        return b.Path;
    }

    /// <param name="backupPath">Kam uložit zálohu diskety (u zápisového testu povinné, u čtecího volitelné).</param>
    public void CheckDisk(DetectedDrive d, WriteTestMode mode, bool repair, string? backupPath)
    {
        if (d.Kind == DriveKind.Greaseweazle) { GwCheck(d, mode, repair, backupPath); return; }
        Title(Tr.F("Test povrchu a kontrola — {0}", d.Id));
        using var dev = _h.Factory.OpenForRead(d, write: mode != WriteTestMode.None || repair);
        var backup = new BackupState { Path = backupPath };
        var r = SurfaceTest.Run(dev, mode, P, Ct, beforeWrite: res =>
        {
            if (EnsureBackup(res.Contents, d, backup, Tr.T("Kam uložit zálohu diskety před zápisovým testem")) == null)
                throw new OperationCanceledException();
        });
        if (mode == WriteTestMode.None && backup.Path != null) EnsureBackup(r.Contents, d, backup, "");
        ShowSurface(r.Map, r.Warnings);
        if (r.RestoreFailures.Count > 0) Err(Tr.F("Na {0} sektorů se nepodařilo vrátit původní data — jsou v záloze {1}.", r.RestoreFailures.Count, backup.Path));

        if (mode == WriteTestMode.Destructive)
        {
            var (fresh, style, marked, sys) = Repair.RebuildAfterDestructive(r);
            var result = new OperationResult();
            DiskOperations.WriteSectorList(dev, fresh.Data, Enumerable.Range(0, sys).Select(i => (long)i).ToList(), result, P, Ct);
            Report(result);
            if (!result.Success) { Err(Tr.T("Nový souborový systém se nepodařilo zapsat — disketa není použitelná, naformátuj ji znovu.")); return; }
            Ok(Tr.T("Disketa je znovu naformátovaná.") + (marked > 0 ? " " + Tr.F("Vadných clusterů označeno ve FAT: {0}.", marked) : ""));
            return;
        }
        CheckFileSystem(r.Contents, r.Map, repair, plan =>
        {
            if (EnsureBackup(r.Contents, d, backup, Tr.T("Kam uložit zálohu diskety před opravou")) == null)
            {
                Warn(Tr.T("Bez zálohy opravu nezapíšu."));
                return;
            }
            if (!_h.Confirm(Tr.T("Zapsat opravu na disketu?") + "\n\n" + string.Join("\n", plan.Actions.Take(12).Select(a => "• " + a)))) return;
            var result = new OperationResult();
            DiskOperations.WriteSectorList(dev, plan.Repaired, plan.ChangedSectors, result, P, Ct);
            Report(result);
            if (!result.Success) { Err(Tr.F("Některé sektory se nepodařilo zapsat. Původní stav je v {0} — zapiš ho na jinou disketu.", backup.Path)); return; }
            var after = FatDoctor.Check(plan.Repaired, r.Map);
            if (!after.HasRepairable) Ok(Tr.T("Oprava zapsaná a ověřená. Souborový systém je v pořádku."));
            else Warn(Tr.T("Oprava zapsaná, ale kontrola pořád něco hlásí — spusť kontrolu znovu."));
        }, () => EnsureBackup(r.Contents, d, backup, Tr.T("Kam uložit obsah diskety (pro opravu obrazu)")));
    }

    private void GwCheck(DetectedDrive d, WriteTestMode mode, bool repair, string? backupPath)
    {
        var gw = _h.Factory.OpenGw(d, Ct);
        Title(Tr.F("Test povrchu a kontrola — {0}", d.Name));
        var fmt = GwProbe(gw, null);
        var r = gw.Read(fmt, P, Ct);
        var backup = new BackupState { Path = backupPath };
        if (backup.Path != null) EnsureBackup(r.Image, d, backup, "");
        ShowSurface(r.Map, r.Warnings);

        if (mode != WriteTestMode.None)
        {
            if (EnsureBackup(r.Image, d, backup, Tr.T("Kam uložit zálohu diskety před zápisovým testem")) == null) throw new OperationCanceledException();
            var content = mode == WriteTestMode.PreserveData ? r.Image : Repair.BlankLike(r.Image, fmt.Name.StartsWith("amiga"));
            try
            {
                var w = gw.Write(content, fmt, true, P, Ct);
                if (w.VerifyRetries.Count > 0) Warn(Tr.F("U {0} stop se zápis musel opakovat — slabá místa.", w.VerifyRetries.Count));
                else Ok(Tr.T("Všechny stopy zapsané a ověřené napoprvé."));
            }
            catch (OperationException e)
            {
                Err(e.Message);
                Err(Tr.F("Disketa má místa, kam nejde spolehlivě zapisovat. Původní obsah je v {0}.", backup.Path));
                return;
            }
            if (mode == WriteTestMode.Destructive) return;
        }

        CheckFileSystem(r.Image, r.Map, repair, plan =>
        {
            if (EnsureBackup(r.Image, d, backup, Tr.T("Kam uložit zálohu diskety před opravou")) == null)
            {
                Warn(Tr.T("Bez zálohy opravu nezapíšu."));
                return;
            }
            if (!_h.Confirm(Tr.T("Zapsat opravu na disketu? Greaseweazle přepíše celou disketu opraveným obsahem."))) return;
            gw.Write(new DiskImage(r.Image.Geometry, plan.Repaired), fmt, true, P, Ct);
            Ok(Tr.T("Oprava zapsaná a ověřená."));
        }, () => EnsureBackup(r.Image, d, backup, Tr.T("Kam uložit obsah diskety (pro opravu obrazu)")));
    }

    /// <summary>Rozpozná souborový systém, u FAT12 zkontroluje a případně nabídne bezpečnou opravu.</summary>
    private void CheckFileSystem(DiskImage contents, SurfaceMap map, bool repair, Action<DiskRepairPlan> writeRepair, Func<string?> saveForImageRepair)
    {
        var fs = FileSystemDetector.Detect(contents.Data);
        Title(Tr.T("Souborový systém"));
        Info(fs.Name + (fs.Details != null ? " — " + fs.Details : ""));
        Dim(fs.UsedFor);
        if (fs.Kind == FsKind.Amiga && AmigaDos.TryOpen(contents.Data) is { } vol)
            Info(Tr.F("Souborů: {0}, volno {1} KB", vol.ListAll().Count(e => !e.IsDirectory), vol.FreeBlocks / 2));
        if (!fs.CanCheck)
        {
            Dim(Tr.T("Kontrolu souborového systému umím zatím jen u FAT12 (Atari, PC, MSX). Test povrchu výše platí pro každou disketu."));
            return;
        }
        var report = FatDoctor.Check(contents.Data, map);
        ShowReport(report);
        if (!report.HasRepairable) return;
        if (!repair) { Dim(Tr.T("Opravit: spusť kontrolu znovu se zaškrtnutou opravou.")); return; }

        Title(Tr.T("Oprava"));
        var plan = Repair.PlanDisk(contents, map, report);
        if (plan.Refusal != null)
        {
            Err(plan.Refusal);
            string? saved = saveForImageRepair();
            if (saved != null) Warn(Tr.F("Obsah diskety je uložený v {0} — oprav ho přes Obrazy → Kontrola obrazu a zapiš na jinou disketu.", saved));
            else Warn(Tr.T("Ulož si zálohu diskety (Záloha do obrazu), oprav ji přes Obrazy → Kontrola obrazu a zapiš na jinou disketu."));
            return;
        }
        if (plan.ChangedSectors.Count == 0) { Ok(Tr.T("Není co zapsat.")); return; }
        foreach (var a in plan.Actions) Info("• " + a);
        Dim(Tr.F("Změní se {0} sektorů (FAT a adresáře), data souborů zůstanou.", plan.ChangedSectors.Count));
        writeRepair(plan);
    }

    // ================================================================ obrazy

    public void CheckImage(string path)
    {
        var loaded = ImageFile.Load(path);
        Title(Tr.F("Kontrola obrazu: {0}", Path.GetFileName(path)));
        var img = loaded.Image ?? throw new OperationException(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."));
        Info(Tr.F("Geometrie: {0}", img.Geometry));
        var fs = FileSystemDetector.Detect(img.Data);
        Info(fs.Name + (fs.Details != null ? " — " + fs.Details : ""));
        if (!fs.CanCheck) { Dim(Tr.T("Kontrolu souborového systému umím zatím jen u FAT12 (Atari, PC, MSX).")); return; }
        var report = FatDoctor.Check(img.Data);
        ShowReport(report);
        if (!report.HasRepairable) return;
        if (!_h.Confirm(Tr.T("Obraz má opravitelné chyby. Uložit opravenou kopii? Původní soubor zůstane beze změny."))) return;

        var repaired = (byte[])img.Data.Clone();
        var done = FatDoctor.Repair(repaired);
        Title(Tr.T("Oprava"));
        foreach (var a in done.Actions) Info("• " + a);
        string output = Repair.RepairedImagePath(path, loaded.Kind);
        if (File.Exists(output) && !_h.Confirm(Tr.F("Soubor {0} už existuje. Přepsat?", output))) return;
        ImageFile.Save(new DiskImage(img.Geometry, repaired), output);
        var after = FatDoctor.Check(repaired);
        Ok(Tr.F("Opravený obraz: {0}", output) + (after.HasRepairable ? Tr.T(" (kontrola pořád něco hlásí)") : Tr.T(" — souborový systém je v pořádku.")));
        Dim(Tr.T("Původní soubor zůstal beze změny."));
    }

    // ================================================================ doplňky (gw.exe, fdrawcmd.sys, firmware)

    private void DownloadProgress(long done, long total) =>
        _h.Progress((int)(done / 1024), (int)(total / 1024), Tr.F("Stahuji… {0:N0} KB", done / 1024));

    /// <summary>Stáhne nejnovější Greaseweazle Host Tools z GitHubu (nebo oznámí, že už jsou aktuální).</summary>
    public void InstallGw()
    {
        Title(Tr.T("Greaseweazle Host Tools (gw.exe)"));
        _h.Progress(0, 0, Tr.T("Zjišťuji nejnovější verzi na GitHubu…"));
        var latest = GreaseweazleSetup.GetLatest(Ct);
        Info(Tr.F("Nejnovější verze: {0} ({1}, {2:N0} KB)", latest.Version, latest.AssetName, latest.Size / 1024));
        var current = GwTool.Find(_h.Settings.Drive.GwPath);
        string? curVer = current != null ? GreaseweazleSetup.VersionOf(current.Path) : null;
        if (current != null)
            Info(Tr.F("Teď používám: {0} (verze {1})", current.Path, curVer ?? Tr.T("neznámá")));
        if (curVer != null && GreaseweazleSetup.CompareVersions(curVer, latest.Version) >= 0 &&
            !_h.Confirm(Tr.F("Verze {0} je nejnovější. Přesto stáhnout znovu?", curVer)))
        {
            Ok(Tr.T("Greaseweazle Host Tools jsou aktuální."));
            return;
        }
        string gw = GreaseweazleSetup.Install(latest, DownloadProgress, Ct);
        if (_h.Settings.Drive.GwPath != null && !string.Equals(_h.Settings.Drive.GwPath, gw, StringComparison.OrdinalIgnoreCase))
        {
            Dim(Tr.F("Ruční cestu ke gw.exe v nastavení ({0}) jsem zrušil — používám staženou verzi.", _h.Settings.Drive.GwPath));
            _h.Settings.Drive.GwPath = null;
            _h.Settings.Save();
        }
        Ok(Tr.F("Nainstalováno: {0} (verze {1}). Obsahuje i CAPSImg.dll pro zápis .IPF.", gw, latest.Version));
        Dim(Tr.T("Když je Greaseweazle připojený, doporučuji ještě aktualizovat jeho firmware (stejné tlačítko → Aktualizovat firmware)."));
    }

    /// <summary>Aktualizace firmwaru v Greaseweazle (gw update si firmware stáhne sám).</summary>
    public void UpdateGwFirmware(DetectedDrive d)
    {
        var gw = _h.Factory.OpenGw(d, Ct);
        Title(Tr.F("Firmware Greaseweazle — {0}", d.Name));
        _h.Progress(0, 0, Tr.T("Zjišťuji verzi firmwaru…"));
        string? fw = gw.FirmwareVersion(Ct);
        if (fw == null) throw new OperationException(Tr.T("Greaseweazle neodpovídá — je připojený k USB?"));
        Info(Tr.F("Firmware v zařízení: {0}", fw));
        if (!_h.Confirm(Tr.T("Aktualizovat firmware Greaseweazle na nejnovější verzi? Během aktualizace Greaseweazle neodpojuj.")))
            return;
        _h.Progress(0, 0, Tr.T("Aktualizuji firmware… (neodpojuj Greaseweazle)"));
        bool failed = false;
        int code = gw.UpdateFirmware(line =>
        {
            if (line.Trim().Length == 0) return;
            bool err = line.Contains("ERROR") || line.Contains("FATAL") || line.StartsWith("Command Failed");
            failed |= err;
            _h.Log(line.Trim(), err ? LogKind.Error : LogKind.Dim);
        }, Ct);
        if (code != 0 || failed)
        {
            Err(Tr.T("Aktualizace firmwaru se nepovedla. Starší Greaseweazle F1 potřebuje při aktualizaci propojku (jumper) na pinech „DFU“ — viz návod na github.com/keirf/greaseweazle/wiki."));
            return;
        }
        string? after = gw.FirmwareVersion(Ct);
        Ok(Tr.F("Firmware Greaseweazle: {0}", after ?? "?"));
    }

    /// <summary>Stáhne oficiální instalátor fdrawcmd.sys ze simonowen.com, ověří podpis a spustí ho.</summary>
    public void InstallFdrawcmd()
    {
        Title(Tr.T("Ovladač fdrawcmd.sys"));
        string? ver = null;
        bool installed = false;
        try { installed = FdrawCmd.IsInstalled(out ver); } catch { }
        if (installed && !_h.Confirm(Tr.F("fdrawcmd.sys je už nainstalovaný (verze {0}). Přesto instalovat znovu?", ver)))
            return;
        _h.Progress(0, 0, Tr.T("Stahuji FdInstall.exe ze simonowen.com…"));
        string exe = FdrawcmdSetup.Download(DownloadProgress, Ct);
        string? signer = FdrawcmdSetup.Signer(exe);
        if (signer == null)
        {
            Warn(Tr.T("Instalátor není digitálně podepsaný (nebo podpis nejde ověřit)."));
            if (!_h.Confirm(Tr.T("Instalátor stažený ze simonowen.com není digitálně podepsaný. Přesto ho spustit?"), danger: true)) return;
        }
        else if (signer.IndexOf("Simon Owen", StringComparison.OrdinalIgnoreCase) < 0)
            throw new OperationException(Tr.F("Instalátor je podepsaný někým jiným než autorem ({0}) — z bezpečnostních důvodů ho nespustím.", signer));
        else Dim(Tr.F("Podpis instalátoru: {0}", signer));

        if (!_h.Confirm(Tr.T("Spustím instalátor fdrawcmd.sys. Otevře se jeho okno — potvrď instalaci (Install) a pak ho zavři. Pokračovat?")))
            return;
        _h.Progress(0, 0, Tr.T("Čekám na dokončení instalátoru…"));
        FdrawcmdSetup.RunInstaller(exe, Ct);

        installed = false;
        try { installed = FdrawCmd.IsInstalled(out ver); } catch { }
        if (installed) Ok(Tr.F("fdrawcmd.sys je nainstalovaný a běží (verze {0}).", ver));
        else
        {
            Warn(Tr.T("Ovladač zatím neodpovídá. Zkus restartovat počítač."));
            Warn(Tr.T("Když to nepomůže: ovladač je z roku 2010 a Windows 11 ho může blokovat funkcí Integrita paměti. Start → Zabezpečení Windows → Zabezpečení zařízení → Podrobnosti izolace jádra → Integrita paměti: Vypnuto, pak restart."));
        }
    }

    // ================================================================ výpisy

    private void Report(OperationResult r)
    {
        foreach (var n in r.Notes) Dim("• " + n);
        foreach (var w in r.Warnings) Warn(w);
        if (r.BadSectors.Count > 0)
        {
            Err(Tr.F("Vadné sektory ({0}):", r.BadSectors.Count));
            foreach (var b in r.BadSectors.Take(30)) Err("   " + b);
            if (r.BadSectors.Count > 30) Err(Tr.F("   … a dalších {0}", r.BadSectors.Count - 30));
        }
        if (r.VerifyErrors.Count > 0)
        {
            Err(Tr.F("Kontrola po zápisu našla chyby ({0}):", r.VerifyErrors.Count));
            foreach (var b in r.VerifyErrors.Take(30)) Err("   " + b);
        }
    }

    private void ShowSurface(SurfaceMap m, System.Collections.Generic.IEnumerable<string> warnings)
    {
        int ok = m.Count(SectorStatus.Ok), weak = m.Count(SectorStatus.Weak), bad = m.Count(SectorStatus.Bad), wf = m.Count(SectorStatus.WriteFailed);
        _h.Log(Tr.F("Sektorů {0}: v pořádku {1}, slabých {2}, nečitelných {3}", m.Geometry.TotalSectors, ok, weak, bad) + (wf > 0 ? Tr.F(", selhal zápis {0}", wf) : ""),
               bad + wf > 0 ? LogKind.Error : weak > 0 ? LogKind.Warn : LogKind.Ok);
        foreach (long l in m.Problems.Take(20))
        {
            var (t, s, n) = m.Chs(l);
            Dim("   " + Tr.F("Stopa {0}, strana {1}, sektor {2}: {3}", t, s, n, Theme.CellName(TrackActivity.FromSurface(m[l]))));
        }
        foreach (var w in warnings) Warn(w);
        if (bad + wf > 0) Err(Tr.T("Disketa má vadná místa — na důležitá data ji už nepoužívej."));
        else if (weak > 0) Warn(Tr.T("Disketa slábne — data z ní zkopíruj, na nová data ji raději nepoužívej."));
        else Ok(Tr.T("Povrch je v pořádku."));
    }

    private void ShowReport(FatCheckReport rep)
    {
        if (!rep.IsFat) { foreach (var i in rep.Issues) Err(i.Message); return; }
        Info(Tr.F("Souborů {0}, adresářů {1}, obsazeno {2} KB, volno {3} KB", rep.Files, rep.Directories, rep.UsedBytes / 1024, rep.FreeBytes / 1024));
        if (rep.Issues.Count == 0) { Ok(Tr.T("Souborový systém je v pořádku.")); return; }
        foreach (var i in rep.Issues.OrderByDescending(i => i.Severity))
            _h.Log(i.Message + (i.Repairable ? "  [" + Tr.T("lze opravit") + "]" : ""),
                   i.Severity == IssueSeverity.Error ? LogKind.Error : i.Severity == IssueSeverity.Warning ? LogKind.Warn : LogKind.Dim);
    }
}
