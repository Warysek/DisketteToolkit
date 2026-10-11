using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AtariFloppy.Gui;

/// <summary>Velké tlačítko akce: symbol, název a podtitulek (u nedostupné akce důvod).</summary>
internal sealed class ActionButton : Control
{
    private bool _hover, _down;
    private string _subtitle = "";
    private bool _blocked;

    public string Glyph { get; set; } = "";
    public string Subtitle { get => _subtitle; set { _subtitle = value ?? ""; Invalidate(); } }
    /// <summary>Akce teď nejde (důvod je v podtitulku) — tlačítko vypadá tlumeně, ale kliknout jde (vysvětlí proč).</summary>
    public bool Blocked { get => _blocked; set { _blocked = value; Invalidate(); } }
    public bool Danger { get; set; }

    public ActionButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.UiFont(9.5f, FontStyle.Bold);
        BackColor = Theme.Panel;
        TabStop = true;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        float dpi = g.DpiX / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        Color back = !Enabled ? Theme.Window : _down ? Color.FromArgb(0xE3, 0xEC, 0xF8) : _hover ? Color.FromArgb(0xEE, 0xF4, 0xFC) : Theme.Panel;
        Color border = Focused ? Theme.Accent : _hover && Enabled ? Color.FromArgb(0xA8, 0xC7, 0xEE) : Theme.Border;
        using (var path = Rounded(r, 8 * dpi))
        {
            using var b = new SolidBrush(back);
            g.FillPath(b, path);
            using var p = new Pen(border, Focused ? 1.6f : 1f);
            g.DrawPath(p, path);
        }

        bool dim = !Enabled || Blocked;
        Color glyphColor = dim ? Theme.TextDim : Danger ? Theme.Danger : Theme.Accent;
        int gw = (int)(34 * dpi);
        using (var gf = new Font("Segoe UI Symbol", 15f))
            TextRenderer.DrawText(g, Glyph, gf, new Rectangle((int)(6 * dpi), 0, gw, Height), glyphColor,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int x = (int)(6 * dpi) + gw + (int)(4 * dpi), w = Width - x - (int)(8 * dpi);
        using var small = Theme.UiFont(8f);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        var th = TextRenderer.MeasureText(g, Text, Font, new Size(w, 0), flags).Height;
        var sub = Subtitle;
        var sflags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;
        int sh = sub.Length > 0 ? Math.Min(TextRenderer.MeasureText(g, sub, small, new Size(w, 0), sflags).Height, Height - th - (int)(10 * dpi)) : 0;
        int y = (Height - th - sh) / 2;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, y, w, th), dim ? Theme.TextDim : Theme.Text, flags);
        if (sh > 0)
            TextRenderer.DrawText(g, sub, small, new Rectangle(x, y + th, w, sh), Blocked ? Theme.Warn : Theme.TextDim, sflags);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
