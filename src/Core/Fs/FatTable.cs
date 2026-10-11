namespace AtariFloppy.Core.Fs;

/// <summary>Čtení a zápis 12bitových položek FAT přímo v bajtech disku.</summary>
public static class FatTable
{
    public const int Free = 0x000;
    public const int BadCluster = 0xFF7;
    public const int EndOfChain = 0xFFF;

    public static bool IsEnd(int v) => v >= 0xFF8;

    public static int Get(byte[] disk, int fatOffset, int cluster)
    {
        int o = fatOffset + cluster * 3 / 2;
        int v = disk[o] | (disk[o + 1] << 8);
        return (cluster & 1) != 0 ? v >> 4 : v & 0xFFF;
    }

    public static void Set(byte[] disk, int fatOffset, int cluster, int value)
    {
        int o = fatOffset + cluster * 3 / 2;
        value &= 0xFFF;
        if ((cluster & 1) == 0)
        {
            disk[o] = (byte)value;
            disk[o + 1] = (byte)((disk[o + 1] & 0xF0) | (value >> 8));
        }
        else
        {
            disk[o] = (byte)((disk[o] & 0x0F) | ((value & 0x0F) << 4));
            disk[o + 1] = (byte)(value >> 4);
        }
    }
}
