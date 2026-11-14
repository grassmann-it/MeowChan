using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>
/// Validation for the derived preview PNGs of <see cref="VideoPosterCache"/> and <see cref="VideoScrubPreviewCache"/>.
/// <list type="bullet">
/// <item><see cref="IsCompleteDecodable"/> is used for fresh decoder output before it is published: the full structure check plus one
/// complete pixel decode that must report success (a truncated image is not accepted as "incomplete but usable").</item>
/// <item><see cref="IsIntactStored"/> is used for warm cache hits: the full structure check, a bounded check of the PNG zlib stream,
/// and the Skia codec header, but no pixel decode. A truncated, bit-flipped, malformed or appended-to file therefore never passes;
/// the one remaining full decode is the display decode of the caller.</item>
/// </list>
/// Neither check fetches anything or touches originals. The stored-image check consumes at most the exact scanline length derived from
/// IHDR (and a fixed technical ceiling) before accepting the zlib stream; it never inflates into an unbounded buffer.
/// </summary>
internal static class PreviewPngIntegrity
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Trns = 0x74524E53, Idat = 0x49444154, Iend = 0x49454E44;
    // This is a parser safety ceiling, not an image-size setting. The actual callers have much smaller IHDR bounds.
    private const int MaximumInflatedBytes = 64 * 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Warm-hit check: complete structure, bounded zlib scanlines and codec header; no pixel decode.</summary>
    internal static bool IsIntactStored(byte[] png, int maximumBytes, int maximumWidth, int maximumHeight)
    {
        if (png is null || !TryParse(png, maximumBytes, maximumWidth, maximumHeight, out ParsedPng? parsed) || parsed is null)
            return false;
        return HasCompleteScanlines(png, parsed) && CodecHeaderMatches(png, parsed.Width, parsed.Height);
    }

    /// <summary>Publish check: complete structure, CRC-32, bounds, and one successful full pixel decode.</summary>
    internal static bool IsCompleteDecodable(byte[] png, int maximumBytes, int maximumWidth, int maximumHeight, Action? fullDecodeObserver = null)
    {
        if (png is null || !TryParse(png, maximumBytes, maximumWidth, maximumHeight, out ParsedPng? parsed) || parsed is null)
            return false;
        using MemoryStream encoded = new(png, writable: false);
        using SKCodec? codec = SKCodec.Create(encoded);
        if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png
            || codec.Info.Width != parsed.Width || codec.Info.Height != parsed.Height) return false;
        fullDecodeObserver?.Invoke();
        SKImageInfo info = new(parsed.Width, parsed.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new(info);
        // SKCodec.GetPixels reports IncompleteInput for a truncated image. A published preview must decode completely.
        return codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success;
    }

    /// <summary>The structural part of both checks (signature, chunk walk with CRC-32, IHDR bounds, IDAT, IEND at the exact end).</summary>
    internal static bool TryReadStructure(ReadOnlySpan<byte> png, int maximumBytes, int maximumWidth, int maximumHeight,
        out int width, out int height)
    {
        if (!TryParse(png, maximumBytes, maximumWidth, maximumHeight, out ParsedPng? parsed) || parsed is null)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = parsed.Width;
        height = parsed.Height;
        return true;
    }

    private sealed class ParsedPng
    {
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal byte ColorType { get; set; }
        internal byte BitDepth { get; set; }
        internal byte InterlaceMethod { get; set; }
        internal List<(int Offset, int Length)> IdatSegments { get; } = new();
        internal int InflatedLength { get; set; }
    }

    private static bool TryParse(ReadOnlySpan<byte> png, int maximumBytes, int maximumWidth, int maximumHeight,
        out ParsedPng? parsed)
    {
        parsed = null;
        // The positive checks are deliberate: a zero/negative caller limit must never turn into an unbounded parser invocation.
        if (maximumBytes <= 0 || maximumWidth <= 0 || maximumHeight <= 0
            || png.Length < Signature.Length + 12 || png.Length > maximumBytes
            || !png[..Signature.Length].SequenceEqual(Signature)) return false;

        int offset = Signature.Length;
        bool first = true, sawPalette = false, inIdatRun = false, idatRunEnded = false, sawIdat = false, sawTransparency = false;
        byte colorType = 0, bitDepth = 0, interlaceMethod = 0;
        int paletteEntries = 0;
        ParsedPng candidate = new();

        while (true)
        {
            if (png.Length - offset < 12) return false;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > int.MaxValue || length > (uint)(png.Length - offset - 12)) return false;

            ReadOnlySpan<byte> typeBytes = png.Slice(offset + 4, 4);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
            if (!ValidChunkType(typeBytes) || typeBytes[2] is < (byte)'A' or > (byte)'Z') return false;
            ReadOnlySpan<byte> data = png.Slice(offset + 8, (int)length);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + (int)length)..]);
            if (Crc32(png.Slice(offset + 4, 4 + (int)length)) != stored) return false;
            offset += 12 + (int)length;

            if (first)
            {
                if (type != Ihdr || length != 13) return false;
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data);
                uint h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (w < 1 || h < 1 || w > (uint)maximumWidth || h > (uint)maximumHeight) return false;
                bitDepth = data[8];
                colorType = data[9];
                interlaceMethod = data[12];
                // Compression and filter methods are fixed by the PNG specification. Both interlace methods are handled below.
                if (!ValidDepth(colorType, bitDepth) || data[10] != 0 || data[11] != 0 || interlaceMethod > 1) return false;
                candidate.Width = (int)w;
                candidate.Height = (int)h;
                candidate.ColorType = colorType;
                candidate.BitDepth = bitDepth;
                candidate.InterlaceMethod = interlaceMethod;
                first = false;
                continue;
            }

            if (type == Ihdr) return false;
            // A derived PNG may carry ancillary metadata, but an unknown critical chunk cannot be accepted by this bounded validator.
            if ((typeBytes[0] & 0x20) == 0 && type is not (Plte or Trns or Idat or Iend)) return false;

            if (type == Idat)
            {
                if (idatRunEnded || (colorType == 3 && !sawPalette)) return false;
                inIdatRun = true;
                sawIdat = true;
                // offset now points after the chunk; the IDAT payload starts eight bytes before that position minus its length.
                candidate.IdatSegments.Add((offset - (int)length - 4, (int)length));
                continue;
            }

            if (inIdatRun)
            {
                inIdatRun = false;
                idatRunEnded = true;
            }

            if (type == Plte)
            {
                // PLTE is forbidden for grayscale and gray-alpha images, and must precede IDAT.
                if (colorType is 0 or 4 || sawIdat || sawPalette || length is < 3 or > 768 || length % 3 != 0) return false;
                paletteEntries = (int)length / 3;
                if (colorType == 3 && paletteEntries > (1 << bitDepth)) return false;
                sawPalette = true;
                continue;
            }

            if (type == Trns)
            {
                if (sawIdat || sawTransparency) return false;
                bool valid = colorType switch
                {
                    0 => length == 2,
                    2 => length == 6,
                    3 => sawPalette && length <= (uint)paletteEntries,
                    _ => false,
                };
                if (!valid) return false;
                sawTransparency = true;
                continue;
            }

            if (type == Iend)
            {
                if (length != 0 || offset != png.Length || !sawIdat || (colorType == 3 && !sawPalette)) return false;
                if (!BuildScanlineLayout(candidate)) return false;
                parsed = candidate;
                return true;
            }
        }
    }

    private static bool BuildScanlineLayout(ParsedPng png)
    {
        try
        {
            int channels = png.ColorType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                6 => 4,
                _ => 0,
            };
            if (channels == 0) return false;

            long total = 0;
            if (png.InterlaceMethod == 0)
            {
                if (!AddRows(png.Width, png.Height, channels, png.BitDepth, ref total)) return false;
            }
            else
            {
                // Adam7 pass origins and steps from the PNG specification.
                ReadOnlySpan<int> startX = stackalloc int[] { 0, 4, 0, 2, 0, 1, 0 };
                ReadOnlySpan<int> startY = stackalloc int[] { 0, 0, 4, 0, 2, 0, 1 };
                ReadOnlySpan<int> stepX = stackalloc int[] { 8, 8, 4, 4, 2, 2, 1 };
                ReadOnlySpan<int> stepY = stackalloc int[] { 8, 8, 8, 4, 4, 2, 2 };
                for (int pass = 0; pass < 7; pass++)
                {
                    int passWidth = CountPassSamples(png.Width, startX[pass], stepX[pass]);
                    int passHeight = CountPassSamples(png.Height, startY[pass], stepY[pass]);
                    if (passWidth != 0 && passHeight != 0
                        && !AddRows(passWidth, passHeight, channels, png.BitDepth, ref total)) return false;
                }
            }

            if (total < 1 || total > MaximumInflatedBytes) return false;
            png.InflatedLength = (int)total;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool AddRows(int width, int height, int channels, int bitDepth, ref long total)
    {
        long bits = checked((long)width * channels * bitDepth);
        long rowLength = checked(1 + (bits + 7) / 8);
        total = checked(total + checked(rowLength * height));
        return total <= MaximumInflatedBytes;
    }

    private static int CountPassSamples(int length, int start, int step) =>
        length <= start ? 0 : checked((length - start + step - 1) / step);

    private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private enum DeflateTableKind
    {
        CodeLength,
        LiteralLength,
        Distance,
    }

    private static readonly DeflateHuffmanTable FixedLiteralLength = CreateFixedLiteralLengthTable();
    private static readonly DeflateHuffmanTable FixedDistance = CreateFixedDistanceTable();

    /// <summary>
    /// Finds the one RFC 1951 member boundary without inflating it. ZLibStream is still the authority for the bytes and Adler-32;
    /// this pass supplies the missing BFINAL/EOB/boundary proof while limiting parsed output to the already-derived scanline length.
    /// </summary>
    private static bool HasCompleteDeflateMember(byte[] compressed, int expectedOutput)
    {
        if (compressed.Length < 6 || !ValidZlibHeader(compressed)) return false;
        int windowSize = 1 << ((compressed[0] >> 4) + 8);
        DeflateBitReader reader = new(compressed.AsSpan(2, compressed.Length - 6));
        int output = 0;
        bool final;
        do
        {
            if (!reader.TryReadBits(1, out int finalBit) || !reader.TryReadBits(2, out int blockType)) return false;
            final = finalBit != 0;
            switch (blockType)
            {
                case 0:
                    if (!ReadStoredBlock(ref reader, expectedOutput, ref output)) return false;
                    break;
                case 1:
                    if (!ReadCompressedBlock(ref reader, FixedLiteralLength, FixedDistance, windowSize, expectedOutput, ref output)) return false;
                    break;
                case 2:
                    if (!ReadDynamicTables(ref reader, out DeflateHuffmanTable? literalLength, out DeflateHuffmanTable? distance)
                        || literalLength is null || distance is null
                        || !ReadCompressedBlock(ref reader, literalLength, distance, windowSize, expectedOutput, ref output)) return false;
                    break;
                default:
                    return false;
            }
        }
        while (!final);

        reader.AlignToByte();
        return output == expectedOutput && reader.AtEnd;
    }

    private static bool ReadStoredBlock(ref DeflateBitReader reader, int expectedOutput, ref int output)
    {
        reader.AlignToByte();
        if (!reader.TryReadBits(16, out int length) || !reader.TryReadBits(16, out int complement)
            || (length ^ 0xFFFF) != complement || !reader.TrySkipBytes(length)) return false;
        return AddOutput(length, expectedOutput, ref output);
    }

    private static bool ReadCompressedBlock(ref DeflateBitReader reader, DeflateHuffmanTable literalLength,
        DeflateHuffmanTable distance, int windowSize, int expectedOutput, ref int output)
    {
        while (true)
        {
            if (!literalLength.TryReadSymbol(ref reader, out int symbol)) return false;
            if (symbol <= 255)
            {
                if (!AddOutput(1, expectedOutput, ref output)) return false;
                continue;
            }
            if (symbol == 256) return true;
            if (symbol is < 257 or > 285) return false;

            int lengthIndex = symbol - 257;
            if (!reader.TryReadBits(LengthExtra[lengthIndex], out int lengthExtra)) return false;
            int length = LengthBase[lengthIndex] + lengthExtra;
            if (!distance.TryReadSymbol(ref reader, out int distanceSymbol) || distanceSymbol is < 0 or > 29) return false;
            if (!reader.TryReadBits(DistanceExtra[distanceSymbol], out int distanceExtra)) return false;
            int distanceValue = DistanceBase[distanceSymbol] + distanceExtra;
            if (distanceValue > output || distanceValue > windowSize || !AddOutput(length, expectedOutput, ref output)) return false;
        }
    }

    private static bool ReadDynamicTables(ref DeflateBitReader reader, out DeflateHuffmanTable? literalLength,
        out DeflateHuffmanTable? distance)
    {
        literalLength = null;
        distance = null;
        if (!reader.TryReadBits(5, out int hlitBits) || !reader.TryReadBits(5, out int hdistBits)
            || !reader.TryReadBits(4, out int hclenBits)) return false;

        // RFC 1951 permits 257..286 literal/length codes; 287 and 288 are not encodable tables.
        if (hlitBits > 29) return false;
        int hlit = hlitBits + 257;
        int hdist = hdistBits + 1;
        int hclen = hclenBits + 4;
        int[] codeLengthLengths = new int[19];
        for (int index = 0; index < hclen; index++)
            if (!reader.TryReadBits(3, out codeLengthLengths[CodeLengthOrder[index]])) return false;
        if (!TryBuildHuffman(codeLengthLengths, DeflateTableKind.CodeLength, out DeflateHuffmanTable? codeLengthTable) || codeLengthTable is null) return false;

        int[] lengths = new int[hlit + hdist];
        int written = 0;
        while (written < lengths.Length)
        {
            if (!codeLengthTable.TryReadSymbol(ref reader, out int symbol)) return false;
            switch (symbol)
            {
                case <= 15:
                    lengths[written++] = symbol;
                    break;
                case 16:
                    if (written == 0 || !reader.TryReadBits(2, out int repeatPreviousBits)) return false;
                    int repeatPrevious = repeatPreviousBits + 3;
                    if (written + repeatPrevious > lengths.Length) return false;
                    int previous = lengths[written - 1];
                    for (int index = 0; index < repeatPrevious; index++) lengths[written++] = previous;
                    break;
                case 17:
                    if (!reader.TryReadBits(3, out int repeatZeroBits) || written + repeatZeroBits + 3 > lengths.Length) return false;
                    written += repeatZeroBits + 3;
                    break;
                case 18:
                    if (!reader.TryReadBits(7, out int longRepeatBits) || written + longRepeatBits + 11 > lengths.Length) return false;
                    written += longRepeatBits + 11;
                    break;
                default:
                    return false;
            }
        }

        if (lengths[256] == 0) return false;
        if (!TryBuildHuffman(lengths.AsSpan(0, hlit), DeflateTableKind.LiteralLength, out literalLength) || literalLength is null) return false;
        if (!TryBuildHuffman(lengths.AsSpan(hlit, hdist), DeflateTableKind.Distance, out distance) || distance is null) return false;
        return true;
    }

    private static bool AddOutput(int amount, int expectedOutput, ref int output)
    {
        if (amount < 0 || output > expectedOutput - amount) return false;
        output += amount;
        return true;
    }

    private static DeflateHuffmanTable CreateFixedLiteralLengthTable()
    {
        int[] lengths = new int[288];
        for (int symbol = 0; symbol <= 143; symbol++) lengths[symbol] = 8;
        for (int symbol = 144; symbol <= 255; symbol++) lengths[symbol] = 9;
        for (int symbol = 256; symbol <= 279; symbol++) lengths[symbol] = 7;
        for (int symbol = 280; symbol <= 287; symbol++) lengths[symbol] = 8;
        return BuildHuffmanOrThrow(lengths, DeflateTableKind.LiteralLength);
    }

    private static DeflateHuffmanTable CreateFixedDistanceTable()
    {
        int[] lengths = new int[32];
        Array.Fill(lengths, 5);
        return BuildHuffmanOrThrow(lengths, DeflateTableKind.Distance);
    }

    private static DeflateHuffmanTable BuildHuffmanOrThrow(ReadOnlySpan<int> lengths, DeflateTableKind tableKind) =>
        TryBuildHuffman(lengths, tableKind, out DeflateHuffmanTable? result) && result is not null
            ? result
            : throw new InvalidOperationException("The fixed DEFLATE Huffman table is invalid.");

    private static bool TryBuildHuffman(ReadOnlySpan<int> lengths, DeflateTableKind tableKind, out DeflateHuffmanTable? result)
    {
        result = null;
        int maximumLength = 0;
        int[] counts = new int[16];
        foreach (int length in lengths)
        {
            if (length is < 0 or > 15) return false;
            if (length != 0)
            {
                counts[length]++;
                maximumLength = Math.Max(maximumLength, length);
            }
        }

        Dictionary<int, int>?[] symbolsByLength = new Dictionary<int, int>?[maximumLength + 1];
        int code = 0;
        int codeSpace = 1;
        int[] nextCode = new int[16];
        for (int bitLength = 1; bitLength <= maximumLength; bitLength++)
        {
            codeSpace = (codeSpace << 1) - counts[bitLength];
            if (codeSpace < 0) return false;
            code = (code + counts[bitLength - 1]) << 1;
            if (code + counts[bitLength] > (1 << bitLength)) return false;
            nextCode[bitLength] = code;
        }

        // zlib's inflate_table contract: the code-length alphabet must be complete;
        // LENS/DISTS may be incomplete only for a one-bit singleton. A zero-symbol
        // distance table is the literal-only exception and is rejected naturally
        // when a length symbol attempts to read a distance.
        if (codeSpace != 0 && (tableKind == DeflateTableKind.CodeLength
            || (tableKind == DeflateTableKind.LiteralLength && maximumLength != 1)
            || (tableKind == DeflateTableKind.Distance && maximumLength is not (0 or 1)))) return false;
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int bitLength = lengths[symbol];
            if (bitLength == 0) continue;
            Dictionary<int, int> map = symbolsByLength[bitLength] ??= new Dictionary<int, int>();
            map.Add(nextCode[bitLength]++, symbol);
        }
        result = new DeflateHuffmanTable(symbolsByLength, maximumLength);
        return true;
    }

    private sealed class DeflateHuffmanTable
    {
        private readonly Dictionary<int, int>?[] _symbolsByLength;
        private readonly int _maximumLength;

        internal DeflateHuffmanTable(Dictionary<int, int>?[] symbolsByLength, int maximumLength)
        {
            _symbolsByLength = symbolsByLength;
            _maximumLength = maximumLength;
        }

        internal bool TryReadSymbol(ref DeflateBitReader reader, out int symbol)
        {
            int code = 0;
            for (int length = 1; length <= _maximumLength; length++)
            {
                if (!reader.TryReadBits(1, out int bit)) break;
                // Unlike integer fields, DEFLATE Huffman codes are emitted most-significant bit first.
                // TryReadBits(1) advances in wire order; assemble that prefix in canonical code order.
                code = (code << 1) | bit;
                if (_symbolsByLength[length] is { } symbols && symbols.TryGetValue(code, out symbol)) return true;
            }
            symbol = 0;
            return false;
        }
    }

    private ref struct DeflateBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _byteOffset;
        private int _bitOffset;

        internal DeflateBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _byteOffset = 0;
            _bitOffset = 0;
        }

        internal bool AtEnd => _byteOffset == _data.Length && _bitOffset == 0;

        internal bool TryReadBits(int count, out int value)
        {
            value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                if (!TryReadBit(out int current)) return false;
                value |= current << bit;
            }
            return true;
        }

        internal bool TryReadBit(out int value)
        {
            if (_byteOffset >= _data.Length)
            {
                value = 0;
                return false;
            }
            value = (_data[_byteOffset] >> _bitOffset) & 1;
            if (++_bitOffset == 8)
            {
                _bitOffset = 0;
                _byteOffset++;
            }
            return true;
        }

        internal void AlignToByte()
        {
            if (_bitOffset != 0)
            {
                _bitOffset = 0;
                _byteOffset++;
            }
        }

        internal bool TrySkipBytes(int count)
        {
            if (_bitOffset != 0 || count < 0 || _data.Length - _byteOffset < count) return false;
            _byteOffset += count;
            return true;
        }
    }

    private static bool HasCompleteScanlines(byte[] png, ParsedPng parsed)
    {
        try
        {
            int compressedLength = 0;
            foreach ((_, int length) in parsed.IdatSegments) compressedLength = checked(compressedLength + length);
            if (compressedLength < 6) return false; // zlib header plus the mandatory Adler-32 trailer

            byte[] compressedBytes = new byte[compressedLength];
            int compressedOffset = 0;
            foreach ((int offset, int length) in parsed.IdatSegments)
            {
                Buffer.BlockCopy(png, offset, compressedBytes, compressedOffset, length);
                compressedOffset += length;
            }
            if (!ValidZlibHeader(compressedBytes) || !HasCompleteDeflateMember(compressedBytes, parsed.InflatedLength)) return false;

            // MemoryStream supplies blocks to the managed/native inflater. The previous one-byte adapter made every compressed byte
            // an inflater roundtrip; exact scanline bounds plus the explicit zlib header/trailer checks below provide the same bound
            // without that avoidable per-byte cost.
            using MemoryStream compressed = new(compressedBytes, writable: false);
            using ZLibStream inflater = new(compressed, CompressionMode.Decompress, leaveOpen: true);
            byte[] discard = new byte[8192];
            uint adlerA = 1, adlerB = 0;
            if (!ValidateRows(inflater, parsed, discard, ref adlerA, ref adlerB)) return false;
            if (inflater.ReadByte() != -1) return false;

            uint expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(compressedBytes.AsSpan(compressedLength - 4));
            uint actualAdler = (adlerB << 16) | adlerA;
            return actualAdler == expectedAdler;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValidZlibHeader(byte[] compressed)
    {
        int cmf = compressed[0];
        int flg = compressed[1];
        return (cmf & 0x0F) == 8
            && (cmf >> 4) <= 7
            && ((cmf << 8) | flg) % 31 == 0
            && (flg & 0x20) == 0; // PNG forbids a preset dictionary.
    }

    private static bool ValidateRows(ZLibStream inflater, ParsedPng png, byte[] discard,
        ref uint adlerA, ref uint adlerB)
    {
        int channels = png.ColorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (channels == 0) return false;

        long consumed = 0;
        if (png.InterlaceMethod == 0)
        {
            if (!ReadRows(inflater, png.Width, png.Height, channels, png.BitDepth, discard, ref consumed, ref adlerA, ref adlerB)) return false;
        }
        else
        {
            ReadOnlySpan<int> startX = stackalloc int[] { 0, 4, 0, 2, 0, 1, 0 };
            ReadOnlySpan<int> startY = stackalloc int[] { 0, 0, 4, 0, 2, 0, 1 };
            ReadOnlySpan<int> stepX = stackalloc int[] { 8, 8, 4, 4, 2, 2, 1 };
            ReadOnlySpan<int> stepY = stackalloc int[] { 8, 8, 8, 4, 4, 2, 2 };
            for (int pass = 0; pass < 7; pass++)
            {
                int passWidth = CountPassSamples(png.Width, startX[pass], stepX[pass]);
                int passHeight = CountPassSamples(png.Height, startY[pass], stepY[pass]);
                if (passWidth != 0 && passHeight != 0
                    && !ReadRows(inflater, passWidth, passHeight, channels, png.BitDepth, discard, ref consumed, ref adlerA, ref adlerB)) return false;
            }
        }
        return consumed == png.InflatedLength;
    }

    private static bool ReadRows(ZLibStream inflater, int width, int height, int channels, int bitDepth,
        byte[] discard, ref long consumed, ref uint adlerA, ref uint adlerB)
    {
        long bits = checked((long)width * channels * bitDepth);
        long rowLength = checked(1 + (bits + 7) / 8);
        for (int row = 0; row < height; row++)
        {
            if (!ReadExactly(inflater, discard, 1)) return false;
            UpdateAdler(discard.AsSpan(0, 1), ref adlerA, ref adlerB);
            if (discard[0] > 4) return false;
            long remaining = rowLength - 1;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(remaining, discard.Length);
                int read = inflater.Read(discard, 0, requested);
                if (read <= 0) return false;
                UpdateAdler(discard.AsSpan(0, read), ref adlerA, ref adlerB);
                remaining -= read;
            }
            consumed = checked(consumed + rowLength);
            if (consumed > MaximumInflatedBytes) return false;
        }
        return true;
    }

    private static void UpdateAdler(ReadOnlySpan<byte> bytes, ref uint first, ref uint second)
    {
        const uint Modulus = 65_521;
        foreach (byte value in bytes)
        {
            first += value;
            if (first >= Modulus) first -= Modulus;
            second += first;
            if (second >= Modulus) second %= Modulus;
        }
    }

    private static bool ReadExactly(Stream input, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = input.Read(buffer, total, count - total);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static bool CodecHeaderMatches(byte[] png, int width, int height)
    {
        using MemoryStream encoded = new(png, writable: false);
        using SKCodec? codec = SKCodec.Create(encoded);
        return codec != null && codec.EncodedFormat == SKEncodedImageFormat.Png
            && codec.Info.Width == width && codec.Info.Height == height;
    }

    private static bool ValidChunkType(ReadOnlySpan<byte> type)
    {
        foreach (byte value in type)
            if (value is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z'))) return false;
        return true;
    }

    private static bool ValidDepth(byte colorType, byte bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false,
    };

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint state = uint.MaxValue;
        foreach (byte value in bytes) state = CrcTable[(byte)(state ^ value)] ^ (state >> 8);
        return ~state;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xEDB88320u);
            table[index] = value;
        }
        return table;
    }
}
