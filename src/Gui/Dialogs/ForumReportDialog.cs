using System;
using System.Drawing;
using System.Windows.Forms;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Ukáže zprávu pro fórum, která je už ve schránce — ať tester vidí, co přesně vkládá.</summary>
internal sealed class ForumReportDialog : DialogBase
{
    public ForumReportDialog(string text, bool copied, Action<string> openLink)
        : base(Tr.T("Zpráva pro fórum"), new Size(820, 600))
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var notes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        notes.Controls.Add(Note(copied
            ? Tr.T("Zpráva je ve schránce. Vlož ji (Ctrl+V) do příspěvku na fóru, ve skupině na Facebooku nebo v GitHub Discussions — program sám nic neodesílá.")
            : Tr.T("Do schránky se zprávu nepodařilo vložit. Označ text níže (Ctrl+A) a zkopíruj ho (Ctrl+C)."), Theme.Text, 760));
        notes.Controls.Add(Note(Tr.T("Jméno uživatele a počítače jsou ve zprávě nahrazené zástupnými slovy, domovská složka je %USERPROFILE%."), null, 760));
        layout.Controls.Add(notes, 0, 0);
        var box = new TextBox
        {
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = Theme.Window, Font = Theme.UiFont(9f),
        };
        layout.Controls.Add(box, 0, 1);
        Body.Controls.Add(layout);
        AddButton(Tr.T("Zavřít"), DialogResult.Cancel, primary: true);
        var again = AddButton(Tr.T("Kopírovat znovu"), DialogResult.None);
        again.Click += (_, _) => { try { Clipboard.SetText(text); } catch (Exception) { box.Focus(); box.SelectAll(); } };
        var discuss = AddButton("GitHub Discussions", DialogResult.None);
        discuss.Click += (_, _) => openLink(Program.ProjectUrl + "/discussions");
        Shown += (_, _) => box.Select(0, 0);
        Finish();
    }
}
