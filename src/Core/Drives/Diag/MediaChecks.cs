using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Diag;

/// <summary>Vyhodnocení stavu diskety z výsledků čtení.</summary>
public static class MediaChecks
{
    /// <summary>
    /// Poplach „disketa se rozpadá“: při opakovaném čtení chyb přibývá (průměr posledních tří čtení je
    /// výrazně horší než prvních tří). Magnetická vrstva se nejspíš odlupuje — další čtení ničí disketu i hlavu.
    /// </summary>
    public static bool IsDeteriorating(IReadOnlyList<int> failsPerPass)
    {
        if (failsPerPass.Count < 5) return false;
        double first = failsPerPass.Take(3).Average();
        double last = failsPerPass.Skip(failsPerPass.Count - 3).Average();
        bool rising = failsPerPass[failsPerPass.Count - 1] >= failsPerPass[failsPerPass.Count - 3];
        return last >= first + 2 && last >= 2 && rising;
    }

    /// <summary>
    /// Hlasování po bitech z několika vadných kopií sektoru (jako SpinRite): každý bit má hodnotu, kterou má
    /// většina kopií. uncertain = počet bitů, kde se kopie skoro rovnoměrně přou (40–60 %).
    /// Výsledek NENÍ ověřený kontrolním součtem — je to nejpravděpodobnější obsah.
    /// </summary>
    public static (byte[] data, int uncertain) Vote(IReadOnlyList<byte[]> copies)
    {
        int len = copies[0].Length, n = copies.Count, uncertain = 0;
        var result = new byte[len];
        for (int i = 0; i < len; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                int ones = 0;
                foreach (var c in copies) if ((c[i] >> bit & 1) != 0) ones++;
                if (ones * 2 > n) result[i] |= (byte)(1 << bit);
                if (ones * 10 >= n * 4 && ones * 10 <= n * 6) uncertain++;
            }
        return (result, uncertain);
    }

    /// <summary>Známka diskety po zápisovém testu: A = bez chyb, B = jen pár slabých míst, C = nespolehlivá.</summary>
    public static (char grade, string text) Grade(int total, int weak, int bad)
    {
        if (bad > 0) return ('C', Tr.T("C — má místa, kam nejde spolehlivě zapsat. Na data ji nepoužívej."));
        if (weak == 0) return ('A', Tr.T("A — čistá, vhodná i na zálohy a archiv."));
        if (weak <= Math.Max(1, total / 100)) return ('B', Tr.T("B — pár slabých míst. Na hry a pokusy ano, na archiv ne."));
        return ('C', Tr.T("C — hodně slabých míst. Na data ji nepoužívej."));
    }
}
