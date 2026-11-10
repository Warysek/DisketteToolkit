using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.Formats;

/// <summary>
/// DMS (Disk Masher System) — zabalený obraz diskety Amigy; tak se šířila většina her a dem. Rozbalí se na ADF
/// (80 stop × 2 strany × 11 sektorů, HD 22). Formát podle xDMS (André Rodrigues de la Rocha, volné dílo):
/// hlavička „DMS!“ (56 B), za ní stopy „TR“ (20 B hlavička + data; jedna stopa = válec, obě strany), každá zabalená
/// jedním ze 7 způsobů. Stopa 0xFFFF je banner, stopa 80 FILE_ID.DIZ — text se vrátí zvlášť.
/// Každá stopa má CRC zabalených dat a součet rozbalených — poškozená stopa se pozná a v obrazu jsou místo ní nuly.
/// Archiv chráněný heslem rozbalit neumím.
/// </summary>
public static class DmsFormat
{
    private const int HeaderLength = 56, TrackHeaderLength = 20, Cylinders = 80;

    public static bool IsDms(byte[] head) => head.Length >= 4 && head[0] == 'D' && head[1] == 'M' && head[2] == 'S' && head[3] == '!';

    public sealed class Result
    {
        public DiskImage Image { get; init; } = null!;
        /// <summary>Banner a FILE_ID.DIZ z archivu (null = žádný text).</summary>
        public string? Text { get; init; }
        public List<string> Warnings { get; } = new();
    }

    /// <summary>Rozbalí archiv. Vadný nebo zašifrovaný archiv: InvalidDataException se srozumitelným popisem.</summary>
    public static Result Unpack(byte[] file)
    {
        if (file.Length < HeaderLength || !IsDms(file)) throw new InvalidDataException(Tr.T("Soubor není archiv DMS."));
        if (Crc16(file, 4, HeaderLength - 6) != U16(file, HeaderLength - 2))
            throw new InvalidDataException(Tr.T("Archiv DMS má poškozenou hlavičku."));
        int info = U16(file, 10);
        if ((info & 2) != 0)
            throw new InvalidDataException(Tr.T("Archiv DMS je zašifrovaný heslem — to rozbalit neumím. Rozbal ho programem DMS na Amize (nebo xDMS s heslem) a otevři vzniklý .adf."));

        var dec = new Decoder();
        var tracks = new Dictionary<int, byte[]>();
        var bad = new SortedSet<int>();
        var texts = new List<string>();
        int trackBytes = (info & 0x10) != 0 ? 22 * 1024 : 11 * 1024;
        bool broken = false;
        int pos = HeaderLength;
        while (pos + TrackHeaderLength <= file.Length)
        {
            if (file[pos] != 'T' || file[pos + 1] != 'R') break;                       // konec stop (za nimi může být cokoli)
            if (Crc16(file, pos, TrackHeaderLength - 2) != U16(file, pos + TrackHeaderLength - 2)) { broken = true; break; }
            int number = U16(file, pos + 2), packedLen = U16(file, pos + 6), midLen = U16(file, pos + 8), len = U16(file, pos + 10);
            int flags = file[pos + 12], mode = file[pos + 13], sum = U16(file, pos + 14), crc = U16(file, pos + 16);
            pos += TrackHeaderLength;
            if (pos + packedLen > file.Length) { broken = true; break; }
            var packed = new byte[packedLen];
            Buffer.BlockCopy(file, pos, packed, 0, packedLen);
            pos += packedLen;
            bool crcOk = Crc16(packed, 0, packedLen) == crc;

            if (number < Cylinders && len > 2048)
            {
                if (tracks.Count == 0 && bad.Count == 0 && len == 22 * 1024) trackBytes = len;
                // i vadnou stopu rozbalíme: další stopy můžou navazovat na její Huffmanovy tabulky a slovník
                byte[]? data = null;
                try { data = dec.UnpackTrack(packed, midLen, len, mode, flags); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException) { dec.Reset(); }
                if (data != null && crcOk && Checksum(data) == sum) { tracks[number] = data; bad.Remove(number); }
                else if (!tracks.ContainsKey(number)) bad.Add(number);
            }
            else if (number == 0xFFFF || number == Cylinders || (number == 0 && len == 1024))
            {
                // banner a FILE_ID.DIZ: xDMS je rozbaluje zvlášť, s čistým stavem — stav datových stop se jich netýká
                try
                {
                    var t = crcOk ? new Decoder().UnpackTrack(packed, midLen, len, mode, flags) : null;
                    if (t != null && Checksum(t) == sum && CleanText(t) is { } s) texts.Add(s);
                }
                catch (InvalidDataException) { }
            }
        }
        if (tracks.Count == 0)
            throw new InvalidDataException(bad.Count > 0 ? Tr.T("Archiv DMS je poškozený — žádnou stopu nejde rozbalit.") : Tr.T("V archivu DMS nejsou žádné stopy diskety."));

        var image = new byte[Cylinders * trackBytes];
        foreach (var t in tracks)
            Buffer.BlockCopy(t.Value, 0, image, t.Key * trackBytes, Math.Min(t.Value.Length, trackBytes));
        var r = new Result
        {
            Image = new DiskImage(new DiskGeometry(Cylinders, 2, trackBytes / 1024), image),
            Text = texts.Count > 0 ? string.Join("\n\n", texts) : null,
        };
        var missing = Enumerable.Range(0, Cylinders).Where(c => !tracks.ContainsKey(c) && !bad.Contains(c)).ToList();
        if (bad.Count > 0)
            r.Warnings.Add(Tr.F("Stopy {0} se z archivu DMS nepodařilo rozbalit (poškozená data) — v obrazu jsou místo nich nuly.", Ranges(bad)));
        if (broken)
            r.Warnings.Add(Tr.T("Archiv DMS je useknutý nebo poškozený (neúplně stažený?) — stopy za tím místem chybí."));
        if (missing.Count > 0)
            r.Warnings.Add(Tr.F("V archivu DMS nejsou stopy {0} — v obrazu jsou místo nich nuly.", Ranges(missing)));
        return r;
    }

    /// <summary>„3, 10–12, 79“.</summary>
    private static string Ranges(IEnumerable<int> values)
    {
        var parts = new List<string>();
        int? start = null, prev = null;
        foreach (int v in values.OrderBy(x => x))
        {
            if (prev != null && v == prev + 1) { prev = v; continue; }
            if (start != null) parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
            start = prev = v;
        }
        if (start != null) parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
        return string.Join(", ", parts);
    }

    /// <summary>Text z Amigy (Latin-1): bez barev a pohybů kurzoru (ANSI), bez řídicích znaků, řádky \n.</summary>
    private static string? CleanText(byte[] data)
    {
        string s = Encoding.GetEncoding("ISO-8859-1").GetString(data);
        s = Regex.Replace(s, "(\u001b\\[|\u009b)[0-9;? ]*[@-~]", "");
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = new string(s.Where(c => c == '\n' || c == '\t' || (c >= ' ' && c != '\u007f' && (c < '\u0080' || c >= ' '))).ToArray());
        var lines = s.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

    private static int Checksum(byte[] d)
    {
        int s = 0;
        foreach (byte x in d) s += x;
        return s & 0xFFFF;
    }

    private static readonly ushort[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        int c = i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
        return (ushort)c;
    }).ToArray();

    /// <summary>CRC-16 (ARC: polynom 0xA001 obráceně, začátek 0) — jako CreateCRC v xDMS.</summary>
    internal static int Crc16(byte[] d, int offset, int count)
    {
        int crc = 0;
        for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // ================================================================ rozbalování

    private sealed class Bits
    {
        private readonly byte[] _d;
        private int _pos, _count;
        private ulong _buf;
        public Bits(byte[] d) => _d = d;

        /// <summary>n bitů od nejvyššího (za koncem dat nuly — xDMS čte za konec vyrovnávací paměti).</summary>
        public int Read(int n)
        {
            if (n == 0) return 0;
            while (_count < n)
            {
                _buf = (_buf << 8) | (_pos < _d.Length ? _d[_pos] : 0u);
                _pos++;
                _count += 8;
            }
            _count -= n;
            return (int)((_buf >> _count) & ((1UL << n) - 1));
        }
    }

    /// <summary>Kanonický Huffmanův kód z délek (kratší kódy vlevo, stejně dlouhé podle pořadí znaku) — jako make_table v xDMS.</summary>
    private sealed class Huffman
    {
        private readonly int[] _count = new int[33];
        private int[] _symbols = Array.Empty<int>();
        private int _maxLen, _constant = -1;

        public void Build(byte[] lengths, int n)
        {
            _constant = -1;
            Array.Clear(_count, 0, _count.Length);
            _maxLen = 0;
            for (int i = 0; i < n; i++) { _count[lengths[i]]++; _maxLen = Math.Max(_maxLen, lengths[i]); }
            _count[0] = 0;
            _symbols = Enumerable.Range(0, n).Where(i => lengths[i] > 0).OrderBy(i => lengths[i]).ThenBy(i => i).ToArray();
            if (_symbols.Length == 0) throw new InvalidDataException("DMS: empty Huffman table");
        }

        /// <summary>Tabulka s jediným znakem — ten se čte bez bitů.</summary>
        public void Constant(int symbol) => _constant = symbol;

        public int Decode(Bits b)
        {
            if (_constant >= 0) return _constant;
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= _maxLen; len++)
            {
                code |= b.Read(1);
                int count = _count[len];
                int d = code - first;
                if (d >= 0 && d < count) return _symbols[index + d];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException("DMS: bad Huffman code");
        }
    }

    /// <summary>Stav rozbalování, který může přecházet ze stopy do stopy (příznak 1 = „zachovat stav“).</summary>
    private sealed class Decoder
    {
        private const int DeepThreshold = 2, DeepF = 60, DeepNChar = 256 - DeepThreshold + DeepF, DeepT = DeepNChar * 2 - 1, DeepR = DeepT - 1;
        private const int HeavyNc = 510, HeavyNpt = 20, HeavyOffset = 253;

        private static readonly byte[] DCode = Table(new[] { (1, 32), (3, 16), (8, 8), (12, 4), (24, 2), (16, 1) }, code: true);
        private static readonly byte[] DLen = Table(new[] { (1, 32), (3, 16), (8, 8), (12, 4), (24, 2), (16, 1) }, code: false);

        /// <summary>Tabulky d_code / d_len z LZHUF: skupiny (počet hodnot, kolikrát se každá opakuje).</summary>
        private static byte[] Table((int values, int repeat)[] groups, bool code)
        {
            var t = new List<byte>();
            int value = 0;
            for (int g = 0; g < groups.Length; g++)
                for (int v = 0; v < groups[g].values; v++, value++)
                    for (int r = 0; r < groups[g].repeat; r++) t.Add((byte)(code ? value : g + 3));
            return t.ToArray();
        }

        private readonly byte[] _text = new byte[0x4000];
        private int _quickPos, _mediumPos, _deepPos, _heavyPos, _heavyLast;
        private bool _deepInit;
        private readonly ushort[] _freq = new ushort[DeepT + 1], _prnt = new ushort[DeepT + DeepNChar], _son = new ushort[DeepT];
        private readonly byte[] _cLen = new byte[HeavyNc], _ptLen = new byte[HeavyNpt];
        private readonly Huffman _c = new(), _p = new();
        private int _np;

        public Decoder() => Reset();

        public void Reset()
        {
            _quickPos = 251;
            _mediumPos = 0x3FBE;
            _deepPos = 0x3FC4;
            _heavyPos = 0;
            _heavyLast = 0;
            _deepInit = true;
            Array.Clear(_text, 0, 0x3FC8);
        }

        public byte[] UnpackTrack(byte[] packed, int midLen, int len, int mode, int flags)
        {
            var output = new byte[len];
            switch (mode)
            {
                case 0:
                    if (packed.Length < len) throw new InvalidDataException("DMS: short track");
                    Buffer.BlockCopy(packed, 0, output, 0, len);
                    break;
                case 1: Rle(packed, output); break;
                case 2: { var s = new byte[midLen]; Quick(packed, s); Rle(s, output); break; }
                case 3: { var s = new byte[midLen]; Medium(packed, s); Rle(s, output); break; }
                case 4: { var s = new byte[midLen]; Deep(packed, s); Rle(s, output); break; }
                case 5:
                case 6:
                {
                    var s = new byte[Math.Max(midLen, len)];
                    Heavy(packed, s, midLen, mode == 6, (flags & 2) != 0);
                    if ((flags & 4) != 0) Rle(s, output);
                    else Buffer.BlockCopy(s, 0, output, 0, len);
                    break;
                }
                default: throw new InvalidDataException("DMS: unknown compression " + mode);
            }
            if ((flags & 1) == 0) Reset();
            return output;
        }

        private static void Rle(byte[] input, byte[] output)
        {
            int i = 0, o = 0;
            byte In() => i < input.Length ? input[i++] : throw new InvalidDataException("DMS: RLE overrun");
            while (o < output.Length)
            {
                byte a = In();
                if (a != 0x90) { output[o++] = a; continue; }
                byte b = In();
                if (b == 0) { output[o++] = a; continue; }
                a = In();
                int n = b;
                if (b == 0xFF) { int hi = In(); n = (hi << 8) | In(); }
                if (o + n > output.Length) throw new InvalidDataException("DMS: RLE overrun");
                for (int k = 0; k < n; k++) output[o++] = a;
            }
        }

        /// <summary>Zápis do okna i výstupu; shoda může přetéct konec stopy — okno se posouvá dál jako v xDMS.</summary>
        private void Put(byte[] output, ref int o, ref int pos, int mask, byte v)
        {
            _text[pos & mask] = v;
            pos++;
            if (o < output.Length) output[o] = v;
            o++;
        }

        private void Copy(byte[] output, ref int o, ref int pos, int mask, int from, int count)
        {
            for (int k = 0; k < count; k++) Put(output, ref o, ref pos, mask, _text[(from + k) & mask]);
        }

        private void Quick(byte[] input, byte[] output)
        {
            var b = new Bits(input);
            int o = 0;
            while (o < output.Length)
            {
                if (b.Read(1) != 0) Put(output, ref o, ref _quickPos, 0xFF, (byte)b.Read(8));
                else
                {
                    int len = b.Read(2) + 2;
                    int from = _quickPos - b.Read(8) - 1;
                    Copy(output, ref o, ref _quickPos, 0xFF, from, len);
                }
            }
            _quickPos = (_quickPos + 5) & 0xFF;
        }

        private void Medium(byte[] input, byte[] output)
        {
            var b = new Bits(input);
            int o = 0;
            while (o < output.Length)
            {
                if (b.Read(1) != 0) Put(output, ref o, ref _mediumPos, 0x3FFF, (byte)b.Read(8));
                else
                {
                    int c = b.Read(8);
                    int len = DCode[c] + 3;
                    int u = DLen[c];
                    c = ((c << u) | b.Read(u)) & 0xFF;
                    u = DLen[c];
                    c = (DCode[c] << 8) | (((c << u) | b.Read(u)) & 0xFF);
                    Copy(output, ref o, ref _mediumPos, 0x3FFF, _mediumPos - c - 1, len);
                }
            }
            _mediumPos = (_mediumPos + 66) & 0x3FFF;
        }

        // ------------------------------------------------------------ DEEP (adaptivní Huffman z LZHUF)

        private void Deep(byte[] input, byte[] output)
        {
            var b = new Bits(input);
            if (_deepInit) InitDeep();
            int o = 0;
            while (o < output.Length)
            {
                int c = _son[DeepR];
                while (c < DeepT) c = _son[c + b.Read(1)];
                c -= DeepT;
                DeepUpdate(c);
                if (c < 256) Put(output, ref o, ref _deepPos, 0x3FFF, (byte)c);
                else
                {
                    int len = c - 255 + DeepThreshold;
                    int i = b.Read(8);
                    int hi = DCode[i] << 8, j = DLen[i];
                    i = ((i << j) | b.Read(j)) & 0xFF;
                    Copy(output, ref o, ref _deepPos, 0x3FFF, _deepPos - (hi | i) - 1, len);
                }
            }
            _deepPos = (_deepPos + 60) & 0x3FFF;
        }

        private void InitDeep()
        {
            for (int i = 0; i < DeepNChar; i++)
            {
                _freq[i] = 1;
                _son[i] = (ushort)(i + DeepT);
                _prnt[i + DeepT] = (ushort)i;
            }
            for (int i = 0, j = DeepNChar; j <= DeepR; i += 2, j++)
            {
                _freq[j] = (ushort)(_freq[i] + _freq[i + 1]);
                _son[j] = (ushort)i;
                _prnt[i] = _prnt[i + 1] = (ushort)j;
            }
            _freq[DeepT] = 0xFFFF;
            _prnt[DeepR] = 0;
            _deepInit = false;
        }

        private void DeepReconst()
        {
            int j = 0;
            for (int i = 0; i < DeepT; i++)
                if (_son[i] >= DeepT)
                {
                    _freq[j] = (ushort)((_freq[i] + 1) / 2);
                    _son[j] = _son[i];
                    j++;
                }
            for (int i = 0, n = DeepNChar; n < DeepT; i += 2, n++)
            {
                ushort f = (ushort)(_freq[i] + _freq[i + 1]);
                _freq[n] = f;
                int k = n - 1;
                while (f < _freq[k]) k--;
                k++;
                Array.Copy(_freq, k, _freq, k + 1, n - k);
                _freq[k] = f;
                Array.Copy(_son, k, _son, k + 1, n - k);
                _son[k] = (ushort)i;
            }
            for (int i = 0; i < DeepT; i++)
            {
                int k = _son[i];
                if (k >= DeepT) _prnt[k] = (ushort)i;
                else _prnt[k] = _prnt[k + 1] = (ushort)i;
            }
        }

        private void DeepUpdate(int c)
        {
            if (_freq[DeepR] == 0x8000) DeepReconst();
            c = _prnt[c + DeepT];
            do
            {
                int k = ++_freq[c];
                int l = c + 1;
                if (k > _freq[l])
                {
                    while (k > _freq[++l]) { }
                    l--;
                    _freq[c] = _freq[l];
                    _freq[l] = (ushort)k;
                    int i = _son[c];
                    _prnt[i] = (ushort)l;
                    if (i < DeepT) _prnt[i + 1] = (ushort)l;
                    int j = _son[l];
                    _son[l] = (ushort)i;
                    _prnt[j] = (ushort)c;
                    if (j < DeepT) _prnt[j + 1] = (ushort)c;
                    _son[c] = (ushort)j;
                    c = l;
                }
                c = _prnt[c];
            } while (c != 0);
        }

        // ------------------------------------------------------------ HEAVY (LZH, slovník 4 KB / 8 KB)

        private void Heavy(byte[] input, byte[] output, int midLen, bool bigDict, bool rebuild)
        {
            int mask = bigDict ? 0x1FFF : 0x0FFF;
            _np = bigDict ? 15 : 14;
            var b = new Bits(input);
            if (rebuild)
            {
                ReadTree(b, 9, 5, _cLen, HeavyNc, HeavyNc, _c);
                ReadTree(b, 5, 4, _ptLen, HeavyNpt, _np, _p);
            }
            int o = 0;
            while (o < midLen)
            {
                int c = _c.Decode(b);
                if (c < 256) Put(output, ref o, ref _heavyPos, mask, (byte)c);
                else Copy(output, ref o, ref _heavyPos, mask, _heavyPos - HeavyDistance(b) - 1, c - HeavyOffset);
            }
        }

        private static void ReadTree(Bits b, int countBits, int lenBits, byte[] lengths, int max, int used, Huffman h)
        {
            int n = b.Read(countBits);
            if (n == 0) { h.Constant(b.Read(countBits)); return; }
            if (n > max) throw new InvalidDataException("DMS: bad Huffman table");
            for (int i = 0; i < n; i++) lengths[i] = (byte)b.Read(lenBits);
            for (int i = n; i < lengths.Length; i++) lengths[i] = 0;
            h.Build(lengths, used);
        }

        private int HeavyDistance(Bits b)
        {
            int j = _p.Decode(b);
            if (j != _np - 1)
            {
                if (j > 0) j = b.Read(j - 1) | (1 << (j - 1));
                _heavyLast = j;
            }
            return _heavyLast;
        }
    }
}
