using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Volby záchrany vadné diskety: kam (nebo do kterého rozpracovaného obrazu), kolik pokusů, skládání.</summary>
internal sealed class RescueDialog : DialogBase
{
    private readonly TextBox _path = new() { Width = 440 };
    private readonly Button _browse = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly NumericUpDown _tries = new() { Minimum = 3, Maximum = 500, Value = 30, Width = 60 };
    private readonly CheckBox _vote = new() { AutoSize = true };
    private readonly Label _state = Note("", null, 580);

    public string Path_ => _path.Text.Trim();
    public int Tries => (int)_tries.Value;
    public bool Vote => _vote.Checked && _vote.Enabled;

    public RescueDialog(DetectedDrive d, string suggested)
        : base(Tr.F("Záchrana vadné diskety — {0}", d.Id), new Size(660, 520))
    {
        bool fdraw = d.Kind == DriveKind.Internal && d.FdrawcmdInstalled;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Heading(Tr.T("Jak záchrana funguje")));
        flow.Controls.Add(Note(Tr.T("Program disketu přečte do obrazu a každý nečitelný sektor zkusí ještě mnohokrát. Co nepřečte, zapíše si do seznamu vedle obrazu. Když pak záchranu spustíš znovu se stejným souborem — klidně s disketou v jiné mechanice, po vyčištění hlavy nebo přes Greaseweazle — přečtou se jen chybějící sektory. Co nepřečte jedna mechanika, často přečte jiná."), Theme.Text, 600));
        if (d.Kind == DriveKind.Greaseweazle)
            flow.Controls.Add(Note(Tr.T("Greaseweazle: víc vytáhneš z uloženého toku. Ulož disketu jako tok (.scp, ideálně 5 otáček), otevři ho přes Otevřít obraz… a klikni na Záchrana sektorů…"), Theme.Accent, 600));

        flow.Controls.Add(Heading(Tr.T("Obraz")));
        _path.Text = suggested;
        _browse.Text = Tr.T("Procházet…");
        _browse.Click += (_, _) =>
        {
            using var s = new SaveFileDialog
            {
                Title = Tr.T("Nový obraz, nebo rozpracovaná záchrana"), Filter = ImageFilters.Save(false), OverwritePrompt = false,
                FileName = Path.GetFileName(_path.Text), InitialDirectory = Path.GetDirectoryName(_path.Text),
            };
            if (s.ShowDialog(this) == DialogResult.OK) { _path.Text = s.FileName; UpdateState(); }
        };
        _path.TextChanged += (_, _) => UpdateState();
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(_path);
        row.Controls.Add(_browse);
        flow.Controls.Add(row);
        flow.Controls.Add(_state);

        flow.Controls.Add(Heading(Tr.T("Pokusy")));
        var triesRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        triesRow.Controls.Add(new Label { Text = Tr.T("Každý nečitelný sektor zkusit až"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        triesRow.Controls.Add(_tries);
        triesRow.Controls.Add(new Label { Text = Tr.T("× (víc = déle, ale větší šance)"), AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        flow.Controls.Add(triesRow);

        _vote.Text = Tr.T("Skládat obsah z vadných čtení (EXPERIMENTÁLNÍ, jen fdrawcmd.sys)");
        _vote.Enabled = fdraw;
        flow.Controls.Add(_vote);
        flow.Controls.Add(Note(fdraw
            ? Tr.T("Když se sektor nepřečte ani po všech pokusech, program z vadných kopií hlasováním bit po bitu složí nejpravděpodobnější obsah (jako SpinRite). Ověřit ho nejde — sektor zůstane v seznamu chybějících a v mapě žlutě, další mechanika ho může přečíst správně. Jestli ovladač data z vadného čtení vůbec vrací, zatím není ověřené.")
            : Tr.T("Skládání z vadných čtení jde jen s interní mechanikou a ovladačem fdrawcmd.sys."), null, 600));
        Body.Controls.Add(flow);
        AddCancel();
        AddButton(Tr.T("Spustit záchranu"), DialogResult.OK, primary: true);
        UpdateState();
        Finish();
    }

    private void UpdateState()
    {
        string p = _path.Text.Trim();
        bool cont = p.Length > 0 && File.Exists(p) && File.Exists(RescueState.SidecarPath(p));
        _state.Text = cont ? Tr.T("Rozpracovaná záchrana — přečtou se jen chybějící sektory.")
                    : File.Exists(p) ? Tr.T("Soubor existuje a není to rozpracovaná záchrana — přepíše se.")
                    : Tr.T("Nová záchrana.");
        _state.ForeColor = cont ? Theme.Ok : File.Exists(p) ? Theme.Warn : Theme.TextDim;
    }
}
