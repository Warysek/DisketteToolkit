using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Gw;

/// <summary>
/// Oficiální program Greaseweazle Host Tools (gw.exe, volně šiřitelný — github.com/keirf/greaseweazle).
/// Diskette Toolkit ho používá jako „motor“: gw umí magnetický tok, my vybereme formát, hlídáme průběh a výsledek.
/// </summary>
public sealed class GwTool
{
    public string Path { get; }
    private GwTool(string path) { Path = path; }

    public static string DownloadHint => Tr.T("Diskette Toolkit ho umí stáhnout sám: v okně tlačítko „Stáhnout Greaseweazle Host Tools“, v příkazové řádce dtk install gw. Ručně: github.com/keirf/greaseweazle/releases, rozbalit do složky „greaseweazle“ vedle programu.");

    /// <summary>Kde gw hledáme: vedle programu, ve složce greaseweazle*, v proměnné AFLOPPY_GW a v PATH.</summary>
    public static IEnumerable<string> Candidates()
    {
        string? env = Environment.GetEnvironmentVariable("AFLOPPY_GW");
        if (!string.IsNullOrEmpty(env)) yield return env!;
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        yield return System.IO.Path.Combine(baseDir, "gw.exe");
        yield return System.IO.Path.Combine(baseDir, "greaseweazle", "gw.exe");
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(baseDir, "greaseweazle*"); } catch { subdirs = new string[0]; }
        foreach (var d in subdirs)
        {
            yield return System.IO.Path.Combine(d, "gw.exe");
            string[] inner;
            try { inner = Directory.GetDirectories(d); } catch { inner = new string[0]; }
            foreach (var i in inner) yield return System.IO.Path.Combine(i, "gw.exe");
        }
        // sem ho stáhne program sám (vedle programu, nebo v %LOCALAPPDATA% — i ze starších verzí)
        yield return System.IO.Path.Combine(AtariFloppy.Core.App.GreaseweazleSetup.InstallDir, "gw.exe");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return System.IO.Path.Combine(local, "DisketteToolkit", "greaseweazle", "gw.exe");
        yield return System.IO.Path.Combine(local, "AtariFloppy", "greaseweazle", "gw.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            yield return System.IO.Path.Combine(dir.Trim(), "gw.exe");
            yield return System.IO.Path.Combine(dir.Trim(), "gw");
        }
    }

    public static GwTool? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath) ? new GwTool(explicitPath!) : null;
        foreach (var c in Candidates())
        {
            try { if (File.Exists(c)) return new GwTool(c); } catch { }
        }
        return null;
    }

    /// <summary>Spustí gw s parametry, řádky výstupu posílá průběžně; při zrušení gw ukončí.</summary>
    public int Run(IEnumerable<string> args, Action<string> onLine, CancellationToken ct = default, string? workDir = null)
    {
        var psi = new ProcessStartInfo(Path, string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        using var p = new Process { StartInfo = psi };
        var sync = new object();
        void Handle(string? line)
        {
            if (line == null) return;
            lock (sync) onLine(line.TrimEnd('\r'));
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        try { p.Start(); }
        catch (Exception e) { throw new OperationException(Tr.F("Program gw nejde spustit ({0}): {1}", Path, e.Message)); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        while (!p.WaitForExit(200))
        {
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(); } catch { }
                p.WaitForExit(3000);
                ct.ThrowIfCancellationRequested();
            }
        }
        p.WaitForExit(); // dočíst výstup
        return p.ExitCode;
    }

    private static string Quote(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
