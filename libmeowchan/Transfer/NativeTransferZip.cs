using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Transfer;

// Stored-only bounded ZIP subset. Do not switch to ZipArchiveEntry.Open for deflate:
// its decoder can stop at the declared expanded size rather than true compressed EOF.
internal sealed record TransferZipEntry(string Name, long DataOffset, long Length, uint Crc32);
internal static class NativeTransferZip
{
    internal static async Task<IReadOnlyList<TransferZipEntry>> ReadDirectoryAsync(Stream source,
        TransferEnvelopeLimits limits, CancellationToken ct)
    {
        if (source.Length < 22) throw Error(TransferEnvelopeError.InvalidZip);
        byte[] footer = new byte[22];
        source.Position = source.Length - footer.Length;
        await source.ReadExactlyAsync(footer, ct).ConfigureAwait(false);
        if (U32(footer, 0) != 0x06054b50) throw Error(TransferEnvelopeError.UnsupportedZip);
        int count = U16(footer, 10);
        uint directoryBytes = U32(footer, 12), directoryOffset = U32(footer, 16);
        if (U16(footer, 4) != 0 || U16(footer, 6) != 0 || U16(footer, 8) != count || count == ushort.MaxValue
            || directoryBytes == uint.MaxValue || directoryOffset == uint.MaxValue || U16(footer, 20) != 0)
            throw Error(TransferEnvelopeError.UnsupportedZip);
        if (count > limits.Entries) throw Error(TransferEnvelopeError.EntryLimit);
        if (directoryBytes > limits.DirectoryBytes) throw Error(TransferEnvelopeError.DirectoryLimit);
        if (count < 2 || (long)directoryOffset + directoryBytes != source.Length - 22
            || directoryBytes < count * 46L) throw Error(TransferEnvelopeError.InvalidZip);
        byte[] directory = new byte[checked((int)directoryBytes)];
        source.Position = directoryOffset;
        await source.ReadExactlyAsync(directory, ct).ConfigureAwait(false);
        int offset = 0;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<(long Start, long End, TransferZipEntry Entry)> ranges = new();
        for (int index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (directory.Length - offset < 46 || U32(directory, offset) != 0x02014b50)
                throw Error(TransferEnvelopeError.InvalidZip);
            int version = U16(directory, offset + 6), flags = U16(directory, offset + 8), method = U16(directory, offset + 10);
            uint crc = U32(directory, offset + 16), compressed = U32(directory, offset + 20), length = U32(directory, offset + 24);
            int nameLength = U16(directory, offset + 28), extraLength = U16(directory, offset + 30), commentLength = U16(directory, offset + 32);
            uint attributes = U32(directory, offset + 38), localOffset = U32(directory, offset + 42);
            if (version is < 10 or > 20 || (flags & ~0x800) != 0 || method != 0 || U16(directory, offset + 34) != 0
                || compressed == uint.MaxValue || length == uint.MaxValue || compressed != length
                || localOffset >= directoryOffset || extraLength != 0 || commentLength != 0)
                throw Error(TransferEnvelopeError.UnsupportedZip);
            int kind = (int)((attributes >> 16) & 0xf000);
            if ((kind != 0 && kind != 0x8000) || (attributes & (0x10 | 0x400)) != 0)
                throw Error(TransferEnvelopeError.InvalidEntry);
            if (nameLength > directory.Length - offset - 46) throw Error(TransferEnvelopeError.InvalidZip);
            byte[] rawName = directory.AsSpan(offset + 46, nameLength).ToArray();
            if (rawName.Any(x => x > 127)) throw Error(TransferEnvelopeError.InvalidEntry);
            string name = Encoding.ASCII.GetString(rawName);
            if (!IsEntryName(name)) throw Error(TransferEnvelopeError.InvalidEntry);
            if (!names.Add(name)) throw Error(TransferEnvelopeError.DuplicateEntry);
            long dataOffset = checked((long)localOffset + 30 + nameLength);
            long end = checked(dataOffset + length);
            if (end > directoryOffset) throw Error(TransferEnvelopeError.InvalidZip);
            source.Position = localOffset;
            byte[] header = new byte[30];
            await source.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            if (U32(header, 0) != 0x04034b50 || U16(header, 4) != version || U16(header, 6) != flags
                || U16(header, 8) != method || U32(header, 14) != crc || U32(header, 18) != compressed
                || U32(header, 22) != length || U16(header, 26) != nameLength || U16(header, 28) != 0
                || U16(header, 10) != U16(directory, offset + 12) || U16(header, 12) != U16(directory, offset + 14))
                throw Error(TransferEnvelopeError.InvalidZip);
            byte[] localName = new byte[nameLength];
            await source.ReadExactlyAsync(localName, ct).ConfigureAwait(false);
            if (!localName.AsSpan().SequenceEqual(rawName)) throw Error(TransferEnvelopeError.InvalidZip);
            ranges.Add((localOffset, end, new(name, dataOffset, length, crc)));
            offset = checked(offset + 46 + nameLength);
        }
        if (offset != directory.Length) throw Error(TransferEnvelopeError.InvalidZip);
        long boundary = 0;
        foreach (var range in ranges.OrderBy(x => x.Start))
        {
            ct.ThrowIfCancellationRequested();
            // No self-extracting prefix, descriptor, padding, hidden local record or overlap.
            if (range.Start != boundary) throw Error(TransferEnvelopeError.InvalidZip);
            boundary = range.End;
        }
        if (boundary != directoryOffset) throw Error(TransferEnvelopeError.InvalidZip);
        return ranges.Select(x => x.Entry).ToArray();
    }
    private static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static bool IsEntryName(string name) => name is "manifest.json" or "catalog.json" or "private.json"
        || name.StartsWith("payload/", StringComparison.Ordinal) && name.Length == 72
            && name.AsSpan(8).IndexOfAnyExcept("0123456789abcdef") < 0;
    private static TransferEnvelopeException Error(TransferEnvelopeError error) => new(error);
}
internal sealed class TransferEnvelopeException(TransferEnvelopeError error) : Exception
{ internal TransferEnvelopeError Error { get; } = error; }

internal sealed class TransferCrc32
{
    private static readonly uint[] Table = CreateTable();
    private uint state = uint.MaxValue;
    internal uint Value => ~state;
    internal void Add(ReadOnlySpan<byte> bytes)
    {
        foreach (byte item in bytes) state = Table[(byte)(state ^ item)] ^ (state >> 8);
    }
    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
}
