using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Volby testu povrchu a kontroly souborového systému.</summary>
internal sealed class TestDialog : DialogBase
{
    private readonly RadioButton _read = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _preserve = new() { AutoSize = true };
    private readonly RadioButton _destructive = new() { AutoSize = true, ForeColor = Theme.Danger };
    private readonly CheckBox _repair = new() { AutoSize = true, Checked = true };
    private readonly CheckBox _save = new() { AutoSize = true };
    private readonly Label _saveNote = Note("", null, 560);
    private readonly TextBox _path = new() { Width = 440 };
    private readonly Button _browse = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };

    public WriteTestMode Mode => _destructive.Checked ? WriteTestMode.Destructive : _preserve.Checked ? WriteTestMode.PreserveData : WriteTestMode.None;
    public bool Repair => _repair.Checked && !_destructive.Checked;
    /// <summary>Kam uložit zálohu diskety (null = bez zálohy; u zápisového testu vždy vyplněné).</summary>
    public string? BackupPath => (_save.Checked || Mode != WriteTestMode.None) && _path.Text.Trim().Length > 0 ? _path.Text.Trim() : null;

    public TestDialog(DetectedDrive d, string backupFolder)
        : base(Tr.F("Test povrchu a kontrola — {0}", d.Id), new Size(660, 600))
    {
        bool gw = d.Kind == DriveKind.Greaseweazle;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Heading(Tr.T("Jak důkladně testovat")));
        flow.Controls.Add(Note(gw
            ? Tr.T("Greaseweazle přečte celou disketu a ukáže každý sektor v mapě. Zápisový test přepíše disketu stejným (nebo prázdným) obsahem a každou stopu ověří.")
            : Tr.F("Čtecí test projde celou disketu, u chyb až {0} pokusů na sektor. Slabá místa (čtou se až napodruhé) ukáže žlutě.", SurfaceTest.ScanRetries)));

        _read.Text = Tr.T("Jen čtení — nic se nemění (doporučeno)");
        _preserve.Text = Tr.T("Čtení i zápis, data zůstanou — před zápisem se uloží záloha");
        _destructive.Text = Tr.T("Destruktivní — smaže disketu, zapíše vzorky, pak nový formát");
        flow.Controls.AddRange(new Control[] { _read, _preserve, _destructive });
        flow.Controls.Add(new Label { Height = 8 });

        _repair.Text = Tr.T("Opravit souborový systém, když to půjde bezpečně (zeptám se před zápisem)");
        flow.Controls.Add(_repair);
        flow.Controls.Add(Note(Tr.T("Oprava se na disketu zapíše jen tehdy, když je systémová oblast v pořádku a mění se jen zdravé sektory. Jinak doporučím opravit obraz a zapsat ho na jinou disketu.")));

        flow.Controls.Add(Heading(Tr.T("Záloha diskety")));
        _save.Text = Tr.T("Uložit zálohu diskety do souboru:");
        _path.Text = AtariFloppy.Core.App.Backups.SuggestPath(null, AtariFloppy.Core.App.Backups.DriveTag(d), backupFolder);
        _browse.Text = Tr.T("Procházet…");
        _browse.Click += (_, _) =>
        {
            using var s = new SaveFileDialog
            {
                Title = Tr.T("Kam uložit zálohu diskety"), Filter = ImageFilters.Save(false),
                FileName = Path.GetFileName(_path.Text), InitialDirectory = Path.GetDirectoryName(_path.Text),
            };
            if (s.ShowDialog(this) == DialogResult.OK) { _path.Text = s.FileName; _save.Checked = true; }
        };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(18, 0, 0, 0) };
        row.Controls.Add(_path);
        row.Controls.Add(_browse);
        flow.Controls.Add(_save);
        flow.Controls.Add(row);
        flow.Controls.Add(_saveNote);
        _save.CheckedChanged += (_, _) => UpdateBackup();
        foreach (var r in new[] { _read, _preserve, _destructive }) r.CheckedChanged += (_, _) => UpdateBackup();
        _destructive.CheckedChanged += (_, _) => _repair.Enabled = !_destructive.Checked;
        UpdateBackup();
        Body.Controls.Add(flow);

        AddCancel();
        var ok = AddButton(Tr.T("Spustit test"), DialogResult.None, primary: true);
        ok.Click += (_, _) =>
        {
            if (BackupPath == null && Mode != WriteTestMode.None)
            {
                MessageBox.Show(this, Tr.T("Zadej, kam uložit zálohu diskety."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        Finish();
    }

    private void UpdateBackup()
    {
        bool required = Mode != WriteTestMode.None;
        if (required) _save.Checked = true;
        _save.Enabled = !required;
        _path.Enabled = _browse.Enabled = _save.Checked;
        _saveNote.Text = required
            ? Tr.T("Zápisový test přepisuje disketu, proto je záloha povinná — uloží se před prvním zápisem. Kdyby se něco pokazilo, zapíšeš ji zpět (Zapsat obraz na disketu).")
            : Tr.T("Záloha je obraz celé diskety (.ST): můžeš ji kdykoli zapsat zpět na disketu, otevřít v emulátoru nebo z ní vytáhnout soubory. Nečitelné sektory v ní budou prázdné. Test sám disketu nemění, záloha je jen pro jistotu.");
    }
}
