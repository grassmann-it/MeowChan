using System.Buffers.Binary;
using System.Text;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Direct vectors for the bounded DEFLATE member walk used by warm PNG hits.
/// These tests deliberately exercise table conformance and the member/window boundaries without a player or decoder.
/// </summary>
public sealed class DeflateBoundaryConformanceTests
{
    [Fact]
    public void LiteralOnlyDynamicMemberMayHaveAnEmptyDistanceTable()
    {
        byte[] raw = [0, 0];
        byte[] zlib = DynamicLiteralOnlyZlib(raw, final: true, singletonDistance: false);

        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(1, 1, zlib)));
    }

    [Fact]
    public void DynamicSingletonDistanceTableSupportsABackReference()
    {
        byte[] raw = [0, 65, 65, 65, 65];
        byte[] zlib = DynamicBackReferenceZlib(raw, singletonDistance: true, includeDistance: true);

        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(4, 1, zlib)));
    }

    [Fact]
    public void DynamicMemberCanBeFollowedByAFinalStoredBlock()
    {
        byte[] raw = [0, 0, 0, 65];
        DeflateBitWriter writer = new();
        WriteDynamicLiteralOnlyBlock(writer, final: false);
        WriteStoredBlock(writer, raw[2..], final: true);

        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(3, 1, Zlib(writer.ToArray(), raw))));
    }

    [Fact]
    public void DynamicHeaderRejectsHlitValues287And288()
    {
        foreach (int hlitBits in new[] { 30, 31 })
        {
            DeflateBitWriter writer = new();
            writer.Bits(1, 1); // BFINAL
            writer.Bits(2, 2); // BTYPE=10, dynamic Huffman
            writer.Bits(hlitBits, 5);
            writer.Bits(0, 5); // HDIST=1
            writer.Bits(0, 4); // HCLEN=4; the parser must reject HLIT first

            Assert.False(VideoPosterCache.ValidStored(SyntheticPng(1, 1, Zlib(writer.ToArray(), [0, 0]))));
        }
    }

    [Fact]
    public void DynamicHeaderRejectsAnIncompleteCodeLengthAlphabet()
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1);
        writer.Bits(2, 2);
        writer.Bits(0, 5); // HLIT=257
        writer.Bits(0, 5); // HDIST=1
        writer.Bits(0, 4); // HCLEN=4: symbols 16, 17, 18 and 0
        writer.Bits(1, 3); // only code-length symbol 16 has a one-bit code
        writer.Bits(0, 3);
        writer.Bits(0, 3);
        writer.Bits(0, 3);

        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(1, 1, Zlib(writer.ToArray(), [0, 0]))));
    }

    [Fact]
    public void DynamicHeaderRejectsAnIncompleteLiteralTableLongerThanOneBit()
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1);
        writer.Bits(2, 2);
        writer.Bits(0, 5); // HLIT=257
        writer.Bits(0, 5); // HDIST=1
        writer.Bits(15, 4); // HCLEN=19

        int[] codeLengthLengths = new int[19];
        codeLengthLengths[0] = 1;
        codeLengthLengths[2] = 1;
        WriteCodeLengthAlphabet(writer, codeLengthLengths);

        // One length-two code at symbol 0, zeros through symbol 255, and EOB at 256.
        // The resulting two-code table occupies only half of its length-two space.
        for (int symbol = 0; symbol < 257; symbol++)
            WriteShortCodeLengthSymbol(writer, symbol is 0 or 256 ? 2 : 0);
        WriteShortCodeLengthSymbol(writer, 0); // the sole distance code is absent

        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(1, 1, Zlib(writer.ToArray(), [0, 0]))));
    }

    [Fact]
    public void EmptyDistanceTableRejectsAnActualBackReference()
    {
        byte[] raw = [0, 65, 65, 65, 65];
        byte[] zlib = DynamicBackReferenceZlib(raw, singletonDistance: false, includeDistance: false);

        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(4, 1, zlib)));
    }

    [Fact]
    public void DistanceCannotExceedTheZlibCinfoWindow()
    {
        byte[] raw = new byte[260];
        raw[0] = 0;
        for (int index = 1; index < 257; index++) raw[index] = 65;
        // A distance of 257 points at raw[0] and therefore copies 0, 65, 65.
        raw[257] = 0;
        raw[258] = 65;
        raw[259] = 65;

        byte[] deflate = FixedDistance257Deflate();
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(259, 1, Zlib(deflate, raw, cinfo: 7))));
        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(259, 1, Zlib(deflate, raw, cinfo: 0))));
    }

    private static byte[] DynamicLiteralOnlyZlib(byte[] raw, bool final, bool singletonDistance)
    {
        DeflateBitWriter writer = new();
        WriteDynamicLiteralOnlyBlock(writer, final, singletonDistance);
        return Zlib(writer.ToArray(), raw);
    }

    private static void WriteDynamicLiteralOnlyBlock(DeflateBitWriter writer, bool final, bool singletonDistance = false)
    {
        writer.Bits(final ? 1 : 0, 1);
        writer.Bits(2, 2); // BTYPE=10, dynamic Huffman
        writer.Bits(0, 5); // HLIT=257
        writer.Bits(0, 5); // HDIST=1
        writer.Bits(15, 4); // HCLEN=19

        int[] codeLengthLengths = new int[19];
        codeLengthLengths[0] = 2;
        codeLengthLengths[1] = 2;
        codeLengthLengths[2] = 2;
        codeLengthLengths[18] = 2;
        WriteCodeLengthAlphabet(writer, codeLengthLengths);

        WriteCodeLengthSymbol(writer, 1); // literal symbol 0 has length one
        WriteCodeLengthSymbol(writer, 18);
        writer.Bits(127, 7); // 138 zero lengths, symbols 1..138
        WriteCodeLengthSymbol(writer, 18);
        writer.Bits(106, 7); // 117 zero lengths, symbols 139..255
        WriteCodeLengthSymbol(writer, 1); // EOB symbol 256 has length one
        WriteCodeLengthSymbol(writer, singletonDistance ? 1 : 0); // distance symbol 0

        WriteCanonicalCode(writer, 0, 1);
        WriteCanonicalCode(writer, 0, 1);
        WriteCanonicalCode(writer, 1, 1); // EOB
    }

    private static byte[] DynamicBackReferenceZlib(byte[] raw, bool singletonDistance, bool includeDistance)
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1); // BFINAL
        writer.Bits(2, 2); // BTYPE=10, dynamic Huffman
        writer.Bits(1, 5); // HLIT=258, including length symbol 257
        writer.Bits(0, 5); // HDIST=1
        writer.Bits(15, 4); // HCLEN=19

        int[] codeLengthLengths = new int[19];
        codeLengthLengths[0] = 2;
        codeLengthLengths[1] = 2;
        codeLengthLengths[2] = 2;
        codeLengthLengths[18] = 2;
        WriteCodeLengthAlphabet(writer, codeLengthLengths);

        WriteCodeLengthSymbol(writer, 2); // literal symbol 0 has length two
        WriteCodeLengthSymbol(writer, 18);
        writer.Bits(53, 7); // 64 zero lengths, symbols 1..64
        WriteCodeLengthSymbol(writer, 2); // literal 'A' has length two
        WriteCodeLengthSymbol(writer, 18);
        writer.Bits(127, 7); // 138 zero lengths, symbols 66..203
        WriteCodeLengthSymbol(writer, 18);
        writer.Bits(41, 7); // 52 zero lengths, symbols 204..255
        WriteCodeLengthSymbol(writer, 2); // EOB symbol 256 has length two
        WriteCodeLengthSymbol(writer, 2); // length symbol 257 has length two
        WriteCodeLengthSymbol(writer, singletonDistance ? 1 : 0); // distance symbol 0

        // Canonical two-bit codes, in symbol order: 0 -> 00, 65 -> 01, 256 -> 10, 257 -> 11.
        WriteCanonicalCode(writer, 0, 2);
        WriteCanonicalCode(writer, 1, 2);
        WriteCanonicalCode(writer, 3, 2); // length 3 (symbol 257)
        writer.Bits(0, 0); // length symbol 257 has no extra bits
        if (includeDistance) WriteCanonicalCode(writer, 0, 1);
        WriteCanonicalCode(writer, 2, 2); // EOB
        return Zlib(writer.ToArray(), raw);
    }

    private static byte[] FixedDistance257Deflate()
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1); // BFINAL
        writer.Bits(1, 2); // BTYPE=01, fixed Huffman
        WriteFixedSymbol(writer, 0);
        for (int index = 0; index < 256; index++) WriteFixedSymbol(writer, 65);
        WriteFixedSymbol(writer, 257); // length three
        WriteFixedDistanceSymbol(writer, 16); // distance base 257
        writer.Bits(0, 7); // distance extra
        WriteFixedSymbol(writer, 256); // EOB
        return writer.ToArray();
    }

    private static void WriteStoredBlock(DeflateBitWriter writer, ReadOnlySpan<byte> raw, bool final)
    {
        writer.Bits(final ? 1 : 0, 1);
        writer.Bits(0, 2); // BTYPE=00, stored
        writer.Align();
        writer.Bits(raw.Length, 16);
        writer.Bits(raw.Length ^ 0xFFFF, 16);
        foreach (byte value in raw) writer.Bits(value, 8);
    }

    private static void WriteFixedDistanceSymbol(DeflateBitWriter writer, int symbol)
    {
        if (symbol is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(symbol));
        WriteCanonicalCode(writer, symbol, 5);
    }

    private static void WriteFixedSymbol(DeflateBitWriter writer, int symbol)
    {
        (int code, int length) = symbol switch
        {
            <= 143 => (0x30 + symbol, 8),
            <= 255 => (0x190 + symbol - 144, 9),
            <= 279 => (symbol - 256, 7),
            <= 287 => (0xC0 + symbol - 280, 8),
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };
        WriteCanonicalCode(writer, code, length);
    }

    private static void WriteCodeLengthAlphabet(DeflateBitWriter writer, int[] lengths)
    {
        int[] order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
        foreach (int symbol in order) writer.Bits(lengths[symbol], 3);
    }

    private static void WriteCodeLengthSymbol(DeflateBitWriter writer, int symbol)
    {
        int code = symbol switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            18 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };
        WriteCanonicalCode(writer, code, 2);
    }

    private static void WriteShortCodeLengthSymbol(DeflateBitWriter writer, int symbol)
    {
        if (symbol is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(symbol));
        writer.Bits(symbol == 0 ? 0 : 1, 1);
    }

    private static void WriteCanonicalCode(DeflateBitWriter writer, int code, int length) =>
        writer.Bits(ReverseBits(code, length), length);

    private static int ReverseBits(int value, int length)
    {
        int reversed = 0;
        for (int index = 0; index < length; index++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }
        return reversed;
    }

    private static byte[] Zlib(byte[] deflate, byte[] raw, int cinfo = 7)
    {
        byte cmf = (byte)((cinfo << 4) | 8);
        int flagBase = 0x80; // FLEVEL=2, FDICT=0
        int fcheck = (31 - (((cmf << 8) | flagBase) % 31)) % 31;
        byte[] result = new byte[2 + deflate.Length + 4];
        result[0] = cmf;
        result[1] = (byte)(flagBase | fcheck);
        Buffer.BlockCopy(deflate, 0, result, 2, deflate.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), Adler32(raw));
        return result;
    }

    private static uint Adler32(byte[] bytes)
    {
        const uint Modulus = 65_521;
        uint first = 1, second = 0;
        foreach (byte value in bytes)
        {
            first += value;
            if (first >= Modulus) first -= Modulus;
            second += first;
            if (second >= Modulus) second %= Modulus;
        }
        return (second << 16) | first;
    }

    private sealed class DeflateBitWriter
    {
        private readonly List<byte> _bytes = [0];
        private int _bit;

        internal void Bits(int value, int count)
        {
            for (int index = 0; index < count; index++)
            {
                if (((value >> index) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
                if (++_bit == 8)
                {
                    _bit = 0;
                    _bytes.Add(0);
                }
            }
        }

        internal void Align()
        {
            if (_bit == 0) return;
            _bit = 0;
            _bytes.Add(0);
        }

        internal byte[] ToArray()
        {
            if (_bit == 0) _bytes.RemoveAt(_bytes.Count - 1);
            return _bytes.ToArray();
        }
    }

    private static byte[] SyntheticPng(int width, int height, byte[] zlib)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 0; // 8-bit grayscale: each row is one filter byte plus one byte per pixel.
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", ihdr), .. Chunk("IDAT", zlib), .. Chunk("IEND", [])];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4),
            PreviewPngIntegrity.Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }
}
