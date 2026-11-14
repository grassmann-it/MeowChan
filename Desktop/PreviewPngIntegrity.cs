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
            if (!ValidChunkType(typeBytes) || typeBytes[3] is < (byte)'A' or > (byte)'Z') return false;
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

    private static bool HasCompleteScanlines(byte[] png, ParsedPng parsed)
    {
        try
        {
            int compressedLength = 0;
            foreach ((_, int length) in parsed.IdatSegments) compressedLength = checked(compressedLength + length);
            byte[] compressedBytes = new byte[compressedLength];
            int compressedOffset = 0;
            foreach ((int offset, int length) in parsed.IdatSegments)
            {
                Buffer.BlockCopy(png, offset, compressedBytes, compressedOffset, length);
                compressedOffset += length;
            }

            using MemoryStream compressed = new(compressedBytes, writable: false);
            // Returning at most one source byte per read prevents a decompressor read-ahead from hiding trailing IDAT bytes.
            using SingleByteReadStream exactInput = new(compressed);
            using ZLibStream inflater = new(exactInput, CompressionMode.Decompress, leaveOpen: true);
            byte[] discard = new byte[8192];
            if (!ValidateRows(inflater, parsed, discard)) return false;
            if (inflater.ReadByte() != -1 || exactInput.Position != exactInput.Length) return false;
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateRows(ZLibStream inflater, ParsedPng png, byte[] discard)
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
            if (!ReadRows(inflater, png.Width, png.Height, channels, png.BitDepth, discard, ref consumed)) return false;
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
                    && !ReadRows(inflater, passWidth, passHeight, channels, png.BitDepth, discard, ref consumed)) return false;
            }
        }
        return consumed == png.InflatedLength;
    }

    private static bool ReadRows(ZLibStream inflater, int width, int height, int channels, int bitDepth,
        byte[] discard, ref long consumed)
    {
        long bits = checked((long)width * channels * bitDepth);
        long rowLength = checked(1 + (bits + 7) / 8);
        for (int row = 0; row < height; row++)
        {
            if (!ReadExactly(inflater, discard, 1)) return false;
            if (discard[0] > 4) return false;
            long remaining = rowLength - 1;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(remaining, discard.Length);
                int read = inflater.Read(discard, 0, requested);
                if (read <= 0) return false;
                remaining -= read;
            }
            consumed = checked(consumed + rowLength);
            if (consumed > MaximumInflatedBytes) return false;
        }
        return true;
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

    private sealed class SingleByteReadStream : Stream
    {
        private readonly Stream _inner;

        internal SingleByteReadStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => count == 0 ? 0 : _inner.Read(buffer, offset, 1);
        public override int Read(Span<byte> buffer) => buffer.Length == 0 ? 0 : _inner.Read(buffer[..1]);
        public override int ReadByte() => _inner.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
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
