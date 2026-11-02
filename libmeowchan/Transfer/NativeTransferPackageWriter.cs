using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Transfer;

/// <summary>Trusted caller's frozen selection. OpenRead returns an owned stream, not a path from package data.</summary>
internal sealed record TransferPayloadSource(TransferBlobDescriptor Descriptor, Func<CancellationToken, Task<Stream>> OpenRead);
internal enum TransferWriteError
{
    None, InvalidSelection, OutputNotEmpty, InvalidCatalog, OutputLimit, SourceLengthMismatch,
    SourceHashMismatch, ValidationFailed, IoFailure
}
internal sealed record TransferWriteResult(NativeTransferPackage? Package, TransferWriteError Error, long? BytesWritten)
{
    internal bool IsComplete => Package != null && Error == TransferWriteError.None;
}
/// <summary>
/// Stream-only package producer. Caller owns an exclusive empty seekable staging stream and
/// discards it on any failure/cancellation. No destination publication, profile read, store or network.
/// </summary>
internal static class NativeTransferPackageWriter
{
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long ContainerCap = 2L * 1024 * 1024 * 1024;
    private const int LocalHeaderBytes = 30, CentralDirectoryHeaderBytes = 46, EndOfDirectoryBytes = 22;

    internal sealed record TransferPackageBudget(bool WithinContainerCap, long ContainerBytes, long PayloadBytes,
        int CatalogBytes, int ManifestBytes, long ZipOverhead)
    {
        internal int PrivateMetadataBytes { get; init; }
    }

    /// <summary>
    /// Computes the prospective stored-ZIP size using the same catalog and manifest writers as
    /// <see cref="WriteAsync"/>. This is an admission check, not a replacement for the capped
    /// stream: the latter remains the final guard if the ZIP implementation ever adds bytes.
    /// </summary>
    internal static TransferPackageBudget MeasureBudget(NativeTransferCatalog catalog,
        IReadOnlyList<TransferBlobDescriptor> selectedPayloads, Guid packageId,
        CancellationToken cancellationToken = default, NativePrivateMetadataProjection? privateMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(selectedPayloads);
        cancellationToken.ThrowIfCancellationRequested();
        if (packageId == Guid.Empty || selectedPayloads.Count > (privateMetadata is null ? 9998 : 9997))
            throw new InvalidDataException("Invalid transfer package selection.");
        HashSet<string> hashes = new(StringComparer.Ordinal);
        long payloadBytes = 0;
        foreach (TransferBlobDescriptor descriptor in selectedPayloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor is null || descriptor.Length < 0 || descriptor.Length > 1024L * 1024 * 1024
                || !HashName(descriptor.Sha256) || !hashes.Add(descriptor.Sha256))
                throw new InvalidDataException("Invalid transfer payload descriptor.");
            payloadBytes = checked(payloadBytes + descriptor.Length);
        }
        // Preserve the public-only over-budget fast path. A private selection must
        // first pass the bounded public serializer before the binding visits its graph.
        if (payloadBytes > ContainerCap && privateMetadata is null)
            return new(false, long.MaxValue, payloadBytes, 0, 0, 0);
        byte[] catalogBytes = NativeTransferCatalogSerializer.Serialize(catalog, cancellationToken);
        string contentSchema = NativeTransferCatalogSerializer.ContentSchema(catalog, cancellationToken);
        byte[]? privateBytes = SerializePrivateMetadata(catalog, privateMetadata, cancellationToken);
        if (payloadBytes > ContainerCap)
            return new(false, long.MaxValue, payloadBytes, 0, 0, 0) { PrivateMetadataBytes = privateBytes?.Length ?? 0 };
        byte[] manifest;
        try { manifest = Manifest(packageId, contentSchema, catalogBytes, selectedPayloads, privateBytes); }
        catch (WriterException) { throw new InvalidDataException("The transfer manifest exceeds its native export limit."); }
        int privateLength = privateBytes?.Length ?? 0;
        long containerBytes = ContainerBytes(selectedPayloads.Count, payloadBytes, catalogBytes.Length, manifest.Length, privateLength);
        long zipOverhead = containerBytes - payloadBytes - catalogBytes.Length - manifest.Length - privateLength;
        return new(containerBytes <= ContainerCap, containerBytes, payloadBytes, catalogBytes.Length, manifest.Length, zipOverhead)
            { PrivateMetadataBytes = privateLength };
    }
    internal static async Task<TransferWriteResult> WriteAsync(Stream staging, NativeTransferCatalog catalog,
        IReadOnlyList<TransferPayloadSource> selectedPayloads, Guid packageId, CancellationToken cancellationToken = default,
        NativePrivateMetadataProjection? privateMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(staging); ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(selectedPayloads);
        cancellationToken.ThrowIfCancellationRequested();
        bool cancellationObserved = false;
        void RememberCancellation() => cancellationObserved = true;
        try
        {
            if (!staging.CanRead || !staging.CanWrite || !staging.CanSeek || staging.Length != 0 || staging.Position != 0)
                return Fail(TransferWriteError.OutputNotEmpty);
            if (packageId == Guid.Empty || selectedPayloads.Count > (privateMetadata is null ? 9998 : 9997)) return Fail(TransferWriteError.InvalidSelection);
            // Copy the bounded selection. Never enumerate an unbounded plugin-produced object graph.
            List<TransferPayloadSource> payloads = new(selectedPayloads.Count);
            HashSet<string> hashes = new(StringComparer.Ordinal);
            long declared = 0;
            for (int i = 0; i < selectedPayloads.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferPayloadSource item = selectedPayloads[i];
                if (item?.Descriptor is not { } blob || item.OpenRead == null || blob.Length < 0 || blob.Length > 1024L * 1024 * 1024
                    || !HashName(blob.Sha256) || !hashes.Add(blob.Sha256)) return Fail(TransferWriteError.InvalidSelection);
                declared = checked(declared + blob.Length);
                if (declared > ContainerCap) return Fail(TransferWriteError.OutputLimit);
                payloads.Add(item);
            }
            // Explicit serializer cannot leak authority fields by reflection. Full admission follows below.
            byte[] catalogBytes;
            string contentSchema;
            try
            {
                catalogBytes = NativeTransferCatalogSerializer.Serialize(catalog, cancellationToken);
                contentSchema = NativeTransferCatalogSerializer.ContentSchema(catalog, cancellationToken);
            }
            catch (InvalidDataException) { return Fail(TransferWriteError.InvalidCatalog); }
            // Binding expects a bounded, well-shaped public graph. Do not traverse
            // raw caller catalog data before the serializer's structural budgets.
            byte[]? privateBytes;
            try { privateBytes = SerializePrivateMetadata(catalog, privateMetadata, cancellationToken); }
            catch (InvalidDataException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Fail(TransferWriteError.InvalidSelection);
            }
            byte[] manifest = Manifest(packageId, contentSchema, catalogBytes, payloads, privateBytes);
            if (ContainerBytes(payloads.Count, declared, catalogBytes.Length, manifest.Length, privateBytes?.Length ?? 0) > ContainerCap)
                return Fail(TransferWriteError.OutputLimit);
            using CappedStaging target = new(staging, ContainerCap);
            using (ZipArchive zip = new(target, ZipArchiveMode.Create, leaveOpen: true))
            {
                try
                {
                    await AddBytesAsync(zip, "manifest.json", manifest, cancellationToken, RememberCancellation).ConfigureAwait(false);
                    await AddBytesAsync(zip, "catalog.json", catalogBytes, cancellationToken, RememberCancellation).ConfigureAwait(false);
                    if (privateBytes != null)
                        await AddBytesAsync(zip, "private.json", privateBytes, cancellationToken, RememberCancellation).ConfigureAwait(false);
                    foreach (TransferPayloadSource payload in payloads.OrderBy(p => p.Descriptor.Sha256, StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Stream input = await payload.OpenRead(cancellationToken).ConfigureAwait(false);
                        if (input == null || ReferenceEquals(input, staging)) throw new WriterException(TransferWriteError.InvalidSelection);
                        await using (input.ConfigureAwait(false))
                        {
                            try
                            {
                                if (!input.CanRead) throw new WriterException(TransferWriteError.InvalidSelection);
                                using Stream entry = Entry(zip, "payload/" + payload.Descriptor.Sha256).Open();
                                try { await CopyPayloadAsync(input, entry, payload.Descriptor, cancellationToken).ConfigureAwait(false); }
                                catch (OperationCanceledException) { RememberCancellation(); throw; }
                            }
                            catch (OperationCanceledException) { RememberCancellation(); throw; }
                        }
                    }
                }
                // Record BEFORE ZipArchive's generated finally can replace the exception.
                catch (OperationCanceledException) { RememberCancellation(); throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await staging.FlushAsync(cancellationToken).ConfigureAwait(false);
            staging.Position = 0;
            TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(staging, cancellationToken).ConfigureAwait(false);
            if (!inspection.IsPackageValid) return Fail(TransferWriteError.ValidationFailed, staging.Length);
            staging.Position = 0;
            return new(inspection.Package, TransferWriteError.None, staging.Length);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
        // A real observed cancellation, not merely a later-cancelled token, wins over
        // a secondary disposal/finalization exception. Every nested cleanup still runs.
        catch (Exception) when (cancellationObserved) { throw new OperationCanceledException(cancellationToken); }
        catch (WriterException e) { return Fail(e.Error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException or OverflowException)
        { return Fail(TransferWriteError.IoFailure); }
    }
    // Pure preflight: no private bytes reach staging (or source handles) until both
    // the strict codec and exact public/private identity binding have accepted them.
    private static byte[]? SerializePrivateMetadata(NativeTransferCatalog catalog,
        NativePrivateMetadataProjection? projection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (projection is null) return null;
        NativePrivateMetadataSelection selection = projection.Selection;
        if (!selection.ThreadFavorites && !selection.MediaFavorites && !selection.PostBookmarksAndNotes)
            throw new InvalidDataException("The private metadata selection is invalid.");
        byte[] bytes = NativePrivateMetadataCodec.Serialize(projection, ct);
        NativePrivateMetadataCatalogBinding.Validate(catalog, projection, ct);
        ct.ThrowIfCancellationRequested();
        return bytes;
    }
    private static byte[] Manifest(Guid id, string contentSchema, byte[] catalog,
        IReadOnlyList<TransferPayloadSource> payloads, byte[]? privateBytes)
        => Manifest(id, contentSchema, catalog, payloads.Select(payload => payload.Descriptor).ToArray(), privateBytes);

    private static byte[] Manifest(Guid id, string contentSchema, byte[] catalog,
        IReadOnlyList<TransferBlobDescriptor> payloads, byte[]? privateBytes)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject(); writer.WriteString("format", "meowchan-transfer"); writer.WriteNumber("envelopeVersion", privateBytes is null ? 1 : 2);
            writer.WriteString("packageId", id.ToString("D")); writer.WriteString("contentSchema", contentSchema);
            writer.WriteStartArray("requiredFeatures"); writer.WriteStringValue(contentSchema);
            if (privateBytes != null) writer.WriteStringValue("meowchan-private-v1");
            writer.WriteEndArray();
            writer.WritePropertyName("catalog"); Blob(writer, new(catalog.Length, Convert.ToHexString(SHA256.HashData(catalog)).ToLowerInvariant()));
            writer.WriteStartArray("payloads");
            foreach (TransferBlobDescriptor payload in payloads.OrderBy(p => p.Sha256, StringComparer.Ordinal)) Blob(writer, payload);
            writer.WriteEndArray();
            if (privateBytes != null)
            {
                writer.WritePropertyName("privateMetadata");
                Blob(writer, new(privateBytes.Length, Convert.ToHexString(SHA256.HashData(privateBytes)).ToLowerInvariant()));
            }
            writer.WriteEndObject();
        }
        if (output.Length > 1024 * 1024) throw new WriterException(TransferWriteError.OutputLimit);
        return output.ToArray();
    }
    private static void Blob(Utf8JsonWriter writer, TransferBlobDescriptor blob)
    { writer.WriteStartObject(); writer.WriteNumber("length", blob.Length); writer.WriteString("sha256", blob.Sha256); writer.WriteEndObject(); }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = ZipEpoch; entry.ExternalAttributes = 0;
        return entry;
    }
    private static async Task AddBytesAsync(ZipArchive zip, string name, byte[] bytes, CancellationToken ct, Action rememberCancellation)
    {
        using Stream entry = Entry(zip, name).Open();
        try { await entry.WriteAsync(bytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { rememberCancellation(); throw; }
    }
    private static async Task CopyPayloadAsync(Stream input, Stream target, TransferBlobDescriptor blob, CancellationToken ct)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; long count = 0;
        while (count < blob.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, blob.Length - count)), ct).ConfigureAwait(false);
            if (read == 0) throw new WriterException(TransferWriteError.SourceLengthMismatch);
            count += read; hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0) throw new WriterException(TransferWriteError.SourceLengthMismatch);
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != blob.Sha256) throw new WriterException(TransferWriteError.SourceHashMismatch);
    }
    private static long ContainerBytes(int payloadCount, long payloadBytes, int catalogBytes, int manifestBytes,
        int privateMetadataBytes)
    {
        bool hasPrivateMetadata = privateMetadataBytes != 0;
        long entries = checked((long)payloadCount + 2 + (hasPrivateMetadata ? 1 : 0));
        long names = checked(13L + 12L + (long)payloadCount * 72L + (hasPrivateMetadata ? 12L : 0));
        long zipOverhead = checked(entries * (LocalHeaderBytes + CentralDirectoryHeaderBytes) + 2 * names + EndOfDirectoryBytes);
        return checked(payloadBytes + catalogBytes + manifestBytes + privateMetadataBytes + zipOverhead);
    }
    private static bool HashName(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static TransferWriteResult Fail(TransferWriteError error, long? bytes = null) => new(null, error, bytes);
    private sealed class WriterException(TransferWriteError error) : Exception { internal TransferWriteError Error => error; }
    private sealed class CappedStaging(Stream inner, long cap) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { if (value < 0 || value > cap) throw new WriterException(TransferWriteError.OutputLimit); inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
        private void Check(int count) { if (inner.Position < 0 || inner.Position > cap - count) throw new WriterException(TransferWriteError.OutputLimit); }
        // Deliberately do not own/dispose the caller's staging handle.
    }
}
