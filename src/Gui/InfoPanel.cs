using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AtariFloppy.Gui;

/// <summary>Posuvný seznam řádků: nadpisy, pole „název: hodnota“ a body se značkou (✓ ! ✗) a vysvětlením.</summary>
internal sealed class InfoPanel : Panel
{
    private enum Kind { Heading, Field, Item, Text }

    private sealed record Row(Kind Kind, string Title, string Detail, string Glyph, Color Color);

    /// <summary>Plátno v plné výšce obsahu; posouvání řeší Panel (Windows posunou hotový obraz, nic se nepřekresluje přes sebe).</summary>
    private sealed class Canvas : Control
    {
        private readonly InfoPanel _owner;
        public Canvas(InfoPanel owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            _owner.Arrange(e.Graphics, e.Graphics, Width);
        }
    }

    private readonly List<Row> _rows = new();
    private readonly Font _bold;
    private readonly Font _small;
    private readonly Canvas _canvas;
    private int _lastWidth = -1;

    public InfoPanel()
    {
        AutoScroll = true;
        BackColor = Theme.Panel;
        Font = Theme.UiFont(9f);
        _bold = Theme.UiFont(9.5f, FontStyle.Bold);
        _small = Theme.UiFont(8.25f);
        _canvas = new Canvas(this) { Location = Point.Empty, Font = Font };
        Controls.Add(_canvas);
        // jen svislé posouvání
        AutoScroll = false;
        HorizontalScroll.Maximum = 0;
        HorizontalScroll.Visible = false;
        HorizontalScroll.Enabled = false;
        AutoScroll = true;
    }

    public void Clear() { _rows.Clear(); if (IsHandleCreated) AutoScrollPosition = Point.Empty; Relayout(); }
    public void Heading(string text) => _rows.Add(new Row(Kind.Heading, text, "", "", Theme.Text));
    public void Field(string name, string value, Color? color = null) => _rows.Add(new Row(Kind.Field, name, value, "", color ?? Theme.Text));
    public void Item(string glyph, Color glyphColor, string title, string detail) => _rows.Add(new Row(Kind.Item, title, detail, glyph, glyphColor));
    public void Note(string text, Color? color = null) => _rows.Add(new Row(Kind.Text, text, "", "", color ?? Theme.TextDim));

    public void Done() => Relayout();

    private void Relayout()
    {
        if (!IsHandleCreated) return;
        // šířka bez posuvníku, ať se text nezalamuje jinak podle toho, jestli posuvník je vidět
        int width = ContentWidth;
        using var g = _canvas.CreateGraphics();
        int h = Arrange(g, null, width);
        var pos = AutoScrollPosition;
        _canvas.Bounds = new Rectangle(AutoScrollPosition.X, AutoScrollPosition.Y, width, Math.Max(h, 1));
        _lastWidth = width;
        _canvas.Invalidate();
        AutoScrollPosition = new Point(-pos.X, -pos.Y);
    }

    /// <summary>Šířka obsahu — místo pro svislý posuvník je rezervované vždy, vodorovný posuvník se tak nikdy neobjeví.</summary>
    private int ContentWidth => Math.Max(60, Width - SystemInformation.VerticalScrollBarWidth - 2);

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int width = ContentWidth;
        if (IsHandleCreated && width != _lastWidth) Relayout();
    }

    /// <summary>Spočítá (a když je draw != null, i nakreslí) řádky; vrací celkovou výšku.</summary>
    private int Arrange(Graphics measure, Graphics? draw, int areaWidth)
    {
        int pad = (int)(12 * measure.DpiX / 96), y = pad / 2;
        int width = areaWidth - 2 * pad - (int)(4 * measure.DpiX / 96);
        if (width < 40) return 0;
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        int glyphW = (int)(18 * measure.DpiX / 96);

        foreach (var r in _rows)
        {
            switch (r.Kind)
            {
                case Kind.Heading:
                    y += pad / 2;
                    var hs = TextRenderer.MeasureText(measure, r.Title, _bold, new Size(width, 0), flags);
                    if (draw != null)
                    {
                        TextRenderer.DrawText(draw, r.Title, _bold, new Rectangle(pad, y, width, hs.Height), Theme.Text, flags);
                        using var p = new Pen(Theme.Border);
                        draw.DrawLine(p, pad, y + hs.Height + 2, pad + width, y + hs.Height + 2);
                    }
                    y += hs.Height + pad / 2 + 2;
                    break;

                case Kind.Field:
                    int nameW = Math.Min(width / 2, (int)(96 * measure.DpiX / 96));
                    var vs = TextRenderer.MeasureText(measure, r.Detail, Font, new Size(width - nameW, 0), flags);
                    if (draw != null)
                    {
                        TextRenderer.DrawText(draw, r.Title, _small, new Rectangle(pad, y + 1, nameW, vs.Height), Theme.TextDim, flags);
                        TextRenderer.DrawText(draw, r.Detail, Font, new Rectangle(pad + nameW, y, width - nameW, vs.Height), r.Color, flags);
                    }
                    y += vs.Height + 3;
                    break;

                case Kind.Item:
                    var ts = TextRenderer.MeasureText(measure, r.Title, Font, new Size(width - glyphW, 0), flags);
                    var ds = r.Detail.Length > 0 ? TextRenderer.MeasureText(measure, r.Detail, _small, new Size(width - glyphW, 0), flags) : Size.Empty;
                    if (draw != null)
                    {
                        TextRenderer.DrawText(draw, r.Glyph, _bold, new Rectangle(pad, y, glyphW, ts.Height), r.Color, TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(draw, r.Title, Font, new Rectangle(pad + glyphW, y, width - glyphW, ts.Height), Theme.Text, flags);
                        if (r.Detail.Length > 0)
                            TextRenderer.DrawText(draw, r.Detail, _small, new Rectangle(pad + glyphW, y + ts.Height, width - glyphW, ds.Height), Theme.TextDim, flags);
                    }
                    y += ts.Height + ds.Height + 5;
                    break;

                case Kind.Text:
                    var xs = TextRenderer.MeasureText(measure, r.Title, _small, new Size(width, 0), flags);
                    if (draw != null) TextRenderer.DrawText(draw, r.Title, _small, new Rectangle(pad, y, width, xs.Height), r.Color, flags);
                    y += xs.Height + 4;
                    break;
            }
        }
        return y + pad;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold.Dispose(); _small.Dispose(); }
        base.Dispose(disposing);
    }
}
