using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Analysis;

/// <summary>
/// Histogram časů mezi magnetickými změnami na stopě (jako v KryoFlux / FluxEngine). Zdravá stopa má
/// ostré špičky (u DD diskety MFM kolem 4, 6 a 8 µs, u HD kolem 2, 3 a 4 µs); rozmazané špičky znamenají
/// slábnoucí záznam, špinavou hlavu nebo kolísající otáčky. Hodnocení je orientační.
/// </summary>
public sealed class FluxHistogram
{
    public const double BinUs = 0.05;
    public const double MaxUs = 12.0;

    public int[] Bins { get; } = new int[(int)(MaxUs / BinUs)];
    public int Samples { get; private set; }
    /// <summary>Polohy špiček v µs (seřazené).</summary>
    public List<double> Peaks { get; } = new();
    /// <summary>Průměrná odchylka vzorků od nejbližší špičky v ns (čím menší, tím ostřejší).</summary>
    public double SpreadNs { get; private set; }
    /// <summary>Podíl vzorků daleko od všech špiček (šum, poškozená místa).</summary>
    public double OutlierShare { get; private set; }

    public static FluxHistogram Build(IReadOnlyList<double> fluxUs)
    {
        var h = new FluxHistogram();
        foreach (double us in fluxUs)
        {
            int i = (int)(us / BinUs);
            if (i >= 0 && i < h.Bins.Length) h.Bins[i]++;
        }
        h.Samples = fluxUs.Count;
        h.FindPeaks();
        if (h.Peaks.Count > 0)
        {
            double sum = 0;
            int near = 0, far = 0;
            foreach (double us in fluxUs)
            {
                double d = h.Peaks.Min(p => Math.Abs(p - us));
                if (d <= 0.5) { sum += d * d; near++; }
                else far++;
            }
            h.SpreadNs = near > 0 ? Math.Sqrt(sum / near) * 1000 : 0;
            h.OutlierShare = fluxUs.Count > 0 ? (double)far / fluxUs.Count : 0;
        }
        return h;
    }

    private void FindPeaks()
    {
        // vyhlazení přes 5 košů, pak lokální maxima nad 8 % nejvyššího
        var s = new double[Bins.Length];
        for (int i = 0; i < Bins.Length; i++)
        {
            double sum = 0; int n = 0;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(Bins.Length - 1, i + 2); k++) { sum += Bins[k]; n++; }
            s[i] = sum / n;
        }
        double max = s.Max();
        if (max <= 0) return;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] < max * 0.08 || s[i] < s[i - 1] || s[i] < s[i + 1]) continue;
            double pos = (i + 0.5) * BinUs;
            if (Peaks.Count > 0 && pos - Peaks[Peaks.Count - 1] < 0.6) continue;   // stejná špička (plochý vrchol)
            Peaks.Add(pos);
        }
    }

    public enum Quality { Sharp, Fair, Blurred, Empty }

    public Quality Grade => Samples < 1000 || Peaks.Count == 0 ? Quality.Empty
        : SpreadNs <= 130 && OutlierShare < 0.01 ? Quality.Sharp
        : SpreadNs <= 230 && OutlierShare < 0.05 ? Quality.Fair
        : Quality.Blurred;

    /// <summary>Odhad zápisu podle polohy první (nejkratší) špičky.</summary>
    public string Encoding
    {
        get
        {
            if (Peaks.Count == 0) return Tr.T("nic — stopa je prázdná nebo smazaná");
            double first = Peaks[0];
            if (first > 3.4 && first < 4.6 && Peaks.Count >= 2) return Tr.T("MFM, dvojitá hustota (DD) — PC, Atari ST, Amiga, MSX…");
            if (first > 1.6 && first < 2.4 && Peaks.Count >= 2 && Peaks.Count <= 3 && Peaks[1] < 3.4) return Tr.T("MFM, vysoká hustota (HD)");
            if (first > 1.6 && first < 2.4) return Tr.T("jiný zápis (např. GCR Mac / Commodore) nebo HD");
            if (first > 7 && Peaks.Count <= 2) return Tr.T("FM, jednoduchá hustota (SD)");
            return Tr.T("neobvyklý zápis — možná ochrana proti kopírování nebo jiný počítač");
        }
    }

    public string Verdict => Grade switch
    {
        Quality.Sharp => Tr.T("ostré špičky — zdravá stopa"),
        Quality.Fair => Tr.T("trochu rozmazané špičky — stopa se čte, ale slábne"),
        Quality.Blurred => Tr.T("rozmazané špičky — slábnoucí záznam, špinavá hlava nebo kolísající otáčky"),
        _ => Tr.T("na stopě skoro nic není — nenaformátovaná, smazaná nebo poškozená"),
    };
}
