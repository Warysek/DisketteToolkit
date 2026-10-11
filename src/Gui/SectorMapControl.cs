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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
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

        // záhlaví: název operace a poloha hlavy
        string title = _model.Title.Length > 0 ? _model.Title : (g != null ? Tr.T("Mapa diskety") : "");
        using (var white = new SolidBrush(Color.FromArgb(0xE6, 0xED, 0xF3)))
            gfx.DrawString(title, titleFont, white, pad, y);
        string right = g == null ? "" : Tr.F("{0} stop × {1} {2} × {3} sektorů", g.Tracks, g.Sides, g.Sides == 1 ? Tr.T("strana") : Tr.T("strany"), g.SectorsPerTrack);
        if (_model.Running && _model.HeadTrack >= 0)
            right = Tr.F("{0}: stopa {1}{2}", Theme.PhaseName(_model.HeadPhase), _model.HeadTrack,
                         _model.HeadSide >= 0 ? Tr.F(", strana {0}", _model.HeadSide) : "") + "   ·   " + right;
        var rs = gfx.MeasureString(right, Font);
        gfx.DrawString(right, Font, textBrush, Width - pad - rs.Width, y + 3);
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

        // pravítko stop
        int rulerY = y;
        for (int t = 0; t < g.Tracks; t++)
        {
            float x = left + t * _cellW;
            if (t % 10 == 0)
            {
                gfx.DrawString(t.ToString(), small, textBrush, x - 1, rulerY);
                using var p = new Pen(Theme.MapText);
                gfx.DrawLine(p, x, rulerY + rulerH - Scale(4), x, rulerY + rulerH - 1);
            }
            else if (t % 5 == 0)
            {
                using var p = new Pen(Color.FromArgb(0x55, 0x5E, 0x69));
                gfx.DrawLine(p, x, rulerY + rulerH - Scale(3), x, rulerY + rulerH - 1);
            }
        }
        y += rulerH;

        var rects = new List<Rectangle>();
        var brushes = new Dictionary<CellState, SolidBrush>();
        SolidBrush BrushFor(CellState s) => brushes.TryGetValue(s, out var b) ? b : brushes[s] = new SolidBrush(Theme.Cell(s));
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
                        gfx.FillRectangle(BrushFor(st), left + t * _cellW, y + n * _cellH, Math.Max(1f, _cellW - gap), Math.Max(1f, _cellH - gap));
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

            DrawLegend(gfx, small, textBrush, pad, Height - pad - legendH + Scale(4), Width - 2 * pad, BrushFor);
        }
        finally
        {
            foreach (var b in brushes.Values) b.Dispose();
        }
        _sideRects = rects.ToArray();
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
        Scale(12) + Scale(5) + (int)gfx.MeasureString(Theme.CellName(st), f).Width + Scale(16);

    private void DrawLegend(Graphics gfx, Font f, Brush text, int x0, int y0, int width, Func<CellState, SolidBrush> brush)
    {
        int x = 0, y = y0, rowH = (int)f.GetHeight(gfx) + Scale(6), box = Scale(11);
        foreach (var st in LegendStates)
        {
            int w = LegendItemWidth(gfx, f, st);
            if (x + w > width && x > 0) { x = 0; y += rowH; }
            gfx.FillRectangle(brush(st), x0 + x, y + (rowH - box) / 2 - 1, box, box);
            gfx.DrawString(Theme.CellName(st), f, text, x0 + x + box + Scale(5), y + (rowH - f.GetHeight(gfx)) / 2 - 1);
            x += w;
        }
    }

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

    private string HitText(Point p)
    {
        var g = _model.Geometry;
        if (g == null || _cellW <= 0 || _cellH <= 0) return "";
        for (int side = 0; side < _sideRects.Length; side++)
        {
            var r = _sideRects[side];
            if (!r.Contains(p)) continue;
            int t = (int)((p.X - r.X) / _cellW), n = (int)((p.Y - r.Y) / _cellH);
            if (t < 0 || t >= g.Tracks || n < 0 || n >= g.SectorsPerTrack) return "";
            return Tr.F("Stopa {0}, strana {1}, sektor {2}: {3}", t, side, n + 1, Theme.CellName(_model.Get(t, side, n)));
        }
        return "";
    }
}
