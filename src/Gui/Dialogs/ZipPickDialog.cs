using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

/// <summary>V .zip je víc obrazů (hra na několika disketách) — který otevřít.</summary>
internal sealed class ZipPickDialog : DialogBase
{
    private readonly ListBox _list = new() { Width = 480, Height = 200, HorizontalScrollbar = true };

    public string Selected => (string)_list.SelectedItem;

    public ZipPickDialog(string zipPath, IEnumerable<string> entries)
        : base(Tr.F("Archiv {0}", Path.GetFileName(zipPath)), new Size(540, 380))
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        flow.Controls.Add(Note(Tr.T("V archivu je víc obrazů disket. Který chceš použít?"), Theme.Text, 480));
        foreach (var e in entries) _list.Items.Add(e);
        _list.SelectedIndex = 0;
        flow.Controls.Add(_list);
        Body.Controls.Add(flow);
        AddCancel();
        AddButton(Tr.T("Vybrat"), DialogResult.OK, primary: true);
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Finish();
    }
}
