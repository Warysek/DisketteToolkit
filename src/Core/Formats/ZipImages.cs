using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>
/// Obrazy disket uvnitř .zip (hry z internetu bývají zabalené): seznam obrazů v archivu a rozbalení jednoho
/// do dočasné složky — dál se s ním pracuje jako s obyčejným souborem.
/// </summary>
public static class ZipImages
{
    private static readonly string[] ImageExtensions =
        { ".st", ".msa", ".dim", ".img", ".ima", ".vfd", ".flp", ".dsk", ".d81", ".adf", ".dms", ".stx", ".ipf", ".scp", ".hfe" };

    /// <summary>Větší položku nerozbalíme — obraz diskety má nanejvýš desítky MB (tok), tohle by byl omyl nebo past.</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    public static bool IsZip(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageName(string name) => ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>Obrazy disket v archivu (celé cesty uvnitř), seřazené podle názvu; složky a smetí z macOS vynechá.</summary>
    public static List<string> List(string zipPath)
    {
        using var zip = Open(zipPath);
        return zip.Entries
            .Where(e => e.Name.Length > 0 && !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith("._") && IsImageName(e.Name))
            .Select(e => e.FullName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Rozbalí jednu položku do nové dočasné složky (pod původním názvem souboru) a vrátí cestu k ní.</summary>
    public static string Extract(string zipPath, string entryName)
    {
        using var zip = Open(zipPath);
        var e = zip.GetEntry(entryName) ?? throw new OperationException(Tr.F("V archivu {0} není {1}.", Path.GetFileName(zipPath), entryName));
        if (e.Length > MaxEntryBytes) throw new OperationException(Tr.F("{0} je v archivu příliš velký ({1:N0} B) — obraz diskety to nebude.", e.Name, e.Length));
        string dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(e.Name));   // jen název — žádné „..\“ z archivu
        e.ExtractToFile(path);
        return path;
    }

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "DisketteToolkit-zip");

    /// <summary>Smaže dočasně rozbalené obrazy starší než den (při spuštění programu; chyby nevadí).</summary>
    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var d in Directory.GetDirectories(TempRoot))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ZipArchive Open(string zipPath)
    {
        try { return ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new OperationException(Tr.F("{0} není platný ZIP archiv (nebo je poškozený).", Path.GetFileName(zipPath))); }
    }
}
