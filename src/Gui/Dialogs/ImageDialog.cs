using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.Fs.Amiga;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>
/// Obsah obrazu diskety: formát, systém, jestli ho zapíše běžná mechanika, seznam souborů.
/// Výsledek: Yes = zapsat na disketu, Retry = kontrola a oprava, Cancel = zavřít.
/// </summary>
internal sealed class ImageDialog : DialogBase
{
    private readonly LoadedImage _loaded;

    public ImageDialog(string path, LoadedImage loaded, string folder)
        : base(Tr.F("Obraz — {0}", Path.GetFileName(path)), new Size(780, 620))
    {
        _loaded = loaded;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(top, 0, 0);

        void Line(string name, string value, Color? color = null)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 1) };
            row.Controls.Add(new Label { Text = name, Width = 170, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 6, 0) });
            row.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = color ?? Theme.Text, Margin = new Padding(0, 2, 0, 0) });
            top.Controls.Add(row);
        }

        top.Controls.Add(Heading(Path.GetFileName(path)));
        Line(Tr.T("Typ souboru"), ImageFile.Describe(loaded.Kind));
        Line(Tr.T("Velikost souboru"), Tr.F("{0:N0} B", loaded.FileSize));

        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        list.Columns.Add(Tr.T("Soubor"), 380);
        list.Columns.Add(Tr.T("Velikost"), 110, HorizontalAlignment.Right);
        list.Columns.Add(Tr.T("Změněno"), 150);
        bool hasFiles = false;

        if (loaded.Image == null)
        {
            top.Controls.Add(Note(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst."), Theme.Warn, 720));
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

            if (fs.CanCheck && Fat12Volume.TryOpen(img.Data, out _) is { } fat)
            {
                var check = FatDoctor.Check(img.Data);
                int problems = check.Issues.Count(i => i.Severity != IssueSeverity.Info);
                Line(Tr.T("Souborový systém"), Tr.F("FAT12, {0} KB, volno {1} KB", fat.CapacityBytes / 1024, fat.FreeBytes / 1024));
                Line(Tr.T("Kontrola FAT"), problems == 0 ? Tr.T("v pořádku") : Tr.F("{0} problémů — spusť kontrolu a opravu", problems),
                     problems == 0 ? Theme.Ok : Theme.Warn);
                foreach (var e in fat.ListAll())
                {
                    var it = new ListViewItem(e.Path + (e.IsDirectory ? "\\" : ""));
                    it.SubItems.Add(e.IsDirectory ? Tr.T("<složka>") : e.Size.ToString("N0"));
                    it.SubItems.Add(e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "");
                    if (e.IsDirectory) it.ForeColor = Theme.Accent;
                    list.Items.Add(it);
                    hasFiles = true;
                }
            }
            else if (fs.Kind == FsKind.Amiga && AmigaDos.TryOpen(img.Data) is { } vol)
            {
                Line(Tr.T("Volno"), Tr.F("{0} KB z {1} KB", vol.FreeBlocks / 2, vol.Blocks / 2));
                foreach (var e in vol.ListAll())
                {
                    var it = new ListViewItem(e.Path + (e.IsDirectory ? "/" : ""));
                    it.SubItems.Add(e.IsDirectory ? Tr.T("<složka>") : e.Size.ToString("N0"));
                    it.SubItems.Add(e.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "");
                    if (e.IsDirectory) it.ForeColor = Theme.Accent;
                    list.Items.Add(it);
                    hasFiles = true;
                }
            }
            if (!hasFiles && (fs.CanCheck || fs.Kind == FsKind.Amiga))
                list.Items.Add(new ListViewItem(Tr.T("(prázdná disketa)")) { ForeColor = Theme.TextDim });
            else if (!hasFiles)
                list.Items.Add(new ListViewItem(Tr.T("(výpis souborů u tohoto systému zatím neumím)")) { ForeColor = Theme.TextDim });
        }
        layout.Controls.Add(list, 0, 1);
        Body.Controls.Add(layout);

        AddButton(Tr.T("Zavřít"), DialogResult.Cancel);
        var write = AddButton(Tr.T("Zapsat na disketu…"), DialogResult.Yes, primary: true);
        write.Enabled = loaded.Image != null;
        var check2 = AddButton(Tr.T("Kontrola a oprava"), DialogResult.Retry);
        check2.Enabled = loaded.Image != null && FileSystemDetector.Detect(loaded.Image.Data).CanCheck;
        if (Path.GetExtension(path).Equals(".scp", StringComparison.OrdinalIgnoreCase))
        {
            var hist = AddButton(Tr.T("Histogram toku…"), DialogResult.None);
            hist.Click += (_, _) =>
            {
                try { using var h = new FluxHistogramForm(path, 0, 0); h.ShowDialog(this); }
                catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            AddButton(Tr.T("Záchrana sektorů…"), DialogResult.Ignore);
        }
        var extract = AddButton(Tr.T("Rozbalit soubory…"), DialogResult.None);
        extract.Enabled = hasFiles;
        extract.Click += (_, _) =>
        {
            using var f = new FolderBrowserDialog { Description = Tr.T("Kam rozbalit soubory z obrazu"), SelectedPath = folder };
            if (f.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string target = Path.Combine(f.SelectedPath, Path.GetFileNameWithoutExtension(path));
                int n = Extractor.Extract(_loaded.Image!, target);
                MessageBox.Show(this, Tr.F("Rozbaleno souborů: {0} → {1}", n, target), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        Finish();
    }
}
