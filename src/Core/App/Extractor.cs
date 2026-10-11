using System.IO;
using System.Linq;
using System.Text;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Rozbalení souborů z obrazu (FAT12 nebo AmigaDOS) do složky.</summary>
public static class Extractor
{
    /// <summary>Vrací počet rozbalených souborů.</summary>
    public static int Extract(DiskImage img, string folder)
    {
        var amiga = AmigaDos.TryOpen(img.Data);
        int files = 0;
        if (amiga != null)
        {
            foreach (var e in amiga.ListAll())
            {
                string target = Path.Combine(folder, Path.Combine(e.Path.Split('/').Select(SafeName).ToArray()));
                if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, amiga.ReadFile(e));
                if (e.Modified != null) File.SetLastWriteTime(target, e.Modified.Value);
                files++;
            }
            return files;
        }
        var fat = Fat12Volume.TryOpen(img.Data, out string? err) ?? throw new Drives.OperationException(err ?? Tr.T("Image nemá čitelný souborový systém."));
        foreach (var e in fat.ListAll())
        {
            string target = Path.Combine(folder, Path.Combine(e.Path.Split('\\').Select(SafeName).ToArray()));
            if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, fat.ReadFile(e));
            if (e.Modified != null) File.SetLastWriteTime(target, e.Modified.Value);
            files++;
        }
        return files;
    }

    /// <summary>Jména z Amigy i poškozených FAT mohou obsahovat znaky, které Windows nedovolí.</summary>
    public static string SafeName(string n)
    {
        var sb = new StringBuilder();
        foreach (char c in n) sb.Append("<>:\"|?*\\/".IndexOf(c) >= 0 || c < 0x20 ? '_' : c);
        string r = sb.ToString().TrimEnd('.', ' ');
        return r.Length == 0 ? "_" : r;
    }
}
