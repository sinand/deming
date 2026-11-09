using System.Text;
using Deming.Server.Panel;

namespace Deming.Tests;

/// <summary>
/// The hand-written QR encoder. The decoder below is written independently of the encoder (its own
/// function-pattern layout, block table and unmasking), so a round trip proves the matrix; the
/// Reed-Solomon vector is the well-known "HELLO WORLD" version 1-M example.
/// </summary>
public class QrCodeTests
{
    // Error correction codewords per block and block count, level M, versions 1-10.
    private static readonly int[] Ecc = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] Blocks = [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    private static readonly int[][] Alignment =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    // Format information of level M with masks 0 and 1 (ISO/IEC 18004, annex C).
    private static readonly Dictionary<int, int> FormatBits = new()
    {
        [0] = 0b101010000010010,
        [1] = 0b101000100100101,
        [2] = 0b101111001111100,
        [3] = 0b101101101001011,
        [4] = 0b100010111111001,
        [5] = 0b100000011001110,
        [6] = 0b100111110010111,
        [7] = 0b100101010100000,
    };

    [Fact]
    public void Reed_Solomon_matches_the_published_version_1_M_example()
    {
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];

        Assert.Equal(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 }, QrCode.Remainder(data, 10));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 28)]
    [InlineData(5, 86)]
    [InlineData(10, 216)]
    [InlineData(40, 2334)]
    public void Data_capacity_matches_the_published_table(int version, int dataCodewords) =>
        Assert.Equal(dataCodewords, QrCode.DataCodewords(version));

    [Theory]
    [InlineData("HELLO")]
    [InlineData("otpauth://totp/Deming:owner?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Deming&digits=6")]
    [InlineData("otpauth://totp/Deming:do%C4%9Fanl%C4%B1?secret=ABCDEFGHIJKLMNOPQRSTUVWXYZ234567&issuer=Deming&digits=6")]
    public void The_matrix_decodes_back_to_the_text(string text)
    {
        var matrix = QrCode.Encode(text);

        Assert.Equal(text, Decode(matrix));
    }

    [Fact]
    public void A_long_text_uses_a_larger_version_and_still_decodes()
    {
        var text = new string('x', 200);
        var matrix = QrCode.Encode(text);

        Assert.Equal(17 + 4 * 10, matrix.GetLength(0));
        Assert.Equal(text, Decode(matrix));
    }

    [Fact]
    public void The_finder_patterns_timing_and_dark_module_are_in_place()
    {
        var matrix = QrCode.Encode("HELLO");
        var size = matrix.GetLength(0);

        Assert.Equal(21, size);
        foreach (var (cx, cy) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 7; x++)
                {
                    var ring = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                    Assert.Equal(ring != 2, matrix[cy + y, cx + x]);
                }
        for (var i = 8; i < size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, matrix[6, i]);
            Assert.Equal(i % 2 == 0, matrix[i, 6]);
        }
        Assert.True(matrix[size - 8, 8]);
    }

    [Fact]
    public void The_same_text_always_gives_the_same_matrix()
    {
        var a = QrCode.Encode("otpauth://totp/Deming:owner?secret=ABCD");
        var b = QrCode.Encode("otpauth://totp/Deming:owner?secret=ABCD");

        Assert.Equal(a.Cast<bool>(), b.Cast<bool>());
    }

    [Fact]
    public void Text_beyond_the_largest_symbol_is_refused() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 2400)));

    [Fact]
    public void The_svg_is_self_contained_dark_on_white_with_a_quiet_zone()
    {
        var matrix = QrCode.Encode("HELLO");
        var svg = QrCode.ToSvg(matrix, "A \"label\" & more");

        Assert.StartsWith("<svg ", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 29 29\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#fff\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#000\"", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"A &quot;label&quot; &amp; more\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", svg.Replace("http://www.w3.org/2000/svg", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        // The top-left finder pattern starts at the quiet zone offset.
        Assert.Contains("M4,4h1v1h-1z", svg, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ independent decoder

    private static string Decode(bool[,] m)
    {
        var size = m.GetLength(0);
        var version = (size - 17) / 4;
        Assert.InRange(version, 1, 10);

        // Format information, first copy: must be one of the valid level M words.
        var raw = 0;
        for (var i = 0; i <= 5; i++) raw |= (m[i, 8] ? 1 : 0) << i;
        raw |= (m[7, 8] ? 1 : 0) << 6;
        raw |= (m[8, 8] ? 1 : 0) << 7;
        raw |= (m[8, 7] ? 1 : 0) << 8;
        for (var i = 9; i < 15; i++) raw |= (m[8, 14 - i] ? 1 : 0) << i;
        var mask = FormatBits.Single(p => p.Value == raw).Key;

        // Second copy must agree.
        var second = 0;
        for (var i = 0; i < 8; i++) second |= (m[8, size - 1 - i] ? 1 : 0) << i;
        for (var i = 8; i < 15; i++) second |= (m[size - 15 + i, 8] ? 1 : 0) << i;
        Assert.Equal(raw, second);

        var function = FunctionModules(version, size);
        var bits = new List<bool>();
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var y = ((right + 1) & 2) == 0 ? size - 1 - vertical : vertical;
                    if (function[y, x]) continue;
                    var flip = mask switch
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
                    bits.Add(m[y, x] ^ flip);
                }
        }

        var codewords = new byte[bits.Count / 8];
        for (var i = 0; i < codewords.Length * 8; i++)
            if (bits[i])
                codewords[i / 8] |= (byte)(0x80 >> (i % 8));

        // De-interleave and check every block's error correction.
        var blocks = Blocks[version];
        var shortBlocks = blocks - codewords.Length % blocks;
        var shortLength = codewords.Length / blocks;
        var dataBlocks = new List<byte>[blocks];
        var eccBlocks = new List<byte>[blocks];
        for (var b = 0; b < blocks; b++) { dataBlocks[b] = []; eccBlocks[b] = []; }

        var k = 0;
        for (var i = 0; i < shortLength - Ecc[version] + 1; i++)
            for (var b = 0; b < blocks; b++)
            {
                var dataLength = shortLength - Ecc[version] + (b < shortBlocks ? 0 : 1);
                if (i < dataLength)
                    dataBlocks[b].Add(codewords[k++]);
            }
        for (var i = 0; i < Ecc[version]; i++)
            for (var b = 0; b < blocks; b++)
                eccBlocks[b].Add(codewords[k++]);
        Assert.Equal(codewords.Length, k);

        var data = new List<byte>();
        for (var b = 0; b < blocks; b++)
        {
            Assert.Equal(eccBlocks[b], QrCode.Remainder([.. dataBlocks[b]], Ecc[version]));
            data.AddRange(dataBlocks[b]);
        }

        // Byte mode: 0100, count (8 bits up to version 9, else 16), the bytes.
        var stream = new List<bool>();
        foreach (var d in data)
            for (var i = 7; i >= 0; i--)
                stream.Add(((d >> i) & 1) != 0);
        int Read(int from, int count) => stream.Skip(from).Take(count).Aggregate(0, (a, bit) => (a << 1) | (bit ? 1 : 0));

        Assert.Equal(0b0100, Read(0, 4));
        var countBits = version <= 9 ? 8 : 16;
        var length = Read(4, countBits);
        var text = new byte[length];
        for (var i = 0; i < length; i++)
            text[i] = (byte)Read(4 + countBits + i * 8, 8);
        return Encoding.UTF8.GetString(text);
    }

    private static bool[,] FunctionModules(int version, int size)
    {
        var f = new bool[size, size];
        void Fill(int x0, int y0, int w, int h)
        {
            for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                    if (x >= 0 && x < size && y >= 0 && y < size)
                        f[y, x] = true;
        }

        Fill(0, 0, 9, 9);                 // top-left finder, separator, format
        Fill(size - 8, 0, 8, 9);          // top-right
        Fill(0, size - 8, 9, 8);          // bottom-left
        Fill(6, 0, 1, size);              // timing
        Fill(0, 6, size, 1);
        if (version >= 7)
        {
            Fill(size - 11, 0, 3, 6);
            Fill(0, size - 11, 6, 3);
        }

        var positions = Alignment[version];
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
            {
                var last = positions.Length - 1;
                if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0))
                    continue;
                Fill(positions[i] - 2, positions[j] - 2, 5, 5);
            }
        return f;
    }
}
