using System.Linq;
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Nastavení mechanik, Greaseweazle, záloh a jazyků.</summary>
internal sealed class SettingsDialog : DialogBase
{
    private readonly AppSettings _s;
    private readonly CheckBox _stepOn = new() { AutoSize = true };
    private readonly NumericUpDown _step = new() { Minimum = 1, Maximum = 30, Width = 60 };
    private readonly CheckBox _fdraw = new() { AutoSize = true };
    private readonly TextBox _gwPath = new() { Width = 380 };
    private readonly ComboBox _gwDrive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly NumericUpDown _revs = new() { Minimum = 1, Maximum = 10, Width = 60 };
    private readonly NumericUpDown _tracks = new() { Minimum = 40, Maximum = 86, Width = 60 };
    private readonly TextBox _backup = new() { Width = 380 };

    public enum SetupAction { None, InstallGw, GwFirmware, InstallFdrawcmd }

    /// <summary>Uživatel chce po zavření nastavení spustit instalaci / aktualizaci.</summary>
    public SetupAction Request { get; private set; }

    private Button ActionButton(string text, SetupAction a)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 8, 6) };
        b.Click += (_, _) => { Apply(); Request = a; DialogResult = DialogResult.OK; };
        return b;
    }

    public SettingsDialog(AppSettings settings, System.Collections.Generic.IReadOnlyList<AtariFloppy.Core.Drives.DetectedDrive>? drives = null)
        : base(Tr.T("Nastavení"), new Size(640, 640))
    {
        _s = settings;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        flow.Controls.Add(Heading(Tr.T("Interní mechanika (fdrawcmd.sys)")));
        _stepOn.Text = Tr.T("Vlastní rychlost krokování hlavy (step rate), ms:");
        _stepOn.Checked = settings.Drive.StepRateMs != null;
        _step.Value = settings.Drive.StepRateMs ?? 6;
        _step.Enabled = _stepOn.Checked;
        _stepOn.CheckedChanged += (_, _) => _step.Enabled = _stepOn.Checked && _stepOn.Enabled;
        flow.Controls.Add(Row(_stepOn, _step));
        flow.Controls.Add(Note(Tr.T("Atari má standardně 3 ms; starší nebo opotřebené mechaniky potřebují 6 ms. Funguje jen s fdrawcmd.sys a Greaseweazle — USB disketovka si rychlost řídí sama.")));
        var list = drives ?? new System.Collections.Generic.List<AtariFloppy.Core.Drives.DetectedDrive>();
        bool lowLevel = list.Any(d => d.Kind == AtariFloppy.Core.Drives.DriveKind.Greaseweazle ||
                                      d.Kind == AtariFloppy.Core.Drives.DriveKind.Internal && d.FdrawcmdInstalled);
        bool anyFdraw = list.Any(d => d.Kind == AtariFloppy.Core.Drives.DriveKind.Internal && d.FdrawcmdInstalled);
        if (!lowLevel)
        {
            _stepOn.Enabled = _step.Enabled = false;
            flow.Controls.Add(Note(Tr.T("Teď nejde nastavit: připojená mechanika (USB disketovka) si rychlost krokování řídí sama a změnit ji nedovolí. Volba se zpřístupní, až bude připojená interní mechanika s fdrawcmd.sys nebo Greaseweazle."), Theme.Warn));
        }
        _fdraw.Text = Tr.T("Vždy použít fdrawcmd.sys (i pro standardní diskety)");
        _fdraw.Checked = settings.Drive.ForceFdraw;
        _fdraw.Enabled = anyFdraw;
        flow.Controls.Add(_fdraw);
        string? fdVer = null;
        bool fdOk = false;
        try { fdOk = AtariFloppy.Core.Drives.Windows.FdrawCmd.IsInstalled(out fdVer); } catch { }
        flow.Controls.Add(Note(fdOk ? Tr.F("fdrawcmd.sys je nainstalovaný (verze {0}).", fdVer) : Tr.T("fdrawcmd.sys není nainstalovaný. Má smysl jen na počítači s disketovým řadičem na základní desce."),
                               fdOk ? Theme.Ok : Theme.TextDim));
        flow.Controls.Add(ActionButton(fdOk ? Tr.T("Přeinstalovat fdrawcmd.sys…") : Tr.T("Nainstalovat fdrawcmd.sys…"), SetupAction.InstallFdrawcmd));

        flow.Controls.Add(new Label { Height = 10 });
        flow.Controls.Add(Heading("Greaseweazle"));
        _gwPath.Text = settings.Drive.GwPath ?? "";
        flow.Controls.Add(new Label { Text = Tr.T("Program gw.exe (prázdné = najít automaticky):"), AutoSize = true });
        flow.Controls.Add(Row(_gwPath, Browse(() =>
        {
            using var o = new OpenFileDialog { Filter = "gw.exe|gw.exe|" + Tr.T("Programy") + " (*.exe)|*.exe" };
            if (o.ShowDialog(this) == DialogResult.OK) _gwPath.Text = o.FileName;
        })));
        var found = GwTool.Find(settings.Drive.GwPath);
        string? gwVer = found != null ? AtariFloppy.Core.App.GreaseweazleSetup.VersionOf(found.Path) : null;
        flow.Controls.Add(Note(found != null ? Tr.F("Nalezeno: {0}", found.Path) + (gwVer != null ? Tr.F(" (verze {0})", gwVer) : "") : Tr.T("gw.exe jsem nenašel."),
                               found != null ? Theme.Ok : Theme.Warn));
        flow.Controls.Add(Row(ActionButton(found != null ? Tr.T("Aktualizovat gw.exe z GitHubu…") : Tr.T("Stáhnout gw.exe z GitHubu…"), SetupAction.InstallGw),
                              ActionButton(Tr.T("Aktualizovat firmware v Greaseweazle…"), SetupAction.GwFirmware)));
        _gwDrive.Items.AddRange(new object[] { "A", "B", "0", "1", "2" });
        _gwDrive.SelectedItem = settings.Drive.GwDrive;
        if (_gwDrive.SelectedIndex < 0) _gwDrive.SelectedIndex = 0;
        flow.Controls.Add(Row(new Label { Text = Tr.T("Mechanika na kabelu:"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _gwDrive));
        _revs.Value = settings.Drive.Revs;
        _tracks.Value = settings.Drive.FluxTracks ?? 82;
        flow.Controls.Add(Row(new Label { Text = Tr.T("Záloha toku — otáček na stopu:"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _revs,
                              new Label { Text = Tr.T("stop:"), AutoSize = true, Margin = new Padding(16, 6, 6, 0) }, _tracks));

        flow.Controls.Add(new Label { Height = 10 });
        flow.Controls.Add(Heading(Tr.T("Zálohy")));
        _backup.Text = settings.BackupFolder;
        flow.Controls.Add(new Label { Text = Tr.T("Výchozí složka pro zálohy (před zápisovým testem a opravou se program vždy zeptá, kam uložit):"), AutoSize = true });
        flow.Controls.Add(Row(_backup, Browse(() =>
        {
            using var f = new FolderBrowserDialog { SelectedPath = _backup.Text };
            if (f.ShowDialog(this) == DialogResult.OK) _backup.Text = f.SelectedPath;
        })));

        flow.Controls.Add(new Label { Height = 10 });
        flow.Controls.Add(Heading(Tr.T("Jazyky")));
        flow.Controls.Add(Note(Tr.F("Další jazyk přidáš souborem xx.lang ve složce {0}. Šablona obsahuje všechny texty programu s anglickým překladem — přepiš text za „ = “ a soubor ulož.", Tr.LangFolder)));
        var tmpl = new Button { Text = Tr.T("Vytvořit šablonu nového jazyka…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        tmpl.Click += (_, _) => SaveTemplate();
        flow.Controls.Add(tmpl);

        Body.Controls.Add(flow);
        AddCancel();
        var ok = AddButton(Tr.T("Uložit"), DialogResult.OK, primary: true);
        ok.Click += (_, _) => Apply();
        Finish();
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 4) };
        p.Controls.AddRange(controls);
        return p;
    }

    private static Button Browse(Action onClick)
    {
        var b = new Button { Text = Tr.T("Procházet…"), AutoSize = true, FlatStyle = FlatStyle.Flat };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Apply()
    {
        _s.Drive.StepRateMs = _stepOn.Checked ? (int)_step.Value : null;
        _s.Drive.ForceFdraw = _fdraw.Checked;
        _s.Drive.GwPath = _gwPath.Text.Trim().Length > 0 ? _gwPath.Text.Trim() : null;
        _s.Drive.GwDrive = _gwDrive.SelectedItem as string ?? "A";
        _s.Drive.Revs = (int)_revs.Value;
        _s.Drive.FluxTracks = (int)_tracks.Value == 82 ? null : (int)_tracks.Value;
        _s.BackupFolder = _backup.Text.Trim().Length > 0 ? _backup.Text.Trim() : AppSettings.DefaultBackupFolder;
    }

    private void SaveTemplate()
    {
        string? text = Tr.EmbeddedText("en");
        if (text == null) return;
        using var s = new SaveFileDialog { Filter = Tr.T("Jazykový soubor") + " (*.lang)|*.lang", FileName = "xx.lang" };
        try { Directory.CreateDirectory(Tr.LangFolder); s.InitialDirectory = Tr.LangFolder; } catch { }
        if (s.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder();
        sb.AppendLine("#language: " + Tr.T("Název jazyka"));
        sb.AppendLine("# " + Tr.T("Každý řádek: český originál = překlad. Přepiš text za „ = “, {0} {1} … nech na místě."));
        foreach (var line in text.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.StartsWith("#language:")) continue;
            sb.AppendLine(l);
        }
        File.WriteAllText(s.FileName, sb.ToString(), new UTF8Encoding(false));
        MessageBox.Show(this, Tr.F("Šablona uložena: {0}", s.FileName), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
