using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Před zápisem: co je v obrazu, jestli ho tahle mechanika zapíše (a proč ne), volby.</summary>
internal sealed class WriteDialog : DialogBase
{
    private readonly CheckBox _format = new() { AutoSize = true };
    private readonly CheckBox _verify = new() { AutoSize = true };

    public bool FormatFirst => _format.Checked;
    public bool Verify => _verify.Checked;

    public WriteDialog(DetectedDrive d, string path, bool verify)
        : base(Tr.F("Zápis na disketu — {0}", d.Id), new Size(640, 470))
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Heading(Path.GetFileName(path)));

        bool allowed = true;
        bool gw = d.Kind == DriveKind.Greaseweazle;
        string ext = Path.GetExtension(path).ToLowerInvariant();

        void Line(string name, string value, Color? color = null)
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 2), WrapContents = false };
            row.Controls.Add(new Label { Text = name, Width = 150, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 6, 0) });
            row.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = color ?? Theme.Text, Margin = new Padding(0, 2, 0, 0) });
            flow.Controls.Add(row);
        }

        if (gw && ext == ".stx")
        {
            allowed = false;
            flow.Controls.Add(Note(Tr.T("Formát STX (Pasti) program gw zapsat neumí. Hledej stejnou hru jako .IPF nebo .SCP, případně .ST/.MSA (bez ochrany)."), Theme.Danger));
        }
        else if (gw && DeviceFactory.IsFlux(path))
        {
            Line(Tr.T("Obraz"), ext == ".ipf" ? Tr.T("IPF (archivní, s ochranami) — gw potřebuje knihovnu capsimg.dll") : Tr.T("magnetický tok — zapíše se včetně ochran"));
        }
        else
        {
            LoadedImage? loaded = null;
            try { loaded = ImageFile.Load(path); }
            catch (Exception e) { flow.Controls.Add(Note(e.Message, Theme.Danger)); allowed = false; }
            if (loaded != null)
            {
                Line(Tr.T("Typ souboru"), ImageFile.Describe(loaded.Kind));
                if (loaded.Image == null)
                {
                    allowed = false;
                    flow.Controls.Add(Note(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."), Theme.Danger));
                }
                else
                {
                    var img = loaded.Image;
                    var fs = FileSystemDetector.Detect(img.Data);
                    Line(Tr.T("Formát"), img.Geometry.ToString());
                    Line(Tr.T("Systém"), fs.Name + (fs.Details != null ? " — " + fs.Details : ""));
                    Line(Tr.T("K čemu"), fs.UsedFor, Theme.TextDim);
                    if (gw)
                    {
                        Line(Tr.T("Zápis přes"), GwFormat.ForImage(img).ToString());
                        flow.Controls.Add(Note(img.Geometry.IsHighDensity ? Tr.T("Vlož HD disketu (bez pásky).") : Tr.T("Vlož DD disketu (nebo HD s přelepeným otvorem).")));
                    }
                    else
                    {
                        var a = ImageAnalysis.AssessForStandardDrive(loaded);
                        bool viaFdraw = d.FdrawcmdInstalled && !ImageAnalysis.FitsStandardDrive(img.Geometry) && fs.Kind != FsKind.Amiga;
                        allowed = a.Verdict == Verdict.Writable || viaFdraw;
                        var color = a.Verdict == Verdict.Writable ? Theme.Ok : viaFdraw ? Theme.Accent : a.Verdict == Verdict.ContentOnly ? Theme.Warn : Theme.Danger;
                        flow.Controls.Add(Note(viaFdraw ? Tr.T("Zapíše se přes fdrawcmd.sys (nestandardní formát).") : a.Summary, color));
                        foreach (var n in a.Notes) flow.Controls.Add(Note("• " + n));
                        _format.Checked = d.MediaState != MediaState.Ready || d.FormattedAs == null ||
                                          d.FormattedAs.SectorsPerTrack != img.Geometry.SectorsPerTrack || viaFdraw;
                        Line(Tr.T("Disketa v mechanice"), d.MediaText);
                    }
                }
            }
        }

        _format.Text = Tr.T("Nejdřív disketu naformátovat (doporučeno, když má jiný formát)");
        _verify.Text = gw ? Tr.T("Ověřit zápis (Greaseweazle každou stopu přečte zpět)") : Tr.T("Po zápisu zkontrolovat (přečíst a porovnat)");
        _verify.Checked = verify;
        flow.Controls.Add(new Label { Height = 6 });
        if (!gw) flow.Controls.Add(_format);
        flow.Controls.Add(_verify);
        _format.Enabled = _verify.Enabled = allowed;
        Body.Controls.Add(flow);

        AddCancel();
        var ok = AddButton(Tr.T("Zapsat na disketu"), DialogResult.OK, primary: true);
        ok.Enabled = allowed;
        Finish();
    }
}
