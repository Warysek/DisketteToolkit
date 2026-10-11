using System;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Nastavení mechanik — společné pro příkazovou řádku i okno.</summary>
public sealed class DriveSettings
{
    /// <summary>Vždy použít fdrawcmd.sys (když je nainstalovaný).</summary>
    public bool ForceFdraw { get; set; }
    /// <summary>Rychlost krokování v ms (null = výchozí). Jen fdrawcmd.sys a Greaseweazle.</summary>
    public int? StepRateMs { get; set; }
    /// <summary>Mechanika na Greaseweazle: A, B, 0, 1, 2.</summary>
    public string GwDrive { get; set; } = "A";
    /// <summary>Ruční cesta ke gw.exe (null = hledat automaticky).</summary>
    public string? GwPath { get; set; }
    /// <summary>Počet otáček na stopu při záloze toku.</summary>
    public int Revs { get; set; } = 3;
    /// <summary>Počet stop při záloze toku / mazání (null = 82).</summary>
    public int? FluxTracks { get; set; }
}

/// <summary>
/// Výběr „motoru“ pro danou mechaniku: Windows (USB / interní), fdrawcmd.sys (interní, nestandardní formáty)
/// nebo Greaseweazle (gw.exe). Program volí sám podle diskety a formátu; nastavením jde výběr vynutit.
/// </summary>
public sealed class DeviceFactory
{
    public static readonly string[] FluxExtensions = { ".scp", ".hfe", ".a2r", ".ctr" };

    public DriveSettings Settings { get; }
    /// <summary>Kam posílat vysvětlující poznámky („používám fdrawcmd.sys…“).</summary>
    public Action<string>? Log { get; set; }
    /// <summary>Kam posílat varování.</summary>
    public Action<string>? Warn { get; set; }
    /// <summary>Simulace (ukázkový režim, testy): když je nastavená, otevírá místo skutečné mechaniky tohle.</summary>
    public Func<DetectedDrive, bool, DiskGeometry?, ISectorDevice>? Simulator { get; set; }

    public DeviceFactory(DriveSettings settings) => Settings = settings;

    public static bool IsGwArg(string arg)
    {
        string a = arg.Trim().TrimEnd(':').ToUpperInvariant();
        return a is "GW" or "GREASEWEAZLE" || a.StartsWith("COM");
    }

    public static bool IsFlux(string path) => FluxExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ||
                                              Path.GetExtension(path).Equals(".ipf", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Greaseweazle

    public GwTool? FindGwTool(DetectedDrive? drive) =>
        GwTool.Find(Settings.GwPath) ?? (drive?.GwToolPath != null ? GwTool.Find(drive.GwToolPath) : null);

    /// <summary>Otevře Greaseweazle z nalezené mechaniky (Port může být null — gw si zařízení najde sám).</summary>
    public GwSession OpenGw(DetectedDrive drive, System.Threading.CancellationToken ct = default)
    {
        var tool = FindGwTool(drive) ?? throw new OperationException(Tr.T("Chybí program gw.exe. ") + GwTool.DownloadHint);
        drive.GwToolPath ??= tool.Path;
        var session = new GwSession(tool, drive.Port, Settings.GwDrive);
        if (Settings.StepRateMs != null)
        {
            session.SetStepDelay(Settings.StepRateMs.Value * 1000, ct);
            Log?.Invoke(Tr.F("Rychlost krokování nastavena na {0} ms.", Settings.StepRateMs));
        }
        return session;
    }

    /// <summary>Najde Greaseweazle podle zadání („GW“, „COM5“) a otevře ho.</summary>
    public (DetectedDrive drive, GwSession session) ResolveGw(string arg, System.Threading.CancellationToken ct = default)
    {
        string a = arg.Trim().TrimEnd(':').ToUpperInvariant();
        var drives = DriveDetector.DetectAll(includeGreaseweazle: true).Where(d => d.Kind == DriveKind.Greaseweazle).ToList();
        var drive = a.StartsWith("COM") ? drives.FirstOrDefault(d => d.Port == a) : drives.FirstOrDefault();
        if (drive == null)
        {
            var tool = FindGwTool(null) ?? throw new OperationException(Tr.T("Chybí program gw.exe. ") + GwTool.DownloadHint);
            // Detekce přes Windows nic nenašla (jiný ovladač, jiné USB ID) — necháme gw, ať zařízení najde sám.
            Log?.Invoke(Tr.T("Greaseweazle jsem v systému nenašel, zkusím ho nechat najít programem gw."));
            drive = new DetectedDrive { Kind = DriveKind.Greaseweazle, Port = a.StartsWith("COM") ? a : null, Name = "Greaseweazle", GwToolPath = tool.Path };
            drive.ComputeFeatures();
        }
        return (drive, OpenGw(drive, ct));
    }

    // ------------------------------------------------------------------ USB / interní

    public FdrawDevice OpenFdraw(DetectedDrive d)
    {
        int index = char.ToUpperInvariant(d.Letter![0]) - 'A';
        var dev = new FdrawDevice(new WinFdc(index), Tr.F("{0} (fdrawcmd)", d.Id), Settings.StepRateMs ?? 6);
        Log?.Invoke(Tr.F("Používám fdrawcmd.sys — {0}; disketa: {1}", dev.Controller, dev.MediaStateMessage));
        return dev;
    }

    /// <summary>Pro čtení/test: Windows, a když disketa vypadá nestandardně a je k dispozici fdrawcmd.sys, přepne se.</summary>
    public ISectorDevice OpenForRead(DetectedDrive d, bool write)
    {
        if (Simulator != null) return Simulator(d, write, null);
        if (d.FdrawcmdInstalled && Settings.ForceFdraw) return OpenFdraw(d);
        if (!d.FdrawcmdInstalled && Settings.ForceFdraw) throw new OperationException(Tr.F("{0}: fdrawcmd.sys pro tuto mechaniku není k dispozici.", d.Id));
        // rychlost krokování se u mechaniky bez fdrawcmd.sys tiše nepoužije (USB disketovka si ji řídí sama)

        var win = new WinFloppyDevice(d.Letter!, write);
        if (!d.FdrawcmdInstalled) return win;

        bool nonStandard = win.MediaState == MediaState.Unrecognized;
        if (!nonStandard && win.MediaState == MediaState.Ready && win.MediaGeometry != null)
        {
            try
            {
                var bs = new BootSector(win.ReadSectors(0, 1));
                nonStandard = bs.BpbLooksValid && (bs.SectorsPerTrack != win.MediaGeometry.SectorsPerTrack || bs.TotalSectors > win.MediaGeometry.TotalSectors);
            }
            catch (SectorIOException) { }
        }
        if (!nonStandard && Settings.StepRateMs == null) return win;
        win.Dispose();
        return OpenFdraw(d);
    }

    /// <summary>Pro zápis/formát dané geometrie: Windows, když to jde, jinak fdrawcmd.sys.</summary>
    public ISectorDevice OpenForGeometry(DetectedDrive d, DiskGeometry g)
    {
        if (Simulator != null) return Simulator(d, true, g);
        if (!Settings.ForceFdraw && Settings.StepRateMs == null && StandardGeometry.Supports(g)) return new WinFloppyDevice(d.Letter!, write: true);
        if (d.FdrawcmdInstalled) return OpenFdraw(d);
        if (StandardGeometry.Supports(g)) return new WinFloppyDevice(d.Letter!, write: true);
        throw new OperationException(Tr.F("Formát {0} tahle mechanika nezvládne. {1} ", g, StandardGeometry.Limits) +
                                     Tr.T("Nestandardní formáty zapíše interní mechanika s ovladačem fdrawcmd.sys nebo Greaseweazle."));
    }
}
