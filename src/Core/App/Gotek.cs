using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Pro jaký počítač je Gotek — podle toho se píše FF.CFG.</summary>
public enum GotekMachine { AtariSt, Amiga, Pc, Msx, Other }

public sealed class GotekResult
{
    /// <summary>Zkopírované obrazy: „007  DSKA0007_hra.st  ← hra.msa“.</summary>
    public List<string> Copied { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>Všechny číslované obrazy na flashce (i dřívější), v pořadí pozic.</summary>
    public List<string> Slots { get; } = new();
    public string? FfCfg { get; set; }
    public string? ImgCfg { get; set; }
    public string? SlotList { get; set; }
}

/// <summary>
/// USB flashka pro Gotek s firmwarem FlashFloppy: obrazy jako DSKA0000_název.ext (číslo = pozice na displeji),
/// převod formátů, které FlashFloppy nečte (.MSA, .DIM → .ST), FF.CFG podle počítače a IMG.CFG pro obrazy,
/// jejichž geometrii by FlashFloppy podle velikosti souboru odhadl špatně. Pravidla podle zdrojů FlashFloppy
/// (github.com/keirf/flashfloppy, src/image/img.c a wiki).
/// </summary>
public static class Gotek
{
    public const string Prefix = "DSKA";

    // FlashFloppy img_type[]: sektorů, stran, třída počtu stop (40 = 38–42, 80 = 77–85); vždy 512 B na sektor.
    private static readonly (int secs, int sides, int cyls)[] FfTypes =
    {
        (8, 1, 40), (9, 1, 40), (10, 1, 40), (8, 2, 40), (9, 2, 40), (10, 2, 40), (15, 2, 80),
        (9, 1, 80), (10, 1, 80), (11, 1, 80), (8, 2, 80), (9, 2, 80), (10, 2, 80), (11, 2, 80),
        (18, 2, 80), (19, 2, 80), (21, 2, 80), (20, 2, 80), (36, 2, 80),
    };

    /// <summary>
    /// Jakou geometrii FlashFloppy odvodí z velikosti souboru (.st jen 80stopé typy, .img všechny) — první shoda
    /// v pořadí jeho tabulky, jako raw_type_open(). null = velikost nezná.
    /// </summary>
    public static DiskGeometry? FlashFloppyGeometry(long size, bool st)
    {
        foreach (var (secs, sides, cyls) in FfTypes)
        {
            if (st && cyls != 80) continue;
            int min = cyls == 40 ? 38 : 77, max = cyls == 40 ? 42 : 85;
            long perCyl = secs * 512L * sides;
            for (int c = min; c <= max; c++)
                if (c * perCyl == size) return new DiskGeometry(c, sides, secs);
        }
        return null;
    }

    /// <summary>
    /// Název pro FAT a displej Goteku: bez diakritiky, jen bezpečné znaky, nejvýš 40 znaků. Bez teček — FlashFloppy
    /// by část za tečkou bral jako značku pro IMG.CFG.
    /// </summary>
    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch < 128 && (char.IsLetterOrDigit(ch) || " _-()+,!&'".IndexOf(ch) >= 0) ? ch : '_');
        }
        string s = Regex.Replace(sb.ToString(), "_{2,}", "_").Trim(' ', '_', '.');
        if (s.Length > 40) s = s.Substring(0, 40).TrimEnd(' ', '_', '.');
        return s.Length > 0 ? s : "disk";
    }

    private static readonly Regex SlotFile = new(@"^DSKA(\d{4})", RegexOptions.IgnoreCase);

    /// <summary>Číslované obrazy, které už na flashce jsou (číslo, název), seřazené.</summary>
    public static List<(int slot, string name)> ExistingSlots(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root).Select(Path.GetFileName)
                .Select(n => (m: SlotFile.Match(n!), n: n!)).Where(x => x.m.Success)
                .Select(x => (int.Parse(x.m.Groups[1].Value), x.n)).OrderBy(x => x.Item1).ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<(int, string)>();

    public static int NextSlot(string root) => ExistingSlots(root).Select(s => s.slot + 1).DefaultIfEmpty(0).Max();

    /// <summary>Řádky FF.CFG pro daný počítač (prázdné = nic měnit netřeba).</summary>
    public static List<string> FfCfgLines(GotekMachine machine, bool indexed)
    {
        var l = new List<string>();
        if (indexed) l.Add("nav-mode = indexed");
        if (machine == GotekMachine.Pc) l.Add("interface = ibmpc");
        if (machine == GotekMachine.Msx) l.Add("host = msx");
        return l;
    }

    /// <summary>Zkopíruje obrazy (i z .zip) na flashku jako další volné pozice a připraví konfiguraci.</summary>
    public static GotekResult Copy(string root, IList<string> files, GotekMachine machine, bool indexed, Drives.ProgressHandler? progress = null)
    {
        var r = new GotekResult();
        if (!Directory.Exists(root)) throw new Drives.OperationException(Tr.F("Složka {0} neexistuje.", root));

        // položky: soubory a obrazy uvnitř .zip
        var items = new List<(string path, string shown, string stem)>();
        foreach (var f in files)
        {
            if (ZipImages.IsZip(f))
            {
                try
                {
                    var names = ZipImages.List(f);
                    if (names.Count == 0) r.Skipped.Add(Tr.F("{0}: v archivu není žádný obraz diskety.", Path.GetFileName(f)));
                    foreach (var n in names)
                        items.Add((ZipImages.Extract(f, n), Path.GetFileName(f) + " → " + n, Path.GetFileNameWithoutExtension(n)));
                }
                catch (Drives.OperationException e) { r.Skipped.Add(e.Message); }
            }
            else items.Add((f, Path.GetFileName(f), Path.GetFileNameWithoutExtension(f)));
        }

        int slot = NextSlot(root), done = 0;
        var imgCfg = new List<string>();
        var tags = new HashSet<string>();
        bool hdAdf = false;
        foreach (var (path, shown, stem) in items)
        {
            progress?.Invoke(done++, items.Count, shown);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] data;
            string outExt;
            string? note = null;
            // .adf, .hfe a .d81 čte FlashFloppy sám (ADF i HD 1760 KB) — beze změny
            if (ext is ".adf" or ".hfe" or ".d81")
            {
                data = File.ReadAllBytes(path);
                outExt = ext;
                hdAdf |= ext == ".adf" && data.Length > 901120 * 3 / 2;
            }
            else if (ext is ".scp" or ".stx" or ".ipf" or ".raw")
            {
                r.Skipped.Add(Tr.F("{0}: formát {1} Gotek (FlashFloppy) nečte — vynecháno.", shown, ext.ToUpperInvariant()));
                continue;
            }
            else
            {
                LoadedImage loaded;
                try { loaded = ImageFile.Load(path); }
                catch (Exception e) when (e is IOException or FormatException or InvalidDataException) { r.Skipped.Add(shown + ": " + e.Message); continue; }
                if (loaded.Kind == ImageKind.CpcDsk) { data = File.ReadAllBytes(path); outExt = ".dsk"; }
                else if (loaded.Image == null) { r.Skipped.Add(shown + ": " + (loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."))); continue; }
                else
                {
                    var img = loaded.Image;
                    bool raw = loaded.Kind is ImageKind.St or ImageKind.RawImg or ImageKind.Adf;
                    data = raw ? File.ReadAllBytes(path) : StFormat.Save(img);
                    if (!raw) note = Tr.F("převedeno z {0} na .ST (Gotek {0} nečte)", ext.ToUpperInvariant());
                    if (FileSystemDetector.Detect(img.Data).Kind == FsKind.Amiga) outExt = ".adf";
                    else
                    {
                        outExt = ext is ".st" or ".msa" or ".dim" ? ".st" : ".img";
                        if (!FlashFloppyKnows(img.Geometry, data.Length, outExt == ".st", machine))
                        {
                            var g = img.Geometry;
                            string tag = $"dtk{g.Tracks}x{g.Sides}x{g.SectorsPerTrack}" + (g.BytesPerSector != 512 ? "x" + g.BytesPerSector : "");
                            outExt = "." + tag + ".img";
                            if (tags.Add(tag + "::" + data.Length))
                                imgCfg.AddRange(new[] { $"[{tag}::{data.Length}]", $"cyls = {g.Tracks}", $"heads = {g.Sides}", $"secs = {g.SectorsPerTrack}", $"bps = {g.BytesPerSector}", "" });
                            note = (note != null ? note + "; " : "") + Tr.F("geometrii {0} by Gotek podle velikosti souboru nepoznal — popsaná v IMG.CFG", g);
                        }
                    }
                }
            }
            string name = $"{Prefix}{slot:D4}_{SafeName(stem)}{outExt}";
            File.WriteAllBytes(Path.Combine(root, name), data);
            r.Copied.Add($"{slot:D3}  {name}  ← {shown}" + (note != null ? $"  ({note})" : ""));
            slot++;
        }
        progress?.Invoke(items.Count, items.Count, "");

        // konfigurace: když existuje složka FF\, FlashFloppy hledá FF.CFG a IMG.CFG jen v ní
        string cfgDir = Directory.Exists(Path.Combine(root, "FF")) ? Path.Combine(root, "FF") : root;
        var ff = FfCfgLines(machine, indexed);
        string ffPath = Path.Combine(cfgDir, "FF.CFG");
        if (ff.Count > 0)
        {
            if (File.Exists(ffPath))
                r.Notes.Add(Tr.F("FF.CFG už na flashce je — nechal jsem ho beze změny. Pro tento počítač do něj patří: {0}", string.Join("; ", ff)));
            else
            {
                File.WriteAllText(ffPath, "# Diskette Toolkit\r\n" + string.Join("\r\n", ff) + "\r\n");
                r.FfCfg = ffPath;
            }
        }
        if (imgCfg.Count > 0)
        {
            string imgPath = Path.Combine(cfgDir, "IMG.CFG");
            File.AppendAllText(imgPath, (File.Exists(imgPath) ? "\r\n" : "") + "# Diskette Toolkit\r\n" + string.Join("\r\n", imgCfg));
            r.ImgCfg = imgPath;
        }
        if (File.Exists(Path.Combine(root, "HXCSDFE.CFG")))
            r.Notes.Add(Tr.T("Na flashce je HXCSDFE.CFG — Gotek může běžet v režimu kompatibility s HxC, kde pozice určuje výběr v programu na počítači (AUTOBOOT.HFE), ne čísla v názvech. Po kopírování v něm obrazy vyber znovu."));
        if (hdAdf)
            r.Notes.Add(Tr.T("HD obraz Amigy (1760 KB) Gotek přečte jen s interface = amiga v FF.CFG a zapnutou identifikací HD mechaniky — viz wiki FlashFloppy, stránka Host Platforms."));
        if (machine == GotekMachine.Other)
            r.Notes.Add(Tr.T("Pro jiné počítače bývá potřeba vlastní nastavení (host = …, interface = …) — viz wiki FlashFloppy, stránka Host Platforms."));

        foreach (var (s, n) in ExistingSlots(root)) r.Slots.Add($"{s:D3}  {n}");
        r.SlotList = Path.Combine(root, Tr.T("seznam-pozic.txt"));
        File.WriteAllText(r.SlotList, Tr.T("Obrazy na flashce pro Gotek (pozice = číslo na displeji)") + "\r\n\r\n" + string.Join("\r\n", r.Slots) + "\r\n", new UTF8Encoding(true));
        return r;
    }

    private static bool FlashFloppyKnows(DiskGeometry g, long size, bool st, GotekMachine machine)
    {
        if (g.BytesPerSector != 512) return false;
        // host = msx: FlashFloppy bere geometrii z boot sektoru (8 nebo 9 sektorů na stopu)
        if (machine == GotekMachine.Msx && !st && g.SectorsPerTrack is 8 or 9) return true;
        var ff = FlashFloppyGeometry(size, st);
        return ff != null && ff.Tracks == g.Tracks && ff.Sides == g.Sides && ff.SectorsPerTrack == g.SectorsPerTrack;
    }
}
