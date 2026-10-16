using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.App;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Záchrana sektorů z uložených toků: které soubory .scp (třeba z různých mechanik) a kam uložit obraz.</summary>
internal sealed class FluxRescueDialog : DialogBase
{
    private readonly ListBox _files = new() { Width = 580, Height = 110, HorizontalScrollbar = true };
    private readonly TextBox _path = new() { Width = 470 };
    private readonly Label _state = Note("", null, 580);

    public IList<string> Files => _files.Items.Cast<string>().ToList();
    public string Output => _path.Text.Trim();

    public FluxRescueDialog(string firstScp)
        : base(Tr.T("Záchrana sektorů z toku"), new Size(660, 560))
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Note(Tr.T("Program převede uložený magnetický tok na sektory vlastním převodem — disketa v mechanice není potřeba. Z každé otáčky vezme sektory s platným kontrolním součtem a stopy s chybami zkusí převést i s jiným nastavením. Přidáš-li toky téže diskety z více mechanik, sektory se doplní navzájem. Umí PC, Atari ST a MSX diskety (MFM, 512 bajtů na sektor)."), Theme.Text, 600));

        flow.Controls.Add(Heading(Tr.T("Soubory toku (.scp)")));
        _files.Items.Add(firstScp);
        flow.Controls.Add(_files);
        var add = new Button { Text = Tr.T("Přidat…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        var remove = new Button { Text = Tr.T("Odebrat"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        add.Click += (_, _) =>
        {
            using var o = new OpenFileDialog { Title = Tr.T("Tok téže diskety (třeba z jiné mechaniky)"), Filter = Tr.T("Magnetický tok SuperCard Pro") + " (*.scp)|*.scp", Multiselect = true, InitialDirectory = Path.GetDirectoryName(firstScp) };
            if (o.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in o.FileNames) if (!_files.Items.Contains(f)) _files.Items.Add(f);
        };
        remove.Click += (_, _) => { if (_files.SelectedItem != null && _files.Items.Count > 1) _files.Items.Remove(_files.SelectedItem); };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        flow.Controls.Add(buttons);

        flow.Controls.Add(Heading(Tr.T("Uložit obraz")));
        _path.Text = Path.ChangeExtension(firstScp, ".img");
        var browse = new Button { Text = Tr.T("Procházet…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
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
        row.Controls.Add(browse);
        flow.Controls.Add(row);
        flow.Controls.Add(_state);
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
        _state.Text = cont ? Tr.T("Rozpracovaná záchrana — doplní se jen chybějící sektory.")
                    : File.Exists(p) ? Tr.T("Soubor existuje a není to rozpracovaná záchrana — přepíše se.")
                    : Tr.T("Nová záchrana.");
        _state.ForeColor = cont ? Theme.Ok : File.Exists(p) ? Theme.Warn : Theme.TextDim;
    }
}
