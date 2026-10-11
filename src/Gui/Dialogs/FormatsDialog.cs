using System.Drawing;
using System.Windows.Forms;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>Přehled 3,5" disket různých počítačů — co s čím přečteš a zapíšeš.</summary>
internal sealed class FormatsDialog : DialogBase
{
    public FormatsDialog() : base(Tr.T("Formáty disket všech systémů"), new Size(1240, 660))
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Note(Tr.T("✓ jde   ? záleží na řadiči / disketě   ✗ nejde.  Najetím myší na řádek se ukáže celý popis."), null, 1000), 0, 0);

        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        list.Columns.Add(Tr.T("Systém"), 190);
        list.Columns.Add(Tr.T("Formát"), 240);
        list.Columns.Add("USB", 50, HorizontalAlignment.Center);
        list.Columns.Add(Tr.T("Interní + fdrawcmd"), 135, HorizontalAlignment.Center);
        list.Columns.Add("Greaseweazle", 105, HorizontalAlignment.Center);
        list.Columns.Add(Tr.T("K čemu"), 300);
        list.Columns.Add(Tr.T("Poznámka"), 190);
        foreach (var f in FormatCatalog.All)
        {
            var it = new ListViewItem(f.System) { ToolTipText = f.UsedFor + (f.Note != null ? "\n" + f.Note : ""), UseItemStyleForSubItems = false };
            it.SubItems.Add(f.Format);
            foreach (var a in new[] { f.Usb, f.InternalFdraw, f.Greaseweazle })
            {
                var (g, c) = a switch
                {
                    Availability.Available => ("✓", Theme.Ok),
                    Availability.Maybe => ("?", Theme.Warn),
                    _ => ("✗", Theme.TextDim),
                };
                it.SubItems.Add(g, c, list.BackColor, list.Font);
            }
            it.SubItems.Add(f.UsedFor, Theme.TextDim, list.BackColor, list.Font);
            it.SubItems.Add(f.Note ?? "", Theme.Warn, list.BackColor, list.Font);
            list.Items.Add(it);
        }
        layout.Controls.Add(list, 0, 1);
        Body.Controls.Add(layout);
        AddButton(Tr.T("Zavřít"), DialogResult.Cancel, primary: true);
        Finish();
    }
}
