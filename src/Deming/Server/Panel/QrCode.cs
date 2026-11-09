using System.Globalization;
using System.Text;

namespace Deming.Server.Panel;

/// <summary>
/// A small QR Code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1-40.
/// Written by hand so the 2FA setup page needs neither a library nor a script; the page embeds
/// the result as inline SVG.
/// </summary>
internal static class QrCode
{
    // Error correction codewords per block and number of blocks, level M, indexed by version (index 0 unused).
    private static readonly int[] EccCodewordsPerBlock =
    [
        -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26,
        26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
    ];

    private static readonly int[] BlockCount =
    [
        -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49,
    ];

    /// <summary>Format bits value of error correction level M.</summary>
    private const int LevelMBits = 0;

    /// <summary>The module matrix of <paramref name="text"/>; true is a dark module. Indexed [row, column].</summary>
    public static bool[,] Encode(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);

        var version = 1;
        while (true)
        {
            if (version > 40)
                throw new ArgumentException("The text is too long for a QR code.", nameof(text));
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + payload.Length * 8 <= DataCodewords(version) * 8
                && payload.Length < 1 << countBits)
                break;
            version++;
        }

        var bits = new List<bool>();
        Append(bits, 0b0100, 4);
        Append(bits, payload.Length, version <= 9 ? 8 : 16);
        foreach (var b in payload)
            Append(bits, b, 8);

        var capacityBits = DataCodewords(version) * 8;
        Append(bits, 0, Math.Min(4, capacityBits - bits.Count));
        Append(bits, 0, (8 - bits.Count % 8) % 8);

        var data = new byte[capacityBits / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i])
                data[i >> 3] |= (byte)(0x80 >> (i & 7));
        for (int i = bits.Count / 8, pad = 0; i < data.Length; i++, pad++)
            data[i] = pad % 2 == 0 ? (byte)0xEC : (byte)0x11;

        return new Builder(version, Interleave(data, version)).Build();
    }

    /// <summary>The matrix as a standalone SVG: dark modules on a white field with the four-module quiet zone.</summary>
    public static string ToSvg(bool[,] matrix, string label)
    {
        const int quiet = 4;
        var size = matrix.GetLength(0);
        var total = size + 2 * quiet;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (matrix[y, x])
                    path.Append(CultureInfo.InvariantCulture, $"M{x + quiet},{y + quiet}h1v1h-1z");

        // Always dark on white, whatever the page theme: scanners need that contrast.
        return string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {total} {total}\" role=\"img\" aria-label=\"{System.Net.WebUtility.HtmlEncode(label)}\" shape-rendering=\"crispEdges\">" +
            $"<rect width=\"{total}\" height=\"{total}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>");
    }

    // ------------------------------------------------------------------ capacity

    private static int RawModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7)
                result -= 36;
        }
        return result;
    }

    internal static int DataCodewords(int version) =>
        RawModules(version) / 8 - EccCodewordsPerBlock[version] * BlockCount[version];

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    // ------------------------------------------------------------------ Reed-Solomon over GF(256), polynomial 0x11D

    private static int Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    /// <summary>The error correction codewords of one block of data codewords.</summary>
    internal static byte[] Remainder(byte[] data, int eccLength)
    {
        var divisor = Divisor(eccLength);
        var result = new byte[eccLength];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, eccLength - 1);
            result[eccLength - 1] = 0;
            for (var i = 0; i < eccLength; i++)
                result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    /// <summary>Splits the data into blocks, adds error correction to each and interleaves them.</summary>
    private static byte[] Interleave(byte[] data, int version)
    {
        var blocks = BlockCount[version];
        var eccLength = EccCodewordsPerBlock[version];
        var raw = RawModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;

        var parts = new byte[blocks][];
        var eccs = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            parts[i] = data[k..(k + length)];
            k += length;
            eccs[i] = Remainder(parts[i], eccLength);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < shortLength - eccLength + 1; i++)
            for (var j = 0; j < blocks; j++)
                if (i < parts[j].Length)
                    result.Add(parts[j][i]);
        for (var i = 0; i < eccLength; i++)
            for (var j = 0; j < blocks; j++)
                result.Add(eccs[j][i]);
        return [.. result];
    }

    // ------------------------------------------------------------------ the matrix

    private sealed class Builder
    {
        private readonly int _version;
        private readonly int _size;
        private readonly byte[] _codewords;
        private readonly bool[,] _modules;
        private readonly bool[,] _function;

        public Builder(int version, byte[] codewords)
        {
            _version = version;
            _size = version * 4 + 17;
            _codewords = codewords;
            _modules = new bool[_size, _size];
            _function = new bool[_size, _size];
        }

        public bool[,] Build()
        {
            DrawFunctionPatterns();
            DrawCodewords();

            var best = 0;
            var bestPenalty = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormat(mask);
                var penalty = Penalty();
                if (penalty < bestPenalty)
                {
                    best = mask;
                    bestPenalty = penalty;
                }
                ApplyMask(mask); // XOR again: undo
            }
            ApplyMask(best);
            DrawFormat(best);
            return _modules;
        }

        private void Set(int x, int y, bool dark)
        {
            _modules[y, x] = dark;
            _function[y, x] = true;
        }

        private void DrawFunctionPatterns()
        {
            for (var i = 0; i < _size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(_size - 4, 3);
            DrawFinder(3, _size - 4);

            var positions = AlignmentPositions();
            var last = positions.Count - 1;
            for (var i = 0; i < positions.Count; i++)
                for (var j = 0; j < positions.Count; j++)
                {
                    // The three corners that hold finder patterns carry no alignment pattern.
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0))
                        continue;
                    DrawAlignment(positions[i], positions[j]);
                }

            DrawFormat(0); // reserves the format area; rewritten once the mask is chosen
            DrawVersion();
        }

        private List<int> AlignmentPositions()
        {
            if (_version == 1)
                return [];
            var count = _version / 7 + 2;
            var step = _version == 32 ? 26 : (_version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
            var result = new List<int> { 6 };
            for (var position = _size - 7; result.Count < count; position -= step)
                result.Insert(1, position);
            return result;
        }

        private void DrawFinder(int cx, int cy)
        {
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < _size && y >= 0 && y < _size)
                        Set(x, y, distance != 2 && distance != 4);
                }
        }

        private void DrawAlignment(int cx, int cy)
        {
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                    Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private void DrawFormat(int mask)
        {
            var data = (LevelMBits << 3) | mask;
            var rem = data;
            for (var i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            var bits = ((data << 10) | rem) ^ 0x5412;

            for (var i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
            Set(8, 7, Bit(bits, 6));
            Set(8, 8, Bit(bits, 7));
            Set(7, 8, Bit(bits, 8));
            for (var i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));

            for (var i = 0; i < 8; i++) Set(_size - 1 - i, 8, Bit(bits, i));
            for (var i = 8; i < 15; i++) Set(8, _size - 15 + i, Bit(bits, i));
            Set(8, _size - 8, true); // the always-dark module
        }

        private void DrawVersion()
        {
            if (_version < 7)
                return;
            var rem = _version;
            for (var i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var bits = (_version << 12) | rem;
            for (var i = 0; i < 18; i++)
            {
                var a = _size - 11 + i % 3;
                var b = i / 3;
                Set(a, b, Bit(bits, i));
                Set(b, a, Bit(bits, i));
            }
        }

        private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

        /// <summary>Zigzag placement of the codeword bits in the free modules.</summary>
        private void DrawCodewords()
        {
            var i = 0;
            for (var right = _size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                    right = 5;
                for (var vertical = 0; vertical < _size; vertical++)
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? _size - 1 - vertical : vertical;
                        if (!_function[y, x] && i < _codewords.Length * 8)
                        {
                            _modules[y, x] = Bit(_codewords[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        private void ApplyMask(int mask)
        {
            for (var y = 0; y < _size; y++)
                for (var x = 0; x < _size; x++)
                {
                    var invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert && !_function[y, x])
                        _modules[y, x] = !_modules[y, x];
                }
        }

        /// <summary>The four penalty rules of the standard; the lowest total picks the mask.</summary>
        private int Penalty()
        {
            var penalty = 0;
            var dark = 0;

            for (var a = 0; a < _size; a++)
            {
                penalty += RunPenalty(a, horizontal: true) + RunPenalty(a, horizontal: false);
                penalty += FinderLikePenalty(a, horizontal: true) + FinderLikePenalty(a, horizontal: false);
                for (var b = 0; b < _size; b++)
                {
                    if (_modules[a, b])
                        dark++;
                    if (a + 1 < _size && b + 1 < _size
                        && _modules[a, b] == _modules[a, b + 1]
                        && _modules[a, b] == _modules[a + 1, b]
                        && _modules[a, b] == _modules[a + 1, b + 1])
                        penalty += 3;
                }
            }

            var total = _size * _size;
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            return penalty + k * 10;
        }

        private bool At(int line, int index, bool horizontal) => horizontal ? _modules[line, index] : _modules[index, line];

        private int RunPenalty(int line, bool horizontal)
        {
            var penalty = 0;
            var run = 1;
            for (var i = 1; i <= _size; i++)
            {
                if (i < _size && At(line, i, horizontal) == At(line, i - 1, horizontal))
                {
                    run++;
                    continue;
                }
                if (run >= 5)
                    penalty += 3 + (run - 5);
                run = 1;
            }
            return penalty;
        }

        /// <summary>Dark-light-dark-dark-dark-light-dark with four light modules on one side looks like a finder pattern.</summary>
        private int FinderLikePenalty(int line, bool horizontal)
        {
            ReadOnlySpan<bool> core = [true, false, true, true, true, false, true];
            var penalty = 0;
            for (var start = -4; start + 7 <= _size + 4; start++)
            {
                if (!Matches(line, horizontal, start, core))
                    continue;
                if (Light(line, horizontal, start - 4, start) || Light(line, horizontal, start + 7, start + 11))
                    penalty += 40;
            }
            return penalty;
        }

        private bool Matches(int line, bool horizontal, int start, ReadOnlySpan<bool> pattern)
        {
            for (var i = 0; i < pattern.Length; i++)
            {
                var index = start + i;
                var value = index >= 0 && index < _size && At(line, index, horizontal);
                if (value != pattern[i] || index < 0 || index >= _size)
                    return false;
            }
            return true;
        }

        /// <summary>True when every module in [from, to) is light; modules outside the symbol count as light.</summary>
        private bool Light(int line, bool horizontal, int from, int to)
        {
            for (var i = from; i < to; i++)
                if (i >= 0 && i < _size && At(line, i, horizontal))
                    return false;
            return true;
        }
    }
}
