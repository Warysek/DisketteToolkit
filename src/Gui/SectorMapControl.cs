using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui;

/// <summary>
/// Mapa diskety jako ve FastCopy: sloupce = stopy, řádky = sektory, pro každou stranu zvlášť.
/// Barva = co se se sektorem stalo; rámeček = kde je právě hlava.
/// </summary>
internal sealed class SectorMapControl : Control
{
    private static readonly CellState[] LegendStates =
    {
        CellState.Read, CellState.Formatted, CellState.Written, CellState.Verified,
        CellState.Weak, CellState.Bad, CellState.WriteFailed, CellState.Skipped,
    };

    private readonly Timer _timer = new() { Interval = 40 };
    private readonly ToolTip _tip = new() { InitialDelay = 200, ReshowDelay = 50, AutoPopDelay = 8000 };
    private SectorMapModel _model = new();
    private int _shownVersion = -1;
    private string _tipText = "";

    // rozložení z posledního kreslení (pro myš)
    private Rectangle[] _sideRects = Array.Empty<Rectangle>();
    private float _cellW, _cellH;

    public SectorMapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.MapBack;
        ForeColor = Theme.MapText;
        Font = Theme.UiFont(8.5f);
        _timer.Tick += (_, _) =>
        {
            int v = _model.Version;
            if (v != _shownVersion) Invalidate();
        };
        _timer.Start();
    }

    public SectorMapModel Model
    {
        get => _model;
        set { _model = value ?? new SectorMapModel(); Invalidate(); }
    }

    /// <summary>Text zobrazený, když mapa nemá žádnou geometrii.</summary>
    public string EmptyText { get; set; } = "";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ kreslení

    protected override void OnPaint(PaintEventArgs e)
    {
        _shownVersion = _model.Version;
        var gfx = e.Graphics;
        _dpi = gfx.DpiX > 0 ? gfx.DpiX : 96f;
        gfx.Clear(Theme.MapBack);
        gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var g = _model.Geometry;
        var cells = _model.Snapshot();
        using var textBrush = new SolidBrush(Theme.MapText);
        using var titleFont = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Math.Max(6.5f, Font.Size - 1.5f));

        int pad = Scale(12);
        int y = pad;

        // záhlaví: název operace a vpravo poloha hlavy a geometrie — co se nevejde, vynecháme (napřed geometrii)
        string title = _model.Title.Length > 0 ? _model.Title : (g != null ? Tr.T("Mapa diskety") : "");
        string geo = g == null ? "" : Tr.F("{0} stop × {1} {2} × {3} sektorů", g.Tracks, g.Sides, g.Sides == 1 ? Tr.T("strana") : Tr.T("strany"), g.SectorsPerTrack);
        string head = _model.Running && _model.HeadTrack >= 0
            ? Tr.F("{0}: stopa {1}{2}", Theme.PhaseName(_model.HeadPhase), _model.HeadTrack, _model.HeadSide >= 0 ? Tr.F(", strana {0}", _model.HeadSide) : "")
            : "";
        float titleW = gfx.MeasureString(title, titleFont).Width, room = Width - 2 * pad - Scale(24);
        string right = "";
        foreach (var candidate in new[] { head.Length > 0 && geo.Length > 0 ? head + "   ·   " + geo : head + geo, head })
            if (candidate.Length > 0 && titleW + gfx.MeasureString(candidate, Font).Width <= room) { right = candidate; break; }
        float rightW = right.Length > 0 ? gfx.MeasureString(right, Font).Width : 0;
        using (var white = new SolidBrush(Color.FromArgb(0xE6, 0xED, 0xF3)))
        using (var trim = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            gfx.DrawString(title, titleFont, white, new RectangleF(pad, y, Math.Max(10, Width - 2 * pad - rightW - Scale(16)), titleFont.GetHeight(gfx) + 2), trim);
        if (right.Length > 0) gfx.DrawString(right, Font, textBrush, Width - pad - rightW, y + 3);
        y += (int)titleFont.GetHeight(gfx) + Scale(8);

        if (g == null)
        {
            var sz = gfx.MeasureString(EmptyText, Font);
            gfx.DrawString(EmptyText, Font, textBrush, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
            _sideRects = Array.Empty<Rectangle>();
            return;
        }

        // rozměry
        int legendH = LegendHeight(gfx, small, Width - 2 * pad);
        int rulerH = (int)small.GetHeight(gfx) + Scale(6);
        int labelW = (int)gfx.MeasureString(Tr.F("Strana {0}", 1), Font).Width + Scale(10);
        int numW = (int)gfx.MeasureString("21", small).Width + Scale(4);
        int left = pad + labelW + numW;
        int gridW = Width - left - pad;
        int sideGap = Scale(12);
        int gridH = Height - y - rulerH - legendH - pad - sideGap * (g.Sides - 1);
        if (gridW < 20 || gridH < 20) return;

        _cellW = (float)gridW / g.Tracks;
        _cellH = Math.Min(Scale(26), (float)gridH / (g.Sides * g.SectorsPerTrack));
        float gap = _cellW >= 5 && _cellH >= 5 ? 1f : 0f;

        // pravítko stop: čísla po 10 stopách, v úzkém okně po 20 nebo 40, ať se nepřekrývají
        int rulerY = y, step = 10;
        float numSpace = gfx.MeasureString("80", small).Width + Scale(6);
        while (step < 80 && _cellW * step < numSpace) step *= 2;
        for (int t = 0; t < g.Tracks; t++)
        {
            float x = left + t * _cellW;
            if (t % step == 0)
            {
                gfx.DrawString(t.ToString(), small, textBrush, x - 1, rulerY);
                using var p = new Pen(Theme.MapText);
                gfx.DrawLine(p, x, rulerY + rulerH - Scale(4), x, rulerY + rulerH - 1);
            }
            else if (t % 5 == 0 && _cellW * 5 >= Scale(4))
            {
                using var p = new Pen(Color.FromArgb(0x55, 0x5E, 0x69));
                gfx.DrawLine(p, x, rulerY + rulerH - Scale(3), x, rulerY + rulerH - 1);
            }
        }
        y += rulerH;

        var rects = new List<Rectangle>();
        var brushes = new Dictionary<CellState, SolidBrush>();
        SolidBrush BrushFor(CellState s) => brushes.TryGetValue(s, out var b) ? b : brushes[s] = new SolidBrush(Theme.Cell(s));
        using var markPen = new Pen(Theme.MapGrid, Math.Max(1.2f, Scale(1) * 1.4f));
        using var markBrush = new SolidBrush(Theme.MapGrid);
        try
        {
            for (int side = 0; side < g.Sides; side++)
            {
                float blockH = g.SectorsPerTrack * _cellH;
                var block = new Rectangle(left, y, (int)(g.Tracks * _cellW), (int)Math.Ceiling(blockH));
                rects.Add(block);

                // popisek strany a čísla sektorů
                string label = Tr.F("Strana {0}", side);
                var ls = gfx.MeasureString(label, Font);
                gfx.DrawString(label, Font, textBrush, pad, y + (blockH - ls.Height) / 2);
                if (_cellH >= small.GetHeight(gfx) + 1)
                    for (int n = 0; n < g.SectorsPerTrack; n++)
                    {
                        string num = (n + 1).ToString();
                        var ns = gfx.MeasureString(num, small);
                        gfx.DrawString(num, small, textBrush, left - ns.Width - Scale(3), y + n * _cellH + (_cellH - ns.Height) / 2);
                    }

                using (var gridBrush = new SolidBrush(Theme.MapGrid)) gfx.FillRectangle(gridBrush, block);
                for (int t = 0; t < g.Tracks; t++)
                    for (int n = 0; n < g.SectorsPerTrack; n++)
                    {
                        long lba = g.Lba(t, side, n);
                        var st = lba < cells.Length ? cells[lba] : CellState.Untouched;
                        var cell = new RectangleF(left + t * _cellW, y + n * _cellH, Math.Max(1f, _cellW - gap), Math.Max(1f, _cellH - gap));
                        gfx.FillRectangle(BrushFor(st), cell);
                        DrawMark(gfx, st, cell, markPen, markBrush);
                    }

                // kurzor ovládaný klávesnicí (šipky) — jen když má mapa fokus
                if (Focused && _cursorOn && _cursor.side == side && _cursor.track < g.Tracks && _cursor.sector < g.SectorsPerTrack)
                {
                    using var pen = new Pen(Theme.MapHead, Math.Max(1.5f, Scale(2)));
                    gfx.DrawRectangle(pen, left + _cursor.track * _cellW - 1, y + _cursor.sector * _cellH - 1, Math.Max(2f, _cellW) + 1, Math.Max(2f, _cellH) + 1);
                }

                // hlava
                if (_model.Running && _model.HeadTrack >= 0 && _model.HeadTrack < g.Tracks && (_model.HeadSide < 0 || _model.HeadSide == side))
                {
                    float hx = left + _model.HeadTrack * _cellW;
                    using var pen = new Pen(Theme.MapHead, Math.Max(1.5f, Scale(2)));
                    gfx.DrawRectangle(pen, hx - 1, y - 1, Math.Max(2f, _cellW) + 1, blockH + 1);
                }
                y += (int)Math.Ceiling(blockH) + sideGap;
            }

            // šipka nad aktuální stopou
            if (_model.Running && _model.HeadTrack >= 0 && _model.HeadTrack < g.Tracks)
            {
                float cx = left + (_model.HeadTrack + 0.5f) * _cellW;
                float ty = rulerY + rulerH - 1;
                using var hb = new SolidBrush(Theme.MapHead);
                gfx.SmoothingMode = SmoothingMode.AntiAlias;
                gfx.FillPolygon(hb, new[] { new PointF(cx - Scale(5), ty - Scale(7)), new PointF(cx + Scale(5), ty - Scale(7)), new PointF(cx, ty) });
                gfx.SmoothingMode = SmoothingMode.Default;
            }

            DrawLegend(gfx, small, textBrush, pad, Height - pad - legendH + Scale(4), Width - 2 * pad, BrushFor, markPen, markBrush);
        }
        finally
        {
            foreach (var b in brushes.Values) b.Dispose();
        }
        _sideRects = rects.ToArray();
    }

    /// <summary>
    /// Značka ve čtverečku, aby stavy nerozlišovala jen barva (červená/zelená, oranžová/růžová):
    /// nečitelný a selhaný zápis = křížek, slabý = tečka. V úplně malých čtverečcích se nekreslí.
    /// </summary>
    private void DrawMark(Graphics gfx, CellState st, RectangleF cell, Pen pen, Brush brush)
    {
        if (st is not (CellState.Bad or CellState.WriteFailed or CellState.Weak)) return;
        float size = Math.Min(cell.Width, cell.Height);
        if (size < Scale(5)) return;
        float s = size * (st == CellState.Weak ? 0.36f : 0.56f);
        float cx = cell.X + cell.Width / 2, cy = cell.Y + cell.Height / 2;
        var smooth = gfx.SmoothingMode;
        gfx.SmoothingMode = SmoothingMode.AntiAlias;
        if (st == CellState.Weak) gfx.FillEllipse(brush, cx - s / 2, cy - s / 2, s, s);
        else
        {
            gfx.DrawLine(pen, cx - s / 2, cy - s / 2, cx + s / 2, cy + s / 2);
            gfx.DrawLine(pen, cx - s / 2, cy + s / 2, cx + s / 2, cy - s / 2);
        }
        gfx.SmoothingMode = smooth;
    }

    private int LegendHeight(Graphics gfx, Font f, int width)
    {
        int rows = 1, x = 0;
        foreach (var st in LegendStates)
        {
            int w = LegendItemWidth(gfx, f, st);
            if (x + w > width && x > 0) { rows++; x = 0; }
            x += w;
        }
        return rows * ((int)f.GetHeight(gfx) + Scale(6)) + Scale(4);
    }

    private int LegendItemWidth(Graphics gfx, Font f, CellState st) =>
        Scale(12) + Scale(5) + (int)gfx.MeasureString(LegendName(st), f).Width + Scale(16);

    private void DrawLegend(Graphics gfx, Font f, Brush text, int x0, int y0, int width, Func<CellState, SolidBrush> brush, Pen markPen, Brush markBrush)
    {
        int x = 0, y = y0, rowH = (int)f.GetHeight(gfx) + Scale(6), box = Scale(11);
        foreach (var st in LegendStates)
        {
            int w = LegendItemWidth(gfx, f, st);
            if (x + w > width && x > 0) { x = 0; y += rowH; }
            var r = new RectangleF(x0 + x, y + (rowH - box) / 2 - 1, box, box);
            gfx.FillRectangle(brush(st), r);
            DrawMark(gfx, st, r, markPen, markBrush);
            gfx.DrawString(LegendName(st), f, text, x0 + x + box + Scale(5), y + (rowH - f.GetHeight(gfx)) / 2 - 1);
            x += w;
        }
    }

    /// <summary>V legendě krátce (celé vysvětlení je v bublině nad čtverečkem), ať se vejde i do malého okna.</summary>
    private static string LegendName(CellState st) => st == CellState.Weak ? Tr.T("slabý") : Theme.CellName(st);

    private float _dpi = 96f;
    private int Scale(int px) => (int)Math.Round(px * _dpi / 96.0);

    // ------------------------------------------------------------------ myš

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string text = HitText(e.Location);
        if (text != _tipText)
        {
            _tipText = text;
            _tip.SetToolTip(this, text);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipText = "";
        _tip.SetToolTip(this, "");
    }

    private string HitText(Point p) => Hit(p) is { } c ? CellText(c.track, c.side, c.sector) : "";

    private (int track, int side, int sector)? Hit(Point p)
    {
        var g = _model.Geometry;
        if (g == null || _cellW <= 0 || _cellH <= 0) return null;
        for (int side = 0; side < _sideRects.Length; side++)
        {
            var r = _sideRects[side];
            if (!r.Contains(p)) continue;
            int t = (int)((p.X - r.X) / _cellW), n = (int)((p.Y - r.Y) / _cellH);
            if (t < 0 || t >= g.Tracks || n < 0 || n >= g.SectorsPerTrack) return null;
            return (t, side, n);
        }
        return null;
    }

    private string CellText(int t, int side, int n)
    {
        string why = _model.ReasonFor(t, side, n) is string reason ? "\n" + reason : "";
        return Tr.F("Stopa {0}, strana {1}, sektor {2}: {3}", t, side, n + 1, Theme.CellName(_model.Get(t, side, n))) + why;
    }

    // ------------------------------------------------------------------ klávesnice
    // Tab na mapu, šipky posouvají kurzor (vlevo/vpravo stopa, nahoru/dolů sektor a strana); popis jako u myši.

    private (int track, int side, int sector) _cursor;
    private bool _cursorOn;   // kurzor až po kliknutí nebo šipce — ne hned, když mapa dostane fokus při startu

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Hit(e.Location) is { } c) { _cursor = c; _cursorOn = true; }
        Focus();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _cursorOn = false; _tip.Hide(this); Invalidate(); }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var g = _model.Geometry;
        if (g == null) return;
        var (t, s, n) = _cursor;
        int row = Math.Min(s, g.Sides - 1) * g.SectorsPerTrack + Math.Min(n, g.SectorsPerTrack - 1), rows = g.Sides * g.SectorsPerTrack;
        switch (e.KeyCode)
        {
            case Keys.Left: t--; break;
            case Keys.Right: t++; break;
            case Keys.Up: row--; break;
            case Keys.Down: row++; break;
            default: return;
        }
        t = Math.Max(0, Math.Min(g.Tracks - 1, t));
        row = Math.Max(0, Math.Min(rows - 1, row));
        // první šipka jen ukáže kurzor tam, kde je
        if (_cursorOn) _cursor = (t, row / g.SectorsPerTrack, row % g.SectorsPerTrack);
        _cursorOn = true;
        e.Handled = true;
        Invalidate();
        if (_cursor.side < _sideRects.Length)
        {
            var r = _sideRects[_cursor.side];
            _tip.Show(CellText(_cursor.track, _cursor.side, _cursor.sector), this,
                      (int)(r.X + (_cursor.track + 1) * _cellW) + Scale(6), (int)(r.Y + (_cursor.sector + 1) * _cellH) + Scale(6), 8000);
        }
    }
}
