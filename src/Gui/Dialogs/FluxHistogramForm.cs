using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Histogram magnetického toku jedné stopy ze souboru .scp (Greaseweazle / SuperCard Pro).</summary>
internal sealed class FluxHistogramForm : Form
{
    private readonly ScpFile _scp;
    private readonly NumericUpDown _cyl = new() { Minimum = 0, Maximum = 83, Width = 60 };
    private readonly NumericUpDown _head = new() { Minimum = 0, Maximum = 1, Width = 50 };
    private readonly Panel _chart = new() { Dock = DockStyle.Fill, BackColor = Theme.MapBack };
    private readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(12, 8, 12, 8), Font = Theme.UiFont(9.5f) };
    private FluxHistogram? _hist;

    public FluxHistogramForm(string scpPath, int cyl, int head)
    {
        _scp = ScpFile.Load(scpPath);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Tr.F("Histogram toku — {0}", System.IO.Path.GetFileName(scpPath));
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = Theme.UiFont(9f);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 8, 10, 4), WrapContents = false };
        top.Controls.Add(new Label { Text = Tr.T("stopa:"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_cyl);
        top.Controls.Add(new Label { Text = Tr.T("strana:"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
        top.Controls.Add(_head);
        top.Controls.Add(new Label
        {
            Text = Tr.T("Vodorovně délka mezery mezi magnetickými změnami (µs), svisle jak často se vyskytuje."),
            AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(16, 6, 0, 0),
        });
        var tracks = _scp.Tracks().ToList();
        if (tracks.Count > 0) _cyl.Maximum = Math.Max(0, tracks.Max(t => t.cyl));
        _cyl.Value = Math.Min(cyl, (int)_cyl.Maximum);
        _head.Value = head;
        _cyl.ValueChanged += (_, _) => LoadTrack();
        _head.ValueChanged += (_, _) => LoadTrack();
        _chart.Paint += (_, e) => DrawChart(e.Graphics);
        _chart.Resize += (_, _) => _chart.Invalidate();
        Controls.Add(_chart);
        Controls.Add(_info);
        Controls.Add(top);
        LoadTrack();
    }

    private void LoadTrack()
    {
        int c = (int)_cyl.Value, h = (int)_head.Value;
        if (!_scp.HasTrack(c, h))
        {
            _hist = null;
            _info.Text = Tr.F("Stopa {0}, strana {1} v souboru není.", c, h);
            _chart.Invalidate();
            return;
        }
        _hist = FluxHistogram.Build(_scp.FluxUs(c, h));
        var revs = _scp.RevolutionMs(c, h).Where(ms => ms > 100).ToList();
        string rpm = revs.Count > 0 ? Tr.F("otáčky {0:0.0} ot./min", 60000 / revs.Average()) : "";
        _info.Text = Tr.F("Stopa {0}, strana {1}: {2} změn, {3} otáček{4}", c, h, _hist.Samples, revs.Count, rpm.Length > 0 ? ", " + rpm : "") + "\n" +
                     Tr.T("Zápis: ") + _hist.Encoding + "\n" +
                     Tr.T("Hodnocení: ") + _hist.Verdict +
                     (_hist.Peaks.Count > 0 ? Tr.F(" (rozptyl {0:0} ns, mimo špičky {1:0.0} %)", _hist.SpreadNs, _hist.OutlierShare * 100) : "") + "\n" +
                     Tr.T("Hodnocení je orientační — nejvíc řekne srovnání se stopou z dobré diskety.");
        _info.ForeColor = _hist.Grade switch
        {
            FluxHistogram.Quality.Sharp => Theme.Ok,
            FluxHistogram.Quality.Fair => Theme.Warn,
            _ => Theme.Danger,
        };
        _chart.Invalidate();
    }

    private void DrawChart(Graphics g)
    {
        g.Clear(Theme.MapBack);
        var h = _hist;
        if (h == null || h.Samples == 0) return;
        int pad = 40, w = _chart.Width - pad - 16, ht = _chart.Height - pad - 16;
        if (w < 50 || ht < 50) return;
        int x0 = pad, y0 = 12 + ht;
        double show = Math.Min(FluxHistogram.MaxUs, Math.Max(6, (h.Peaks.Count > 0 ? h.Peaks.Max() : 8) + 2));
        int bins = (int)(show / FluxHistogram.BinUs);
        int max = h.Bins.Take(bins).DefaultIfEmpty(1).Max();
        using var bar = new SolidBrush(Theme.Cell(CellState.Read));
        using var axis = new Pen(Theme.MapText);
        using var text = new SolidBrush(Theme.MapText);
        using var peakPen = new Pen(Theme.MapHead) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        float bw = (float)w / bins;
        for (int i = 0; i < bins; i++)
        {
            float bh = max > 0 ? (float)h.Bins[i] / max * ht : 0;
            if (bh > 0) g.FillRectangle(bar, x0 + i * bw, y0 - bh, Math.Max(1f, bw), bh);
        }
        g.DrawLine(axis, x0, y0, x0 + w, y0);
        for (int us = 0; us <= (int)show; us++)
        {
            float x = x0 + (float)(us / show) * w;
            g.DrawLine(axis, x, y0, x, y0 + 4);
            g.DrawString(us + " µs", Font, text, x - 12, y0 + 6);
        }
        foreach (double p in h.Peaks)
        {
            float x = x0 + (float)(p / show) * w;
            g.DrawLine(peakPen, x, 12, x, y0);
            g.DrawString(p.ToString("0.0"), Font, text, x + 3, 14);
        }
    }
}
