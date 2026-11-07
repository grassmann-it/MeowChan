using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace MeowChan.Transfer;

/// <summary>
/// Per-inspection evidence gathered during the envelope's existing measured read. No second
/// stream read, decoding, extraction or source-video inference. Catalog binding stays separate.
/// A PNG result attests only critical chunk structure and all chunk CRCs, not renderability,
/// ancillary metadata semantics, pixel safety, or the claimed derivation from the original.
/// </summary>
internal sealed class NativeTransferDerivedPayloadValidator
{
    private readonly HashSet<string> pngPayloads = new(StringComparer.Ordinal);
    internal TransferCatalogError CatalogPreflightError { get; private set; }
    internal void Reset() { pngPayloads.Clear(); CatalogPreflightError = TransferCatalogError.None; }
    internal bool PreflightCatalog(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        CatalogPreflightError = NativeTransferCatalogValidator.PreflightV4ReferenceBudget(bytes, ct);
        return CatalogPreflightError == TransferCatalogError.None;
    }
    internal void Record(string measuredSha256, PngStructure png)
    {
        if (png.IsValid) pngPayloads.Add(measuredSha256);
    }

    // Call only after strict catalog validation. Equal-hash roles share byte verification,
    // never the original's occurrence, acceptance, tag target or availability.
    internal bool Validate(NativeTransferCatalog catalog, CancellationToken ct)
    {
        foreach (TransferThread thread in catalog.Threads)
        {
            ct.ThrowIfCancellationRequested();
            foreach (TransferPost post in thread.Posts)
            {
                ct.ThrowIfCancellationRequested();
                foreach (TransferAttachment attachment in post.Attachments)
                    if (!ValidateAttachment(attachment, ct)) return false;
            }
            foreach (TransferRetainedBinding retained in thread.RetainedBindings)
                if (!ValidateAttachment(retained.Attachment, ct)) return false;
        }
        ct.ThrowIfCancellationRequested();
        return true;
    }
    private bool ValidateAttachment(TransferAttachment attachment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (TransferDerivedRepresentation item in attachment.DerivedRepresentations)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind == TransferDerivedRepresentationKind.VideoPosterPng
                && item.Status == TransferContentStatus.Contained && !pngPayloads.Contains(item.PayloadSha256!)) return false;
        }
        return true;
    }

    // Constant memory streaming parser; bytes/chunks are bounded by the admitted payload length.
    // Ancillary chunks are opaque (PNG forward compatibility); unknown critical chunks reject.
    // No compressed IDAT or ancillary stream is inflated. An invalid candidate stops PNG work,
    // but the envelope still measures the ENTIRE payload and checks its ZIP CRC/SHA/length.
    internal sealed class PngStructure(long length)
    {
        private const uint Ihdr = 0x49484452, Plte = 0x504c5445, Idat = 0x49444154, Iend = 0x49454e44;
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
        private static readonly uint[] CrcTable = CreateCrcTable();
        private enum Phase { Signature, Header, Data, Crc, Complete, Invalid }
        private Phase phase;
        private readonly byte[] token = new byte[8], ihdr = new byte[13];
        private long remaining = length, dataRemaining, idatBytes;
        private int tokenCount;
        private uint type, crc;
        private byte colorType, bitDepth;
        private bool headerSeen, paletteSeen, dataSeen, dataEnded;
        internal bool IsValid => phase == Phase.Complete && remaining == 0;

        internal void Append(ReadOnlySpan<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (phase == Phase.Invalid) return;
            if (bytes.Length > remaining) { phase = Phase.Invalid; return; }
            while (!bytes.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                if (phase == Phase.Invalid) return;
                if (phase == Phase.Complete) { phase = Phase.Invalid; return; }
                if (phase == Phase.Data)
                {
                    int count = (int)Math.Min(bytes.Length, dataRemaining);
                    ReadOnlySpan<byte> part = bytes[..count];
                    AddCrc(part);
                    if (type == Ihdr) part.CopyTo(ihdr.AsSpan(13 - (int)dataRemaining));
                    dataRemaining -= count; remaining -= count; bytes = bytes[count..];
                    if (dataRemaining != 0) continue;
                    if (type == Ihdr && !ValidHeader()) { phase = Phase.Invalid; return; }
                    phase = Phase.Crc;
                    continue;
                }
                int wanted = phase == Phase.Crc ? 4 : 8;
                int take = Math.Min(bytes.Length, wanted - tokenCount);
                bytes[..take].CopyTo(token.AsSpan(tokenCount));
                tokenCount += take; remaining -= take; bytes = bytes[take..];
                if (tokenCount != wanted) continue;
                tokenCount = 0;
                switch (phase)
                {
                    case Phase.Signature:
                        phase = token.AsSpan().SequenceEqual(Signature) ? Phase.Header : Phase.Invalid;
                        break;
                    case Phase.Header:
                        if (!StartChunk()) phase = Phase.Invalid;
                        break;
                    case Phase.Crc:
                        if (BinaryPrimitives.ReadUInt32BigEndian(token) != ~crc) phase = Phase.Invalid;
                        else if (type == Iend) phase = remaining == 0 ? Phase.Complete : Phase.Invalid;
                        else phase = Phase.Header;
                        break;
                }
            }
        }

        private bool StartChunk()
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(token);
            type = BinaryPrimitives.ReadUInt32BigEndian(token.AsSpan(4));
            // PNG lengths/dimensions are at most 2^31-1. Compare in Int64 before consuming.
            if (size > int.MaxValue || remaining < 4 || size > remaining - 4) return false;
            for (int i = 4; i < 8; i++)
                if (token[i] is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) return false;
            if (token[6] is < (byte)'A' or > (byte)'Z' || !headerSeen && type != Ihdr) return false;
            switch (type)
            {
                case Ihdr:
                    if (headerSeen || size != 13) return false;
                    headerSeen = true;
                    break;
                case Plte:
                    if (paletteSeen || dataSeen || colorType is 0 or 4 || size == 0 || size > 768 || size % 3 != 0
                        || colorType == 3 && size / 3 > (1 << bitDepth)) return false;
                    paletteSeen = true;
                    break;
                case Idat:
                    if (dataEnded || colorType == 3 && !paletteSeen) return false;
                    dataSeen = true;
                    idatBytes += size;
                    break;
                case Iend:
                    if (size != 0 || !dataSeen || idatBytes == 0) return false;
                    break;
                default:
                    if (token[4] is >= (byte)'A' and <= (byte)'Z') return false;
                    break;
            }
            if (dataSeen && type != Idat) dataEnded = true;
            crc = uint.MaxValue;
            AddCrc(token.AsSpan(4, 4));
            dataRemaining = size;
            phase = size == 0 ? Phase.Crc : Phase.Data;
            return true;
        }
        private bool ValidHeader()
        {
            uint width = BinaryPrimitives.ReadUInt32BigEndian(ihdr), height = BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4));
            bitDepth = ihdr[8]; colorType = ihdr[9];
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
                || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1) return false;
            return colorType switch
            {
                0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                2 or 4 or 6 => bitDepth is 8 or 16,
                3 => bitDepth is 1 or 2 or 4 or 8,
                _ => false
            };
        }
        private void AddCrc(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes) crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        }
        private static uint[] CreateCrcTable()
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
}
