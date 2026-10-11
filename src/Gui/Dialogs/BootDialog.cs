using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AtariFloppy.Core;
using AtariFloppy.Core.Boot;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Spouštěcí disketa: FreeDOS, MS-DOS z Windows, Amiga (install), Atari (AUTO), hotový obraz (RawWrite).</summary>
internal sealed class BootDialog : DialogBase
{
    private readonly DetectedDrive? _drive;
    private readonly Dictionary<RadioButton, BootSystem?> _choices = new();
    private readonly Dictionary<RadioButton, string?> _blocked = new();
    private readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _label = new() { Width = 160, MaxLength = 11, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _prg = new() { Width = 330 };
    private readonly Button _prgBrowse = new() { AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Label _status = Note("", Theme.Warn, 600);
    private readonly Button _writeBtn, _saveBtn;
    private RadioButton? _raw;
    private readonly CheckBox _tools = new() { AutoSize = false, Size = new Size(620, 36), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft, Checked = true, Margin = new Padding(22, 0, 0, 4) };
    private readonly Dictionary<RadioButton, Label> _reasons = new();

    /// <summary>diskcopy.dll, kterou uživatel vybral (uložit do nastavení).</summary>
    public string? ChosenDiskcopy { get; private set; }

    public BootOptions? Options { get; private set; }
    public string? SavePath { get; private set; }
    public bool WriteToDisk { get; private set; }
    /// <summary>Uživatel chce zapsat hotový obraz (RawWrite).</summary>
    public bool RawWrite { get; private set; }

    public BootDialog(DetectedDrive? drive, string folder)
        : base(Tr.T("Spouštěcí disketa"), new Size(720, 740))
    {
        _drive = drive;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Heading(Tr.T("Jakou spouštěcí disketu vytvořit")));

        string fdNote = FreeDosFiles.Cached ? Tr.T("Soubory FreeDOS jsou už stažené.") : Tr.T("Při prvním použití se stáhnou oficiální balíky FreeDOS 1.3 (asi 3,5 MB, licence GPL).");
        bool msdos = WindowsMsDos.IsAvailable(out string msdosWhy);
        bool gw = drive?.Kind == DriveKind.Greaseweazle;
        string? amigaWrite = drive != null && !gw ? Tr.T("Na disketu ji zapíše jen Greaseweazle — s touto mechanikou jen jako obraz .ADF (Gotek, emulátor).") : null;

        var fd = Choice(flow, BootSystem.FreeDos, BootDisks.Describe(BootSystem.FreeDos),
               Tr.T("Svobodný DOS kompatibilní s MS-DOS — na starém PC spustí DOSové programy, hry a nástroje.") + " " + fdNote, null, check: true);
        _tools.Text = Tr.T("Přidat nástroje jako na spouštěcí disketě Windows 98: FORMAT, FDISK, SYS, EDIT, MEM, XCOPY, ATTRIB, DELTREE, MORE, CHOICE");

        flow.Controls.Add(_tools);
        var ms = Choice(flow, BootSystem.MsDosFromWindows, BootDisks.Describe(BootSystem.MsDosFromWindows),
               Tr.T("Stejná disketa, jakou vytvářely Windows XP až 10 (MS-DOS z Windows ME). Vezme se z Windows v tomto počítači; jen 1,44 MB HD."),
               msdos ? null : msdosWhy);
        if (!msdos)
        {
            var pick = new LinkLabel { Text = Tr.T("Mám diskcopy.dll ze starších Windows (XP, 7, 10), ke kterým mám licenci — vybrat soubor…"), AutoSize = true, Margin = new Padding(22, 0, 0, 4) };
            pick.LinkClicked += (_, _) =>
            {
                using var o = new OpenFileDialog { Filter = "diskcopy.dll|diskcopy.dll|DLL (*.dll)|*.dll", Title = Tr.T("Vyber diskcopy.dll ze starších Windows") };
                if (o.ShowDialog(this) != DialogResult.OK) return;
                string? old = WindowsMsDos.CustomPath;
                WindowsMsDos.CustomPath = o.FileName;
                if (WindowsMsDos.IsAvailable(out string why))
                {
                    ChosenDiskcopy = o.FileName;
                    ms.Enabled = true;
                    ms.Checked = true;
                    _reasons[ms].Text = "✓ " + Tr.F("Použije se {0}", o.FileName);
                    _reasons[ms].ForeColor = Theme.Ok;
                    pick.Visible = false;
                }
                else
                {
                    WindowsMsDos.CustomPath = old;
                    MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            flow.Controls.Add(pick);
        }
        Choice(flow, BootSystem.AmigaFfs, BootDisks.Describe(BootSystem.AmigaFfs),
               Tr.T("Bootblok jako po příkazu „install“ — Amiga z ní naběhne do příkazového řádku AmigaDOS. A500+, A600, A1200, A4000."), null, amigaWrite);
        Choice(flow, BootSystem.AmigaOfs, BootDisks.Describe(BootSystem.AmigaOfs),
               Tr.T("Totéž pro starší Amigy s Kickstartem 1.2 / 1.3 (A500, A1000, A2000)."), null, amigaWrite);
        var atari = Choice(flow, BootSystem.AtariAuto, BootDisks.Describe(BootSystem.AtariAuto),
               Tr.T("Atari naběhne z každé diskety (TOS je v ROM). Program ve složce AUTO se po zapnutí spustí sám — vyber ho:"), null);
        _prgBrowse.Text = Tr.T("Procházet…");
        _prgBrowse.Click += (_, _) =>
        {
            using var o = new OpenFileDialog { Filter = Tr.T("Programy Atari") + " (*.prg;*.tos;*.ttp;*.app)|*.prg;*.tos;*.ttp;*.app|" + Tr.T("Všechny soubory") + " (*.*)|*.*", InitialDirectory = folder };
            if (o.ShowDialog(this) == DialogResult.OK) { _prg.Text = o.FileName; atari.Checked = true; }
        };
        var prgRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 0, 0, 6) };
        prgRow.Controls.Add(_prg);
        prgRow.Controls.Add(_prgBrowse);
        flow.Controls.Add(prgRow);
        _raw = Choice(flow, null, Tr.T("Hotový obraz spouštěcí diskety (jako RawWrite)"),
               Tr.T("Zapíše stažený obraz sektor po sektoru — Linux, instalační a diagnostické diskety, DOS, hry (.IMG, .IMA, .ST, .ADF…)."), null);

        var opts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        opts.Controls.Add(new Label { Text = Tr.T("Velikost:"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _size.Items.AddRange(new object[] { Tr.T("1,44 MB (HD disketa)"), Tr.T("720 KB (DD disketa)") });
        _size.SelectedIndex = drive?.Media == InsertedMedia.DD ? 1 : 0;
        opts.Controls.Add(_size);
        opts.Controls.Add(new Label { Text = Tr.T("Jmenovka:"), AutoSize = true, Margin = new Padding(18, 6, 6, 0) });
        opts.Controls.Add(_label);
        flow.Controls.Add(opts);
        flow.Controls.Add(_status);

        var info = new LinkLabel { Text = Tr.T("Jak se z diskety spouštěly jiné počítače?"), AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        info.LinkClicked += (_, _) => ShowOtherSystems();
        flow.Controls.Add(info);
        Body.Controls.Add(flow);

        AddCancel();
        _saveBtn = AddButton(Tr.T("Uložit jako obraz…"), DialogResult.None);
        _saveBtn.Click += (_, _) => Save(folder);
        _writeBtn = AddButton(drive != null ? Tr.F("Zapsat na disketu ({0})", drive.Id) : Tr.T("Zapsat na disketu"), DialogResult.None, primary: true);
        _writeBtn.Click += (_, _) => Write();
        foreach (var r in _choices.Keys) r.CheckedChanged += (_, _) => UpdateState();
        UpdateState();
        Finish();
    }

    private RadioButton Choice(FlowLayoutPanel flow, BootSystem? s, string title, string text, string? unavailable, string? writeBlock = null, bool check = false)
    {
        var r = new RadioButton { Text = title, AutoSize = true, Font = Theme.UiFont(9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0), Checked = check && unavailable == null };
        r.Enabled = unavailable == null;
        _choices[r] = s;
        _blocked[r] = writeBlock;
        flow.Controls.Add(r);
        flow.Controls.Add(Note(text, null, 640));
        if (unavailable != null)
        {
            var l = Note("✗ " + unavailable, Theme.Warn, 640);
            _reasons[r] = l;
            flow.Controls.Add(l);
        }
        return r;
    }

    private RadioButton? Selected
    {
        get { foreach (var r in _choices.Keys) if (r.Checked) return r; return null; }
    }

    private void UpdateState()
    {
        var r = Selected;
        var s = r != null ? _choices[r] : null;
        _size.Enabled = s is BootSystem.FreeDos or BootSystem.AtariAuto;
        _tools.Enabled = s == BootSystem.FreeDos;
        _label.Enabled = s is not (null or BootSystem.MsDosFromWindows);
        _prg.Enabled = _prgBrowse.Enabled = s == BootSystem.AtariAuto;
        _saveBtn.Enabled = r != null && r != _raw;
        string? block = r != null ? _blocked[r] : null;
        if (_drive == null && r != _raw) block = Tr.T("Není vybraná mechanika — disketu můžeš uložit jako obraz.");
        _writeBtn.Enabled = r != null && block == null;
        _status.Text = block ?? "";
        _status.Visible = block != null;
    }

    private BootOptions? Build()
    {
        var r = Selected;
        if (r == null || _choices[r] == null) return null;
        var o = new BootOptions
        {
            System = _choices[r]!.Value,
            Geometry = _size.SelectedIndex == 1 ? DiskGeometry.Dd720 : DiskGeometry.Hd1440,
            FreeDosTools = _tools.Checked,
            Label = _label.Text.Trim().Length > 0 ? _label.Text.Trim() : null,
        };
        if (o.System == BootSystem.AtariAuto)
        {
            if (!File.Exists(_prg.Text))
            {
                MessageBox.Show(this, Tr.T("Vyber program pro složku AUTO."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            o.AtariProgram = File.ReadAllBytes(_prg.Text);
            o.AtariProgramName = Path.GetFileName(_prg.Text);
            if (o.AtariProgram.Length < 2 || o.AtariProgram[0] != 0x60 || o.AtariProgram[1] != 0x1A)
            {
                MessageBox.Show(this, Tr.T("Soubor není program pro Atari (chybí hlavička 0x601A)."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }
        return o;
    }

    private void Write()
    {
        if (Selected == _raw) { RawWrite = true; DialogResult = DialogResult.OK; return; }
        var o = Build();
        if (o == null) return;
        Options = o;
        WriteToDisk = true;
        DialogResult = DialogResult.OK;
    }

    private void Save(string folder)
    {
        var o = Build();
        if (o == null) return;
        bool amiga = o.System is BootSystem.AmigaFfs or BootSystem.AmigaOfs;
        string name = o.System switch
        {
            BootSystem.FreeDos => "freedos-boot.img",
            BootSystem.MsDosFromWindows => "msdos-boot.img",
            BootSystem.AtariAuto => Path.GetFileNameWithoutExtension(o.AtariProgramName ?? "auto") + ".st",
            _ => "amiga-boot.adf",
        };
        using var dlg = new SaveFileDialog
        {
            FileName = name, InitialDirectory = folder,
            Filter = amiga ? "Amiga (*.adf)|*.adf" : o.System == BootSystem.AtariAuto ? "Atari ST (*.st)|*.st|Atari MSA (*.msa)|*.msa" : Tr.T("PC / obecný obraz") + " (*.img)|*.img|(*.ima)|*.ima",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Options = o;
        SavePath = dlg.FileName;
        DialogResult = DialogResult.OK;
    }

    private void ShowOtherSystems()
    {
        using var f = new DialogBase2(Tr.T("Jak se z diskety spouštěly jiné počítače"));
        f.ShowDialog(this);
    }

    /// <summary>Jednoduché okno s přehledem (text se dá kopírovat).</summary>
    private sealed class DialogBase2 : DialogBase
    {
        public DialogBase2(string title) : base(title, new Size(760, 600))
        {
            var box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Panel, Font = Theme.UiFont(9.5f) };
            foreach (var (system, text) in BootDisks.OtherSystems)
            {
                box.SelectionFont = Theme.UiFont(10f, FontStyle.Bold);
                box.AppendText(system + "\n");
                box.SelectionFont = Theme.UiFont(9.5f);
                box.AppendText(text + "\n\n");
            }
            Body.Controls.Add(box);
            AddButton(Tr.T("Zavřít"), DialogResult.Cancel, primary: true);
            Finish();
        }
    }
}
