using System;
using System.IO;
using System.Text;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

public enum ImageKind
{
    Unknown,
    St,
    Msa,
    Dim,
    /// <summary>Pasti (.STX) — uchovává i ochrany.</summary>
    Stx,
    /// <summary>SPS / CAPS (.IPF) — archivní formát s ochranami.</summary>
    Ipf,
    /// <summary>SuperCard Pro (.SCP) — surový magnetický tok.</summary>
    Scp,
    /// <summary>HxC (.HFE).</summary>
    Hfe,
    /// <summary>KryoFlux stream (trackXX.Y.raw).</summary>
    KryoFlux,
    /// <summary>Amiga .ADF — holé sektory (80×2×11 nebo 80×2×22).</summary>
    Adf,
    /// <summary>PC/MSX/C1581 holý obraz (.IMG, .IMA, .VFD, .DSK, .D81).</summary>
    RawImg,
    /// <summary>Amstrad CPC / ZX Spectrum +3 .DSK s hlavičkou (3" diskety).</summary>
    CpcDsk,
}

public sealed class LoadedImage
{
    public ImageKind Kind { get; init; }
    public DiskImage? Image { get; init; }
    /// <summary>Proč Image chybí (formát zatím jen rozpoznaný).</summary>
    public string? NotLoadedReason { get; init; }
    public long FileSize { get; init; }
}

public static class ImageFile
{
    public static string Describe(ImageKind k) => k switch
    {
        ImageKind.St => Tr.T(".ST (holé sektory)"),
        ImageKind.Msa => ".MSA (Magic Shadow Archiver)",
        ImageKind.Dim => ".DIM (FastCopy Pro)",
        ImageKind.Stx => Tr.T(".STX (Pasti — s ochranami)"),
        ImageKind.Ipf => Tr.T(".IPF (SPS/CAPS — archivní, s ochranami)"),
        ImageKind.Scp => Tr.T(".SCP (SuperCard Pro — surový tok)"),
        ImageKind.Hfe => ".HFE (HxC)",
        ImageKind.KryoFlux => "KryoFlux stream (.raw)",
        ImageKind.Adf => ".ADF (Amiga)",
        ImageKind.RawImg => Tr.T("holý obraz (.IMG / .IMA / .VFD / .DSK / .D81)"),
        ImageKind.CpcDsk => ".DSK Amstrad CPC / Spectrum +3",
        _ => Tr.T("neznámý formát"),
    };

    public static ImageKind Detect(string path, byte[] head)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (StartsWith(head, "RSY\0")) return ImageKind.Stx;
        if (StartsWith(head, "CAPS")) return ImageKind.Ipf;
        if (StartsWith(head, "SCP")) return ImageKind.Scp;
        if (StartsWith(head, "HXCPICFE") || StartsWith(head, "HXCHFEV3")) return ImageKind.Hfe;
        if (StartsWith(head, "MV - CPC") || StartsWith(head, "EXTENDED CPC DSK")) return ImageKind.CpcDsk;
        if (MsaFormat.IsMsa(head) && ext == ".msa") return ImageKind.Msa;
        if (DimFormat.IsDim(head) && ext == ".dim") return ImageKind.Dim;
        return ext switch
        {
            ".st" => ImageKind.St,
            ".msa" => ImageKind.Msa,
            ".dim" => ImageKind.Dim,
            ".stx" => ImageKind.Stx,
            ".ipf" => ImageKind.Ipf,
            ".scp" => ImageKind.Scp,
            ".hfe" => ImageKind.Hfe,
            ".raw" => ImageKind.KryoFlux,
            ".adf" => ImageKind.Adf,
            ".img" or ".ima" or ".vfd" or ".dsk" or ".d81" or ".flp" => ImageKind.RawImg,
            _ => MsaFormat.IsMsa(head) ? ImageKind.Msa : ImageKind.Unknown,
        };
    }

    public static LoadedImage Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(Tr.F("Soubor neexistuje: {0}", path));
        byte[] bytes = File.ReadAllBytes(path);
        var kind = Detect(path, bytes);
        DiskImage? img = null;
        string? reason = null;
        switch (kind)
        {
            case ImageKind.St:
            case ImageKind.Adf:
            case ImageKind.RawImg:
                img = StFormat.Load(bytes); break;
            case ImageKind.CpcDsk:
                reason = Tr.T("Obraz 3\" diskety Amstrad CPC / ZX Spectrum +3 — ty se do 3,5\" mechaniky nevejdou a tento program je nepodporuje.");
                break;
            case ImageKind.Msa: img = MsaFormat.Load(bytes); break;
            case ImageKind.Dim: img = DimFormat.Load(bytes); break;
            case ImageKind.Scp:
                reason = Tr.T("Magnetický tok — seznam souborů z něj neukážu. Sektory PC / Atari / MSX diskety z něj vytáhne Záchrana sektorů…, na disketu ho zapíše Greaseweazle.");
                break;
            case ImageKind.Stx:
            case ImageKind.Ipf:
            case ImageKind.Hfe:
            case ImageKind.KryoFlux:
                reason = Tr.T("Tento formát zatím umím jen rozpoznat. Čtení a převod přijdou v dalším kroku vývoje.");
                break;
            default:
                // Poslední pokus: soubor bez známé přípony, zkusíme ho brát jako .ST.
                var g = StFormat.GuessGeometry(bytes);
                if (g != null) { img = new DiskImage(g, bytes); kind = ImageKind.St; }
                else reason = Tr.T("Formát souboru se nepodařilo rozpoznat.");
                break;
        }
        return new LoadedImage { Kind = kind, Image = img, NotLoadedReason = reason, FileSize = bytes.Length };
    }

    public static void Save(DiskImage image, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] data = ext switch
        {
            ".st" or ".img" or ".ima" or ".vfd" or ".adf" or ".d81" or ".dsk" or ".flp" => StFormat.Save(image),
            ".msa" => MsaFormat.Save(image),
            ".dim" => DimFormat.Save(image),
            _ => throw new NotSupportedException(Tr.F("Ukládat umím do .ST, .MSA, .DIM, .IMG (.IMA, .VFD), .ADF a .D81 (ne „{0}“).", ext)),
        };
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, data);
    }

    private static bool StartsWith(byte[] b, string sig)
    {
        if (b.Length < sig.Length) return false;
        var s = Encoding.ASCII.GetBytes(sig);
        for (int i = 0; i < s.Length; i++) if (b[i] != s[i]) return false;
        return true;
    }
}
