using System.Collections.Generic;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

public enum DriveKind
{
    /// <summary>USB disketovka s vlastním řadičem (protokol UFI) — jen standardní formáty.</summary>
    Usb,
    /// <summary>Mechanika na řadiči základní desky (A:/B:).</summary>
    Internal,
    /// <summary>Greaseweazle — řídí obyčejnou mechaniku na úrovni magnetického signálu.</summary>
    Greaseweazle,
}

public enum Feature
{
    Standard720,        // 80×2×9 čtení / zápis / formát
    Standard1440,       // 80×2×18
    ExtendedGeometry,   // 10 sektorů, 81–83 stop
    ElevenSectors,      // 11 sektorů na stopu
    StepRate,
    SimpleProtection,   // nestandardní sektory, CRC chyby, sektory navíc
    FluxProtection,     // weak bits, dlouhé stopy, časování
    WriteStxIpf,        // plný zápis STX / IPF / raw včetně ochran
}

public enum Availability
{
    /// <summary>Funguje v této verzi.</summary>
    Available,
    /// <summary>Hardware to umí, program to doplní v dalších verzích.</summary>
    Planned,
    /// <summary>Hardware to umí, ale chybí ovladač (fdrawcmd.sys).</summary>
    NeedsDriver,
    /// <summary>Hardware to principiálně neumí.</summary>
    Unsupported,
    /// <summary>Závisí na konkrétním řadiči.</summary>
    Maybe,
    /// <summary>Hardware to umí, ale chybí program gw.exe (Greaseweazle Host Tools).</summary>
    NeedsTool,
}

public static class Capabilities
{
    public static string Describe(Feature f) => f switch
    {
        Feature.Standard720 => Tr.T("Standardní 720 KB (80×2×9)"),
        Feature.Standard1440 => Tr.T("Standardní 1,44 MB (80×2×18)"),
        Feature.ExtendedGeometry => Tr.T("10 sektorů, 81–83 stop (800–830 KB)"),
        Feature.ElevenSectors => Tr.T("11 sektorů na stopu"),
        Feature.StepRate => Tr.T("Nastavení step rate"),
        Feature.SimpleProtection => Tr.T("Jednoduché ochrany (CRC, nestandardní sektory)"),
        Feature.FluxProtection => Tr.T("Weak bits, dlouhé stopy, časování"),
        Feature.WriteStxIpf => Tr.T("Plný zápis STX / IPF / raw"),
        _ => f.ToString(),
    };

    public static string Describe(Availability a) => a switch
    {
        Availability.Available => Tr.T("funguje"),
        Availability.Planned => Tr.T("hardware umí, program doplní později"),
        Availability.NeedsDriver => Tr.T("potřebuje ovladač fdrawcmd.sys"),
        Availability.NeedsTool => Tr.T("potřebuje program gw.exe (Greaseweazle Host Tools)"),
        Availability.Unsupported => Tr.T("hardware neumí"),
        Availability.Maybe => Tr.T("záleží na řadiči"),
        _ => a.ToString(),
    };
}

/// <summary>Jedna nalezená mechanika a co s ní jde dělat.</summary>
public sealed class DetectedDrive
{
    public DriveKind Kind { get; init; }
    /// <summary>Písmeno jednotky ("A") u USB/interní, u Greaseweazle null.</summary>
    public string? Letter { get; init; }
    /// <summary>COM port u Greaseweazle.</summary>
    public string? Port { get; init; }
    public string Name { get; init; } = "";
    public string? Details { get; init; }
    public bool FdrawcmdInstalled { get; init; }
    /// <summary>Cesta k gw.exe (jen u Greaseweazle); null = program chybí.</summary>
    public string? GwToolPath { get; set; }
    /// <summary>Formáty médií, které mechanika hlásí (z Windows).</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = new List<string>();
    /// <summary>Co je vložené: žádná / DD / HD disketa (podle čidla hustoty v mechanice).</summary>
    public InsertedMedia Media { get; set; } = InsertedMedia.Unknown;
    /// <summary>Na jaký formát je vložená disketa naformátovaná (podle Windows); null = nenaformátovaná nebo nestandardní.</summary>
    public DiskGeometry? FormattedAs { get; set; }
    public MediaState? MediaState { get; set; }

    public string MediaText => Media switch
    {
        InsertedMedia.None => Tr.T("žádná disketa"),
        InsertedMedia.DD => Tr.T("DD disketa (nebo HD s přelepeným otvorem)"),
        InsertedMedia.HD => Tr.T("HD disketa"),
        _ => MediaState == Drives.MediaState.NoDisk ? Tr.T("žádná disketa") : Tr.T("disketa (hustotu se nepodařilo zjistit)"),
    } + (MediaState switch
    {
        Drives.MediaState.Ready when FormattedAs != null => Tr.F(", naformátovaná {0}", FormattedAs),
        Drives.MediaState.Unrecognized => Tr.T(", nenaformátovaná nebo nestandardní formát"),
        _ => "",
    });
    public bool Supports720 { get; init; }
    public bool Supports1440 { get; init; }
    public Dictionary<Feature, Availability> Features { get; } = new();

    public string Id => Letter != null ? Letter + ":" : Port ?? "?";

    public string KindText => Kind switch
    {
        DriveKind.Usb => Tr.T("USB disketovka"),
        DriveKind.Internal => FdrawcmdInstalled ? Tr.T("Interní mechanika (fdrawcmd.sys nainstalován)") : Tr.T("Interní mechanika"),
        DriveKind.Greaseweazle => "Greaseweazle",
        _ => Kind.ToString(),
    };

    public Availability Get(Feature f) => Features.TryGetValue(f, out var a) ? a : Availability.Unsupported;

    /// <summary>Vyplní tabulku schopností podle typu mechaniky.</summary>
    public void ComputeFeatures()
    {
        Features.Clear();
        switch (Kind)
        {
            case DriveKind.Usb:
                Features[Feature.Standard720] = Supports720 ? Availability.Available : Availability.Unsupported;
                Features[Feature.Standard1440] = Supports1440 ? Availability.Available : Availability.Unsupported;
                foreach (var f in new[] { Feature.ExtendedGeometry, Feature.ElevenSectors, Feature.StepRate,
                                          Feature.SimpleProtection, Feature.FluxProtection, Feature.WriteStxIpf })
                    Features[f] = Availability.Unsupported;
                break;

            case DriveKind.Internal:
                Features[Feature.Standard720] = Supports720 ? Availability.Available : Availability.Unsupported;
                Features[Feature.Standard1440] = Supports1440 ? Availability.Available : Availability.Unsupported;
                var low = FdrawcmdInstalled ? Availability.Available : Availability.NeedsDriver;
                Features[Feature.ExtendedGeometry] = low;
                Features[Feature.ElevenSectors] = FdrawcmdInstalled ? Availability.Maybe : Availability.NeedsDriver;
                Features[Feature.StepRate] = low;
                Features[Feature.SimpleProtection] = FdrawcmdInstalled ? Availability.Planned : Availability.NeedsDriver;
                Features[Feature.FluxProtection] = Availability.Unsupported;
                Features[Feature.WriteStxIpf] = Availability.Unsupported;
                break;

            case DriveKind.Greaseweazle:
                var gw = GwToolPath != null ? Availability.Available : Availability.NeedsTool;
                foreach (Feature f in System.Enum.GetValues(typeof(Feature)))
                    Features[f] = gw;
                // gw zapíše IPF (s knihovnou capsimg) a SCP/HFE, ale formát STX (Pasti) ne
                if (gw == Availability.Available) Features[Feature.WriteStxIpf] = Availability.Maybe;
                break;
        }
    }

    /// <summary>Zvládne tato verze programu standardní čtení/zápis přes Windows?</summary>
    public bool CanUseStandardAccess => Kind != DriveKind.Greaseweazle && Letter != null;
}
