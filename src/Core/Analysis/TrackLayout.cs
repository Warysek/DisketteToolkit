using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.Drives.Fdraw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Analysis;

/// <summary>
/// Rozložení jedné stopy podle hlaviček sektorů (jako AnaDisk / SAMdisk scan): pořadí, velikosti, poloha
/// od indexu a odchylky od běžného formátu — duplicitní čísla, cizí stopa v hlavičce, chybějící sektory.
/// </summary>
public sealed class TrackLayout
{
    public int Cyl { get; }
    public int Head { get; }
    public int? TrackTimeUs { get; }
    public IReadOnlyList<(int? Us, SectorId Id)> Sectors { get; }
    /// <summary>Odchylky, které běžný formát nemá (prázdné = standardní stopa).</summary>
    public List<string> Problems { get; } = new();

    public TrackLayout(int cyl, int head, int? trackTimeUs, IReadOnlyList<(int? Us, SectorId Id)> sectors, int expectedCount = 0)
    {
        Cyl = cyl;
        Head = head;
        TrackTimeUs = trackTimeUs;
        Sectors = sectors;
        Analyze(expectedCount);
    }

    public bool IsStandard => Problems.Count == 0;

    private void Analyze(int expected)
    {
        if (Sectors.Count == 0)
        {
            Problems.Add(Tr.T("žádné MFM sektory — nenaformátovaná stopa, nebo jiný záznam (Amiga, Mac, ochrana)"));
            return;
        }
        var ids = Sectors.Select(s => s.Id).ToList();
        foreach (var g in ids.GroupBy(i => i.Sector).Where(g => g.Count() > 1))
            Problems.Add(Tr.F("sektor {0} je na stopě {1}×", g.Key, g.Count()));
        var badCyl = ids.Where(i => i.Cyl != Cyl).Select(i => i.Cyl).Distinct().ToList();
        if (badCyl.Count > 0) Problems.Add(Tr.F("hlavička sektoru uvádí jinou stopu ({0})", string.Join(", ", badCyl)));
        var badHead = ids.Where(i => i.Head != Head).Select(i => i.Head).Distinct().ToList();
        if (badHead.Count > 0) Problems.Add(Tr.F("hlavička sektoru uvádí jinou stranu ({0})", string.Join(", ", badHead)));
        var sizes = ids.Select(i => i.Length).Distinct().ToList();
        if (sizes.Count > 1) Problems.Add(Tr.F("různé velikosti sektorů ({0} B)", string.Join(" / ", sizes)));
        if (ids.Any(i => i.SizeCode > 6)) Problems.Add(Tr.T("nesmyslná velikost sektoru v hlavičce"));

        var numbers = ids.Where(i => i.Sector != FdrawDevice.SpareSectorId).Select(i => (int)i.Sector).Distinct().OrderBy(x => x).ToList();
        if (numbers.Count > 0)
        {
            var missing = Enumerable.Range(numbers[0], numbers[numbers.Count - 1] - numbers[0] + 1).Except(numbers).ToList();
            if (missing.Count > 0) Problems.Add(Tr.F("chybí sektory {0}", string.Join(", ", missing)));
        }
        if (expected > 0 && numbers.Count != expected && Problems.Count == 0)
            Problems.Add(Tr.F("{0} sektorů místo {1} jako na ostatních stopách", numbers.Count, expected));
    }

    /// <summary>Jeden řádek pro výpis: „0.0: 9 × 512 B, pořadí 1 2 3 …, první sektor 3,1 ms za indexem“.</summary>
    public string Describe()
    {
        string head = $"{Cyl,2}.{Head}: ";
        if (Sectors.Count == 0) return head + Problems[0];
        var sizes = Sectors.Select(s => s.Id.Length).Distinct().ToList();
        string text = Tr.F("{0} × {1} B, pořadí {2}", Sectors.Count, sizes.Count == 1 ? sizes[0].ToString() : "?",
                           string.Join(" ", Sectors.Select(s => s.Id.Sector == FdrawDevice.SpareSectorId ? "F0*" : s.Id.Sector.ToString())));
        if (Sectors[0].Us is int us) text += Tr.F(", první sektor {0:0.0} ms za indexem", us / 1000.0);
        if (Sectors.Any(s => s.Id.Sector == FdrawDevice.SpareSectorId)) text += Tr.T(" (* sektor navíc z opravného formátu)");
        if (Problems.Count > 0) text += " — " + string.Join("; ", Problems);
        return head + text;
    }
}
