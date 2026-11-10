using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.I18n;
using Microsoft.Win32;

namespace AtariFloppy.Core.App;

/// <summary>
/// „Zpráva pro fórum“: krátké shrnutí v prostém textu, které tester vloží na fórum, do skupiny na Facebooku
/// nebo do GitHub Discussions — verze programu a Windows, nalezené mechaniky, typ řadiče a jeho rychlosti
/// (rozhodují o 11 sektorech), gw a firmware Greaseweazle, výsledek poslední operace a konec výpisu.
/// Domovská složka se nahradí %USERPROFILE%; jméno uživatele, název počítače, e-mail ani sériové číslo
/// Greaseweazle do zprávy nepatří.
/// </summary>
public static class ForumReport
{
    public const int LogLines = 40;

    /// <summary>Co o mechanikách řekne až hardware: popis řadiče (fdrawcmd.sys) a výstup „gw info“.</summary>
    public sealed class HardwareInfo
    {
        public Dictionary<string, string> Controllers { get; } = new();
        public Dictionary<string, List<string>> Gw { get; } = new();
    }

    /// <summary>Zeptá se hardwaru: krátce otevře řadič přes fdrawcmd.sys, u Greaseweazle spustí „gw info“.</summary>
    public static HardwareInfo Probe(IEnumerable<DetectedDrive> drives, string? gwPath, CancellationToken ct = default)
    {
        var hw = new HardwareInfo();
        foreach (var d in drives)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (d.FdrawcmdInstalled && d.Letter != null)
                {
                    using var fdc = new WinFdc(char.ToUpperInvariant(d.Letter[0]) - 'A');
                    hw.Controllers[d.Id] = fdc.Description;
                }
                else if (d.Kind == DriveKind.Greaseweazle)
                {
                    var tool = GwTool.Find(gwPath) ?? (d.GwToolPath != null ? GwTool.Find(d.GwToolPath) : null);
                    if (tool != null) hw.Gw[d.Id] = new GwSession(tool, d.Port).Info(ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                string msg = Tr.T("nepodařilo se zjistit — ") + e.Message;
                if (d.Kind == DriveKind.Greaseweazle) hw.Gw[d.Id] = new List<string> { msg };
                else hw.Controllers[d.Id] = msg;
            }
        }
        return hw;
    }

    /// <summary>Hotová zpráva pro tento počítač (už bez jména uživatele a počítače).</summary>
    public static string Create(IReadOnlyList<DetectedDrive> drives, HardwareInfo hw, string? outcome, string log) =>
        Anonymize(Build(drives, hw, outcome, log, WindowsVersion()),
                  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public static string Build(IReadOnlyList<DetectedDrive> drives, HardwareInfo hw, string? outcome, string log, string windows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Tr.F("Diskette Toolkit {0} — zpráva pro fórum", ProgramVersion()));
        sb.AppendLine("Windows: " + windows);
        sb.AppendLine(Tr.T("Mechaniky:"));
        if (drives.Count == 0) sb.AppendLine("- " + Tr.T("žádná nenalezena"));
        foreach (var d in drives)
        {
            string head = $"- {d.Id} {d.KindText} — {d.Name}";
            if (!string.IsNullOrEmpty(d.Details)) head += $" ({d.Details})";
            sb.AppendLine(head);
            if (hw.Controllers.TryGetValue(d.Id, out var c)) sb.AppendLine("    " + Tr.T("řadič: ") + c);
            if (d.Kind == DriveKind.Greaseweazle)
            {
                if (hw.Gw.TryGetValue(d.Id, out var lines))
                    foreach (var l in lines.Select(l => Regex.Replace(l.Trim(), @"\s+", " "))
                                           .Where(l => l.Length > 0 && !l.EndsWith(":") && !l.StartsWith("Serial", StringComparison.OrdinalIgnoreCase)))
                        sb.AppendLine("    gw: " + l);
                else if (d.GwToolPath == null) sb.AppendLine("    " + Tr.T("program gw.exe chybí"));
            }
            else
            {
                sb.AppendLine("    " + Tr.T("disketa: ") + d.MediaText);
                if (d.Note720 != null) sb.AppendLine("    720 KB: " + d.Note720);
            }
        }
        if (!string.IsNullOrWhiteSpace(outcome)) sb.AppendLine(Tr.T("Poslední výsledek: ") + outcome!.Trim());
        var tail = (log ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (tail.Count > 0 && tail[tail.Count - 1].Length == 0) tail.RemoveAt(tail.Count - 1);
        if (tail.Count > LogLines) tail = tail.Skip(tail.Count - LogLines).ToList();
        if (tail.Count > 0)
        {
            sb.AppendLine(Tr.F("Konec výpisu (posledních {0} řádků):", tail.Count));
            foreach (var l in tail) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    /// <summary>Domovská složka → %USERPROFILE%, jméno uživatele a počítače → zástupné slovo, e-mail → &lt;e-mail&gt;.</summary>
    public static string Anonymize(string text, string? userProfile, string? userName, string? machineName)
    {
        if (!string.IsNullOrEmpty(userProfile))
        {
            string p = userProfile!.TrimEnd('\\', '/');
            if (p.Length > 3)
                text = Regex.Replace(text, Regex.Escape(p).Replace(@"\\", @"[\\/]") + @"(?![\w-])", "%USERPROFILE%", RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "<e-mail>");
        text = Word(text, machineName, "%COMPUTERNAME%");
        text = Word(text, userName, "%USERNAME%");
        return text;

        static string Word(string t, string? w, string with) =>
            string.IsNullOrEmpty(w) || w!.Length < 3 ? t : Regex.Replace(t, @"(?<![\w-])" + Regex.Escape(w) + @"(?![\w-])", with, RegexOptions.IgnoreCase);
    }

    /// <summary>„0.10 (2ad3666)“ — verze a začátek commitu, ze kterého je program sestavený.</summary>
    public static string ProgramVersion()
    {
        string? info = typeof(ForumReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = info?.IndexOf('+') ?? -1;
        string commit = plus >= 0 ? info!.Substring(plus + 1) : "";
        return commit.Length >= 7 ? $"{AppPaths.Version} ({commit.Substring(0, 7)})" : AppPaths.Version;
    }

    /// <summary>„Windows 11 Pro 24H2, build 26100, 64bitové“ (z registru; Windows 11 se tam pořád hlásí jako Windows 10).</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string name = k?.GetValue("ProductName") as string ?? "Windows";
            string? release = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? k?.GetValue("CSDVersion") as string;
            string build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name}{(release != null ? " " + release : "")}, build {build}, "
                   + (Environment.Is64BitOperatingSystem ? Tr.T("64bitové") : Tr.T("32bitové"));
        }
        catch (Exception) { return Environment.OSVersion.VersionString; }
    }
}
