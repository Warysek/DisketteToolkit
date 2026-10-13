using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui.Dialogs;

internal enum DiagAction { Rpm, RpmLive, Sensors, HeadZero, HeadLast, Sweep, Clean, TrackRead, Layout }

internal sealed class DiagRequest
{
    public DiagAction Action { get; init; }
    public int Cyl { get; init; }
    public int Head { get; init; }
    public int Reads { get; init; } = 20;
    public int Passes { get; init; } = 3;
}

/// <summary>Diagnostika a servis mechaniky: otáčky, čidla, pohyb hlavy, čištění, čtení stopy, rozložení stop.</summary>
internal sealed class DiagDialog : DialogBase
{
    private readonly (RadioButton radio, DiagAction action, Func<string> help)[] _items;
    private readonly NumericUpDown _cyl = new() { Minimum = 0, Maximum = 83, Width = 60 };
    private readonly NumericUpDown _head = new() { Minimum = 0, Maximum = 1, Width = 50 };
    private readonly NumericUpDown _reads = new() { Minimum = 2, Maximum = 200, Value = 20, Width = 60 };
    private readonly NumericUpDown _passes = new() { Minimum = 1, Maximum = 10, Value = 3, Width = 50 };
    private readonly Label _help = Note("", Theme.Text, 600);
    private readonly TextBox _note = new() { Width = 560 };

    public DiagRequest Request
    {
        get
        {
            var a = _items.First(i => i.radio.Checked).action;
            return new DiagRequest { Action = a, Cyl = (int)_cyl.Value, Head = (int)_head.Value, Reads = (int)_reads.Value, Passes = (int)_passes.Value };
        }
    }

    public string DriveNote => _note.Text.Trim();

    public DiagDialog(DetectedDrive d, string note, string lastRpm, string lastCleaned)
        : base(Tr.F("Diagnostika mechaniky — {0}", d.Id), new Size(680, 660))
    {
        bool gw = d.Kind == DriveKind.Greaseweazle;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Heading(Tr.T("Co udělat")));

        RadioButton R(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        _items = new (RadioButton, DiagAction, Func<string>)[]
        {
            (R(Tr.T("Změřit otáčky (10 otáček)")), DiagAction.Rpm,
                () => Tr.T("Vlož libovolnou naformátovanou disketu (ne originál, kterého si vážíš). Program změří 10 otáček a řekne, jestli mechanika točí 300 ot./min a jestli otáčky nekolísají.")),
            (R(Tr.T("Živé měření otáček — pro seřízení")), DiagAction.RpmLive,
                () => Tr.T("Otevře se okénko s velkým číslem otáček, které se průběžně obnovuje. Při seřizování otáčej trimrem rychlosti na desce mechaniky a sleduj, jak se číslo blíží k 300. Měření běží, dokud nestiskneš Zastavit / Přerušit.")),
            (R(Tr.T("Kontrola čidel")), DiagAction.Sensors,
                () => Tr.T("Ověří čidlo stopy 0 (hlava na stopě 0 a mimo ni), ochranu proti zápisu, vloženou disketu a index. Zkus to dvakrát: s otevřeným a se zavřeným okénkem ochrany proti zápisu.")),
            (R(Tr.T("Hlava na stopu 0")), DiagAction.HeadZero,
                () => Tr.T("Přesune hlavu na začátek (vnější okraj). Hodí se při čištění hlavy nebo kontrole mechaniky bez krytu.")),
            (R(Tr.T("Hlava na poslední stopu (79)")), DiagAction.HeadLast,
                () => Tr.T("Přesune hlavu na konec dráhy (vnitřní okraj) — třeba kvůli promazání vodítek.")),
            (R(Tr.T("Projet hlavou celou dráhu 10×")), DiagAction.Sweep,
                () => Tr.T("Hlava jezdí mezi stopou 0 a 79. Rozjede zatuhlou mechaniku po letech ve skříni a rozetře mazivo po promazání vodítek. Disketa není potřeba.")),
            (R(Tr.T("Vyčistit hlavu čisticí disketou")), DiagAction.Clean,
                () => Tr.T("Vlož čisticí disketu (navlhčenou podle návodu). Hlava bude jezdit cik-cak po celé dráze s točící se disketou. Obyčejnou disketu nevkládej — čištění ji opotřebuje.")),
            (R(Tr.T("Opakované čtení jedné stopy")), DiagAction.TrackRead,
                () => Tr.T("Přečte zvolenou stopu opakovaně a ukáže, kolikrát se přečetla celá. Když chyby skáčou po různých sektorech, bývá špinavá hlava; když jsou pořád na stejném místě, je poškozená disketa.")),
            (R(Tr.T("Výpis rozložení všech stop")), DiagAction.Layout,
                gw ? () => Tr.T("Tohle zatím umí jen interní mechanika s fdrawcmd.sys.")
                   : () => Tr.T("Pro každou stopu vypíše pořadí sektorů a jejich polohu od indexu a upozorní na odchylky (duplicitní sektory, cizí čísla stop, chybějící sektory) — typické znaky ochrany proti kopírování.")),
        };
        _items[0].radio.Checked = true;
        if (gw) _items.First(i => i.action == DiagAction.Layout).radio.Enabled = false;

        foreach (var (radio, action, _) in _items)
        {
            radio.CheckedChanged += (_, _) => UpdateHelp();
            if (action == DiagAction.Clean)
                flow.Controls.Add(Row(radio, new Label { Text = Tr.T("průchodů:"), AutoSize = true, Margin = new Padding(12, 6, 3, 0) }, _passes));
            else if (action == DiagAction.TrackRead)
                flow.Controls.Add(Row(radio,
                    new Label { Text = Tr.T("stopa:"), AutoSize = true, Margin = new Padding(12, 6, 3, 0) }, _cyl,
                    new Label { Text = Tr.T("strana:"), AutoSize = true, Margin = new Padding(8, 6, 3, 0) }, _head,
                    new Label { Text = Tr.T("kolikrát:"), AutoSize = true, Margin = new Padding(8, 6, 3, 0) }, _reads));
            else flow.Controls.Add(radio);
        }
        var helpBox = new Panel { Width = 610, Height = 78, BackColor = Theme.Window, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 10, 0, 8) };
        _help.Dock = DockStyle.Fill;
        _help.AutoSize = false;
        helpBox.Controls.Add(_help);
        flow.Controls.Add(helpBox);

        flow.Controls.Add(Heading(Tr.T("Záznam o mechanice")));
        flow.Controls.Add(Note(Tr.F("Naposledy změřené otáčky: {0}", lastRpm.Length > 0 ? lastRpm : Tr.T("zatím neměřeno")), null, 600));
        flow.Controls.Add(Note(Tr.F("Naposledy čištěno: {0}", lastCleaned.Length > 0 ? lastCleaned : Tr.T("zatím ne")), null, 600));
        flow.Controls.Add(new Label { Text = Tr.T("Poznámka (model, co se opravovalo…):"), AutoSize = true, Margin = new Padding(0, 4, 0, 2) });
        _note.Text = note;
        flow.Controls.Add(_note);
        flow.Controls.Add(Note(gw
            ? Tr.T("Greaseweazle: měření a čidla obstarává program gw. Výsledky se zapíšou do výpisu — odtud je můžeš zkopírovat (pravé tlačítko myši).")
            : Tr.T("fdrawcmd.sys: měření obstarává řadič na základní desce; různé řadiče se mohou chovat různě. Výsledky se zapíšou do výpisu — odtud je můžeš zkopírovat (pravé tlačítko myši)."), null, 600));
        Body.Controls.Add(flow);

        AddCancel();
        AddButton(Tr.T("Spustit"), DialogResult.OK, primary: true);
        UpdateHelp();
        Finish();
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private void UpdateHelp() => _help.Text = _items.First(i => i.radio.Checked).help();
}
