using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AtariFloppy.Core.App;

namespace AtariFloppy.Gui;

/// <summary>Nastavení okna — ukládá se do %APPDATA%\DisketteToolkit\settings.ini (jednoduché „klíč = hodnota“).</summary>
internal sealed class AppSettings
{
    public string Language { get; set; } = "";
    public DriveSettings Drive { get; } = new();
    /// <summary>Kam ukládat automatické zálohy (před testem, před opravou).</summary>
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public string LastFolder { get; set; } = "";
    public string LastDrive { get; set; } = "";
    public bool VerifyAfterWrite { get; set; } = true;
    /// <summary>diskcopy.dll ze starších Windows pro spouštěcí disketu MS-DOS.</summary>
    public string? DiskcopyPath { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static string DefaultBackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Diskette Toolkit");

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisketteToolkit", "settings.ini");

    /// <summary>Nastavení ze starší verze (když se program ještě jmenoval AtariFloppy).</summary>
    private static string OldFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AtariFloppy", "settings.ini");

    public static AppSettings Load(string? path = null)
    {
        var s = new AppSettings();
        try
        {
            path ??= File.Exists(FilePath) || !File.Exists(OldFilePath) ? FilePath : OldFilePath;
            if (!File.Exists(path)) return s;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.TrimStart().StartsWith("#")) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string? Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
            int? Int(string k) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
            bool Bool(string k, bool def) => Get(k) is string v ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) : def;

            s.Language = Get("language") ?? "";
            s.Drive.ForceFdraw = Bool("force_fdraw", false);
            s.Drive.StepRateMs = Int("step_rate_ms") is int sr && sr is >= 1 and <= 30 ? sr : null;
            s.Drive.GwDrive = Get("gw_drive") ?? "A";
            s.Drive.GwPath = Get("gw_path");
            s.Drive.Revs = Int("flux_revs") is int r && r is >= 1 and <= 10 ? r : 3;
            s.Drive.FluxTracks = Int("flux_tracks") is int t && t is >= 40 and <= 86 ? t : null;
            s.BackupFolder = Get("backup_folder") ?? DefaultBackupFolder;
            s.LastFolder = Get("last_folder") ?? "";
            s.LastDrive = Get("last_drive") ?? "";
            s.VerifyAfterWrite = Bool("verify", true);
            s.DiskcopyPath = Get("diskcopy_dll");
            s.WindowWidth = Int("window_width") ?? 0;
            s.WindowHeight = Int("window_height") ?? 0;
            s.WindowMaximized = Bool("window_maximized", false);
        }
        catch { /* poškozený soubor = výchozí nastavení */ }
        return s;
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            sb.AppendLine("# Diskette Toolkit — nastavení");
            void W(string k, object? v) => sb.AppendLine(k + " = " + Convert.ToString(v, CultureInfo.InvariantCulture));
            W("language", Language);
            W("force_fdraw", Drive.ForceFdraw ? 1 : 0);
            W("step_rate_ms", Drive.StepRateMs);
            W("gw_drive", Drive.GwDrive);
            W("gw_path", Drive.GwPath);
            W("flux_revs", Drive.Revs);
            W("flux_tracks", Drive.FluxTracks);
            W("backup_folder", BackupFolder);
            W("last_folder", LastFolder);
            W("last_drive", LastDrive);
            W("verify", VerifyAfterWrite ? 1 : 0);
            W("diskcopy_dll", DiskcopyPath);
            W("window_width", WindowWidth);
            W("window_height", WindowHeight);
            W("window_maximized", WindowMaximized ? 1 : 0);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* nastavení není kritické */ }
    }
}
