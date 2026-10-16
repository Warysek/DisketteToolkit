using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Windows;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;
using AtariFloppy.Gui.Dialogs;

namespace AtariFloppy.Gui;

internal sealed class MainForm : Form, IJobHost
{
    private readonly AppSettings _settings;
    private readonly DeviceFactory _factory;
    private readonly bool _demo;
    private readonly SectorMapModel _map = new();

    // horní lišta
    private readonly Panel _top = new();
    private readonly Label _appTitle = new();
    private readonly Label _driveLabel = new();
    private readonly ComboBox _driveBox = new();
    private readonly Button _refreshBtn = new();
    private readonly ComboBox _langBox = new();
    private readonly Button _settingsBtn = new();

    // levý panel
    private readonly Panel _left = new();
    private readonly InfoPanel _info = new();

    // střed
    private readonly SplitContainer _split = new();
    private readonly SectorMapControl _mapCtl = new();
    private readonly Panel _progressStrip = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Button _cancelBtn = new();
    private readonly RichTextBox _log = new();

    // pravý panel s akcemi
    private readonly Panel _right = new();
    private readonly FlowLayoutPanel _actions = new();
    private readonly Label _secDisk = new(), _secImages = new(), _secHelp = new();
    private readonly ActionButton _aRead = new(), _aWrite = new(), _aFormat = new(), _aTest = new(), _aErase = new(), _aSetup = new(), _aBoot = new(), _aBatch = new(), _aDiag = new(), _aRescue = new();
    private readonly ActionButton _iOpen = new(), _iCheck = new(), _iConvert = new(), _iNew = new();
    private readonly ActionButton _hFormats = new(), _hAbout = new(), _hDonate = new();
    private readonly ToolTip _tips = new();

    private List<DetectedDrive> _drives = new();
    private readonly Dictionary<ActionButton, OperationVerdict?> _verdicts = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _closeWhenDone, _detecting;
    private string? _mapDrive;

    // průběh (zapisuje vlákno úlohy, čte časovač okna)
    private volatile int _pDone, _pTotal;
    private volatile string _pText = "";
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 60 };

    /// <summary>Pro automatické snímky obrazovky (testy): název scénáře.</summary>
    public string? ScreenshotScript { get; set; }

    public MainForm(AppSettings settings, bool demo)
    {
        _settings = settings;
        _demo = demo;
        _factory = new DeviceFactory(settings.Drive)
        {
            Log = m => Log(m, LogKind.Dim),
            Warn = m => Log(m, LogKind.Warn),
        };
        if (demo) { _factory.Simulator = DemoDrives.Open; _factory.DiagSimulator = DemoDrives.OpenDiag; }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.UiFont(9f);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        // výchozí velikost v „96 DPI“ bodech; Windows ji zvětší podle měřítka obrazovky.
        // Uloženou velikost (skutečné pixely) a omezení na obrazovku řeší až OnLoad — po přepočtu měřítka.
        MinimumSize = new Size(960, 620);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("AtariFloppy.Gui.app.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { }

        BuildTop();
        BuildLeft();
        BuildRight();
        BuildCenter();
        Controls.Add(_split);
        Controls.Add(_right);
        Controls.Add(_left);
        Controls.Add(_top);
        ResumeLayout(false);
        PerformLayout();

        _mapCtl.Model = _map;
        _map.Reset(DiskGeometry.Dd720);
        _uiTimer.Tick += (_, _) => UpdateProgressUi();
        _uiTimer.Start();
        Tr.LanguageChanged += OnLanguageChanged;
        ApplyTexts();
        SetBusy(false);
    }

    // ================================================================ stavba okna

    private void BuildTop()
    {
        _top.Dock = DockStyle.Top;
        _top.Height = 56;
        _top.BackColor = Theme.TopBar;
        _top.Padding = new Padding(16, 0, 16, 0);

        _appTitle.AutoSize = true;
        _appTitle.Font = Theme.UiFont(14f, FontStyle.Bold);
        _appTitle.ForeColor = Theme.TopBarText;
        _appTitle.Location = new Point(16, 14);

        _driveLabel.AutoSize = true;
        _driveLabel.ForeColor = Color.FromArgb(0xB0, 0xBA, 0xC5);
        _driveLabel.Location = new Point(200, 20);

        _driveBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _driveBox.Location = new Point(290, 16);
        _driveBox.Width = 420;
        _driveBox.FlatStyle = FlatStyle.Flat;
        _driveBox.SelectedIndexChanged += (_, _) => { UpdateDrivePanel(); if (Selected is { } d) _settings.LastDrive = d.Id; };

        StyleTopButton(_refreshBtn);
        _refreshBtn.Location = new Point(718, 15);
        _refreshBtn.Width = 110;
        _refreshBtn.Click += (_, _) => DetectDrives();

        _langBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _langBox.Width = 120;
        _langBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _langBox.FlatStyle = FlatStyle.Flat;
        foreach (var l in Tr.Available()) _langBox.Items.Add(l);
        _langBox.DisplayMember = nameof(Tr.LanguageInfo.Name);
        _langBox.SelectedItem = _langBox.Items.Cast<Tr.LanguageInfo>().FirstOrDefault(l => l.Code == Tr.Code);
        _langBox.SelectedIndexChanged += (_, _) =>
        {
            if (_langBox.SelectedItem is Tr.LanguageInfo li && li.Code != Tr.Code)
            {
                Tr.SetLanguage(li.Code);
                _settings.Language = li.Code;
                _settings.Save();
            }
        };

        StyleTopButton(_settingsBtn);
        _settingsBtn.Width = 110;
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += (_, _) => OpenSettings();

        _top.Controls.AddRange(new Control[] { _appTitle, _driveLabel, _driveBox, _refreshBtn, _langBox, _settingsBtn });
        _top.Resize += (_, _) => LayoutTop();
    }

    private void LayoutTop()
    {
        _settingsBtn.Location = new Point(_top.ClientSize.Width - _settingsBtn.Width - 16, (_top.Height - _settingsBtn.Height) / 2);
        _langBox.Location = new Point(_settingsBtn.Left - _langBox.Width - 10, (_top.Height - _langBox.Height) / 2);
        _driveLabel.Location = new Point(_appTitle.Right + 28, (_top.Height - _driveLabel.Height) / 2);
        _driveBox.Location = new Point(_driveLabel.Right + 8, (_top.Height - _driveBox.Height) / 2);
        _driveBox.Width = Math.Max(220, Math.Min(460, _langBox.Left - _driveBox.Left - _refreshBtn.Width - 30));
        _refreshBtn.Location = new Point(_driveBox.Right + 8, (_top.Height - _refreshBtn.Height) / 2);
    }

    private static void StyleTopButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(0x3D, 0x46, 0x54);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x2D, 0x35, 0x42);
        b.BackColor = Color.FromArgb(0x25, 0x2C, 0x38);
        b.ForeColor = Theme.TopBarText;
        b.Height = 28;
        b.Cursor = Cursors.Hand;
    }

    private void BuildLeft()
    {
        _left.Dock = DockStyle.Left;
        _left.Width = 330;
        _left.Padding = new Padding(12, 12, 6, 12);
        _left.BackColor = Theme.Window;
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        _info.Dock = DockStyle.Fill;
        frame.Controls.Add(_info);
        _left.Controls.Add(frame);
    }

    private void BuildRight()
    {
        _right.Dock = DockStyle.Right;
        _right.Width = 268;
        _right.Padding = new Padding(6, 6, 12, 12);
        _right.BackColor = Theme.Window;
        _actions.Dock = DockStyle.Fill;
        _actions.FlowDirection = FlowDirection.TopDown;
        _actions.WrapContents = false;
        _actions.AutoScroll = true;
        _right.Controls.Add(_actions);
        _actions.Resize += (_, _) =>
        {
            int w = _actions.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            foreach (Control c in _actions.Controls) c.Width = Math.Max(120, w);
        };

        void Section(Label l)
        {
            l.AutoSize = false;
            l.Width = 240;
            l.Height = 30;
            l.TextAlign = ContentAlignment.BottomLeft;
            l.Font = Theme.UiFont(8.5f, FontStyle.Bold);
            l.ForeColor = Theme.TextDim;
            l.Margin = new Padding(0, 4, 0, 4);
            _actions.Controls.Add(l);
        }
        void Add(ActionButton b, string glyph, EventHandler click, int height = 62)
        {
            b.Glyph = glyph;
            b.Width = 240;
            b.Height = height;
            b.Margin = new Padding(0, 0, 0, 6);
            b.Click += click;
            _actions.Controls.Add(b);
        }

        Section(_secDisk);
        Add(_aRead, "⭳", (_, _) => DoRead());
        Add(_aBatch, "⭳", (_, _) => DoRead(batch: true));
        Add(_aRescue, "✚", (_, _) => DoRescue());
        Add(_aWrite, "⭱", (_, _) => DoWrite(null));
        Add(_aFormat, "▦", (_, _) => DoFormat());
        Add(_aTest, "✚", (_, _) => DoTest());
        Add(_aBoot, "⏻", (_, _) => DoBoot());
        Add(_aDiag, "⏱", (_, _) => DoDiag());
        Add(_aErase, "✖", (_, _) => DoErase());
        _aErase.Danger = true;
        Add(_aSetup, "⭳", (_, _) => DoSetup());
        Section(_secImages);
        Add(_iOpen, "🗀", (_, _) => DoOpenImage(null), 52);
        Add(_iCheck, "✔", (_, _) => DoCheckImage(), 52);
        Add(_iConvert, "⇄", (_, _) => DoConvert(), 52);
        Add(_iNew, "✦", (_, _) => DoNewImage(), 52);
        Section(_secHelp);
        Add(_hFormats, "☰", (_, _) => { using var f = new FormatsDialog(); f.ShowDialog(this); }, 44);
        Add(_hAbout, "ⓘ", (_, _) => ShowAbout(), 44);
        Add(_hDonate, "♥", (_, _) => OpenLink(Program.DonateUrl), 52);
    }

    private void BuildCenter()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.BackColor = Theme.Window;
        _split.Panel1.Padding = new Padding(6, 12, 6, 0);
        _split.Panel2.Padding = new Padding(6, 6, 6, 12);
        _split.SplitterWidth = 6;
        _split.FixedPanel = FixedPanel.Panel2;

        // průběh pod mapou
        _progressStrip.Dock = DockStyle.Bottom;
        _progressStrip.Height = 46;
        _progressStrip.BackColor = Theme.Panel;
        _progressStrip.Padding = new Padding(12, 8, 12, 8);
        _cancelBtn.Dock = DockStyle.Right;
        _cancelBtn.Width = 110;
        _cancelBtn.FlatStyle = FlatStyle.Flat;
        _cancelBtn.FlatAppearance.BorderColor = Theme.Border;
        _cancelBtn.BackColor = Theme.Panel;
        _cancelBtn.ForeColor = Theme.Danger;
        _cancelBtn.Click += (_, _) => CancelJob();
        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 8;
        _progress.Style = ProgressBarStyle.Continuous;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        mid.Controls.Add(_status);
        mid.Controls.Add(_progress);
        _progressStrip.Controls.Add(mid);
        _progressStrip.Controls.Add(_cancelBtn);

        var mapFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.MapBack, Padding = new Padding(0) };
        _mapCtl.Dock = DockStyle.Fill;
        mapFrame.Controls.Add(_mapCtl);
        _split.Panel1.Controls.Add(mapFrame);
        _split.Panel1.Controls.Add(_progressStrip);

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Theme.Panel;
        _log.Font = Theme.UiFont(9f);
        _log.DetectUrls = false;
        _log.HideSelection = false;
        var logMenu = new ContextMenuStrip();
        logMenu.Items.Add(new ToolStripMenuItem("", null, (_, _) => { if (_log.TextLength > 0) Clipboard.SetText(_log.Text); }) { Name = "copy" });
        logMenu.Items.Add(new ToolStripMenuItem("", null, (_, _) => SaveLog()) { Name = "save" });
        logMenu.Items.Add(new ToolStripMenuItem("", null, (_, _) => _log.Clear()) { Name = "clear" });
        logMenu.Opening += (_, _) =>
        {
            logMenu.Items["copy"].Text = Tr.T("Kopírovat celý výpis");
            logMenu.Items["save"].Text = Tr.T("Uložit výpis do souboru…");
            logMenu.Items["clear"].Text = Tr.T("Vymazat výpis");
        };
        _log.ContextMenuStrip = logMenu;
        var logFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        var logInner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10, 6, 6, 6) };
        logInner.Controls.Add(_log);
        logFrame.Controls.Add(logInner);
        _split.Panel2.Controls.Add(logFrame);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen();
        LayoutTop();
        try { _split.SplitterDistance = Math.Max(300, _split.Height - 210); } catch { }
        Log(Tr.F("Diskette Toolkit {0} — diskety a obrazy pro Atari ST a další 3,5\" systémy.", Program.Version), LogKind.Title);
        if (_demo) Log(Tr.T("Ukázkový režim: mechaniky jsou simulované, nic se nezapisuje na skutečnou disketu."), LogKind.Warn);
        DetectDrives();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnableFileDrop(this);
        Application.AddMessageFilter(new DropFilter(this));
    }

    /// <summary>Okno nikdy větší než pracovní plocha: uložená velikost, jinak výchozí; vystředěné.</summary>
    private void FitToScreen()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int maxW = (int)(wa.Width * 0.92), maxH = (int)(wa.Height * 0.92);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, maxW), Math.Min(MinimumSize.Height, maxH));
        int w = _settings.WindowWidth, h = _settings.WindowHeight;
        // uložená velikost chybí nebo se na obrazovku nevejde (starší verze ji ukládaly zvětšenou) → výchozí
        if (w <= 0 || h <= 0 || w > maxW || h > maxH) { w = Width; h = Height; }
        w = Math.Max(MinimumSize.Width, Math.Min(w, maxW));
        h = Math.Max(MinimumSize.Height, Math.Min(h, maxH));
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    // ================================================================ texty (přepínání jazyka)

    private void OnLanguageChanged()
    {
        if (InvokeRequired) { BeginInvoke(new Action(OnLanguageChanged)); return; }
        ApplyTexts();
        RefreshDriveList();
        UpdateDrivePanel();
        _mapCtl.Invalidate();
    }

    private void ApplyTexts()
    {
        Text = "Diskette Toolkit " + Program.Version + (_demo ? " — " + Tr.T("ukázkový režim") : "");
        _appTitle.Text = "Diskette Toolkit";
        _driveLabel.Text = Tr.T("Mechanika:");
        _refreshBtn.Text = "⟳ " + Tr.T("Obnovit");
        _settingsBtn.Text = "⚙ " + Tr.T("Nastavení");
        _cancelBtn.Text = Tr.T("Přerušit");
        _mapCtl.EmptyText = Tr.T("Mapa diskety se ukáže při práci s disketou nebo obrazem.");
        _secDisk.Text = Tr.T("DISKETA").ToUpperInvariant();
        _secImages.Text = Tr.T("OBRAZY DISKET").ToUpperInvariant();
        _secHelp.Text = Tr.T("PŘEHLED A NÁPOVĚDA").ToUpperInvariant();

        _aRead.Text = Tr.T("Záloha do obrazu");
        _aBatch.Text = Tr.T("Hromadná záloha…");
        _aRescue.Text = Tr.T("Záchrana vadné diskety…");
        _aWrite.Text = Tr.T("Zapsat obraz na disketu");
        _aFormat.Text = Tr.T("Formátovat…");
        _aTest.Text = Tr.T("Test povrchu a kontrola");
        _aErase.Text = Tr.T("Magneticky smazat");
        _aBoot.Text = Tr.T("Spouštěcí disketa…");
        _aBoot.Subtitle = Tr.T("FreeDOS, MS-DOS, Amiga, Atari, RawWrite");
        _aDiag.Text = Tr.T("Diagnostika mechaniky…");
        _aDiag.Subtitle = Tr.T("otáčky, čidla, hlava, čištění, stopy");
        _iOpen.Text = Tr.T("Otevřít obraz…");
        _iOpen.Subtitle = Tr.T("obsah, formát, mapa sektorů");
        _iCheck.Text = Tr.T("Kontrola a oprava obrazu");
        _iCheck.Subtitle = Tr.T("FAT12 — oprava do kopie");
        _iConvert.Text = Tr.T("Převést formát…");
        _iConvert.Subtitle = "ST ⇄ MSA ⇄ IMG ⇄ DIM";
        _iNew.Text = Tr.T("Nový prázdný obraz…");
        _iNew.Subtitle = Tr.T("Atari, PC, MSX, Mac, Amiga, Linux…");
        _hFormats.Text = Tr.T("Formáty všech systémů");
        _hAbout.Text = Tr.T("O programu");
        _hDonate.Text = Tr.T("Podpořit vývoj");
        _hDonate.Subtitle = Tr.T("dobrovolný příspěvek přes Ko-fi");
        UpdateActionButtons();
        if (!_busy) _status.Text = Tr.T("Připraveno.");
        LayoutTop();
    }

    // ================================================================ mechaniky

    private sealed class DriveItem
    {
        public DetectedDrive Drive { get; }
        public DriveItem(DetectedDrive d) => Drive = d;
        public override string ToString() => $"{Drive.Id}  {Drive.KindText} — {Drive.Name}";
    }

    private DetectedDrive? Selected => (_driveBox.SelectedItem as DriveItem)?.Drive;

    private void DetectDrives()
    {
        if (_detecting || _busy) return;
        _detecting = true;
        _refreshBtn.Enabled = false;
        _status.Text = Tr.T("Hledám mechaniky…");
        var log = new List<string>();
        Task.Run(() =>
        {
            try { return _demo ? DemoDrives.Detect() : DriveDetector.DetectAll(log, includeGreaseweazle: true); }
            catch (Exception e) { log.Add(Tr.T("Hledání mechanik selhalo: ") + e.Message); return new List<DetectedDrive>(); }
        }).ContinueWith(t =>
        {
            _detecting = false;
            _refreshBtn.Enabled = !_busy;
            _drives = t.Result;
            _probe.Clear();
            foreach (var l in log) Log(l, LogKind.Dim);
            RefreshDriveList();
            if (_drives.Count == 0)
                Log(Tr.T("Nenašel jsem žádnou disketovou mechaniku ani Greaseweazle. USB disketovka se ve Windows ukazuje jako jednotka A: nebo B:."), LogKind.Warn);
            if (!_busy) _status.Text = Tr.T("Připraveno.");
            RunScreenshotScript();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshDriveList()
    {
        string? keep = Selected?.Id ?? (_settings.LastDrive.Length > 0 ? _settings.LastDrive : null);
        _driveBox.BeginUpdate();
        _driveBox.Items.Clear();
        foreach (var d in _drives) _driveBox.Items.Add(new DriveItem(d));
        _driveBox.EndUpdate();
        int idx = _drives.FindIndex(d => d.Id == keep);
        if (_driveBox.Items.Count > 0) _driveBox.SelectedIndex = idx >= 0 ? idx : 0;
        else UpdateDrivePanel();
    }

    /// <summary>Po operaci znovu zjistí disketu v mechanice (formát, hustotu).</summary>
    private void RefreshSelectedMedia()
    {
        var d = Selected;
        if (d == null || d.Kind == DriveKind.Greaseweazle) { UpdateDrivePanel(); return; }
        Task.Run(() =>
        {
            try { return _demo ? DemoDrives.Detect() : DriveDetector.DetectAll(null, includeGreaseweazle: false); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result == null || _busy) return;
            var fresh = t.Result.FirstOrDefault(x => x.Id == d.Id);
            if (fresh == null) return;
            int i = _drives.FindIndex(x => x.Id == d.Id);
            if (i >= 0) _drives[i] = fresh;
            _probe.Remove(d.Id);
            RefreshDriveList();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateDrivePanel()
    {
        _info.Clear();
        var d = Selected;
        if (d == null)
        {
            _info.Heading(Tr.T("Mechanika"));
            _info.Note(_detecting ? Tr.T("Hledám mechaniky…") : Tr.T("Nenašel jsem žádnou disketovou mechaniku."));
            _info.Note(Tr.T("USB disketovka se ve Windows ukazuje jako jednotka A: nebo B:. Interní mechanika s ovladačem fdrawcmd.sys a Greaseweazle se najdou samy."));
            _info.Note(Tr.T("S obrazy disket (.ST, .MSA, .IMG, .ADF…) můžeš pracovat i bez mechaniky — vpravo."));
            _info.Done();
            UpdateActionButtons();
            return;
        }

        _info.Heading(d.Id + "  " + d.KindText);
        _info.Field(Tr.T("Název"), d.Name);
        if (!string.IsNullOrEmpty(d.Details)) _info.Field(Tr.T("Detaily"), d.Details!);
        if (d.Kind == DriveKind.Greaseweazle)
        {
            _info.Field(Tr.T("Disketa"), Tr.T("Greaseweazle hustotu diskety nepozná — vlož správnou (DD / HD)."));
            _info.Field("gw.exe", d.GwToolPath ?? Tr.T("chybí"), d.GwToolPath == null ? Theme.Danger : (Color?)null);
        }
        else
        {
            var mediaColor = d.MediaState == MediaState.Ready ? Theme.Ok : d.MediaState == MediaState.NoDisk ? Theme.TextDim : Theme.Warn;
            _info.Field(Tr.T("Disketa"), d.MediaText, mediaColor);
            if (d.MediaState == MediaState.Ready) ShowFileSystem(d);
        }

        _info.Heading(Tr.T("Co jde s disketou dělat"));
        var ops = OperationAdvisor.Evaluate(d);
        bool sameReason = ops.Count > 1 && !ops.Any(o => o.IsReady) && ops.All(o => o.Reason == ops[0].Reason);
        foreach (var v in ops)
        {
            var (g, c) = Theme.Mark(v.Status);
            _info.Item(g, c, v.Operation, sameReason ? "" : v.Reason);
        }
        if (sameReason) _info.Note(ops[0].Reason, Theme.Warn);

        _info.Heading(Tr.T("Co umí mechanika"));
        foreach (var kv in d.Features.OrderBy(k => k.Key))
        {
            var (g, c) = Theme.Mark(kv.Value);
            string what = kv.Key == Feature.Standard720 && d.Note720 != null ? d.Note720 : Capabilities.Describe(kv.Value);
            _info.Item(g, c, Capabilities.Describe(kv.Key), what);
        }
        if (d.Kind == DriveKind.Internal && !d.FdrawcmdInstalled)
            _info.Note(Tr.T("Tip: po instalaci ovladače fdrawcmd.sys (simonowen.com/fdrawcmd) půjdou i nestandardní formáty (10/11 sektorů, 82 stop, step rate)."), Theme.Accent);
        if (d.Kind == DriveKind.Usb)
            _info.Note(Tr.T("USB disketovka zvládá jen standardní formáty PC (720 KB, 1,44 MB). Rozšířené formáty Atari, Amigu a ochrany zvládne interní mechanika s fdrawcmd.sys nebo Greaseweazle."));
        _info.Done();

        // mapu vyměníme jen při výběru jiné mechaniky — výsledek poslední operace má zůstat vidět
        if (!_busy && !_map.Running && _mapDrive != d.Id)
        {
            _mapDrive = d.Id;
            _map.Reset(d.FormattedAs ?? (d.Media == InsertedMedia.HD ? DiskGeometry.Hd1440 : DiskGeometry.Dd720), d.Id + " " + d.Name);
        }
        UpdateActionButtons();
    }

    // ------------------------------------------------------------------ souborový systém vložené diskety

    private readonly Dictionary<string, (DiskSummary? summary, string? error)> _probe = new();
    private readonly HashSet<string> _probing = new();

    /// <summary>Do panelu doplní, co je na disketě za souborový systém (čte se jen začátek diskety, na pozadí).</summary>
    private void ShowFileSystem(DetectedDrive d)
    {
        if (!_probe.TryGetValue(d.Id, out var p))
        {
            _info.Field(Tr.T("Souborový systém"), Tr.T("zjišťuji…"), Theme.TextDim);
            StartProbe(d);
            return;
        }
        if (p.summary == null)
        {
            _info.Field(Tr.T("Souborový systém"), Tr.T("nejde přečíst") + (p.error != null ? " — " + p.error : ""), Theme.Warn);
            return;
        }
        var s = p.summary;
        _info.Field(Tr.T("Souborový systém"), s.Fs.Name + (s.Label != null ? Tr.F(", jmenovka „{0}“", s.Label) : ""));
        _info.Field(Tr.T("K čemu"), s.Fs.UsedFor, Theme.TextDim);
        if (s.UsedBytes != null && s.FreeBytes != null)
            _info.Field(Tr.T("Místo"), Tr.F("obsazeno {0:N0} KB, volno {1:N0} KB", s.UsedBytes / 1024, s.FreeBytes / 1024));
        if (s.Fs.Kind is not (FsKind.Empty or FsKind.Unknown))
            _info.Field(Tr.T("Windows"), s.WindowsReadable ? Tr.T("otevřou ji v Průzkumníku") : Tr.T("nejspíš neotevřou — přečte ji tento program a počítač, pro který je"),
                        s.WindowsReadable ? Theme.Ok : Theme.Warn);
        if (s.Bootable) _info.Field(Tr.T("Spouštěcí"), Tr.T("ano — obsahuje zavaděč systému"), Theme.Ok);
        else if (s.BootSectorExecutable) _info.Field(Tr.T("Boot sektor"), Tr.T("spustitelný (Atari) — hra, demo, nebo boot virus"), Theme.Warn);
    }

    private void StartProbe(DetectedDrive d)
    {
        if (_busy || !_probing.Add(d.Id)) return;
        var factory = new DeviceFactory(_settings.Drive) { Simulator = _factory.Simulator };  // bez výpisu do logu
        Task.Run(() =>
        {
            using var dev = factory.OpenForRead(d, write: false);
            dev.RefreshMedia();
            return DiskProbe.Probe(dev);
        }).ContinueWith(t =>
        {
            _probing.Remove(d.Id);
            _probe[d.Id] = t.Exception == null ? (t.Result, null) : (null, t.Exception.GetBaseException().Message);
            if (Selected?.Id == d.Id && !_busy) UpdateDrivePanel();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateActionButtons()
    {
        var d = Selected;
        var ops = d != null ? OperationAdvisor.Evaluate(d) : new List<OperationVerdict>();
        OperationVerdict? Op(string name) => ops.FirstOrDefault(o => o.Operation == name);
        void Set(ActionButton b, OperationVerdict? v, string readyText)
        {
            _verdicts[b] = v;
            bool ready = v != null && v.IsReady;
            b.Blocked = !ready;
            b.Subtitle = d == null ? Tr.T("není vybraná mechanika") : ready ? readyText : v?.Reason ?? "";
            _tips.SetToolTip(b, d == null ? "" : v?.Reason ?? "");
        }
        Set(_aRead, Op(OperationAdvisor.Read), d?.Kind == DriveKind.Greaseweazle ? Tr.T("formát pozná sám; i tok .SCP / .HFE") : Tr.T("disketa → .ST / .MSA / .IMG"));
        Set(_aBatch, Op(OperationAdvisor.Read), Tr.T("víc disket za sebou, soubory se očíslují"));
        Set(_aRescue, Op(OperationAdvisor.Read), Tr.T("opakované čtení, pokračování v jiné mechanice"));
        Set(_aWrite, Op(OperationAdvisor.Write), Tr.T("obraz → disketa (jako RawWrite), s kontrolou"));
        Set(_aFormat, Op(OperationAdvisor.Format), Tr.T("Atari, PC, MSX, Mac, Amiga…"));
        var surface = Op(OperationAdvisor.Surface);
        Set(_aTest, surface, Tr.T("mapa sektorů, kontrola FAT, oprava"));
        _aErase.Visible = d?.Kind == DriveKind.Greaseweazle;
        _aDiag.Visible = d != null && DeviceFactory.SupportsDiag(d);
        Set(_aErase, surface, Tr.T("celá disketa, bez formátu"));

        // doplňky: ovladač / program, které mechanika potřebuje
        _aSetup.Visible = d != null && (d.Kind == DriveKind.Greaseweazle || d.Kind == DriveKind.Internal && !d.FdrawcmdInstalled);
        _aSetup.Blocked = false;
        _aSetup.Glyph = d?.Kind == DriveKind.Greaseweazle && d.GwToolPath != null ? "⟳" : "⭳";
        if (d?.Kind == DriveKind.Greaseweazle)
        {
            _aSetup.Text = d.GwToolPath == null ? Tr.T("Stáhnout Greaseweazle Host Tools") : Tr.T("Aktualizace Greaseweazle");
            _aSetup.Subtitle = d.GwToolPath == null ? Tr.T("gw.exe z GitHubu, pár MB — jedním kliknutím") : Tr.T("gw.exe z GitHubu, firmware v zařízení");
        }
        else
        {
            _aSetup.Text = Tr.T("Nainstalovat ovladač fdrawcmd.sys");
            _aSetup.Subtitle = Tr.T("ze simonowen.com — odemkne 10/11 sektorů, 82 stop, step rate");
        }
    }

    private bool CheckReady(ActionButton b)
    {
        if (Selected == null)
        {
            MessageBox.Show(this, Tr.T("Není vybraná žádná mechanika."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (_verdicts.TryGetValue(b, out var v) && v != null && !v.IsReady)
        {
            MessageBox.Show(this, v.Operation + "\n\n" + v.Reason, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return true;
    }

    // ================================================================ akce s disketou

    /// <summary>Naposledy zvolený formát zálohy (položka „Uložit jako typ“) — platí, dokud běží program.</summary>
    private int _saveFilterIndex = 1;

    private void DoRead(bool batch = false)
    {
        if (!CheckReady(batch ? _aBatch : _aRead)) return;
        var d = Selected!;
        bool gw = d.Kind == DriveKind.Greaseweazle;
        using var dlg = new SaveFileDialog
        {
            Title = batch ? Tr.T("Hromadná záloha — název prvního obrazu, formát vyber v „Uložit jako typ“ (další se očíslují)") : Tr.T("Kam uložit obraz diskety"),
            Filter = ImageFilters.Save(gw),
            // bez přípony — tu doplní zvolený „Uložit jako typ“ (.st, .msa, .img, .adf, u Greaseweazle i .scp/.hfe)
            FileName = Tr.T("disketa") + (batch ? "-001" : ""),
            AddExtension = true,
            DefaultExt = "st",
            FilterIndex = !gw && _saveFilterIndex > 4 ? 1 : _saveFilterIndex,
            InitialDirectory = InitialFolder(),
            OverwritePrompt = !batch,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _saveFilterIndex = dlg.FilterIndex;
        Remember(dlg.FileName);
        if (batch) RunJob(j => j.BatchRead(d, dlg.FileName));
        else RunJob(j => j.ReadDisk(d, dlg.FileName, SideMode.Auto));
    }

    private void DoRescue()
    {
        if (!CheckReady(_aRescue)) return;
        var d = Selected!;
        string suggested = Path.Combine(InitialFolder(), Tr.T("zachrana") + ".st");
        using var dlg = new RescueDialog(d, suggested);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Path_.Length == 0) return;
        string path = dlg.Path_;
        if (Path.GetExtension(path).Length == 0) path += ".st";
        Remember(path);
        int tries = dlg.Tries;
        bool vote = dlg.Vote;
        RunJob(j => j.RescueDisk(d, path, tries, vote));
    }

    private void DoWrite(string? path)
    {
        if (!CheckReady(_aWrite)) return;
        var d = Selected!;
        if (path == null)
        {
            using var open = new OpenFileDialog { Title = Tr.T("Který obraz zapsat na disketu"), Filter = ImageFilters.Open(d.Kind == DriveKind.Greaseweazle), InitialDirectory = InitialFolder() };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            path = open.FileName;
            Remember(path);
        }
        using var w = new WriteDialog(d, path, _settings.VerifyAfterWrite);
        if (w.ShowDialog(this) != DialogResult.OK) return;
        _settings.VerifyAfterWrite = w.Verify;
        if (!ConfirmDanger(Tr.F("Všechna data na disketě {0} budou přepsána. Pokračovat?", d.Id))) return;
        string p = path;
        bool format = w.FormatFirst, verify = w.Verify;
        RunJob(j => j.WriteImage(d, p, format, verify));
    }

    private void DoFormat()
    {
        if (!CheckReady(_aFormat)) return;
        var d = Selected!;
        using var f = new FormatDialog(d);
        if (f.ShowDialog(this) != DialogResult.OK || f.Profile == null) return;
        if (f.ImagePath != null) { CreateImage(f.Profile, f.ImagePath, f.DiskLabel); return; }
        var p = f.Profile;
        if (!ConfirmDanger(Tr.F("Všechna data na disketě {0} budou smazána a disketa se naformátuje jako {1}. Pokračovat?", d.Id, p.Title))) return;
        bool quick = f.Quick, verify = f.Verify, shift = f.ShiftSectors;
        if (shift && !Confirm(Tr.T("Opravný formát je EXPERIMENTÁLNÍ. Vadná místa na stopách zkusí obejít vloženým sektorem navíc, místo aby je jen označil ve FAT. Disketa pak bude normálně čitelná v Atari i PC, ale vada se může časem rozšířit — na archiv a důležitá data ji nepoužívej. Pokračovat?"))) return;
        string? label = f.DiskLabel;
        RunJob(j => j.Format(d, p, quick, label, verify, shift));
    }

    private void DoTest()
    {
        if (!CheckReady(_aTest)) return;
        var d = Selected!;
        using var t = new TestDialog(d, _settings.BackupFolder);
        if (t.ShowDialog(this) != DialogResult.OK) return;
        if (t.Mode == WriteTestMode.Destructive && !ConfirmDanger(Tr.F("Destruktivní test SMAŽE všechna data na {0}. Záloha se před tím uloží do {1}. Pokračovat?", d.Id, t.BackupPath))) return;
        if (t.Mode == WriteTestMode.PreserveData &&
            !Confirm(Tr.F("Test s obnovou dočasně přepisuje stopy. Před zápisem uložím zálohu diskety do {0}. Pokračovat?", t.BackupPath))) return;
        var mode = t.Mode;
        bool repair = t.Repair;
        string? save = t.BackupPath;
        if (save != null) RememberBackupFolder(save);
        RunJob(j => j.CheckDisk(d, mode, repair, save));
    }

    private void DoBoot()
    {
        var d = Selected;
        using var b = new BootDialog(d, InitialFolder());
        if (b.ShowDialog(this) != DialogResult.OK) return;
        if (b.ChosenDiskcopy != null) { _settings.DiskcopyPath = b.ChosenDiskcopy; _settings.Save(); }
        if (b.RawWrite) { DoWrite(null); return; }
        var o = b.Options!;
        string? save = b.SavePath;
        bool write = b.WriteToDisk;
        if (write)
        {
            if (!CheckReady(_aWrite)) return;
            if (!ConfirmDanger(Tr.F("Všechna data na disketě {0} budou přepsána spouštěcí disketou. Pokračovat?", d!.Id))) return;
        }
        if (save != null) Remember(save);
        bool verify = _settings.VerifyAfterWrite;
        RunJob(j => j.MakeBootDisk(o, write ? d : null, save, verify));
    }

    private void DoSetup()
    {
        var d = Selected;
        if (d == null) return;
        if (d.Kind == DriveKind.Internal) { RunInstall(j => j.InstallFdrawcmd()); return; }
        if (d.GwToolPath == null) { RunInstall(j => j.InstallGw()); return; }
        var menu = new ContextMenuStrip();
        menu.Items.Add(Tr.T("Stáhnout nejnovější Host Tools (gw.exe)"), null, (_, _) => RunInstall(j => j.InstallGw()));
        menu.Items.Add(Tr.T("Aktualizovat firmware v Greaseweazle"), null, (_, _) => RunInstall(j => j.UpdateGwFirmware(d)));
        menu.Show(_aSetup, new Point(0, _aSetup.Height));
    }

    /// <summary>Instalace doplňků — po ní znovu najde mechaniky (změní se, co umí).</summary>
    private void RunInstall(Action<Jobs> work)
    {
        if (_demo) Log(Tr.T("Instalace doplňků funguje i v ukázkovém režimu — stahuje a instaluje doopravdy."), LogKind.Dim);
        RunJob(work, redetect: true);
    }

    private void DoDiag()
    {
        var d = Selected;
        if (d == null) return;
        using var dlg = new DiagDialog(d, _settings.Profile(d.Id, "note"), _settings.Profile(d.Id, "rpm"), _settings.Profile(d.Id, "cleaned"));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.SetProfile(d.Id, "note", dlg.DriveNote);
        _settings.Save();
        var r = dlg.Request;
        RunJob(j => j.Diagnose(d, r));
    }

    private void DoErase()
    {
        if (!CheckReady(_aErase)) return;
        var d = Selected!;
        if (!ConfirmDanger(Tr.F("Magneticky smazat celou disketu přes {0}?", d.Name))) return;
        RunJob(j => j.Erase(d));
    }

    // ================================================================ obrazy

    // ------------------------------------------------------------------ přetažení obrazu do okna
    // Program běží jako správce a Windows mezi neprivilegovaným Průzkumníkem a takovým oknem běžné (OLE)
    // přetahování blokuje. Starší zpráva WM_DROPFILES jde povolit — proto DragAcceptFiles místo AllowDrop.

    private const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;

    [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, System.Text.StringBuilder? file, uint size);
    [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr info);
    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hWnd);

    /// <summary>
    /// Povolí přetahování na okno i na všechny jeho části. Výpis (RichTextBox) si sám registruje moderní (OLE)
    /// přetahování, které Windows z Průzkumníku do okna správce zablokují — tu registraci zrušíme, aby se použila
    /// povolená WM_DROPFILES.
    /// </summary>
    private void EnableFileDrop(Control c)
    {
        try
        {
            foreach (uint m in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                ChangeWindowMessageFilterEx(c.Handle, m, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
            RevokeDragDrop(c.Handle);
            DragAcceptFiles(c.Handle, true);
        }
        catch (Exception) { return; } // jiný systém než Windows (ukázky pod Mono) — přetahování prostě nebude
        foreach (Control child in c.Controls) EnableFileDrop(child);
    }

    /// <summary>WM_DROPFILES může přijít kterékoli části okna — zachytíme ji pro celé vlákno.</summary>
    private sealed class DropFilter : IMessageFilter
    {
        private readonly MainForm _form;
        public DropFilter(MainForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_DROPFILES) return false;
            string? file = null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                if (DragQueryFile(m.WParam, 0xFFFFFFFF, null, 0) > 0 && DragQueryFile(m.WParam, 0, sb, (uint)sb.Capacity) > 0) file = sb.ToString();
            }
            finally { DragFinish(m.WParam); }
            if (file != null) _form.BeginInvoke(new Action(() => _form.OnFileDropped(file)));
            return true;
        }
    }

    private void OnFileDropped(string path)
    {
        Log(Tr.F("Přetažený soubor: {0}", path), LogKind.Dim);
        if (_busy) { Log(Tr.T("Počkej, až doběhne aktuální operace."), LogKind.Warn); return; }
        if (!File.Exists(path)) return;
        Remember(path);
        DoOpenImage(path);
    }

    private void DoOpenImage(string? path)
    {
        if (path == null)
        {
            using var open = new OpenFileDialog { Title = Tr.T("Otevřít obraz diskety"), Filter = ImageFilters.Open(true), InitialDirectory = InitialFolder() };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            path = open.FileName;
            Remember(path);
        }
        LoadedImage loaded;
        try { loaded = ImageFile.Load(path); }
        catch (Exception e) { Error(e.Message); return; }
        if (loaded.Image != null && !_busy) ShowImageOnMap(loaded.Image, Path.GetFileName(path));
        using var dlg = new ImageDialog(path, loaded, InitialFolder());
        var result = dlg.ShowDialog(this);
        if (result == DialogResult.Yes && Selected != null) DoWrite(path);
        else if (result == DialogResult.Retry) RunJob(j => j.CheckImage(path));
        else if (result == DialogResult.Ignore) DoFluxRescue(path);
    }

    private void DoFluxRescue(string scp)
    {
        using var dlg = new FluxRescueDialog(scp);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Output.Length == 0) return;
        string path = dlg.Output;
        if (Path.GetExtension(path).Length == 0) path += ".img";
        var files = dlg.Files;
        Remember(path);
        RunJob(j => j.RescueFromFlux(files, path));
    }

    private void DoCheckImage()
    {
        using var open = new OpenFileDialog { Title = Tr.T("Který obraz zkontrolovat"), Filter = ImageFilters.Open(false), InitialDirectory = InitialFolder() };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        Remember(open.FileName);
        try
        {
            var loaded = ImageFile.Load(open.FileName);
            if (loaded.Image != null) ShowImageOnMap(loaded.Image, Path.GetFileName(open.FileName));
        }
        catch { }
        string file = open.FileName;
        RunJob(j => j.CheckImage(file));
    }

    private void DoConvert()
    {
        using var open = new OpenFileDialog { Title = Tr.T("Který obraz převést"), Filter = ImageFilters.Open(false), InitialDirectory = InitialFolder() };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        Remember(open.FileName);
        LoadedImage loaded;
        try { loaded = ImageFile.Load(open.FileName); }
        catch (Exception e) { Error(e.Message); return; }
        if (loaded.Image == null) { Error(loaded.NotLoadedReason ?? Tr.T("Obraz nejde načíst.")); return; }
        using var save = new SaveFileDialog
        {
            Title = Tr.T("Uložit jako"),
            Filter = ImageFilters.Save(false),
            FileName = Path.GetFileNameWithoutExtension(open.FileName),
            InitialDirectory = Path.GetDirectoryName(open.FileName),
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ImageFile.Save(loaded.Image, save.FileName);
            Log(Tr.F("Převedeno: {0} ({1}) → {2}, {3:N0} B", Path.GetFileName(open.FileName), ImageFile.Describe(loaded.Kind),
                     Path.GetFileName(save.FileName), new FileInfo(save.FileName).Length), LogKind.Ok);
        }
        catch (Exception e) { Error(e.Message); }
    }

    private void DoNewImage()
    {
        using var f = new FormatDialog(null);
        if (f.ShowDialog(this) != DialogResult.OK || f.Profile == null || f.ImagePath == null) return;
        CreateImage(f.Profile, f.ImagePath, f.DiskLabel);
    }

    private void CreateImage(FormatProfile p, string path, string? label)
    {
        try
        {
            var img = p.Build!(p.SupportsLabel ? label : null);
            ImageFile.Save(img, path);
            Remember(path);
            Log(Tr.F("Vytvořen prázdný obraz {0}: {1} ({2}).", Path.GetFileName(path), p.Title, img.Geometry), LogKind.Ok);
            if (p.Need != HardwareNeed.Standard)
                Log(p.Need == HardwareNeed.Flux
                    ? Tr.T("Na disketu ho zapíše jen Greaseweazle; v emulátoru nebo na Goteku ho můžeš použít hned.")
                    : Tr.T("Na disketu ho zapíše jen interní mechanika s fdrawcmd.sys nebo Greaseweazle; v emulátoru nebo na Goteku ho můžeš použít hned."), LogKind.Dim);
            if (!_busy) ShowImageOnMap(img, Path.GetFileName(path));
        }
        catch (Exception e) { Error(e.Message); }
    }

    /// <summary>Obraz v mapě: barevné jsou sektory s daty, tmavé prázdné (vyplněné jedním bajtem).</summary>
    private void ShowImageOnMap(DiskImage img, string name)
    {
        var g = img.Geometry;
        _mapDrive = Selected?.Id;
        _map.Reset(g, Tr.F("Obraz {0} — sektory s daty", name));
        int bps = g.BytesPerSector;
        for (long l = 0; l < g.TotalSectors; l++)
        {
            int o = (int)(l * bps);
            if (o + bps > img.Data.Length) break;
            byte first = img.Data[o];
            bool blank = true;
            for (int k = 1; k < bps && blank; k++) blank = img.Data[o + k] == first;
            if (!blank || l == 0)
            {
                int spt = g.SectorsPerTrack;
                _map.Sector((int)(l / spt / g.Sides), (int)(l / spt % g.Sides), (int)(l % spt), CellState.Read);
            }
        }
    }

    // ================================================================ nastavení a nápověda

    private void OpenSettings()
    {
        using var s = new SettingsDialog(_settings, _drives);
        var r = s.ShowDialog(this);
        if (r != DialogResult.OK && s.Request == SettingsDialog.SetupAction.None) return;
        _settings.Save();
        switch (s.Request)
        {
            case SettingsDialog.SetupAction.InstallGw: RunInstall(j => j.InstallGw()); break;
            case SettingsDialog.SetupAction.InstallFdrawcmd: RunInstall(j => j.InstallFdrawcmd()); break;
            case SettingsDialog.SetupAction.GwFirmware:
                var gw = _drives.FirstOrDefault(x => x.Kind == DriveKind.Greaseweazle);
                if (gw == null) Error(Tr.T("Greaseweazle jsem nenašel. Je připojený k USB?"));
                else RunInstall(j => j.UpdateGwFirmware(gw));
                break;
            default: DetectDrives(); break;
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            $"Diskette Toolkit {Program.Version}\n\n" +
            Tr.T("Čtení, zápis, formátování a test disket pro Atari ST a další 3,5\" systémy — inspirováno programem FastCopy.") + "\n\n" +
            Tr.T("Mechaniky: USB disketovka, interní mechanika (s ovladačem fdrawcmd.sys i nestandardní formáty) a Greaseweazle.") + "\n\n" +
            Tr.T("Nastavení:") + " " + AppSettings.FilePath + "\n" +
            Tr.T("Zálohy:") + " " + _settings.BackupFolder + "\n" +
            Tr.T("Jazyky:") + " " + Tr.LangFolder + "\n\n" +
            Tr.T("Licence: GNU GPL v3 — svobodný software, zdrojové kódy jsou volně k dispozici.") + "\n\n" +
            Tr.T("Stránka projektu:") + " " + Program.ProjectUrl + "\n" +
            Tr.T("Podpořit vývoj:") + " " + Program.DonateUrl,
            Tr.T("O programu"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Otevře odkaz v prohlížeči. Přes explorer.exe, aby prohlížeč neběžel s právy správce jako program.</summary>
    private void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false }); }
        catch (Exception) { MessageBox.Show(this, url, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    // ================================================================ běh úloh

    private void RunJob(Action<Jobs> work, bool redetect = false)
    {
        if (_busy) return;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _pDone = 0; _pTotal = 0; _pText = Tr.T("Začínám…");
        var jobs = new Jobs(this);
        Task.Run(() =>
        {
            using (TrackActivity.Attach(_map))
                work(jobs);
        }, _cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || t.Exception?.GetBaseException() is OperationCanceledException)
                Log(Tr.T("Operace byla přerušena."), LogKind.Warn);
            else if (t.Exception != null)
            {
                var e = t.Exception.GetBaseException();
                bool expected = e is OperationException or FormatException or FileNotFoundException or NotSupportedException or ArgumentException or IOException or SectorIOException;
                Log(expected ? e.Message : Tr.T("Neočekávaná chyba: ") + e, LogKind.Error);
            }
            _map.End();
            SetBusy(false);
            _progress.Style = ProgressBarStyle.Continuous;
            _status.Text = t.Exception == null && !t.IsCanceled ? Tr.T("Hotovo.") : Tr.T("Připraveno.");
            _progress.Value = t.Exception == null && !t.IsCanceled ? _progress.Maximum : 0;
            _probe.Clear();
            if (redetect) DetectDrives(); else RefreshSelectedMedia();
            if (_closeWhenDone) Close();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void CancelJob()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        _status.Text = Tr.T("Přerušuji… (dokončuji právě zapisovanou stopu)");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _cancelBtn.Enabled = busy;
        _cancelBtn.Visible = busy;
        _driveBox.Enabled = !busy;
        _refreshBtn.Enabled = !busy && !_detecting;
        _settingsBtn.Enabled = !busy;
        foreach (Control c in _actions.Controls)
            if (c is ActionButton b && b != _hFormats && b != _hAbout && b != _hDonate) b.Enabled = !busy;
        UseWaitCursor = false;
    }

    private void UpdateProgressUi()
    {
        if (!_busy) return;
        int total = _pTotal, done = _pDone;
        var style = total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (_progress.Style != style) _progress.Style = style;
        if (total > 0)
        {
            _progress.Maximum = total;
            _progress.Value = Math.Max(0, Math.Min(total, done));
        }
        _status.Text = total > 0 ? $"{_pText}   ({done * 100 / Math.Max(1, total)} %)" : _pText;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy)
        {
            if (!Confirm(Tr.T("Právě běží operace s disketou. Přerušit ji a zavřít program?"))) { e.Cancel = true; return; }
            _closeWhenDone = true;
            CancelJob();
            e.Cancel = true;
            return;
        }
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal) { _settings.WindowWidth = Width; _settings.WindowHeight = Height; }
        _settings.Save();
        Tr.LanguageChanged -= OnLanguageChanged;
        base.OnFormClosing(e);
    }

    // ================================================================ IJobHost

    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;
    public DeviceFactory Factory => _factory;
    public AppSettings Settings => _settings;

    public void Progress(int done, int total, string text)
    {
        _pTotal = total;
        _pDone = done;
        _pText = text;
    }

    private RpmGaugeForm? _gauge;

    public void ShowHistogram(string scpPath, int cyl, int head)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => ShowHistogram(scpPath, cyl, head))); return; }
        try { new FluxHistogramForm(scpPath, cyl, head).Show(this); }
        catch (Exception e) { Error(e.Message); }
    }

    public void Gauge(string? value, string detail = "", int grade = 0)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Gauge(value, detail, grade))); return; }
        if (value == null)
        {
            _gauge?.Close();
            _gauge = null;
            return;
        }
        if (_gauge == null || _gauge.IsDisposed)
        {
            _gauge = new RpmGaugeForm(CancelJob);
            _gauge.Show(this);
        }
        _gauge.Show(value, detail, grade == 0 ? Theme.Ok : grade == 1 ? Theme.Warn : Theme.Danger);
    }

    private static readonly Font TitleFont = Theme.UiFont(9.5f, FontStyle.Bold);

    public void Log(string text, LogKind kind = LogKind.Info)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(text, kind))); return; }
        var color = kind switch
        {
            LogKind.Ok => Theme.Ok,
            LogKind.Warn => Theme.Warn,
            LogKind.Error => Theme.Danger,
            LogKind.Dim => Theme.TextDim,
            _ => Theme.Text,
        };
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        if (kind == LogKind.Title && _log.TextLength > 0) _log.AppendText("\n");
        _log.SelectionColor = color;
        _log.SelectionFont = kind == LogKind.Title ? TitleFont : _log.Font;
        string prefix = kind switch { LogKind.Warn => "! ", LogKind.Error => "✗ ", LogKind.Ok => "✓ ", _ => "" };
        _log.AppendText(prefix + text + "\n");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    public bool Confirm(string text, bool danger = false)
    {
        if (InvokeRequired) return (bool)Invoke(new Func<bool>(() => Confirm(text, danger)));
        return MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, danger ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                               MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    public string? AskSavePath(string title, string suggestedPath)
    {
        if (InvokeRequired) return (string?)Invoke(new Func<string?>(() => AskSavePath(title, suggestedPath)));
        using var dlg = new SaveFileDialog
        {
            Title = title,
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            Filter = ImageFilters.Save(false),
            FilterIndex = Path.GetExtension(suggestedPath).ToLowerInvariant() switch { ".msa" => 2, ".img" => 3, ".adf" => 4, _ => 1 },
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        RememberBackupFolder(dlg.FileName);
        return dlg.FileName;
    }

    /// <summary>Příště nabídneme zálohu do stejné složky.</summary>
    private void RememberBackupFolder(string file)
    {
        try
        {
            _settings.BackupFolder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? _settings.BackupFolder;
            _settings.Save();
        }
        catch { }
    }

    private bool ConfirmDanger(string text) => Confirm(text, danger: true);

    private void Error(string text)
    {
        Log(text, LogKind.Error);
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void SaveLog()
    {
        using var s = new SaveFileDialog { Filter = Tr.T("Text") + " (*.txt)|*.txt", FileName = "DisketteToolkit-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt" };
        if (s.ShowDialog(this) == DialogResult.OK) File.WriteAllText(s.FileName, _log.Text.Replace("\n", "\r\n"));
    }

    private string InitialFolder() =>
        Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private void Remember(string path)
    {
        try { _settings.LastFolder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""; } catch { }
    }

    // ================================================================ automatické snímky (testy)

    private void RunScreenshotScript()
    {
        string? s = ScreenshotScript;
        if (s == null) return;
        ScreenshotScript = null;
        var d = Selected;
        string tmp = Path.Combine(Path.GetTempPath(), "afloppy-shot");
        Directory.CreateDirectory(tmp);
        switch (s)
        {
            case "read" when d != null: RunJob(j => j.ReadDisk(d, Path.Combine(tmp, "demo.st"), SideMode.Auto)); break;
            case "test" when d != null: RunJob(j => j.CheckDisk(d, WriteTestMode.None, false, null)); break;
            case "format" when d != null: RunJob(j => j.Format(d, FormatProfiles.Find("atari")!, false, "DEMO", true)); break;
            case "formatdialog" when d != null: BeginInvoke(new Action(() => { using var f = new FormatDialog(d); f.ShowDialog(this); })); break;
            case "formatdialog-internal" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                BeginInvoke(new Action(() => { using var f = new FormatDialog(Selected!); f.ShowDialog(this); }));
                break;
            case "testdialog" when d != null: BeginInvoke(new Action(() => { using var f = new TestDialog(d, _settings.BackupFolder); f.ShowDialog(this); })); break;
            case "settings": BeginInvoke(new Action(() => { using var f = new SettingsDialog(_settings, _drives); f.ShowDialog(this); })); break;
            case "formats": BeginInvoke(new Action(() => new FormatsDialog().ShowDialog(this))); break;
            case "format820" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                var b = Selected!;
                RunJob(j => j.Format(b, FormatProfiles.Find("atari-820")!, false, "DEMO", true));
                break;
            case "writedialog" when d != null:
                {
                    string f = Path.Combine(tmp, "demo.st");
                    if (!File.Exists(f)) ImageFile.Save(FormatProfiles.Find("atari")!.Build!("DEMO"), f);
                    BeginInvoke(new Action(() => { using var w = new WriteDialog(d, f, true); w.ShowDialog(this); }));
                    break;
                }
            case "bootdialog":
                BeginInvoke(new Action(() => { using var f = new BootDialog(d, tmp); f.ShowDialog(this); }));
                break;
            case "bootwrite" when d != null:
                RunJob(j => j.MakeBootDisk(new AtariFloppy.Core.Boot.BootOptions { System = AtariFloppy.Core.Boot.BootSystem.FreeDos, Geometry = DiskGeometry.Dd720 }, d, null, true));
                break;
            case "small":
                WindowState = FormWindowState.Normal;
                Size = MinimumSize;
                BeginInvoke(new Action(() => { foreach (Control c in _left.Controls) foreach (Control i in c.Controls) if (i is InfoPanel ip) ip.AutoScrollPosition = new Point(0, 400); }));
                break;
            case "batch" when d != null:
                RunJob(j => j.BatchRead(d, Path.Combine(tmp, "hra.st")));
                break;
            case "help":
                BeginInvoke(new Action(() => _actions.AutoScrollPosition = new Point(0, 10000)));
                break;
            case "internal":
                _driveBox.SelectedIndex = Math.Min(1, _driveBox.Items.Count - 1);
                break;
            case "gw":
                _driveBox.SelectedIndex = _driveBox.Items.Count - 1;
                break;
            case "diagdialog" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                BeginInvoke(new Action(() => { using var f = new DiagDialog(Selected!, "TEAC FD-235HF, nový řemínek", "300,4 ot./min, kolísání ±0,15 (2026-10-13)", ""); f.ShowDialog(this); }));
                break;
            case "rpm" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                RunJob(j => j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.RpmLive }));
                break;
            case "sensors" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                RunJob(j => { j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.Rpm }); j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.Sensors }); j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.TrackRead, Cyl = 22, Reads = 8 }); });
                break;
            case "rescue" when _drives.Count > 0:
                {
                    _driveBox.SelectedIndex = 0;
                    d = Selected!;
                    string f = Path.Combine(tmp, "zachrana.st");
                    if (File.Exists(f)) File.Delete(f);
                    RunJob(j => { j.RescueDisk(d, f, 5, false); j.RescueDisk(d, f, 5, false); });
                    break;
                }
            case "fluxrescue":
                {
                    // soubory .scp z proměnné DTK_SCP (oddělené ;), jinak ukázkový tok
                    var scps = (Environment.GetEnvironmentVariable("DTK_SCP") ?? DemoDrives.DemoScp(22, 0)).Split(';');
                    string f = Path.Combine(tmp, "zachrana-tok.img");
                    if (File.Exists(f)) File.Delete(f);
                    if (File.Exists(RescueState.SidecarPath(f))) File.Delete(RescueState.SidecarPath(f));
                    RunJob(j => j.RescueFromFlux(scps, f));
                    break;
                }
            case "fluxrescuedialog":
                BeginInvoke(new Action(() => { using var dlg = new FluxRescueDialog(DemoDrives.DemoScp(22, 0)); dlg.ShowDialog(this); }));
                break;
            case "histogram":
                BeginInvoke(new Action(() => new FluxHistogramForm(DemoDrives.DemoScp(22, 0), 22, 0).Show(this)));
                break;
            case "steptest" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                RunJob(j => j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.StepTest }));
                break;
            case "layout" when _drives.Count > 1:
                _driveBox.SelectedIndex = 1;
                RunJob(j => j.Diagnose(Selected!, new DiagRequest { Action = DiagAction.Layout }));
                break;
            case "image":
                {
                    string f = Path.Combine(tmp, "demo.st");
                    if (!File.Exists(f)) ImageFile.Save(FormatProfiles.Find("atari")!.Build!("DEMO"), f);
                    BeginInvoke(new Action(() => DoOpenImage(f)));
                    break;
                }
        }
    }
}

/// <summary>Filtry pro dialogy souborů.</summary>
internal static class ImageFilters
{
    public static string Open(bool withFlux) =>
        Tr.T("Obrazy disket") + (withFlux ? " (*.st;*.msa;*.dim;*.img;*.ima;*.adf;*.dsk;*.d81;*.scp;*.hfe;*.ipf)|*.st;*.msa;*.dim;*.img;*.ima;*.adf;*.dsk;*.d81;*.scp;*.hfe;*.ipf"
                                              : " (*.st;*.msa;*.dim;*.img;*.ima;*.adf;*.dsk)|*.st;*.msa;*.dim;*.img;*.ima;*.adf;*.dsk") +
        "|" + Tr.T("Všechny soubory") + " (*.*)|*.*";

    public static string Save(bool withFlux) =>
        "Atari ST (*.st)|*.st|Atari MSA (*.msa)|*.msa|" + Tr.T("PC / obecný obraz") + " (*.img)|*.img|Amiga (*.adf)|*.adf" +
        (withFlux ? "|" + Tr.T("Magnetický tok SuperCard Pro") + " (*.scp)|*.scp|" + Tr.T("Magnetický tok HxC / Gotek") + " (*.hfe)|*.hfe" : "");
}
