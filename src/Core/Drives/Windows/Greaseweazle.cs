using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Drives.Windows;

/// <summary>
/// Detekce Greaseweazle (USB VID 1209, PID 4D69; starší kusy 1209:0001) a dotaz na firmware
/// přes jeho sériový protokol (příkaz GetInfo).
/// </summary>
public static class Greaseweazle
{
    private static readonly Dictionary<int, string[]> Models = new()
    {
        [1] = new[] { "F1", "F1 Plus", "F1 Plus (Unbuffered)" },
        [4] = new[] { "V4", "V4 Slim", "V4.1" },
        [7] = new[] { "F7 v1", "F7 Plus", "F7 Lightning", "F7 v2", "F7 Plus v2", "F7 Lightning Plus", "F7 Slim", "F7 v3 Thunderbolt" },
        [8] = new[] { "Adafruit Floppy Generic" },
    };

    public sealed record Info(int Major, int Minor, bool UpdateMode, uint SampleFreq, string Model);

    public static IEnumerable<DetectedDrive> Detect(List<string>? log)
    {
        var found = new List<(string port, string name, bool sure)>();
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_1209&PID_4D69%' OR PNPDeviceID LIKE 'USB\\\\VID_1209&PID_0001%'"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"] as string ?? "";
                string id = mo["PNPDeviceID"] as string ?? "";
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (!m.Success) continue;
                found.Add((m.Groups[1].Value, name, id.IndexOf("PID_4D69", StringComparison.OrdinalIgnoreCase) >= 0));
            }
        }

        string? toolPath = Gw.GwTool.Find()?.Path;
        foreach (var (port, name, sure) in found)
        {
            Info? info = null;
            try { info = QueryInfo(port); }
            catch (Exception e) { log?.Add(Tr.F("Greaseweazle na {0}: nepodařilo se přečíst firmware ({1}).", port, e.Message)); }

            if (info == null && !sure) continue; // sdílené testovací PID — bez odpovědi to nemusí být Greaseweazle

            string details = info == null
                ? Tr.T("firmware neodpověděl")
                : $"firmware {info.Major}.{info.Minor}" + (info.UpdateMode ? Tr.T(" (režim aktualizace!)") : "") +
                  (info.SampleFreq > 0 ? Tr.F(", vzorkování {0:0.#} MHz", info.SampleFreq / 1_000_000.0) : "");
            yield return new DetectedDrive
            {
                Kind = DriveKind.Greaseweazle,
                Port = port,
                Name = "Greaseweazle " + (info?.Model ?? ""),
                Details = details + (toolPath != null ? ", gw: " + toolPath : Tr.T(", gw.exe nenalezen")),
                GwToolPath = toolPath,
                Media = InsertedMedia.Unknown,
            };
        }
    }

    public static Info QueryInfo(string port)
    {
        using var sp = new SerialPort(port, 9600) { ReadTimeout = 1500, WriteTimeout = 1500 };
        sp.Open();
        // Reset komunikace jako v oficiálních nástrojích: přepnout rychlost na 10000 a zpět.
        sp.DiscardOutBuffer();
        sp.BaudRate = 10000;
        sp.BaudRate = 9600;
        sp.DiscardInBuffer();

        sp.Write(new byte[] { 0 /*GetInfo*/, 3, 0 /*Firmware*/ }, 0, 3);
        var ack = ReadExactly(sp, 2);
        if (ack[0] != 0) throw new InvalidOperationException(Tr.T("neplatná odpověď"));
        if (ack[1] != 0) throw new InvalidOperationException(Tr.F("chyba {0}", ack[1]));
        var d = ReadExactly(sp, 32);

        int major = d[0], minor = d[1];
        bool main = d[2] != 0;
        uint freq = BitConverter.ToUInt32(d, 4);
        int hw = d[8] == 0 ? 1 : d[8], sub = d[9];
        string model = Models.TryGetValue(hw, out var subs) && sub < subs.Length ? subs[sub] : $"(model {hw}.{sub})";
        return new Info(major, minor, !main, main ? freq : 0, model);
    }

    private static byte[] ReadExactly(SerialPort sp, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n) got += sp.Read(buf, got, n - got);
        return buf;
    }
}
