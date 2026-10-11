using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AtariFloppy.Core.I18n;

/// <summary>
/// Překlady. Klíčem je český text (tak jak je v programu), překlad se hledá v jazykovém souboru:
///   <c>český text = přeložený text</c>, jeden na řádek, <c>#</c> = komentář, <c>{0}</c> = doplněná hodnota.
/// Angličtina je zabudovaná; další jazyky stačí přidat jako soubor <c>lang\xx.lang</c> vedle programu.
/// </summary>
public static class Tr
{
    public sealed record LanguageInfo(string Code, string Name);

    private static readonly object Sync = new();
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static string _code = "cs";
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    public static string Code => _code;
    public static event Action? LanguageChanged;

    /// <summary>Když je zapnuté, zaznamenávají se nepřeložené texty (pro doplnění překladu).</summary>
    public static bool CollectMissing { get; set; }
    public static IReadOnlyCollection<string> MissingKeys { get { lock (Sync) return Missing.ToList(); } }

    public static string LangFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");

    public static IReadOnlyList<LanguageInfo> Available()
    {
        var list = new List<LanguageInfo> { new("cs", "Čeština"), new("en", "English") };
        try
        {
            if (Directory.Exists(LangFolder))
                foreach (var f in Directory.GetFiles(LangFolder, "*.lang"))
                {
                    string code = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (list.Any(l => l.Code == code)) continue;
                    string name = ReadHeader(f, "language") ?? code;
                    list.Add(new LanguageInfo(code, name));
                }
        }
        catch { }
        return list;
    }

    /// <summary>Přepne jazyk. "cs" = originál; jiný kód = zabudovaný nebo externí soubor.</summary>
    public static void SetLanguage(string code)
    {
        code = (code ?? "cs").ToLowerInvariant();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (code != "cs")
        {
            string? builtIn = LoadEmbedded(code);
            if (builtIn != null) Parse(builtIn, map);
            string file = Path.Combine(LangFolder, code + ".lang");
            try { if (File.Exists(file)) Parse(File.ReadAllText(file, Encoding.UTF8), map); } catch { }
        }
        lock (Sync)
        {
            _map = map;
            _code = code;
            Missing.Clear();
        }
        LanguageChanged?.Invoke();
    }

    /// <summary>Přeloží text (český originál je klíč).</summary>
    public static string T(string cs)
    {
        if (_code == "cs" || string.IsNullOrEmpty(cs)) return cs;
        var map = _map;
        if (map.TryGetValue(cs, out var t)) return t;
        // jazykový soubor nemá mezery na krajích — zachováme je z originálu
        string core = cs.Trim();
        if (core.Length != cs.Length && core.Length > 0 && map.TryGetValue(core, out t))
        {
            int lead = cs.Length - cs.TrimStart().Length;
            return cs.Substring(0, lead) + t + cs.Substring(lead + core.Length);
        }
        if (CollectMissing) lock (Sync) Missing.Add(cs);
        return cs;
    }

    /// <summary>Přeloží šablonu s hodnotami: Tr.F("Stopa {0}, strana {1}", t, s).</summary>
    public static string F(string cs, params object?[] args)
    {
        string template = T(cs);
        try { return string.Format(CultureInfo.CurrentCulture, template, args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, cs, args); }
    }

    public static void Parse(string text, Dictionary<string, string> into)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int eq = FindSeparator(line);
            if (eq <= 0) continue;
            string key = Unescape(line.Substring(0, eq).Trim());
            string value = Unescape(line.Substring(eq + 3).Trim());
            if (key.Length > 0 && value.Length > 0) into[key] = value;
        }
    }

    /// <summary>Oddělovač je " = " (mezera, rovnítko, mezera) — v textech se samotné „=“ může vyskytnout.</summary>
    private static int FindSeparator(string line) => line.IndexOf(" = ", StringComparison.Ordinal);

    private static string Unescape(string s) => s.Replace(" \\= ", " = ").Replace("\\n", "\n").Replace("\\\\", "\\");

    private static string? ReadHeader(string file, string key)
    {
        foreach (var line in File.ReadLines(file).Take(10))
            if (line.StartsWith("#" + key + ":", StringComparison.OrdinalIgnoreCase))
                return line.Substring(key.Length + 2).Trim();
        return null;
    }

    private static string? LoadEmbedded(string code)
    {
        var asm = typeof(Tr).Assembly;
        string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + code + ".lang", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Obsah zabudovaného jazyka (pro vytvoření šablony nového překladu).</summary>
    public static string? EmbeddedText(string code) => LoadEmbedded(code);
}
