using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

public sealed class FileRescueResult
{
    public string Folder { get; set; } = "";
    public int Saved { get; set; }
    /// <summary>Uložené soubory, kterým něco chybí (nečitelné sektory, krátký řetězec FAT).</summary>
    public List<string> Damaged { get; } = new();
    /// <summary>Obnovené smazané soubory.</summary>
    public List<string> Recovered { get; } = new();
    /// <summary>Smazané soubory, jejichž místo už zabral jiný soubor.</summary>
    public List<string> Lost { get; } = new();
    public List<string> Failed { get; } = new();
    public bool SystemAreaDamaged { get; set; }
    public string? ReportPath { get; set; }
}

/// <summary>
/// „Zachránit soubory“: všechno, co jde z obrazu s FAT12 přečíst, do složky — běžné soubory i smazané, které
/// ještě nikdo nepřepsal. Obraz se nikdy nemění. Když vedle obrazu leží stav záchrany (.rescue.txt), řekne
/// u každého souboru, jestli v něm jsou nečitelné sektory.
/// </summary>
public static class FileRescue
{
    public static FileRescueResult SaveAll(DiskImage img, string folder, ISet<long>? badSectors)
    {
        var fat = Fat12Volume.TryOpen(img.Data, out string? err) ?? throw new Drives.OperationException(err ?? Tr.T("Obraz nemá čitelný souborový systém."));
        var r = new FileRescueResult { Folder = folder };
        Directory.CreateDirectory(folder);
        var bad = badSectors ?? new HashSet<long>();
        r.SystemAreaDamaged = bad.Any(s => s < fat.DataStartLba);

        int BadIn(IEnumerable<int> clusters) => clusters.SelectMany(fat.ClusterSectors).Count(bad.Contains);

        foreach (var e in fat.ListAll())
        {
            string target = Target(folder, e.Path);
            try
            {
                if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
                var chain = fat.Chain(e.FirstCluster, e.Size);
                var data = fat.ReadFile(e);
                string? why = null;
                if (data.Length < e.Size)
                {
                    why = Tr.F("řetězec FAT končí dřív — chybí {0:N0} B", e.Size - data.Length);
                    data = data.Concat(new byte[e.Size - data.Length]).ToArray();
                }
                int n = BadIn(chain);
                if (n > 0) why = (why != null ? why + "; " : "") + Tr.F("nečitelných sektorů: {0} (jsou v něm nuly)", n);
                string saved = Save(target, data, e.Modified);
                r.Saved++;
                if (why != null) r.Damaged.Add($"{Rel(folder, saved)} — {why}");
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                r.Failed.Add($"{e.Path} — {x.Message}");
            }
        }

        string deletedFolder = Path.Combine(folder, Tr.T("_smazane"));
        foreach (var d in fat.ListDeleted().Where(d => !d.IsDirectory))
        {
            if (!d.Recoverable) { r.Lost.Add(Tr.F("{0} ({1:N0} B) — místo už zabral jiný soubor", d.Path, d.Size)); continue; }
            try
            {
                string saved = Save(Target(deletedFolder, d.Path), fat.ReadClusters(d.Clusters!, d.Size), d.Modified);
                var notes = new List<string>();
                if (!d.Contiguous) notes.Add(Tr.T("soubor mohl být roztříštěný — obsah nemusí sedět"));
                int n = BadIn(d.Clusters!);
                if (n > 0) notes.Add(Tr.F("nečitelných sektorů: {0}", n));
                r.Recovered.Add(Rel(folder, saved) + (notes.Count > 0 ? " — " + string.Join("; ", notes) : ""));
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                r.Failed.Add($"{d.Path} — {x.Message}");
            }
        }

        r.ReportPath = Path.Combine(folder, Tr.T("_zachrana.txt"));
        File.WriteAllText(r.ReportPath, Report(r, badSectors != null), new UTF8Encoding(true));
        return r;
    }

    public static string Report(FileRescueResult r, bool knewBadSectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Tr.F("Zachráněno z obrazu — Diskette Toolkit {0}, {1:yyyy-MM-dd HH:mm}", AppPaths.Version, DateTime.Now));
        sb.AppendLine();
        sb.AppendLine(Tr.F("Uložených souborů: {0}", r.Saved));
        if (!knewBadSectors) sb.AppendLine(Tr.T("Seznam nečitelných sektorů k obrazu není (.rescue.txt) — jestli v souborech něco chybí, nevím."));
        if (r.SystemAreaDamaged) sb.AppendLine(Tr.T("POZOR: nečitelné sektory jsou i v boot sektoru, FAT nebo kořenovém adresáři — seznam souborů nemusí být úplný."));
        void Block(string title, List<string> lines)
        {
            if (lines.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine(title);
            foreach (var l in lines) sb.AppendLine("  " + l);
        }
        Block(Tr.T("Soubory, kterým něco chybí:"), r.Damaged);
        Block(Tr.T("Obnovené smazané soubory (první písmeno jména se při smazání ztratí — je místo něj „_“):"), r.Recovered);
        Block(Tr.T("Smazané soubory, které už obnovit nejde:"), r.Lost);
        Block(Tr.T("Nepodařilo se uložit:"), r.Failed);
        return sb.ToString();
    }

    private static string Target(string folder, string fatPath) =>
        Path.Combine(folder, Path.Combine(fatPath.Split('\\').Select(Extractor.SafeName).ToArray()));

    /// <summary>Uloží soubor; když už takový ve složce je (dvě položky stejného jména v poškozeném adresáři), přidá „ (2)“.</summary>
    private static string Save(string target, byte[] data, DateTime? modified)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string path = target;
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + $" ({n})" + Path.GetExtension(target));
        File.WriteAllBytes(path, data);
        if (modified != null) File.SetLastWriteTime(path, modified.Value);
        return path;
    }

    private static string Rel(string folder, string path) => path.Substring(folder.Length).TrimStart('\\');
}
