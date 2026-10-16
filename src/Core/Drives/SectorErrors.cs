using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

/// <summary>Srozumitelný důvod chyby sektoru podle kódu, který vrátí Windows / fdrawcmd.sys (jako kódy chyb v X-Copy).</summary>
public static class SectorErrors
{
    public static string Explain(SectorIOException e) => e.Win32Error switch
    {
        23 => Tr.T("chyba kontrolního součtu dat (CRC) — data sektoru jsou poškozená"),
        27 => Tr.T("sektor nenalezen — chybí nebo je poškozená jeho hlavička"),
        1122 => Tr.T("chybí značka hlavičky sektoru (stopa nenaformátovaná nebo poškozená)"),
        1123 => Tr.T("hlavička sektoru uvádí jinou stopu — hlava nedojela správně, nebo jde o ochranu proti kopírování"),
        30 => Tr.T("chyba čtení (mechanika nehlásí podrobnosti)"),
        29 => Tr.T("chyba zápisu"),
        19 => Tr.T("disketa je chráněná proti zápisu"),
        21 or 1112 => Tr.T("mechanika není připravená — chybí disketa?"),
        _ => e.Message,
    };
}
