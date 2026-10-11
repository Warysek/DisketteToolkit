using System;
using System.IO;
using System.Linq;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Boot;

/// <summary>
/// Spouštěcí disketa MS-DOS z Windows. Windows XP až 10 mají v knihovně diskcopy.dll uložený obraz
/// spouštěcí diskety (MS-DOS z Windows ME) — z něj vytvářely „spouštěcí disketu MS-DOS“ při formátování.
/// Použijeme stejný obraz z Windows v tomto počítači; nic se nestahuje ani nepřibaluje.
/// </summary>
public static class WindowsMsDos
{
    public const string ResourceType = "BINFILE";
    public const int ResourceId = 1;

    /// <summary>diskcopy.dll vybraná uživatelem (např. zkopírovaná ze starších Windows, ke kterým má licenci).</summary>
    public static string? CustomPath { get; set; }

    public static string[] Candidates()
    {
        string win = CustomPath ?? Environment.GetEnvironmentVariable("AFLOPPY_DISKCOPY") ?? "";
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new[]
        {
            win,
            Path.Combine(sys, "diskcopy.dll"),
            Path.Combine(windir, "Sysnative", "diskcopy.dll"),
            Path.Combine(windir, "SysWOW64", "diskcopy.dll"),
        }.Where(p => p.Length > 0).ToArray();
    }

    /// <summary>Je obraz v těchto Windows k dispozici? Když ne, vrací důvod.</summary>
    public static bool IsAvailable(out string reason)
    {
        try
        {
            Load();
            reason = "";
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    public static DiskImage Load()
    {
        string? file = Candidates().FirstOrDefault(File.Exists);
        if (file == null)
            throw new OperationException(Tr.T("V těchto Windows není diskcopy.dll s obrazem spouštěcí diskety MS-DOS (Windows 11 ho už nemusí mít). Použij FreeDOS."));
        byte[]? data = PeResources.Find(File.ReadAllBytes(file), ResourceType, ResourceId);
        if (data == null)
            throw new OperationException(Tr.T("diskcopy.dll v těchto Windows neobsahuje obraz spouštěcí diskety MS-DOS. Použij FreeDOS."));
        var g = DiskGeometry.Hd1440;
        if (data.Length < 32 * 512 || data.Length > g.SizeBytes)
            throw new OperationException(Tr.F("Obraz v diskcopy.dll má nečekanou velikost ({0} B).", data.Length));
        var full = new byte[g.SizeBytes];
        Buffer.BlockCopy(data, 0, full, 0, data.Length);
        var img = new DiskImage(g, full);
        var fs = Fat12Volume.TryOpen(full, out _) ?? throw new OperationException(Tr.T("Obraz v diskcopy.dll nemá platný souborový systém."));
        if (!fs.ListAll().Any(e => e.Name.Equals("IO.SYS", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("COMMAND.COM", StringComparison.OrdinalIgnoreCase)))
            throw new OperationException(Tr.T("Obraz v diskcopy.dll neobsahuje systémové soubory MS-DOS."));
        return img;
    }
}

/// <summary>Čtení prostředků (resources) ze souboru EXE/DLL bez načítání knihovny — funguje i mimo Windows.</summary>
public static class PeResources
{
    /// <summary>Najde prostředek daného typu (jméno) a čísla; vrací jeho data nebo null.</summary>
    public static byte[]? Find(byte[] pe, string type, int id)
    {
        try
        {
            if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') return null;
            int peOff = BitConverter.ToInt32(pe, 0x3C);
            if (peOff <= 0 || peOff + 24 > pe.Length || BitConverter.ToUInt32(pe, peOff) != 0x00004550) return null;
            int sections = BitConverter.ToUInt16(pe, peOff + 6);
            int optSize = BitConverter.ToUInt16(pe, peOff + 20);
            int opt = peOff + 24;
            ushort magic = BitConverter.ToUInt16(pe, opt);
            int dataDir = opt + (magic == 0x20B ? 112 : 96);          // PE32+ / PE32
            int resRva = BitConverter.ToInt32(pe, dataDir + 2 * 8);   // IMAGE_DIRECTORY_ENTRY_RESOURCE
            if (resRva == 0) return null;
            int secTable = opt + optSize;

            int RvaToOffset(int rva)
            {
                for (int i = 0; i < sections; i++)
                {
                    int s = secTable + i * 40;
                    int va = BitConverter.ToInt32(pe, s + 12), vsize = BitConverter.ToInt32(pe, s + 8);
                    int raw = BitConverter.ToInt32(pe, s + 20), rawSize = BitConverter.ToInt32(pe, s + 16);
                    if (rva >= va && rva < va + Math.Max(vsize, rawSize)) return rva - va + raw;
                }
                return -1;
            }

            int root = RvaToOffset(resRva);
            if (root < 0) return null;

            // úroveň 1: typ (jméno), úroveň 2: číslo, úroveň 3: jazyk (první)
            int? typeDir = FindChild(pe, root, root, name: type, id: null);
            if (typeDir == null) return null;
            int? idDir = FindChild(pe, root, typeDir.Value, name: null, id: id);
            if (idDir == null) return null;
            int langEntries = BitConverter.ToUInt16(pe, idDir.Value + 12) + BitConverter.ToUInt16(pe, idDir.Value + 14);
            if (langEntries == 0) return null;
            int entry = idDir.Value + 16;
            uint off = BitConverter.ToUInt32(pe, entry + 4);
            if ((off & 0x80000000) != 0) return null;
            int dataEntry = root + (int)off;
            int dataRva = BitConverter.ToInt32(pe, dataEntry), size = BitConverter.ToInt32(pe, dataEntry + 4);
            int dataOff = RvaToOffset(dataRva);
            if (dataOff < 0 || size <= 0 || dataOff + size > pe.Length) return null;
            var result = new byte[size];
            Buffer.BlockCopy(pe, dataOff, result, 0, size);
            return result;
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>V adresáři prostředků najde podadresář podle jména nebo čísla; vrací jeho offset.</summary>
    private static int? FindChild(byte[] pe, int root, int dir, string? name, int? id)
    {
        int named = BitConverter.ToUInt16(pe, dir + 12), ids = BitConverter.ToUInt16(pe, dir + 14);
        for (int i = 0; i < named + ids; i++)
        {
            int e = dir + 16 + i * 8;
            uint n = BitConverter.ToUInt32(pe, e), off = BitConverter.ToUInt32(pe, e + 4);
            if ((off & 0x80000000) == 0) continue;
            bool match;
            if ((n & 0x80000000) != 0)
            {
                if (name == null) continue;
                int so = root + (int)(n & 0x7FFFFFFF);
                int len = BitConverter.ToUInt16(pe, so);
                match = string.Equals(System.Text.Encoding.Unicode.GetString(pe, so + 2, len * 2), name, StringComparison.OrdinalIgnoreCase);
            }
            else match = id != null && n == id.Value;
            if (match) return root + (int)(off & 0x7FFFFFFF);
        }
        return null;
    }
}
