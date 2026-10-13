using System;
using System.Drawing;
using System.Windows.Forms;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Okénko s velkým číslem otáček pro seřizování mechaniky (zůstává nad ostatními okny).</summary>
internal sealed class RpmGaugeForm : Form
{
    private readonly Label _value = new() { Dock = DockStyle.Top, Height = 92, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.UiFont(40f, FontStyle.Bold) };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter, Font = Theme.UiFont(10f) };
    private readonly Button _stop = new() { Dock = DockStyle.Bottom, Height = 38, FlatStyle = FlatStyle.Flat };

    public RpmGaugeForm(Action stop)
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Tr.T("Otáčky mechaniky — živě");
        ClientSize = new Size(420, 250);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Padding = new Padding(12);
        _value.Text = "…";
        _detail.Text = Tr.T("Měřím…");
        _stop.Text = Tr.T("Zastavit měření");
        _stop.BackColor = Theme.Panel;
        _stop.ForeColor = Theme.Danger;
        _stop.FlatAppearance.BorderColor = Theme.Border;
        _stop.Click += (_, _) => stop();
        Controls.Add(_detail);
        Controls.Add(_value);
        Controls.Add(_stop);
    }

    public void Show(string value, string detail, Color color)
    {
        _value.Text = value;
        _value.ForeColor = color;
        _detail.Text = detail;
    }
}
