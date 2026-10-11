using System;
using System.IO;
using AtariFloppy.Core;
using AtariFloppy.Core.Boot;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;

namespace AtariFloppy.Cli;

/// <summary>Spouštěcí diskety: FreeDOS, MS-DOS z Windows, Amiga (install), Atari (AUTO).</summary>
internal static class BootCommand
{
    public static int Run(string system, string target, string? size, string? label, string? prg, bool noTools = false, string? diskcopy = null)
    {
        var o = new BootOptions { Label = label, FreeDosTools = !noTools };
        if (diskcopy != null) WindowsMsDos.CustomPath = diskcopy;
        o.System = system.ToLowerInvariant() switch
        {
            "freedos" or "dos" => BootSystem.FreeDos,
            "msdos" or "ms-dos" or "windows" => BootSystem.MsDosFromWindows,
            "amiga" => BootSystem.AmigaFfs,
            "amiga13" or "amiga-ofs" => BootSystem.AmigaOfs,
            "atari" => BootSystem.AtariAuto,
            var x => throw new ArgumentException($"boot {x}: umím freedos, msdos, amiga, amiga13, atari."),
        };
        o.Geometry = size switch
        {
            null => o.System == BootSystem.AtariAuto ? DiskGeometry.Dd720 : DiskGeometry.Hd1440,
            "720" => DiskGeometry.Dd720,
            "1440" or "1.44" or "1,44" => DiskGeometry.Hd1440,
            var x => throw new ArgumentException($"--size {x}: umím 720 nebo 1440."),
        };
        if (o.System == BootSystem.AtariAuto)
        {
            if (prg == null) throw new ArgumentException("Atari: zadej program, který se má spouštět: --prg HRA.PRG");
            o.AtariProgram = File.ReadAllBytes(prg);
            o.AtariProgramName = Path.GetFileName(prg);
        }

        Ui.Title("Spouštěcí disketa — " + BootDisks.Describe(o.System));
        if (o.System == BootSystem.FreeDos && !FreeDosFiles.Cached) Ui.Dim("  Stahuji FreeDOS (kernel.zip a freecom.zip, jen poprvé)…");
        Program.Busy = true;
        DiskImage img;
        try { img = BootDisks.Build(o, (d, t) => Ui.Progress((int)(d / 1024), (int)Math.Max(1, t / 1024), $"Stahuji {d / 1024} KB"), Program.Cancel.Token); }
        finally { Program.Busy = false; Ui.EndProgress(); }
        Ui.Field("Obraz", img.Geometry.ToString());
        Ui.Field("Systém", FileSystemDetector.Detect(img.Data).Name + " — " + (FileSystemDetector.Detect(img.Data).Details ?? ""));

        bool isDrive = Backends.IsGwArg(target) || target.TrimEnd(':', '\\').Length == 1;
        if (!isDrive)
        {
            if (File.Exists(target) && !Ui.Confirm($"Soubor {target} už existuje. Přepsat?")) return 1;
            ImageFile.Save(img, target);
            Ui.Ok($"Uloženo: {target}");
            return 0;
        }
        string ext = o.System is BootSystem.AmigaFfs or BootSystem.AmigaOfs ? ".adf" : o.System == BootSystem.AtariAuto ? ".st" : ".img";
        string tmp = Path.Combine(Path.GetTempPath(), "afloppy-boot" + ext);
        ImageFile.Save(img, tmp);
        try { return Program.Write(tmp, target, format: true, verify: true); }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
