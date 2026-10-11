using System;
using System.Runtime.InteropServices;
using AtariFloppy.Core.Drives;

namespace AtariFloppy.Cli;

internal static class Ui
{
    public static bool AssumeYes;

    public static void Title(string s)
    {
        Console.WriteLine();
        Write(s, ConsoleColor.Cyan, true);
        Write(new string('─', Math.Min(s.Length, 70)), ConsoleColor.DarkCyan, true);
    }

    public static void Write(string s, ConsoleColor color, bool newline = false)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        if (newline) Console.WriteLine(s); else Console.Write(s);
        Console.ForegroundColor = old;
    }

    public static void Ok(string s) => Write(s, ConsoleColor.Green, true);
    public static void Warn(string s) => Write(s, ConsoleColor.Yellow, true);
    public static void Error(string s) => Write(s, ConsoleColor.Red, true);
    public static void Dim(string s) => Write(s, ConsoleColor.DarkGray, true);

    public static void Field(string label, string value, ConsoleColor color = ConsoleColor.Gray)
    {
        Write(("  " + label + ":").PadRight(26), ConsoleColor.DarkGray);
        Write(value, color, true);
    }

    public static bool Confirm(string question)
    {
        if (AssumeYes) return true;
        Write(question + " (a/n) ", ConsoleColor.Yellow);
        string? a = Console.ReadLine();
        return a != null && (a.Trim().StartsWith("a", StringComparison.OrdinalIgnoreCase) || a.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase));
    }

    public static string Ask(string question, string? defaultValue = null)
    {
        Write(question + (defaultValue != null ? $" [{defaultValue}]" : "") + ": ", ConsoleColor.White);
        string a = (Console.ReadLine() ?? "").Trim();
        // přetažený soubor do konzole přijde v uvozovkách
        if (a.Length >= 2 && a[0] == '"' && a[a.Length - 1] == '"') a = a.Substring(1, a.Length - 2);
        return a.Length == 0 && defaultValue != null ? defaultValue : a;
    }

    // ---------------------------------------------------------------- průběh

    private static int _lastLen;

    public static void Progress(int done, int total, string text)
    {
        const int width = 30;
        int filled = total == 0 ? width : done * width / total;
        int pct = total == 0 ? 100 : done * 100 / total;
        string line = $"\r  [{new string('█', filled)}{new string('·', width - filled)}] {pct,3} %  {text}";
        int pad = Math.Max(0, _lastLen - line.Length);
        Console.Write(line + new string(' ', pad));
        _lastLen = line.Length;
    }

    public static void EndProgress()
    {
        Console.WriteLine();
        _lastLen = 0;
    }

    public static void AvailabilityLine(Feature f, Availability a)
    {
        (string mark, ConsoleColor color) = a switch
        {
            Availability.Available => ("  ✓ ", ConsoleColor.Green),
            Availability.Planned => ("  … ", ConsoleColor.Cyan),
            Availability.NeedsDriver => ("  ! ", ConsoleColor.Yellow),
            Availability.Maybe => ("  ? ", ConsoleColor.Yellow),
            _ => ("  ✗ ", ConsoleColor.DarkGray),
        };
        Write(mark, color);
        Write(Capabilities.Describe(f).PadRight(48), a == Availability.Unsupported ? ConsoleColor.DarkGray : ConsoleColor.Gray);
        Write(Capabilities.Describe(a), color, true);
    }

    // ---------------------------------------------------------------- konzole

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] list, uint count);

    /// <summary>Spuštěno dvojklikem / přes UAC ve vlastním okně? Pak na konci počkáme, ať okno hned nezmizí.</summary>
    public static bool OwnsConsole()
    {
        try { return GetConsoleProcessList(new uint[4], 4) <= 1; }
        catch { return false; }
    }
}
