using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Okno, které chce soubory přetažené z Průzkumníku (viz MainForm.DropFilter).</summary>
internal interface IFileDropTarget
{
    void FilesDropped(string[] files);
}

/// <summary>
/// Obsah obrazu diskety: formát, systém, jestli ho zapíše běžná mechanika, seznam souborů. U FAT12 jde soubory
/// přidat (i přetažením) a smazat, smazané soubory jsou v seznamu šedě a Zachránit soubory… uloží všechno čitelné.
/// Výsledek: Yes = zapsat na disketu, Retry = kontrola a oprava, Ignore = záchrana sektorů z toku (.scp), Cancel = zavřít.
/// </summary>
internal sealed class ImageDialog : DialogBase, IFileDropTarget
{
    private readonly string _path;
    private readonly LoadedImage _loaded;
    private DiskImage? _img;   // po úpravách novější obsah než _loaded.Image
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _status = Note("", null, 760);
    private readonly bool _fat;
    private readonly string? _cannotEdit;   // proč úpravy nejdou (null = jdou)
    private Label? _sizeValue, _fsValue, _checkValue;
    private TextBox? _md5, _sha1, _sha256;

    public ImageDialog(string path, LoadedImage loaded, string folder)
        : base(Tr.F("Obraz — {0}", Path.GetFileName(path)), new Size(860, 660))
    {
        _path = path;
        _loaded = loaded;
        _img = loaded.Image;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(top, 0, 0);

        Label Line(string name, string value, Color? color = null)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 1) };
            row.Controls.Add(new Label { Text = name, Width = 170, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 6, 0) });
            var v = new Label { Text = value, AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = color ?? Theme.Text, Margin = new Padding(0, 2, 0, 0) };
            row.Controls.Add(v);
            top.Controls.Add(row);
            return v;
        }
        // kontrolní součet jde označit a zkopírovat (porovnání s jinou zálohou nebo se seznamem TOSEC)
        TextBox CopyLine(string name, string value)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 1) };
            row.Controls.Add(new Label { Text = name, Width = 170, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 6, 0) });
            var box = new TextBox { Text = value, ReadOnly = true, TabStop = false, BorderStyle = BorderStyle.None, Width = 600, BackColor = Theme.Panel, ForeColor = Theme.Text, Margin = new Padding(0, 3, 0, 0) };
            row.Controls.Add(box);
            top.Controls.Add(row);
            return box;
        }

        top.Controls.Add(Heading(Path.GetFileName(path)));
        Line(Tr.T("Typ souboru"), ImageFile.Describe(loaded.Kind));
        foreach (var w in loaded.Warnings) Line(Tr.T("Pozor"), w, Theme.Warn);
        if (loaded.Text != null)
        {
            var lines = loaded.Text.Split('\n');
            Line(Tr.T("Text v archivu"), string.Join("\n", lines.Take(12)) + (lines.Length > 12 ? "\n…" : ""), Theme.TextDim);
        }
        _sizeValue = Line(Tr.T("Velikost souboru"), Tr.F("{0:N0} B", loaded.FileSize));
        try
        {
            var sums = Checksums.Of(path);
            _md5 = CopyLine("MD5", sums.Md5);
            _sha1 = CopyLine("SHA-1", sums.Sha1);
            _sha256 = CopyLine("SHA-256", sums.Sha256);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        _list.Columns.Add(Tr.T("Soubor"), 330);
        _list.Columns.Add(Tr.T("Velikost"), 90, HorizontalAlignment.Right);
        _list.Columns.Add(Tr.T("Změněno"), 130);
        _list.Columns.Add(Tr.T("Poznámka"), 190);
        bool hasFiles = false;

        if (loaded.Image == null)
        {
            top.Controls.Add(Note(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."), Theme.Warn, 760));
        }
        else
        {
            var img = loaded.Image;
            var fs = FileSystemDetector.Detect(img.Data);
            Line(Tr.T("Formát"), img.Geometry.ToString());
            Line(Tr.T("Systém"), fs.Name + (fs.Details != null ? " — " + fs.Details : ""));
            Line(Tr.T("K čemu"), fs.UsedFor, Theme.TextDim);
            if (fs.CanCheck || fs.Kind is FsKind.OwnLoader or FsKind.Unknown)
            {
                var bs = img.BootSector;
                Line(Tr.T("Boot sektor"), bs.DescribeStyle());
                if (bs.IsExecutable) Line(Tr.T("Spustitelný (Atari)"), Tr.T("ANO — boot sektor obsahuje program (hra, demo, nebo boot virus)"), Theme.Warn);
            }

            var a = ImageAnalysis.AssessForStandardDrive(loaded);
            var color = a.Verdict == Verdict.Writable ? Theme.Ok : a.Verdict == Verdict.ContentOnly ? Theme.Warn : Theme.Danger;
            Line(Tr.T("USB / běžná mechanika"), a.Summary, color);

            _fat = fs.CanCheck && Fat12Volume.TryOpen(img.Data, out _) != null;
            if (_fat)
            {
                _fsValue = Line(Tr.T("Souborový systém"), "");
                _checkValue = Line(Tr.T("Kontrola FAT"), "");
                UpdateFat();
            }
            else if (fs.Kind == FsKind.Amiga && AmigaDos.TryOpen(img.Data) is { } vol)
                Line(Tr.T("Volno"), Tr.F("{0} KB z {1} KB", vol.FreeBlocks / 2, vol.Blocks / 2));
            hasFiles = FillList();
        }
        _list.Visible = loaded.Image != null;   // nenačtený obraz (tok, cizí formát) nemá co vypsat
        layout.Controls.Add(_list, 0, 2);

        // úpravy (jen FAT12): přidat, smazat; stav poslední změny
        _cannotEdit = path.StartsWith(ZipImages.TempRoot, StringComparison.OrdinalIgnoreCase) ? Tr.T("Obraz ze .zip upravit nejde — nejdřív ho ulož jako samostatný soubor (Převést formát…).")
                    : !ImageEdit.CanSave(path) ? Tr.F("Do formátu {0} ukládat neumím, takže úpravy nejdou.", Path.GetExtension(path).ToUpperInvariant())
                    : IsReadOnly(path) ? Tr.T("Soubor je jen pro čtení — úpravy nejdou.")
                    : null;
        if (_fat)
        {
            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 6, 0, 4) };
            var add = new Button { Text = Tr.T("Přidat soubory…"), AutoSize = true, FlatStyle = FlatStyle.Flat, Enabled = _cannotEdit == null };
            var del = new Button { Text = Tr.T("Smazat"), AutoSize = true, FlatStyle = FlatStyle.Flat, Enabled = _cannotEdit == null };
            add.Click += (_, _) =>
            {
                using var o = new OpenFileDialog { Title = Tr.T("Soubory, které přidat do obrazu"), Multiselect = true, InitialDirectory = folder };
                if (o.ShowDialog(this) == DialogResult.OK) AddFiles(o.FileNames);
            };
            del.Click += (_, _) => DeleteSelected();
            _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && _cannotEdit == null) DeleteSelected(); };
            bar.Controls.Add(add);
            bar.Controls.Add(del);
            bar.Controls.Add(Note(_cannotEdit ?? Tr.T("Soubory sem jde přetáhnout i z Průzkumníku (do vybrané složky, jinak do kořene). Před první změnou se uloží kopie původního obrazu."),
                                  _cannotEdit != null ? Theme.Warn : Theme.TextDim, 560));
            _status.Margin = new Padding(0, 4, 0, 0);
            bar.SetFlowBreak(bar.Controls[bar.Controls.Count - 1], true);
            bar.Controls.Add(_status);
            layout.Controls.Add(bar, 0, 1);
        }
        Body.Controls.Add(layout);

        AddButton(Tr.T("Zavřít"), DialogResult.Cancel);
        var write = AddButton(Tr.T("Zapsat na disketu…"), DialogResult.Yes, primary: true);
        write.Enabled = loaded.Image != null || DeviceFactory.IsFlux(path);   // tok zapíše Greaseweazle
        if (Path.GetExtension(path).Equals(".scp", StringComparison.OrdinalIgnoreCase))
        {
            // tok: kontrola a rozbalení nedávají smysl, místo nich histogram a záchrana sektorů
            var hist = AddButton(Tr.T("Histogram toku…"), DialogResult.None);
            hist.Click += (_, _) =>
            {
                try { using var h = new FluxHistogramForm(path, 0, 0); h.ShowDialog(this); }
                catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            AddButton(Tr.T("Záchrana sektorů…"), DialogResult.Ignore);
            Finish();
            return;
        }
        var check2 = AddButton(Tr.T("Kontrola a oprava"), DialogResult.Retry);
        check2.Enabled = loaded.Image != null && FileSystemDetector.Detect(loaded.Image.Data).CanCheck;
        var extract = AddButton(Tr.T("Rozbalit soubory…"), DialogResult.None);
        extract.Enabled = hasFiles;
        extract.Click += (_, _) =>
        {
            using var f = new FolderBrowserDialog { Description = Tr.T("Kam rozbalit soubory z obrazu"), SelectedPath = folder };
            if (f.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string target = Path.Combine(f.SelectedPath, Path.GetFileNameWithoutExtension(path));
                int n = Extractor.Extract(_img!, target);
                MessageBox.Show(this, Tr.F("Rozbaleno souborů: {0} → {1}", n, target), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        if (_fat)
        {
            var rescue = AddButton(Tr.T("Zachránit soubory…"), DialogResult.None);
            rescue.Click += (_, _) => RescueFiles(folder);
        }
        Finish();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_fat && _cannotEdit == null) MainForm.EnableFileDrop(this);
    }

    public void FilesDropped(string[] files)
    {
        if (!_fat) return;
        if (_cannotEdit != null) { SetStatus(_cannotEdit, Theme.Warn); return; }
        AddFiles(files);
    }

    private static bool IsReadOnly(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // ------------------------------------------------------------------ seznam souborů

    private bool FillList()
    {
        var img = _img!;
        var fs = FileSystemDetector.Detect(img.Data);
        bool hasFiles = false;
        _list.BeginUpdate();
        _list.Items.Clear();
        if (fs.CanCheck && Fat12Volume.TryOpen(img.Data, out _) is { } fat)
        {
            foreach (var e in fat.ListAll())
            {
                var it = new ListViewItem(e.Path + (e.IsDirectory ? "\\" : "")) { Tag = e };
                it.SubItems.Add(e.IsDirectory ? Tr.T("<složka>") : e.Size.ToString("N0"));
                it.SubItems.Add(e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "");
                if (e.IsDirectory) it.ForeColor = Theme.Accent;
                _list.Items.Add(it);
                hasFiles = true;
            }
            foreach (var d in fat.ListDeleted())
            {
                var it = new ListViewItem(d.Path + (d.IsDirectory ? "\\" : "")) { ForeColor = Theme.TextDim };
                it.SubItems.Add(d.IsDirectory ? Tr.T("<složka>") : d.Size.ToString("N0"));
                it.SubItems.Add(d.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "");
                it.SubItems.Add(d.Recoverable ? Tr.T("smazaný — jde zachránit") : Tr.T("smazaný — přepsaný"));
                _list.Items.Add(it);
            }
        }
        else if (fs.Kind == FsKind.Amiga && AmigaDos.TryOpen(img.Data) is { } vol)
        {
            foreach (var e in vol.ListAll())
            {
                var it = new ListViewItem(e.Path + (e.IsDirectory ? "/" : ""));
                it.SubItems.Add(e.IsDirectory ? Tr.T("<složka>") : e.Size.ToString("N0"));
                it.SubItems.Add(e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "");
                if (e.IsDirectory) it.ForeColor = Theme.Accent;
                _list.Items.Add(it);
                hasFiles = true;
            }
        }
        if (!hasFiles && _list.Items.Count == 0)
            _list.Items.Add(new ListViewItem(fs.CanCheck || fs.Kind == FsKind.Amiga ? Tr.T("(prázdná disketa)") : Tr.T("(výpis souborů u tohoto systému zatím neumím)")) { ForeColor = Theme.TextDim });
        _list.EndUpdate();
        return hasFiles;
    }

    private void UpdateFat()
    {
        if (_fsValue == null || _checkValue == null || Fat12Volume.TryOpen(_img!.Data, out _) is not { } fat) return;
        var check = FatDoctor.Check(_img.Data);
        int problems = check.Issues.Count(i => i.Severity != IssueSeverity.Info);
        _fsValue.Text = Tr.F("FAT12, {0} KB, volno {1} KB", fat.CapacityBytes / 1024, fat.FreeBytes / 1024);
        _checkValue.Text = problems == 0 ? Tr.T("v pořádku") : Tr.F("{0} problémů — spusť kontrolu a opravu", problems);
        _checkValue.ForeColor = problems == 0 ? Theme.Ok : Theme.Warn;
    }

    private void SetStatus(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

    // ------------------------------------------------------------------ úpravy

    /// <summary>Vybraná složka v seznamu = kam přidávat; jinak kořen.</summary>
    private string? SelectedDir() =>
        _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is FatEntry { IsDirectory: true } d ? d.Path : null;

    private void AddFiles(IEnumerable<string> paths)
    {
        List<EditItem> plan;
        try { plan = ImageEdit.Plan(paths, SelectedDir()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(e.Message, Theme.Danger); return; }
        if (plan.Count == 0) return;
        var existing = ImageEdit.Existing(_img!, plan);
        bool replace = false;
        if (existing.Count > 0)
        {
            string names = string.Join(", ", existing.Take(8)) + (existing.Count > 8 ? " …" : "");
            var answer = MessageBox.Show(this, Tr.F("V obrazu už je: {0}\n\nNahradit? (Ne = tyhle soubory přeskočit)", names), Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return;
            replace = answer == DialogResult.Yes;
        }
        int files = plan.Count(i => i.Source != null) - (replace ? 0 : existing.Count);
        Change(w => ImageEdit.AddFiles(w, plan, replace), Tr.F("Přidáno souborů: {0}", files));
    }

    private void DeleteSelected()
    {
        var entries = _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<FatEntry>().ToList();
        if (entries.Count == 0) { SetStatus(Tr.T("Vyber v seznamu soubory, které smazat (smazané soubory jsou šedé a smazat znovu nejdou)."), Theme.Warn); return; }
        string names = string.Join(", ", entries.Take(8).Select(e => e.Path)) + (entries.Count > 8 ? " …" : "");
        string q = Tr.F("Smazat z obrazu: {0}?", names);
        if (entries.Any(e => e.IsDirectory)) q += "\n\n" + Tr.T("Složky se smažou i s obsahem.");
        if (MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Change(w => { foreach (var e in entries) if (w.Exists(e.Path)) w.Delete(e.Path); }, Tr.F("Smazáno: {0}", entries.Count));
    }

    /// <summary>Změna na kopii, uložení obrazu (a jednou kopie původního), obnovení seznamu.</summary>
    private void Change(Action<Fat12Writer> change, string done)
    {
        try
        {
            var (img, backup) = ImageEdit.Apply(_path, _img!, change);
            _img = img;
            FillList();
            UpdateFat();
            if (_sizeValue != null) _sizeValue.Text = Tr.F("{0:N0} B", new FileInfo(_path).Length);
            if (_md5 != null && _sha1 != null && _sha256 != null)
            {
                var sums = Checksums.Of(_path);
                (_md5.Text, _sha1.Text, _sha256.Text) = (sums.Md5, sums.Sha1, sums.Sha256);
            }
            SetStatus(done + ". " + (backup != null ? Tr.F("Obraz je uložený, původní stav je v {0}.", Path.GetFileName(backup)) : Tr.T("Obraz je uložený.")), Theme.Ok);
        }
        catch (Exception e) when (e is OperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SetStatus(e.Message + " " + Tr.T("Obraz zůstal beze změny."), Theme.Danger);
        }
    }

    // ------------------------------------------------------------------ záchrana souborů

    private void RescueFiles(string folder)
    {
        using var f = new FolderBrowserDialog { Description = Tr.T("Kam uložit zachráněné soubory (vznikne v ní nová složka)"), SelectedPath = folder };
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string stem = Path.GetFileNameWithoutExtension(_path), target = Path.Combine(f.SelectedPath, stem);
            for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(f.SelectedPath, $"{stem} ({n})");
            var bad = RescueState.Load(_path, _img!.Geometry)?.Missing;
            var r = FileRescue.SaveAll(_img, target, bad);
            string msg = Tr.F("Uloženo souborů: {0}, obnovených smazaných: {1}.", r.Saved, r.Recovered.Count);
            if (r.Damaged.Count > 0) msg += "\n" + Tr.F("Souborů, kterým něco chybí: {0}.", r.Damaged.Count);
            if (r.Lost.Count > 0) msg += "\n" + Tr.F("Smazaných, které už obnovit nejde: {0}.", r.Lost.Count);
            if (r.Failed.Count > 0) msg += "\n" + Tr.F("Nepodařilo se uložit: {0}.", r.Failed.Count);
            msg += "\n\n" + Tr.F("Složka: {0}\nPodrobnosti: {1}", target, Path.GetFileName(r.ReportPath));
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, r.Damaged.Count + r.Failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is OperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
