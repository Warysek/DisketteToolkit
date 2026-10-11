using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>
/// Výběr formátu ze všech systémů. U každého je vidět, jestli ho jde s touto mechanikou a disketou
/// vytvořit, a když ne, proč. Bez mechaniky slouží k vytvoření prázdného obrazu.
/// </summary>
internal sealed class FormatDialog : DialogBase
{
    private readonly DetectedDrive? _drive;
    private readonly ListView _list = new();
    private readonly Label _title = Heading("");
    private readonly Label _usedFor = Note("", Theme.Text, 330);
    private readonly Label _geometry = Note("", null, 330);
    private readonly Label _reason = Note("", null, 330);
    private readonly Label _note = Note("", Theme.Warn, 330);
    private readonly Label _labelCaption = new() { AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
    private readonly TextBox _label = new() { Width = 200, MaxLength = 11, CharacterCasing = CharacterCasing.Upper };
    private readonly RadioButton _full = new() { AutoSize = false, Size = new Size(330, 36), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft, Checked = true };
    private readonly RadioButton _quick = new() { AutoSize = false, Size = new Size(330, 36), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft };
    private readonly CheckBox _verify = new() { AutoSize = false, Size = new Size(330, 36), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft, Checked = true };
    private readonly Button _formatBtn;
    private readonly Button _imageBtn;
    private readonly Dictionary<ListViewItem, ProfileVerdict> _verdicts = new();

    public FormatProfile? Profile { get; private set; }
    /// <summary>Když není null, uživatel chce jen prázdný obraz do tohoto souboru.</summary>
    public string? ImagePath { get; private set; }
    public string? DiskLabel => _label.Text.Trim().Length > 0 ? _label.Text.Trim() : null;
    public bool Quick => _quick.Checked && _quick.Enabled;
    public bool Verify => _verify.Checked;

    public FormatDialog(DetectedDrive? drive)
        : base(drive != null ? Tr.F("Formátování — {0} {1}", drive.Id, drive.KindText) : Tr.T("Nový prázdný obraz diskety"), new Size(940, 620))
    {
        _drive = drive;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        string intro = drive == null
            ? Tr.T("Vyber systém a formát. Prázdný obraz můžeš použít v emulátoru, na Goteku nebo ho později zapsat na disketu.")
            : drive.Kind == DriveKind.Greaseweazle
                ? Tr.T("Greaseweazle zvládne všechny formáty; hustotu diskety nepozná — vlož správnou (DD / HD).")
                : Tr.F("Vložená disketa: {0}. Formáty, které teď nejdou, jsou šedé — vpravo uvidíš proč.", drive.MediaText);
        var introLabel = Note(intro, null, 880);
        layout.Controls.Add(introLabel, 0, 0);
        layout.SetColumnSpan(introLabel, 2);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add(Tr.T("Formát"), 330);
        _list.Columns.Add(Tr.T("Stav"), 180);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => { if (_formatBtn!.Enabled) _formatBtn.PerformClick(); };
        layout.Controls.Add(_list, 0, 1);

        var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false, Padding = new Padding(14, 0, 0, 0) };
        _labelCaption.Text = Tr.T("Jmenovka diskety (nepovinná):");
        _full.Text = Tr.T("Úplný formát (nízkoúrovňově, s kontrolou povrchu)");
        _quick.Text = Tr.T("Rychlý — jen smazat obsah (disketa už má tento formát)");
        _verify.Text = Tr.T("Zkontrolovat povrch / ověřit zápis");
        side.Controls.AddRange(new Control[] { _title, _usedFor, _geometry, _reason, _note, _labelCaption, _label });
        if (drive != null) side.Controls.AddRange(new Control[] { new Label { Height = 8 }, _full, _quick, _verify });
        layout.Controls.Add(side, 1, 1);
        Body.Controls.Add(layout);

        AddCancel();
        if (drive != null)
        {
            _formatBtn = AddButton(Tr.T("Formátovat"), DialogResult.None, primary: true);
            _formatBtn.Click += (_, _) => { Profile = Current?.Profile; DialogResult = DialogResult.OK; };
            _imageBtn = AddButton(Tr.T("Jen prázdný obraz…"), DialogResult.None);
        }
        else
        {
            _imageBtn = AddButton(Tr.T("Vytvořit obraz…"), DialogResult.None, primary: true);
            _formatBtn = new Button();
        }
        _imageBtn.Click += (_, _) => SaveImage();

        Fill();
        Finish();
    }

    private ProfileVerdict? Current => _list.SelectedItems.Count > 0 && _verdicts.TryGetValue(_list.SelectedItems[0], out var v) ? v : null;

    private ProfileVerdict Evaluate(FormatProfile p)
    {
        if (_drive != null) return FormatProfiles.Evaluate(p, _drive, _drive.Kind == DriveKind.Greaseweazle ? InsertedMedia.Unknown : _drive.Media);
        return p.Build != null
            ? new ProfileVerdict(p, ProfileStatus.Ready, Tr.T("Obraz jde vytvořit."))
            : new ProfileVerdict(p, ProfileStatus.Planned, p.Note ?? Tr.T("Tento formát program zatím neumí vytvořit."));
    }

    private void Fill()
    {
        _list.BeginUpdate();
        var groups = new Dictionary<string, ListViewGroup>();
        ListViewItem? first = null;
        foreach (var p in FormatProfiles.All)
        {
            var v = Evaluate(p);
            if (!groups.TryGetValue(p.System, out var g))
            {
                g = new ListViewGroup(p.System, p.System);
                groups[p.System] = g;
                _list.Groups.Add(g);
            }
            var (glyph, color) = Theme.Mark(v.Status);
            var item = new ListViewItem(glyph + "  " + p.Name, g) { UseItemStyleForSubItems = true };
            item.SubItems.Add(StatusText(v.Status));
            item.ForeColor = v.IsReady ? Theme.Text : Theme.TextDim;
            _list.Items.Add(item);
            _verdicts[item] = v;
            if (first == null && v.IsReady) first = item;
        }
        _list.EndUpdate();
        if (first != null) { first.Selected = true; first.Focused = true; first.EnsureVisible(); }
        else if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        ShowSelected();
    }

    private static string StatusText(ProfileStatus s) => s switch
    {
        ProfileStatus.Ready => Tr.T("jde"),
        ProfileStatus.NoDisk => Tr.T("vlož disketu"),
        ProfileStatus.WrongMedia => Tr.T("jiná disketa (DD/HD)"),
        ProfileStatus.NeedsDriver => Tr.T("potřebuje ovladač / program"),
        ProfileStatus.Planned => Tr.T("zatím neumím"),
        _ => Tr.T("tato mechanika neumí"),
    };

    private void ShowSelected()
    {
        var v = Current;
        if (v == null)
        {
            _title.Text = _usedFor.Text = _geometry.Text = _reason.Text = _note.Text = "";
            _formatBtn.Enabled = _imageBtn.Enabled = false;
            return;
        }
        var p = v.Profile;
        _title.Text = p.Title;
        _usedFor.Text = p.UsedFor;
        _geometry.Text = p.Geometry.ToString();
        _reason.Text = v.Reason;
        _reason.ForeColor = v.IsReady ? Theme.Ok : Theme.Warn;
        _note.Text = (p.Experimental ? Tr.T("Experimentální formát.") + " " : "") + (p.Note ?? "");
        _note.Visible = _note.Text.Length > 0;
        _label.Enabled = _labelCaption.Enabled = p.SupportsLabel;
        _formatBtn.Enabled = v.IsReady && (p.Build != null || p.FatStyle != null);
        _imageBtn.Enabled = p.Build != null;

        bool fatDs = p.FatStyle != null && p.Geometry.Sides == 2;
        bool quickPossible = _drive != null && _drive.Kind != DriveKind.Greaseweazle && fatDs && _drive.MediaState == MediaState.Ready &&
                             _drive.FormattedAs?.SectorsPerTrack == p.Geometry.SectorsPerTrack;
        _quick.Enabled = quickPossible;
        if (!quickPossible) _full.Checked = true;
    }

    private void SaveImage()
    {
        var p = Current?.Profile;
        if (p?.Build == null) return;
        using var dlg = new SaveFileDialog
        {
            Title = Tr.T("Uložit prázdný obraz"),
            FileName = Tr.T("prazdna") + p.ImageExtension,
            Filter = ImageFilters.Save(false) + "|" + Tr.T("Všechny soubory") + " (*.*)|*.*",
            FilterIndex = p.ImageExtension switch { ".st" => 1, ".msa" => 2, ".adf" => 4, _ => 3 },
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string path = dlg.FileName;
        if (Path.GetExtension(path).Length == 0) path += p.ImageExtension;
        Profile = p;
        ImagePath = path;
        DialogResult = DialogResult.OK;
    }
}
