using System;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;

namespace AtariFloppy.Cli;

/// <summary>Menu pro spuštění dvojklikem — stejné funkce jako příkazy, jen se ptá.</summary>
internal static class Menu
{
    public static int Run()
    {
        while (true)
        {
            Program.ResetCancel();
            Ui.Title($"Diskette Toolkit {Program.Version}");
            Console.WriteLine("  1) Zjistit připojené mechaniky");
            Console.WriteLine("  2) Informace o image souboru");
            Console.WriteLine("  3) Načíst disketu do image");
            Console.WriteLine("  4) Zapsat image na disketu");
            Console.WriteLine("  5) Naformátovat disketu (Atari, PC, MSX, Mac, Linux …)");
            Console.WriteLine("  6) Převést image (ST / MSA / DIM)");
            Console.WriteLine("  7) Vytvořit prázdný image (i Amiga .ADF, Mac …)");
            Console.WriteLine("  8) Rozbalit soubory z image");
            Console.WriteLine("  9) Test povrchu a kontrola diskety");
            Console.WriteLine(" 10) Kontrola / oprava image souboru");
            Console.WriteLine(" 11) Přehled formátů 3,5\" disket");
            Console.WriteLine(" 12) Greaseweazle: záloha jako magnetický tok (.scp / .hfe — i chráněné hry)");
            Console.WriteLine("  0) Konec");
            string choice = Ui.Ask("\nVolba");
            if (choice is "0" or "" or "q") return 0;
            try
            {
                switch (choice)
                {
                    case "1": Program.Detect(); break;
                    case "2": Program.Info(AskFile("Image (můžeš ho sem přetáhnout myší)"), false); break;
                    case "3": ReadDisk(); break;
                    case "4": WriteDisk(); break;
                    case "5": ProfilesCommand.Interactive(); break;
                    case "6": Program.Convert(AskFile("Vstupní image"), Ui.Ask("Výstupní soubor (.st / .msa / .dim)")); break;
                    case "7": NewImage(); break;
                    case "8":
                        {
                            string img = AskFile("Image");
                            string def = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(img)) ?? ".", Path.GetFileNameWithoutExtension(img));
                            Program.Extract(img, Ui.Ask("Cílová složka", def));
                            break;
                        }
                    case "9": CheckDisk(); break;
                    case "10":
                        {
                            string img = AskFile("Image (můžeš ho sem přetáhnout myší)");
                            int rc = CheckCommand.CheckImage(img, false);
                            if (rc == 4 && Ui.Confirm("Uložit opravenou kopii image? (původní soubor zůstane)")) CheckCommand.CheckImage(img, true);
                            break;
                        }
                    case "11": CheckCommand.Formats(); break;
                    case "12":
                        {
                            string f = Ui.Ask("Uložit do souboru (.scp nebo .hfe)", "zaloha.scp");
                            if (!Backends.IsFlux(f)) f += ".scp";
                            GwCommands.Read("GW", f, null);
                            break;
                        }
                    default: Ui.Warn("Neznámá volba."); break;
                }
            }
            catch (OperationCanceledException) { Ui.EndProgress(); Ui.Warn("Operace byla přerušena."); }
            catch (Exception e) when (e is OperationException or FormatException or FileNotFoundException or NotSupportedException or ArgumentException)
            {
                Ui.EndProgress();
                Ui.Error(e.Message);
            }
            Console.WriteLine();
            Ui.Dim("Enter = zpět do menu");
            Console.ReadLine();
        }
    }

    private static void ReadDisk()
    {
        string drive = AskDrive();
        string file = Ui.Ask("Uložit do souboru (.st / .msa / .dim)");
        if (Path.GetExtension(file).Length == 0) file += ".st";
        Program.Read(drive, file, SideMode.Auto);
    }

    private static void WriteDisk()
    {
        string file = AskFile("Image k zápisu (můžeš ho sem přetáhnout myší)");
        string drive = AskDrive();
        bool format = Ui.Confirm("Naformátovat disketu před zápisem? (nutné u nové nebo nestandardní diskety)");
        Program.Write(file, drive, format, verify: true);
    }

    private static void CheckDisk()
    {
        string drive = AskDrive();
        string m = Ui.Ask("Test: 1) jen čtení (nic nemění)  2) + zápis s obnovou dat  3) + zápis, SMAŽE disketu", "1");
        var mode = m switch { "2" => WriteTestMode.PreserveData, "3" => WriteTestMode.Destructive, _ => WriteTestMode.None };
        int rc = CheckCommand.CheckDisk(drive, mode, repair: false, saveImage: null);
        if (rc == 4 && mode != WriteTestMode.Destructive && Ui.Confirm("Zkusit chyby souborového systému opravit? (napřed se uloží záloha)"))
            CheckCommand.CheckDisk(drive, WriteTestMode.None, repair: true, saveImage: null);
    }

    /// <summary>Prázdný obraz pro kterýkoli systém — i pro ty, které USB mechanika nezapíše (Amiga, Mac 800 KB).</summary>
    private static void NewImage()
    {
        var list = FormatProfiles.All.Where(p => p.Build != null).ToList();
        Ui.Title("Prázdný obraz diskety");
        string? last = null;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p.System != last) { Ui.Write("   " + p.System, ConsoleColor.Cyan, true); last = p.System; }
            Ui.Write($"  {i + 1,3}) {p.Name.PadRight(40)}", ConsoleColor.White);
            Ui.Write(p.Need == HardwareNeed.Standard ? "zapíše i USB mechanika" : "na disketu jen přes Greaseweazle", ConsoleColor.DarkGray, true);
        }
        if (!int.TryParse(Ui.Ask("\nČíslo formátu (Enter = zpět)"), out int n) || n < 1 || n > list.Count) return;
        var prof = list[n - 1];
        string file = Ui.Ask($"Název souboru (přípona {prof.ImageExtension} se doplní)");
        if (string.IsNullOrWhiteSpace(file)) return;
        string? label = prof.SupportsLabel ? EmptyToNull(Ui.Ask("Jmenovka (Enter = žádná)")) : null;
        Program.NewFromProfile(file, prof, label);
    }

    private static string AskDrive()
    {
        var drives = DriveDetector.DetectAll(includeGreaseweazle: true)
                                  .Where(d => d.CanUseStandardAccess || d.Kind == DriveKind.Greaseweazle).ToList();
        if (drives.Count == 0) throw new OperationException("Nenašel jsem žádnou disketovou mechaniku ani Greaseweazle.");
        string Arg(DetectedDrive d) => d.Letter != null ? d.Letter + ":" : d.Port ?? "GW";
        if (drives.Count == 1)
        {
            Ui.Dim($"Mechanika: {Arg(drives[0])} — {drives[0].KindText}, {drives[0].Name}");
            return Arg(drives[0]);
        }
        for (int i = 0; i < drives.Count; i++)
            Ui.Write($"  {i + 1}) {Arg(drives[i]),-6} {drives[i].KindText} — {drives[i].Name}", ConsoleColor.Gray, true);
        string a = Ui.Ask("Mechanika (číslo)", "1");
        return int.TryParse(a, out int n) && n >= 1 && n <= drives.Count ? Arg(drives[n - 1]) : Arg(drives[0]);
    }

    private static string AskFile(string question)
    {
        string f = Ui.Ask(question);
        if (!File.Exists(f)) throw new FileNotFoundException($"Soubor neexistuje: {f}");
        return f;
    }

    private static string? EmptyToNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
