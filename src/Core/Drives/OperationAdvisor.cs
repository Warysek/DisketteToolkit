using System.Collections.Generic;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives;

public sealed record OperationVerdict(string Operation, ProfileStatus Status, string Reason)
{
    public bool IsReady => Status == ProfileStatus.Ready;
}

/// <summary>Co jde s danou mechanikou a vloženou disketou dělat — a proč ne.</summary>
public static class OperationAdvisor
{
    public static string Read => Tr.T("Záloha diskety do image");
    public static string Write => Tr.T("Zápis image na disketu");
    public static string Format => Tr.T("Formátování");
    public static string Surface => Tr.T("Test povrchu");
    public static string FsCheck => Tr.T("Kontrola a oprava souborů");

    public static List<OperationVerdict> Evaluate(DetectedDrive d)
    {
        var list = new List<OperationVerdict>();
        void Add(string op, ProfileStatus s, string reason) => list.Add(new OperationVerdict(op, s, reason));

        if (d.Kind == DriveKind.Greaseweazle)
        {
            if (d.GwToolPath == null)
            {
                foreach (var op in new[] { Read, Write, Format, Surface, FsCheck })
                    Add(op, ProfileStatus.NeedsDriver, Tr.T("Chybí gw.exe. ") + Gw.GwTool.DownloadHint);
                return list;
            }
            Add(Read, ProfileStatus.Ready, Tr.T("Formát pozná sám (PC, Atari i 10/11 sektorů a 82 stop, MSX, Amiga, Mac 400/800 KB). Chráněné hry: záloha jako tok (.scp / .hfe)."));
            Add(Write, ProfileStatus.Ready, Tr.T("Obrazy .ST, .MSA, .ADF, .IMG, .D81 … i toky .SCP, .HFE a .IPF (IPF potřebuje capsimg.dll u gw.exe). STX gw neumí."));
            Add(Format, ProfileStatus.Ready, Tr.T("Všechny formáty z přehledu včetně Amigy a Atari 800–900 KB."));
            Add(Surface, ProfileStatus.Ready, Tr.T("Čtecí test s mapou sektorů; zápisový test přepíše disketu stejným obsahem (s ověřením)."));
            Add(FsCheck, ProfileStatus.Ready, Tr.T("Kontrola a oprava FAT12; ostatní systémy rozpozná."));
            return list;
        }

        string media = d.Media switch
        {
            InsertedMedia.DD => Tr.T("DD disketa: image 640 KB, 720 KB nebo jednostranný 360 KB."),
            InsertedMedia.HD => Tr.T("HD disketa: image 1,44 MB."),
            _ => "",
        };

        switch (d.MediaState)
        {
            case MediaState.NoDisk:
                foreach (var op in new[] { Read, Write, Format, Surface, FsCheck })
                    Add(op, ProfileStatus.NoDisk, Tr.T("V mechanice není disketa."));
                break;

            case MediaState.Ready:
                Add(Read, ProfileStatus.Ready, Tr.F("Disketa je čitelná ({0}) — záloha funguje u jakéhokoli systému (Atari, PC, Mac HFS, MSX, CP/M, Linux…).", d.FormattedAs?.Short));
                Add(Write, ProfileStatus.Ready, media);
                Add(Format, ProfileStatus.Ready, Tr.T("Nabídka formátů se řídí vloženou disketou — viz přehled formátů."));
                Add(Surface, ProfileStatus.Ready, Tr.T("Čtecí test nic nemění; zápisový test s obnovou nebo destruktivní."));
                Add(FsCheck, ProfileStatus.Ready, Tr.T("Kontrola a oprava: FAT12 (Atari, PC, MSX). Ostatní systémy program rozpozná a otestuje povrch."));
                break;

            case MediaState.Unrecognized:
                string why = Tr.T("Mechanika disketu nepřečte: je nenaformátovaná, nebo má formát, který přes Windows nejde (Atari 10/11 sektorů, Amiga, Mac 400/800 KB, Commodore 1581, chráněná hra). Takovou disketu přečte interní mechanika s fdrawcmd.sys nebo Greaseweazle.");
                bool fd = d.Kind == DriveKind.Internal && d.FdrawcmdInstalled;
                Add(Read, fd ? ProfileStatus.Ready : ProfileStatus.Unsupported,
                    fd ? Tr.T("Windows disketu nepřečtou, ale fdrawcmd.sys zkusí rozpoznat nestandardní formát (Atari 10/11 sektorů, 82 stop …).") : why);
                Add(Write, ProfileStatus.Ready, Tr.T("Jen se zapnutým formátováním. ") + media);
                Add(Format, ProfileStatus.Ready, Tr.T("Formát disketu znovu zpřístupní (data na ní se smažou)."));
                Add(Surface, fd ? ProfileStatus.Ready : ProfileStatus.Unsupported,
                    fd ? Tr.T("Přes fdrawcmd.sys, pokud jde o MFM formát.") : Tr.T("Nejde přečíst — nejdřív disketu naformátuj (nebo použij destruktivní test po formátu)."));
                Add(FsCheck, fd ? ProfileStatus.Ready : ProfileStatus.Unsupported, fd ? Tr.T("Přes fdrawcmd.sys.") : why);
                break;

            default:
                foreach (var op in new[] { Read, Write, Format, Surface, FsCheck })
                    Add(op, ProfileStatus.Unsupported, Tr.T("Stav diskety se nepodařilo zjistit — zkus ji vyndat a vložit znovu."));
                break;
        }
        return list;
    }
}
