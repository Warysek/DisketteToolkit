using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.App;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Průvodce flashkou pro Gotek: které obrazy, na kterou flashku, pro jaký počítač.</summary>
internal sealed class GotekDialog : DialogBase
{
    private readonly ListBox _files = new() { Width = 600, Height = 150, HorizontalScrollbar = true };
    private readonly ComboBox _target = new() { Width = 470, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _machine = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _indexed = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
    private readonly Label _state = Note("", null, 600);
    private readonly Button _ok;

    private sealed record Target(string Path, string Text) { public override string ToString() => Text; }
    private sealed record Machine(GotekMachine Value, string Text) { public override string ToString() => Text; }

    public IList<string> Files => _files.Items.Cast<string>().ToList();
    public string? Folder => (_target.SelectedItem as Target)?.Path;
    public GotekMachine MachineValue => (_machine.SelectedItem as Machine)?.Value ?? GotekMachine.AtariSt;
    public bool Indexed => _indexed.Checked;

    public GotekDialog(string initialFolder)
        : base(Tr.T("Flashka pro Gotek"), new Size(680, 640))
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Note(Tr.T("Obrazy se na flashku zkopírují jako DSKA0000_název.st, DSKA0001_… — číslo je pozice, kterou Gotek ukáže na displeji, v pořadí podle seznamu (za obrazy, které už na flashce jsou). Formáty .MSA a .DIM Gotek nečte, převedou se na .ST; obrazy ze .ZIP se rozbalí. Platí pro Gotek s firmwarem FlashFloppy — s firmwarem HxC je potřeba formát .HFE."), Theme.Text, 620));

        flow.Controls.Add(Heading(Tr.T("Obrazy")));
        flow.Controls.Add(_files);
        var add = new Button { Text = Tr.T("Přidat…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        var remove = new Button { Text = Tr.T("Odebrat"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        var up = new Button { Text = "↑", AutoSize = true, FlatStyle = FlatStyle.Flat };
        var down = new Button { Text = "↓", AutoSize = true, FlatStyle = FlatStyle.Flat };
        add.Click += (_, _) =>
        {
            using var o = new OpenFileDialog { Title = Tr.T("Obrazy pro Gotek"), Filter = ImageFilters.Open(true), Multiselect = true, InitialDirectory = initialFolder };
            if (o.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in o.FileNames) if (!_files.Items.Contains(f)) _files.Items.Add(f);
            UpdateState();
        };
        remove.Click += (_, _) => { if (_files.SelectedItem != null) _files.Items.Remove(_files.SelectedItem); UpdateState(); };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(+1);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(new Control[] { add, remove, up, down });
        flow.Controls.Add(row);

        flow.Controls.Add(Heading(Tr.T("Flashka")));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // výměnné disky větší než disketa (USB mechanika s disketou je taky „výměnná“)
                if (d.DriveType != DriveType.Removable || !d.IsReady || d.TotalSize < 16L * 1024 * 1024) continue;
                _target.Items.Add(new Target(d.RootDirectory.FullName, Tr.F("{0} {1} ({2}, volno {3:N0} MB)", d.Name, d.VolumeLabel, d.DriveFormat, d.AvailableFreeSpace / (1024 * 1024))));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;
        var browse = new Button { Text = Tr.T("Jiná složka…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            using var f = new FolderBrowserDialog { Description = Tr.T("Kořen flashky pro Gotek (nebo složka, kam obrazy připravit)") };
            if (f.ShowDialog(this) != DialogResult.OK) return;
            var t = new Target(f.SelectedPath, f.SelectedPath);
            _target.Items.Add(t);
            _target.SelectedItem = t;
        };
        _target.SelectedIndexChanged += (_, _) => UpdateState();
        var trow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        trow.Controls.Add(_target);
        trow.Controls.Add(browse);
        flow.Controls.Add(trow);

        flow.Controls.Add(Heading(Tr.T("Počítač s Gotekem")));
        _machine.Items.AddRange(new object[]
        {
            new Machine(GotekMachine.AtariSt, "Atari ST / STE"),
            new Machine(GotekMachine.Amiga, "Amiga"),
            new Machine(GotekMachine.Pc, Tr.T("PC (IBM a kompatibilní)")),
            new Machine(GotekMachine.Msx, "MSX"),
            new Machine(GotekMachine.Other, Tr.T("jiný")),
        });
        _machine.SelectedIndex = 0;
        flow.Controls.Add(_machine);
        _indexed.Text = Tr.T("Gotek s třímístným číselným displejem — přepínat jen číslované obrazy (FF.CFG: nav-mode = indexed)");
        _indexed.MaximumSize = new Size(620, 0);
        flow.Controls.Add(_indexed);
        flow.Controls.Add(_state);
        Body.Controls.Add(flow);

        AddCancel();
        _ok = AddButton(Tr.T("Zkopírovat na flashku"), DialogResult.OK, primary: true);
        UpdateState();
        Finish();
    }

    private void MoveSelected(int dir)
    {
        int i = _files.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= _files.Items.Count) return;
        var item = _files.Items[i];
        _files.Items.RemoveAt(i);
        _files.Items.Insert(j, item);
        _files.SelectedIndex = j;
    }

    private void UpdateState()
    {
        string? folder = Folder;
        _ok.Enabled = folder != null && _files.Items.Count > 0;
        if (folder == null) { _state.Text = Tr.T("Není vybraná žádná flashka — zapoj ji, nebo zvol složku."); _state.ForeColor = Theme.Warn; return; }
        int n = Gotek.ExistingSlots(folder).Count;
        _state.Text = n == 0 ? Tr.T("Na flashce zatím nejsou žádné číslované obrazy — první dostane pozici 000.")
                             : Tr.F("Číslovaných obrazů na flashce: {0} — nové dostanou pozice od {1:D3}.", n, Gotek.NextSlot(folder));
        _state.ForeColor = Theme.TextDim;
    }
}
