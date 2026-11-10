using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Kontrolní součty souboru (malá písmena, bez pomlček — jak je vypisují md5sum / sha256sum).</summary>
public sealed record Checksums(long Size, string Md5, string Sha1, string Sha256)
{
    public static Checksums Of(string path)
    {
        var data = File.ReadAllBytes(path);
        static string Hex(HashAlgorithm a, byte[] d) { using (a) return BitConverter.ToString(a.ComputeHash(d)).Replace("-", "").ToLowerInvariant(); }
        return new Checksums(data.Length, Hex(MD5.Create(), data), Hex(SHA1.Create(), data), Hex(SHA256.Create(), data));
    }
}

/// <summary>
/// Záznam o záloze — textový soubor vedle obrazu (disketa.st → disketa.st.log.txt): kdy, z jaké mechaniky, jak,
/// co se nepřečetlo a kontrolní součty spočítané hned po uložení.
/// </summary>
public sealed class BackupRecord
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Drive { get; init; } = "";
    /// <summary>Čím se četlo: ovladač Windows, fdrawcmd.sys + řadič, Greaseweazle.</summary>
    public string Method { get; init; } = "";
    public string Format { get; init; } = "";
    /// <summary>Sektory, které se nepřečetly (jeden řádek na sektor). Prázdné = záloha je úplná.</summary>
    public List<string> Missing { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Complete => Missing.Count == 0;

    public static string LogPath(string imagePath) => imagePath + ".log.txt";

    public string Text(string imagePath, Checksums sums)
    {
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L(Tr.F("Diskette Toolkit {0} — záznam o záloze diskety", AppPaths.Version));
        L("");
        L(Tr.T("Obraz: ") + Path.GetFileName(imagePath));
        L(Tr.T("Datum: ") + Time.ToString("yyyy-MM-dd HH:mm:ss"));
        L(Tr.T("Mechanika: ") + Drive);
        L(Tr.T("Čtení: ") + Method);
        L(Tr.T("Formát: ") + Format);
        L(Tr.T("Výsledek: ") + (Complete ? Tr.T("úplná záloha — přečetly se všechny sektory")
                                         : Tr.F("NEÚPLNÁ záloha — nepřečtených sektorů {0}, na jejich místě v obrazu nejsou původní data", Missing.Count)));
        foreach (var n in Notes) L(Tr.T("Poznámka: ") + n);
        if (!Complete)
        {
            L("");
            L(Tr.T("Nepřečtené sektory:"));
            foreach (var m in Missing) L("   " + m);
        }
        L("");
        L(Tr.F("Velikost: {0} B", sums.Size));
        L("MD5:     " + sums.Md5);
        L("SHA-1:   " + sums.Sha1);
        L("SHA-256: " + sums.Sha256);
        L(Tr.T("(kontrolní součty souboru obrazu, spočítané hned po uložení)"));
        return sb.ToString();
    }
}

/// <summary>Uložená záloha: kde je, její součty a záznam (null = záznam se nepodařilo zapsat).</summary>
public sealed record SavedBackup(string Path, Checksums Sums, string? LogPath);

public static partial class Backups
{
    /// <summary>Značka v názvu neúplné zálohy: „disketa (NEUPLNA).st“.</summary>
    public static string IncompleteTag => Tr.T("NEUPLNA");

    /// <summary>disketa.st → disketa (NEUPLNA).st; když už existuje, disketa (NEUPLNA 2).st…</summary>
    public static string IncompletePath(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        string p = Path.Combine(dir, $"{stem} ({IncompleteTag}){ext}");
        for (int n = 2; File.Exists(p); n++) p = Path.Combine(dir, $"{stem} ({IncompleteTag} {n}){ext}");
        return p;
    }

    /// <summary>
    /// Dokončí zálohu: neúplnou uloží pod názvem s „(NEUPLNA)“ — nikdy pod běžným názvem, ať ji nikdo nepovažuje
    /// za hotovou —, spočítá kontrolní součty a vedle obrazu zapíše záznam.
    /// </summary>
    public static SavedBackup Finish(string path, BackupRecord rec, Action<string> save)
    {
        if (!rec.Complete) path = IncompletePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        save(path);
        var sums = Checksums.Of(path);
        string? log = BackupRecord.LogPath(path);
        try { File.WriteAllText(log, rec.Text(path, sums), new UTF8Encoding(true)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log = null; }
        return new SavedBackup(path, sums, log);
    }
}
