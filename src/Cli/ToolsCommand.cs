using System;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.Drives.Windows;

namespace AtariFloppy.Cli;

/// <summary>Stažení doplňků: Greaseweazle Host Tools, firmware Greaseweazle, ovladač fdrawcmd.sys.</summary>
internal static class ToolsCommand
{
    private static void Progress(long done, long total) =>
        Ui.Progress((int)(done / 1024), (int)Math.Max(1, total / 1024), $"Stahuji {done / 1024:N0} KB".Replace(',', ' '));

    public static int InstallGw()
    {
        Ui.Title("Greaseweazle Host Tools (gw.exe)");
        var latest = GreaseweazleSetup.GetLatest(Program.Cancel.Token);
        Ui.Field("Nejnovější verze", $"{latest.Version} ({latest.AssetName})");
        var current = GwTool.Find(Backends.GwPath);
        string? cur = current != null ? GreaseweazleSetup.VersionOf(current.Path) : null;
        if (current != null) Ui.Field("Teď používám", current.Path + (cur != null ? $" (verze {cur})" : ""));
        if (cur != null && GreaseweazleSetup.CompareVersions(cur, latest.Version) >= 0 && !Ui.Confirm("Máš nejnovější verzi. Přesto stáhnout znovu?"))
            return 0;
        Program.Busy = true;
        string gw;
        try { gw = GreaseweazleSetup.Install(latest, Progress, Program.Cancel.Token); }
        finally { Program.Busy = false; Ui.EndProgress(); }
        Ui.Ok($"Nainstalováno: {gw} (verze {latest.Version}), včetně CAPSImg.dll pro .IPF.");
        Ui.Dim("  Firmware v Greaseweazle aktualizuješ příkazem: dtk firmware GW");
        return 0;
    }

    public static int Firmware(string arg)
    {
        var (drive, gw) = Backends.ResolveGw(arg);
        Ui.Title($"Firmware — {drive.Name}");
        string? fw = gw.FirmwareVersion(Program.Cancel.Token) ?? throw new OperationException("Greaseweazle neodpovídá — je připojený k USB?");
        Ui.Field("Firmware v zařízení", fw);
        if (!Ui.Confirm("Aktualizovat firmware na nejnovější verzi? Během aktualizace Greaseweazle neodpojuj.")) return 1;
        int code = gw.UpdateFirmware(l => { if (l.Trim().Length > 0) Ui.Dim("  " + l.Trim()); }, Program.Cancel.Token);
        Ui.Field("Firmware teď", gw.FirmwareVersion(Program.Cancel.Token) ?? "?");
        return code;
    }

    public static int InstallFdrawcmd()
    {
        Ui.Title("Ovladač fdrawcmd.sys");
        if (FdrawCmd.IsInstalled(out string? ver) && !Ui.Confirm($"fdrawcmd.sys je už nainstalovaný (verze {ver}). Instalovat znovu?")) return 0;
        Program.Busy = true;
        string exe;
        try { exe = FdrawcmdSetup.Download(Progress, Program.Cancel.Token); }
        finally { Program.Busy = false; Ui.EndProgress(); }
        string? signer = FdrawcmdSetup.Signer(exe);
        if (signer == null && !Ui.Confirm("Instalátor není digitálně podepsaný. Přesto spustit?")) return 1;
        if (signer != null && signer.IndexOf("Simon Owen", StringComparison.OrdinalIgnoreCase) < 0)
            throw new OperationException($"Instalátor podepsal někdo jiný než autor ({signer}) — nespustím ho.");
        Ui.Dim("  Otevře se okno instalátoru — potvrď instalaci a zavři ho.");
        FdrawcmdSetup.RunInstaller(exe);
        if (FdrawCmd.IsInstalled(out ver)) Ui.Ok($"fdrawcmd.sys běží (verze {ver}).");
        else Ui.Warn("Ovladač zatím neodpovídá — restartuj počítač. Na Windows 11 ho může blokovat Integrita paměti (Zabezpečení Windows → Zabezpečení zařízení → Izolace jádra).");
        return 0;
    }
}
