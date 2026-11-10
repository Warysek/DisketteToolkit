using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Co kam: soubor z Průzkumníku (null = jen prázdná složka) → cesta na disketě ve jménech 8.3.</summary>
public sealed record EditItem(string? Source, string Target);

/// <summary>
/// Přidávání a mazání souborů v obrazu s FAT12 (.st, .msa, .dim, .img): změna se udělá na kopii dat, obraz se
/// uloží celý znovu a před první změnou vznikne vedle kopie původního obrazu („hra (před úpravou).st“).
/// </summary>
public static class ImageEdit
{
    private static readonly string[] Savable = { ".st", ".msa", ".dim", ".img", ".ima", ".vfd", ".flp" };

    public static bool CanSave(string path) => Savable.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string BackupPath(string image) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image)) ?? "",
                     Path.GetFileNameWithoutExtension(image) + " (" + Tr.T("před úpravou") + ")" + Path.GetExtension(image));

    /// <summary>Kopie původního obrazu — jen jednou, další úpravy ji nepřepíšou. Vrací cestu, když ji teď vytvořil.</summary>
    public static string? KeepOriginal(string image)
    {
        string b = BackupPath(image);
        if (File.Exists(b)) return null;
        File.Copy(image, b);
        return b;
    }

    /// <summary>
    /// Přetažené soubory a složky (složka i s obsahem) → seznam co kam. Jména se zkrátí na 8.3; když by dvě
    /// dlouhá jména dala stejné krátké, druhé dostane „~2“ (jako ve Windows).
    /// </summary>
    public static List<EditItem> Plan(IEnumerable<string> dropped, string? intoDir)
    {
        var items = new List<EditItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = string.IsNullOrEmpty(intoDir) ? "" : intoDir!.TrimEnd('\\') + "\\";

        string Unique(string dir, string name)
        {
            string s = Fat12Writer.ShortNameText(name);
            if (used.Add(dir + s)) return s;
            string stem = Fat12Writer.ShortNameText(Path.GetFileNameWithoutExtension(name)), ext = Path.GetExtension(s);
            for (int n = 2; ; n++)
            {
                string tail = "~" + n;
                string cand = stem.Substring(0, Math.Min(stem.Length, 8 - tail.Length)) + tail + ext;
                if (used.Add(dir + cand)) return cand;
            }
        }

        void AddTree(string folder, string target)
        {
            items.Add(new EditItem(null, target));
            foreach (var d in Directory.GetDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AddTree(d, target + "\\" + Unique(target + "\\", Path.GetFileName(d)));
            foreach (var f in Directory.GetFiles(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                items.Add(new EditItem(f, target + "\\" + Unique(target + "\\", Path.GetFileName(f))));
        }

        foreach (var p in dropped)
        {
            string name = Path.GetFileName(p.TrimEnd('\\', '/'));
            if (Directory.Exists(p)) AddTree(p, prefix + Unique(prefix, name));
            else if (File.Exists(p)) items.Add(new EditItem(p, prefix + Unique(prefix, name)));
        }
        return items;
    }

    /// <summary>Které cíle už na disketě jsou (soubory, ne složky — do existující složky se jen přidává).</summary>
    public static List<string> Existing(DiskImage img, IEnumerable<EditItem> items)
    {
        var w = new Fat12Writer((byte[])img.Data.Clone());
        return items.Where(i => i.Source != null && w.Exists(i.Target)).Select(i => i.Target).ToList();
    }

    /// <summary>Zapíše soubory; replace = co už na disketě je, nahradit (jinak to přeskočí).</summary>
    public static void AddFiles(Fat12Writer w, IEnumerable<EditItem> items, bool replace)
    {
        foreach (var i in items)
        {
            if (i.Source == null) { w.CreateDirectory(i.Target); continue; }
            if (w.Exists(i.Target))
            {
                if (!replace) continue;
                w.Delete(i.Target);
            }
            w.AddFile(i.Target, File.ReadAllBytes(i.Source), File.GetLastWriteTime(i.Source));
        }
    }

    /// <summary>
    /// Provede změnu na kopii dat; když projde (místo, plný adresář…), zazálohuje původní obraz a uloží nový.
    /// Při chybě zůstane soubor beze změny.
    /// </summary>
    public static (DiskImage Image, string? Backup) Apply(string path, DiskImage img, Action<Fat12Writer> change)
    {
        var data = (byte[])img.Data.Clone();
        change(new Fat12Writer(data));
        string? backup = KeepOriginal(path);
        var result = new DiskImage(img.Geometry, data);
        ImageFile.Save(result, path);
        return (result, backup);
    }
}
