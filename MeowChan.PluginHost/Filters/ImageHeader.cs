namespace MeowChan.PluginHost.Filters;

/// <summary>Reads only dimensions, before handing any original to a worker or image decoder.</summary>
internal static class ImageHeader
{
    internal static async Task<(int Width, int Height)> ReadAsync(Stream input, CancellationToken token)
    {
        byte[] start = new byte[24];
        await input.ReadExactlyAsync(start.AsMemory(0, 2), token).ConfigureAwait(false);
        if (start[0] == 0x89 && start[1] == 0x50)
        {
            await input.ReadExactlyAsync(start.AsMemory(2, 22), token).ConfigureAwait(false);
            if (!start.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
                || !start.AsSpan(8, 8).SequenceEqual(new byte[] { 0,0,0,13,73,72,68,82 })) throw new InvalidDataException("Invalid PNG header.");
            uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(16, 4));
            uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Invalid image dimensions.");
            return ((int)width, (int)height);
        }
        if (start[0] != 0xff || start[1] != 0xd8) throw new InvalidDataException("Only JPEG and PNG originals are supported.");
        while (input.Position < input.Length)
        {
            await input.ReadExactlyAsync(start.AsMemory(0, 1), token).ConfigureAwait(false);
            if (start[0] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            do { await input.ReadExactlyAsync(start.AsMemory(1, 1), token).ConfigureAwait(false); } while (start[1] == 0xff);
            byte marker = start[1];
            if (marker == 0xd9 || marker == 0xda || marker == 0) throw new InvalidDataException("JPEG has no readable frame header.");
            if (marker == 0xd8 || marker is >= 0xd0 and <= 0xd7 || marker == 1) continue;
            await input.ReadExactlyAsync(start.AsMemory(2, 2), token).ConfigureAwait(false);
            int length = (start[2] << 8) | start[3];
            if (length < 2 || input.Position + length - 2 > input.Length) throw new InvalidDataException("Truncated JPEG segment.");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) throw new InvalidDataException("Invalid JPEG frame.");
                await input.ReadExactlyAsync(start.AsMemory(0, 6), token).ConfigureAwait(false);
                int height = (start[1] << 8) | start[2], width = (start[3] << 8) | start[4];
                if (width == 0 || height == 0) throw new InvalidDataException("Unknown JPEG dimensions.");
                return (width, height);
            }
            input.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("Image dimensions are unavailable.");
    }
}
