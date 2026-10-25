using System.Buffers.Binary;

namespace MeowChan.PluginHost.Filters;

/// <summary>Reads only dimensions, before handing any original to a worker or image decoder.</summary>
internal static class ImageHeader
{
    internal static async Task<(int Width, int Height)> ReadAsync(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long origin = input.Position;
        byte[] start = new byte[24];
        async Task Read(Memory<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        await Read(start.AsMemory(0, 2)).ConfigureAwait(false);
        if (start[0] == 0x89 && start[1] == 0x50)
        {
            await Read(start.AsMemory(2, 22)).ConfigureAwait(false);
            if (!start.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
                || !start.AsSpan(8, 8).SequenceEqual(new byte[] { 0,0,0,13,73,72,68,82 })) throw new InvalidDataException("Invalid PNG header.");
            uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(16, 4));
            uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Invalid image dimensions.");
            return ((int)width, (int)height);
        }
        if (start[0] == (byte)'G' && start[1] == (byte)'I')
        {
            await Read(start.AsMemory(2, 11)).ConfigureAwait(false);
            if (!start.AsSpan(0, 6).SequenceEqual("GIF87a"u8) && !start.AsSpan(0, 6).SequenceEqual("GIF89a"u8))
                throw new InvalidDataException("Invalid GIF header.");
            int width = BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(6, 2));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(8, 2));
            if (width == 0 || height == 0) throw new InvalidDataException("Invalid GIF dimensions.");
            int paletteBytes = (start[10] & 128) != 0 ? 3 * (2 << (start[10] & 7)) : 0;
            if (paletteBytes > input.Length - input.Position) throw new InvalidDataException("Truncated GIF color table.");
            return (width, height);
        }
        if (start[0] == (byte)'R' && start[1] == (byte)'I')
        {
            await Read(start.AsMemory(2, 10)).ConfigureAwait(false);
            if (!start.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !start.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                || (long)BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(4, 4)) + 8 != input.Length - origin)
                throw new InvalidDataException("Invalid WebP container.");
            await Read(start.AsMemory(0, 8)).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(4, 4));
            if ((long)length + (length & 1) > input.Length - input.Position)
                throw new InvalidDataException("Truncated WebP chunk.");
            bool extended = start.AsSpan(0, 4).SequenceEqual("VP8X"u8);
            bool lossy = start.AsSpan(0, 4).SequenceEqual("VP8 "u8);
            bool lossless = start.AsSpan(0, 4).SequenceEqual("VP8L"u8);
            if (!(extended || lossy || lossless) || extended && length != 10 || lossy && length < 10 || lossless && length < 5)
                throw new InvalidDataException("Invalid WebP image header.");
            await Read(start.AsMemory(0, lossless ? 5 : 10)).ConfigureAwait(false);
            int width, height;
            if (extended)
            {
                if ((start[0] & 193) != 0 || start[1] != 0 || start[2] != 0 || start[3] != 0)
                    throw new InvalidDataException("Invalid extended WebP header.");
                width = 1 + (start[4] | start[5] << 8 | start[6] << 16);
                height = 1 + (start[7] | start[8] << 8 | start[9] << 16);
            }
            else if (lossy)
            {
                if ((start[0] & 1) != 0 || start[3] != 157 || start[4] != 1 || start[5] != 42)
                    throw new InvalidDataException("Invalid lossy WebP header.");
                width = BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(6, 2)) & 16383;
                height = BinaryPrimitives.ReadUInt16LittleEndian(start.AsSpan(8, 2)) & 16383;
            }
            else
            {
                if (start[0] != 47 || (start[4] & 224) != 0) throw new InvalidDataException("Invalid lossless WebP header.");
                width = 1 + (start[1] | (start[2] & 63) << 8);
                height = 1 + (start[2] >> 6 | start[3] << 2 | (start[4] & 15) << 10);
            }
            if (width == 0 || height == 0) throw new InvalidDataException("Invalid WebP dimensions.");
            return (width, height);
        }
        if (start[0] != 0xff || start[1] != 0xd8) throw new InvalidDataException("Only JPEG, PNG, GIF and WebP originals are supported.");
        while (input.Position < input.Length)
        {
            await Read(start.AsMemory(0, 1)).ConfigureAwait(false);
            if (start[0] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            do { await Read(start.AsMemory(1, 1)).ConfigureAwait(false); } while (start[1] == 0xff);
            byte marker = start[1];
            if (marker == 0xd9 || marker == 0xda || marker == 0) throw new InvalidDataException("JPEG has no readable frame header.");
            if (marker == 0xd8 || marker is >= 0xd0 and <= 0xd7 || marker == 1) continue;
            await Read(start.AsMemory(2, 2)).ConfigureAwait(false);
            int length = (start[2] << 8) | start[3];
            if (length < 2 || input.Position + length - 2 > input.Length) throw new InvalidDataException("Truncated JPEG segment.");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) throw new InvalidDataException("Invalid JPEG frame.");
                await Read(start.AsMemory(0, 6)).ConfigureAwait(false);
                int height = (start[1] << 8) | start[2], width = (start[3] << 8) | start[4];
                if (width == 0 || height == 0) throw new InvalidDataException("Unknown JPEG dimensions.");
                return (width, height);
            }
            input.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("Image dimensions are unavailable.");
    }
}
