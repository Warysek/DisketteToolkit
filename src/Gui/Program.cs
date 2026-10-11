using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui;

internal static class Program
{
    public static string Version => AtariFloppy.Core.App.AppPaths.Version;

    /// <summary>Ukázkový režim se simulovanými mechanikami (DisketteToolkit.exe --demo).</summary>
    public static bool Demo { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        Demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("/demo", StringComparison.OrdinalIgnoreCase));
        string? shot = args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault();

        var settings = AppSettings.Load();
        string lang = args.SkipWhile(a => a != "--lang").Skip(1).FirstOrDefault() ?? settings.Language;
        if (string.IsNullOrEmpty(lang))
            lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "cs" or "sk" ? "cs" : "en";
        Tr.SetLanguage(lang);
        settings.Language = Tr.Code;
        AtariFloppy.Core.Boot.WindowsMsDos.CustomPath = settings.DiskcopyPath;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        var form = new MainForm(settings, Demo);
        // automatické snímky jen v ukázkovém režimu — některé skripty formátují nebo zapisují bez potvrzení
        if (shot != null && Demo) form.ScreenshotScript = shot;
        Application.Run(form);
        return 0;
    }

    private static void ShowCrash(Exception? e)
    {
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "DisketteToolkit-chyba.txt");
            File.WriteAllText(log, DateTime.Now + "\r\n" + e);
            MessageBox.Show(Tr.T("Nastala neočekávaná chyba programu:") + "\n\n" + e?.Message + "\n\n" +
                            Tr.F("Podrobnosti jsou v souboru {0}", log), "Diskette Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
