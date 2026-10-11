using System.Drawing;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Gui;

/// <summary>Barvy a písma okna.</summary>
internal static class Theme
{
    public static readonly Color Window = Color.FromArgb(0xF5, 0xF6, 0xF8);
    public static readonly Color Panel = Color.White;
    public static readonly Color Border = Color.FromArgb(0xDD, 0xE1, 0xE6);
    public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
    public static readonly Color TextDim = Color.FromArgb(0x65, 0x6D, 0x76);
    public static readonly Color Accent = Color.FromArgb(0x0A, 0x64, 0xD6);
    public static readonly Color Danger = Color.FromArgb(0xCF, 0x22, 0x2E);
    public static readonly Color Ok = Color.FromArgb(0x1A, 0x7F, 0x37);
    public static readonly Color Warn = Color.FromArgb(0x9A, 0x67, 0x00);
    public static readonly Color TopBar = Color.FromArgb(0x1C, 0x21, 0x2B);
    public static readonly Color TopBarText = Color.FromArgb(0xE6, 0xED, 0xF3);

    // mapa diskety — tmavá plocha jako obrazovka Atari, ale moderní barvy
    public static readonly Color MapBack = Color.FromArgb(0x16, 0x19, 0x20);
    public static readonly Color MapGrid = Color.FromArgb(0x10, 0x12, 0x17);
    public static readonly Color MapText = Color.FromArgb(0x8B, 0x94, 0x9E);
    public static readonly Color MapHead = Color.FromArgb(0xF0, 0xF6, 0xFC);

    public static Font UiFont(float size = 9f, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static Color Cell(CellState s) => s switch
    {
        CellState.Read => Color.FromArgb(0x2E, 0xA0, 0x43),
        CellState.Formatted => Color.FromArgb(0x82, 0x50, 0xDF),
        CellState.Written => Color.FromArgb(0x1F, 0x6F, 0xEB),
        CellState.Verified => Color.FromArgb(0x39, 0xC5, 0xCF),
        CellState.Weak => Color.FromArgb(0xD2, 0x99, 0x22),
        CellState.Bad => Color.FromArgb(0xF8, 0x51, 0x49),
        CellState.WriteFailed => Color.FromArgb(0xDB, 0x61, 0xA2),
        CellState.Skipped => Color.FromArgb(0x6E, 0x76, 0x81),
        _ => Color.FromArgb(0x2B, 0x30, 0x3B),
    };

    public static string CellName(CellState s) => s switch
    {
        CellState.Read => Tr.T("přečteno"),
        CellState.Formatted => Tr.T("naformátováno"),
        CellState.Written => Tr.T("zapsáno"),
        CellState.Verified => Tr.T("ověřeno"),
        CellState.Weak => Tr.T("slabý (čte se až napodruhé)"),
        CellState.Bad => Tr.T("nečitelný"),
        CellState.WriteFailed => Tr.T("selhal zápis"),
        CellState.Skipped => Tr.T("vynecháno"),
        _ => Tr.T("zatím nic"),
    };

    public static string PhaseName(TrackPhase p) => p switch
    {
        TrackPhase.Reading => Tr.T("čtení"),
        TrackPhase.Writing => Tr.T("zápis"),
        TrackPhase.Formatting => Tr.T("formát"),
        TrackPhase.Verifying => Tr.T("kontrola"),
        TrackPhase.Erasing => Tr.T("mazání"),
        _ => "",
    };

    public static (string glyph, Color color) Mark(ProfileStatus s) => s switch
    {
        ProfileStatus.Ready => ("✓", Ok),
        ProfileStatus.NoDisk or ProfileStatus.WrongMedia or ProfileStatus.NeedsDriver => ("!", Warn),
        ProfileStatus.Planned => ("…", Accent),
        _ => ("✗", TextDim),
    };

    public static (string glyph, Color color) Mark(Availability a) => a switch
    {
        Availability.Available => ("✓", Ok),
        Availability.Maybe or Availability.NeedsDriver or Availability.NeedsTool => ("!", Warn),
        Availability.Planned => ("…", Accent),
        _ => ("✗", TextDim),
    };
}
