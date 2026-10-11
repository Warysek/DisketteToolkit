using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Fs;

namespace AtariFloppy.Cli;

/// <summary>Formáty všech systémů: co jde s touto mechanikou a disketou vytvořit, a proč ne.</summary>
internal static class ProfilesCommand
{
    public static void PrintOperations(DetectedDrive d)
    {
        Console.WriteLine();
        Ui.Write("  Co jde s touto disketou dělat:", ConsoleColor.Gray, true);
        foreach (var v in OperationAdvisor.Evaluate(d))
        {
            var (mark, col) = Mark(v.Status);
            Ui.Write($"  {mark} ", col);
            Ui.Write(v.Operation.PadRight(28), v.IsReady ? ConsoleColor.White : ConsoleColor.DarkGray);
            Ui.Write(v.Reason, v.IsReady ? ConsoleColor.Gray : col, true);
        }
    }

    public static (string, ConsoleColor) Mark(ProfileStatus s) => s switch
    {
        ProfileStatus.Ready => ("✓", ConsoleColor.Green),
        ProfileStatus.NoDisk or ProfileStatus.WrongMedia => ("!", ConsoleColor.Yellow),
        ProfileStatus.NeedsDriver => ("!", ConsoleColor.Yellow),
        ProfileStatus.Planned => ("…", ConsoleColor.Cyan),
        _ => ("✗", ConsoleColor.DarkGray),
    };

    /// <summary>Přehled všech formátů; s mechanikou vyhodnotí, které jdou teď vytvořit. Vrací připravené profily v pořadí čísel.</summary>
    public static List<FormatProfile> PrintProfiles(DetectedDrive? drive, bool numbered)
    {
        var ready = new List<FormatProfile>();
        var verdicts = FormatProfiles.All.Select(p => drive != null
            ? FormatProfiles.Evaluate(p, drive, drive.Media)
            : new ProfileVerdict(p, p.Need == HardwareNeed.Standard && p.Build != null ? ProfileStatus.Ready : p.Need == HardwareNeed.SpecialDrive ? ProfileStatus.Unsupported : ProfileStatus.Planned, NeedText(p))).ToList();

        if (drive != null)
        {
            Ui.Title($"Formáty pro {drive.Id} — {drive.KindText}");
            Ui.Field("Vložená disketa", drive.Kind == DriveKind.Greaseweazle ? "Greaseweazle hustotu diskety nepozná — vlož správnou (DD / HD)" : drive.MediaText);
        }
        else Ui.Title("Formáty disket — všechny systémy");

        Console.WriteLine();
        Ui.Write(drive != null ? "  Teď můžeš vytvořit:" : "  Formáty:", ConsoleColor.White, true);
        string? lastSystem = null;
        foreach (var v in verdicts.Where(v => v.IsReady))
        {
            ready.Add(v.Profile);
            if (v.Profile.System != lastSystem) { Ui.Write("   " + v.Profile.System, ConsoleColor.Cyan, true); lastSystem = v.Profile.System; }
            string num = numbered ? $"{ready.Count,3}) " : "   • ";
            Ui.Write("  " + num, ConsoleColor.White);
            Ui.Write(v.Profile.Name.PadRight(40) + " ", ConsoleColor.White);
            Ui.Write(v.Profile.UsedFor + (v.Profile.Experimental ? "  [experimentální]" : ""), ConsoleColor.DarkGray, true);
        }
        if (ready.Count == 0) Ui.Warn("   (nic — viz důvody níže)");

        var rest = verdicts.Where(v => !v.IsReady).ToList();
        if (rest.Count > 0)
        {
            Console.WriteLine();
            Ui.Write(drive != null ? "  Teď nejde:" : "  Potřebují jiný hardware:", ConsoleColor.White, true);
            foreach (var group in rest.GroupBy(v => v.Status).OrderBy(g => g.Key))
                foreach (var v in group)
                {
                    var (mark, col) = Mark(v.Status);
                    Ui.Write($"   {mark} ", col);
                    Ui.Write($"{v.Profile.System} — {v.Profile.Name}".PadRight(52) + " ", ConsoleColor.Gray);
                    Ui.Write(v.Reason, col, true);
                }
        }
        return ready;
    }

    private static string NeedText(FormatProfile p) => p.Need switch
    {
        HardwareNeed.Standard when p.Build == null => p.Note ?? "Program zatím neumí vytvořit.",
        HardwareNeed.Standard => "USB i interní mechanika.",
        HardwareNeed.LowLevelMfm => (p.WhyNotUsb ?? "") + " → interní mechanika s fdrawcmd.sys nebo Greaseweazle.",
        HardwareNeed.Flux => (p.WhyNotUsb ?? "") + " → Greaseweazle.",
        _ => p.WhyNotUsb ?? "",
    };

    public static int Profiles(string? driveArg)
    {
        DetectedDrive? drive = null;
        if (driveArg != null) drive = Program.ResolveDrive(driveArg, Feature.Standard720);
        PrintProfiles(drive, numbered: false);
        Console.WriteLine();
        Ui.Dim("  Formátování:   dtk format A: --profile <id>    (id: " + string.Join(", ", FormatProfiles.All.Where(p => p.Build != null && p.Need == HardwareNeed.Standard).Select(p => p.Id)) + ")");
        Ui.Dim("  Prázdný obraz: dtk new <soubor> --profile <id> (navíc i: " + string.Join(", ", FormatProfiles.All.Where(p => p.Build != null && p.Need != HardwareNeed.Standard).Select(p => p.Id)) + ")");
        return 0;
    }

    /// <summary>Naformátuje disketu podle profilu — ověří mechaniku i vloženou disketu.</summary>
    public static int Format(string driveArg, FormatProfile p, bool quick, string? label, bool verify)
    {
        if (Backends.IsGwArg(driveArg)) return GwCommands.Format(driveArg, p, label, verify);
        var drive = Program.ResolveDrive(driveArg, p.Density == Density.HD ? Feature.Standard1440 : Feature.Standard720);
        var verdict = FormatProfiles.Evaluate(p, drive, drive.Media);
        if (!verdict.IsReady) throw new OperationException($"{p.Title}: {verdict.Reason}");

        bool fatDs = p.FatStyle != null && p.Geometry.Sides == 2;
        if (quick && !fatDs) throw new OperationException("Rychlý formát umím jen u oboustranných FAT disket (Atari, PC, MSX).");

        Ui.Title($"Formátování {drive.Id}");
        Ui.Field("Formát", p.Title);
        Ui.Field("K čemu", p.UsedFor);
        Ui.Field("Geometrie", p.Geometry.ToString());
        Ui.Field("Vložená disketa", drive.MediaText);
        Ui.Field("Způsob", quick ? "rychlý (jen nový souborový systém)" : "úplný" + (verify ? " + kontrola" : ""));
        if (label != null && p.SupportsLabel) Ui.Field("Jmenovka", label);
        if (label != null && !p.SupportsLabel) Ui.Dim("  (tento formát jmenovku nepodporuje, ignoruji ji)");
        if (p.Note != null) Ui.Warn("  " + p.Note);
        if (!Ui.Confirm($"Všechna data na disketě {drive.Id} budou smazána. Pokračovat?")) return 1;

        using var dev = Backends.OpenForGeometry(drive, p.Geometry);
        var result = new OperationResult();
        Program.Busy = true;
        try
        {
            if (fatDs)
                DiskOperations.Format(dev, p.Geometry, p.FatStyle!.Value, label, quick, verify, result, Ui.Progress, Program.Cancel.Token);
            else
            {
                var blank = p.Build!(p.SupportsLabel ? label : null);
                DiskOperations.WriteImage(dev, blank, format: true, verify: verify, result, Ui.Progress, Program.Cancel.Token);
            }
        }
        finally { Program.Busy = false; }
        Ui.EndProgress();
        Program.Report(result);
        if (result.Success) Ui.Ok($"Hotovo — disketa je naformátovaná jako {p.Title}.");
        else Ui.Error("Formátování skončilo s chybami — disketa má vadná místa.");
        return result.Success ? 0 : 4;
    }

    /// <summary>Menu: ukáže mechaniku, disketu, formáty (dostupné očíslované, ostatní s důvodem) a naformátuje.</summary>
    public static void Interactive()
    {
        var drives = DriveDetector.DetectAll(includeGreaseweazle: true).Where(d => d.CanUseStandardAccess || d.Kind == DriveKind.Greaseweazle).ToList();
        if (drives.Count == 0) throw new OperationException("Nenašel jsem žádnou disketovou mechaniku ani Greaseweazle.");
        string Arg(DetectedDrive d) => d.Letter != null ? d.Letter + ":" : d.Port ?? "GW";
        var drive = drives[0];
        if (drives.Count > 1)
        {
            for (int i = 0; i < drives.Count; i++) Ui.Write($"  {i + 1}) {Arg(drives[i]),-6} {drives[i].KindText} — {drives[i].Name}", ConsoleColor.Gray, true);
            if (int.TryParse(Ui.Ask("Mechanika (číslo)", "1"), out int dn) && dn >= 1 && dn <= drives.Count) drive = drives[dn - 1];
        }

        var ready = PrintProfiles(drive, numbered: true);
        if (ready.Count == 0) return;
        Console.WriteLine();
        string ans = Ui.Ask("Číslo formátu (Enter = zpět)");
        if (!int.TryParse(ans, out int n) || n < 1 || n > ready.Count) return;
        var p = ready[n - 1];
        string? label = p.SupportsLabel ? EmptyToNull(Ui.Ask("Jmenovka (Enter = žádná)")) : null;
        bool quick = drive.Kind != DriveKind.Greaseweazle && p.FatStyle != null && p.Geometry.Sides == 2 && drive.MediaState == MediaState.Ready &&
                     drive.FormattedAs?.SectorsPerTrack == p.Geometry.SectorsPerTrack &&
                     Ui.Ask("Způsob: 1) úplný + kontrola  2) rychlý (jen smazat)", "1") == "2";
        Format(Arg(drive), p, quick, label, verify: true);
    }

    private static string? EmptyToNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
