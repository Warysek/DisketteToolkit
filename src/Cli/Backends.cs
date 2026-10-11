using AtariFloppy.Core;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.Drives.Gw;

namespace AtariFloppy.Cli;

/// <summary>Příkazová řádka: výběr mechaniky přes společný <see cref="DeviceFactory"/> z jádra.</summary>
internal static class Backends
{
    public static readonly DriveSettings Settings = new();
    public static readonly DeviceFactory Factory = new(Settings)
    {
        Log = m => Ui.Dim("  " + m),
        Warn = m => Ui.Warn("  " + m),
    };

    public static bool ForceFdraw { get => Settings.ForceFdraw; set => Settings.ForceFdraw = value; }
    public static int? StepRateMs { get => Settings.StepRateMs; set => Settings.StepRateMs = value; }
    public static string GwDrive { get => Settings.GwDrive; set => Settings.GwDrive = value; }
    public static string? GwPath { get => Settings.GwPath; set => Settings.GwPath = value; }
    public static int Revs { get => Settings.Revs; set => Settings.Revs = value; }
    public static int? FluxTracks { get => Settings.FluxTracks; set => Settings.FluxTracks = value; }

    public static bool IsGwArg(string arg) => DeviceFactory.IsGwArg(arg);
    public static bool IsFlux(string path) => DeviceFactory.IsFlux(path);

    public static (DetectedDrive drive, GwSession session) ResolveGw(string arg) => Factory.ResolveGw(arg, Program.Cancel.Token);
    public static FdrawDevice OpenFdraw(DetectedDrive d) => Factory.OpenFdraw(d);
    public static ISectorDevice OpenForRead(DetectedDrive d, bool write) => Factory.OpenForRead(d, write);
    public static ISectorDevice OpenForGeometry(DetectedDrive d, DiskGeometry g) => Factory.OpenForGeometry(d, g);
}
