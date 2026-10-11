namespace AtariFloppy.Core.Fs.Builders;

/// <summary>Zápis čísel v pořadí bajtů big-endian (Mac, Atari/68000) i little-endian (PC).</summary>
internal static class Bytes
{
    public static void Be16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    public static void Be32(byte[] b, int o, long v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    public static void Le16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    public static void Le32(byte[] b, int o, long v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

    public static void W16(byte[] b, int o, int v, bool bigEndian) { if (bigEndian) Be16(b, o, v); else Le16(b, o, v); }
    public static void W32(byte[] b, int o, long v, bool bigEndian) { if (bigEndian) Be32(b, o, v); else Le32(b, o, v); }
}
