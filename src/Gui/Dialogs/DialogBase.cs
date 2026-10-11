using System;
using System.Drawing;
using System.Windows.Forms;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Společný vzhled dialogů: obsah nahoře, tlačítka vpravo dole.</summary>
internal class DialogBase : Form
{
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 8) };
    protected readonly FlowLayoutPanel Buttons = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56,
        Padding = new Padding(12, 10, 12, 10), BackColor = Color.FromArgb(0xEE, 0xF0, 0xF3),
    };

    protected DialogBase(string title, Size size)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = Theme.UiFont(9f);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        ClientSize = size;
        MinimumSize = new Size(Math.Min(size.Width, 520), Math.Min(size.Height, 360));
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        Controls.Add(Body);
        Controls.Add(Buttons);
    }

    protected void Finish() { ResumeLayout(false); PerformLayout(); }

    protected Button AddButton(string text, DialogResult result, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Text = text, DialogResult = result, AutoSize = true, MinimumSize = new Size(110, 32),
            FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(10, 0, 10, 0), Cursor = Cursors.Hand,
        };
        if (primary || danger)
        {
            b.BackColor = danger ? Theme.Danger : Theme.Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = b.BackColor;
            b.Font = Theme.UiFont(9f, FontStyle.Bold);
            AcceptButton = b;
        }
        else
        {
            b.BackColor = Theme.Panel;
            b.FlatAppearance.BorderColor = Theme.Border;
        }
        if (result == DialogResult.Cancel) CancelButton = b;
        Buttons.Controls.Add(b);
        return b;
    }

    protected Button AddCancel() => AddButton(Tr.T("Zrušit"), DialogResult.Cancel);

    protected static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.UiFont(11f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 6),
    };

    protected static Label Note(string text, Color? color = null, int width = 520) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color ?? Theme.TextDim, Margin = new Padding(0, 2, 0, 8),
    };
}
