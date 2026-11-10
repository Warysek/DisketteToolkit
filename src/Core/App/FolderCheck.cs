using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

public enum ImageCheckStatus { Ok, Problems, Unreadable, NotChecked }

/// <summary>Výsledek kontroly jednoho obrazu. Name = cesta vůči kontrolované složce („hry\x.zip → disk1.st“).</summary>
public sealed record ImageCheck(string Name, ImageCheckStatus Status, string Summary, IReadOnlyList<string> Details);

/// <summary>Kontrola celé složky obrazů najednou (i podsložek a obrazů v .zip): které jsou v pořádku a které ne.</summary>
public static class FolderCheck
{
    /// <summary>Obrazy a .zip ve složce i podsložkách; nepřístupnou podsložku přeskočí.</summary>
    public static List<string> FindImages(string folder)
    {
        var list = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 32) return;
            try { list.AddRange(Directory.GetFiles(dir).Where(f => ZipImages.IsImageName(f) || ZipImages.IsZip(f))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            try { foreach (var d in Directory.GetDirectories(dir)) Walk(d, depth + 1); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Walk(folder, 0);
        return list.Where(f => !f.StartsWith(ZipImages.TempRoot, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Zkontroluje soubor; .zip dá jeden výsledek za každý obraz v archivu.</summary>
    public static List<ImageCheck> Check(string file, string root)
    {
        string name = file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Substring(root.Length).TrimStart('\\') : file;
        if (!ZipImages.IsZip(file)) return new List<ImageCheck> { Safe(file, name) };

        var list = new List<ImageCheck>();
        List<string> entries;
        try { entries = ZipImages.List(file); }
        catch (Exception e) when (e is Drives.OperationException or IOException or UnauthorizedAccessException)
        {
            return new List<ImageCheck> { new(name, ImageCheckStatus.Unreadable, e.Message, Array.Empty<string>()) };
        }
        foreach (var entry in entries)
        {
            string shown = name + " → " + entry;
            string? tmp = null;
            try
            {
                tmp = ZipImages.Extract(file, entry);
                list.Add(Safe(tmp, shown));
            }
            catch (Exception e) when (e is Drives.OperationException or IOException or UnauthorizedAccessException)
            {
                list.Add(new ImageCheck(shown, ImageCheckStatus.Unreadable, e.Message, Array.Empty<string>()));
            }
            finally
            {
                if (tmp != null) try { Directory.Delete(Path.GetDirectoryName(tmp)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return list;
    }

    /// <summary>Jeden poškozený soubor nesmí zastavit kontrolu celé složky — cokoli se při čtení pokazí, je výsledek „nejde načíst“.</summary>
    private static ImageCheck Safe(string path, string name)
    {
        try { return CheckImage(path, name); }
        catch (Exception e) { return new ImageCheck(name, ImageCheckStatus.Unreadable, e.Message, Array.Empty<string>()); }
    }

    public static ImageCheck CheckImage(string path, string name)
    {
        LoadedImage loaded;
        try { loaded = ImageFile.Load(path); }
        catch (Exception e) when (e is IOException or FormatException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ImageCheck(name, ImageCheckStatus.Unreadable, e.Message, Array.Empty<string>());
        }
        if (loaded.Image == null)
            return new ImageCheck(name, ImageCheckStatus.NotChecked, ImageFile.Describe(loaded.Kind) + " — " + (loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst.")), Array.Empty<string>());

        var img = loaded.Image;
        var fs = FileSystemDetector.Detect(img.Data);
        string what = $"{img.Geometry}, {fs.Name}";
        if (loaded.Warnings.Count > 0)
            return new ImageCheck(name, ImageCheckStatus.Problems, Tr.F("{0} — obraz není celý", what), loaded.Warnings.ToList());
        if (fs.CanCheck)
        {
            var rep = FatDoctor.Check(img.Data);
            var problems = rep.Issues.Where(i => i.Severity != IssueSeverity.Info).OrderByDescending(i => i.Severity).Select(i => i.Message).ToList();
            return problems.Count == 0
                ? new ImageCheck(name, ImageCheckStatus.Ok, Tr.F("{0}, souborů {1}", what, rep.Files), Array.Empty<string>())
                : new ImageCheck(name, ImageCheckStatus.Problems, Tr.F("{0} — problémů: {1}", what, problems.Count), problems);
        }
        if (fs.Kind == FsKind.Amiga)
        {
            var vol = AmigaDos.TryOpen(img.Data);
            if (vol == null) return new ImageCheck(name, ImageCheckStatus.Problems, Tr.F("{0} — kořenový blok AmigaDOS nejde přečíst", what), Array.Empty<string>());
            try { return new ImageCheck(name, ImageCheckStatus.Ok, Tr.F("{0}, souborů {1} (u Amigy jen výpis, podrobnou kontrolu umím jen u FAT12)", what, vol.ListAll().Count(e => !e.IsDirectory)), Array.Empty<string>()); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                return new ImageCheck(name, ImageCheckStatus.Problems, Tr.F("{0} — seznam souborů je poškozený", what), new[] { e.Message });
            }
        }
        return new ImageCheck(name, ImageCheckStatus.NotChecked, Tr.F("{0} — kontrolu tohoto systému zatím neumím", what), Array.Empty<string>());
    }
}
