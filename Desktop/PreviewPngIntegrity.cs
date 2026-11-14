using System;
using System.Buffers.Binary;
using System.IO;
using SkiaSharp;

namespace MeowChan.Desktop;

/// <summary>
/// Validation for the derived preview PNGs of <see cref="VideoPosterCache"/> and <see cref="VideoScrubPreviewCache"/>.
/// <list type="bullet">
/// <item><see cref="IsCompleteDecodable"/> is used for fresh decoder output before it is published: the full structure check plus one
/// complete pixel decode that must report success (a truncated image is not accepted as "incomplete but usable").</item>
/// <item><see cref="IsIntactStored"/> is used for warm cache hits: the full structure check (every chunk length and CRC-32, IHDR, at
/// least one IDAT run, an IEND chunk ending exactly at the end of the file) plus the Skia codec header, but no pixel decode. A truncated,
/// bit-flipped or appended-to file therefore never passes; the one remaining full decode is the display decode of the caller.</item>
/// </list>
/// Neither check fetches anything or touches originals.
/// </summary>
internal static class PreviewPngIntegrity
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Idat = 0x49444154, Iend = 0x49454E44;
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Warm-hit check: complete and CRC-intact within the bounds, and accepted by the Skia PNG codec header. No pixel decode.</summary>
    internal static bool IsIntactStored(byte[] png, int maximumBytes, int maximumWidth, int maximumHeight) =>
        TryReadStructure(png, maximumBytes, maximumWidth, maximumHeight, out int width, out int height)
        && CodecHeaderMatches(png, width, height);

    /// <summary>Publish check: <see cref="IsIntactStored"/> and one complete, successful pixel decode.</summary>
    internal static bool IsCompleteDecodable(byte[] png, int maximumBytes, int maximumWidth, int maximumHeight, Action? fullDecodeObserver = null)
    {
        if (!TryReadStructure(png, maximumBytes, maximumWidth, maximumHeight, out int width, out int height)) return false;
        using MemoryStream encoded = new(png, writable: false);
        using SKCodec? codec = SKCodec.Create(encoded);
        if (codec == null || codec.EncodedFormat != SKEncodedImageFormat.Png
            || codec.Info.Width != width || codec.Info.Height != height) return false;
        fullDecodeObserver?.Invoke();
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new(info);
        // SKBitmap.Decode also accepts IncompleteInput; a published preview must decode completely.
        return codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success;
    }

    /// <summary>The structural part of both checks (signature, chunk walk with CRC-32, IHDR bounds, IDAT, IEND at the exact end).</summary>
    internal static bool TryReadStructure(ReadOnlySpan<byte> png, int maximumBytes, int maximumWidth, int maximumHeight,
        out int width, out int height)
    {
        width = 0; height = 0;
        if (png.Length < Signature.Length + 12 || png.Length > maximumBytes || !png[..Signature.Length].SequenceEqual(Signature))
            return false;

        int offset = Signature.Length;
        bool first = true, sawPalette = false, inIdatRun = false, idatRunEnded = false, sawIdat = false;
        byte colorType = 0;
        while (true)
        {
            if (png.Length - offset < 12) return false;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > int.MaxValue || length > (uint)(png.Length - offset - 12)) return false;
            ReadOnlySpan<byte> typeAndData = png.Slice(offset + 4, 4 + (int)length);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(typeAndData);
            if (!ValidChunkType(typeAndData[..4])) return false;
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + (int)length)..]);
            if (Crc32(typeAndData) != stored) return false;
            ReadOnlySpan<byte> data = typeAndData[4..];
            offset += 12 + (int)length;

            if (first)
            {
                if (type != Ihdr || length != 13) return false;
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data);
                uint h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (w < 1 || h < 1 || w > (uint)maximumWidth || h > (uint)maximumHeight) return false;
                byte bitDepth = data[8];
                colorType = data[9];
                if (!ValidDepth(colorType, bitDepth) || data[10] != 0 || data[11] != 0 || data[12] > 1) return false;
                width = (int)w; height = (int)h;
                first = false;
                continue;
            }

            if (type == Ihdr) return false;
            if (type == Idat)
            {
                if (idatRunEnded) return false;
                inIdatRun = true; sawIdat = true;
                continue;
            }
            if (inIdatRun) { inIdatRun = false; idatRunEnded = true; }
            if (type == Plte)
            {
                if (sawIdat || sawPalette) return false;
                sawPalette = true;
            }
            if (type == Iend)
                return length == 0 && offset == png.Length && sawIdat && (colorType != 3 || sawPalette);
        }
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
