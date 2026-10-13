using System;
using System.Collections.Generic;
using System.Linq;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Diag;

/// <summary>
/// Otáčky mechaniky z naměřených dob otáčky. Cíl 300 ot./min (3,5", 5,25" DD) nebo 360 (5,25" HD) se
/// pozná podle průměru. Hodnocení je orientační: norma mechanik připouští ±1,5 % (±4,5 ot./min při 300),
/// seřizovat se vyplatí už od ±1,5 ot./min; kolísání do ~0,5 ot./min znamená zdravý řemínek a ložiska.
/// </summary>
public sealed class RpmStats
{
    private readonly List<double> _ms = new();
    /// <summary>Kolik posledních otáček brát do živého měření (starší zahodí, aby šlo sledovat seřizování).</summary>
    public int Window { get; }

    public RpmStats(int window = 0) => Window = window;

    public void Add(double revolutionMs)
    {
        if (revolutionMs < 100 || revolutionMs > 400) return;   // nesmysl (chybějící index, šum)
        _ms.Add(revolutionMs);
        if (Window > 0 && _ms.Count > Window) _ms.RemoveAt(0);
    }

    public int Count => _ms.Count;
    public double Rpm => _ms.Count == 0 ? 0 : 60000.0 / _ms.Average();
    public double MinRpm => _ms.Count == 0 ? 0 : 60000.0 / _ms.Max();
    public double MaxRpm => _ms.Count == 0 ? 0 : 60000.0 / _ms.Min();
    /// <summary>Kolísání: polovina rozdílu nejrychlejší a nejpomalejší otáčky (±).</summary>
    public double Wobble => (MaxRpm - MinRpm) / 2;
    public int Target => Rpm > 330 ? 360 : 300;
    public double Deviation => Rpm - Target;

    public enum Grade { Good, Adjust, OutOfSpec }

    public Grade SpeedGrade => Math.Abs(Deviation) <= 1.5 ? Grade.Good : Math.Abs(Deviation) <= Target * 0.015 ? Grade.Adjust : Grade.OutOfSpec;
    public Grade WobbleGrade => Wobble <= 0.5 ? Grade.Good : Wobble <= 1.5 ? Grade.Adjust : Grade.OutOfSpec;

    public string Summary => Tr.F("{0:0.0} ot./min (cíl {1}), rozsah {2:0.0}–{3:0.0}, kolísání ±{4:0.00}, změřeno {5} otáček",
                                  Rpm, Target, MinRpm, MaxRpm, Wobble, Count);

    public string SpeedVerdict => SpeedGrade switch
    {
        Grade.Good => Tr.T("rychlost v pořádku"),
        Grade.Adjust => Deviation > 0 ? Tr.T("trochu rychlá — v normě, ale vyplatí se seřídit") : Tr.T("trochu pomalá — v normě, ale vyplatí se seřídit"),
        _ => Deviation > 0 ? Tr.T("příliš rychlá — mimo normu, seřídit") : Tr.T("příliš pomalá — mimo normu, seřídit (u řemínkových mechanik i vyměnit řemínek)"),
    };

    public string WobbleVerdict => WobbleGrade switch
    {
        Grade.Good => Tr.T("otáčky stálé — řemínek a ložiska v pořádku"),
        Grade.Adjust => Tr.T("otáčky trochu kolísají — sleduj, jestli se to nezhoršuje"),
        _ => Tr.T("otáčky výrazně kolísají — opotřebený řemínek, ložisko nebo motor"),
    };
}
