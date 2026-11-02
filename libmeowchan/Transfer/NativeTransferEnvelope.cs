using System.Collections.Immutable;

namespace MeowChan.Transfer;

// Internal framing contract only. A validated envelope is NOT a semantically valid import.
internal sealed record TransferBlobDescriptor(long Length, string Sha256);
internal sealed record NativeTransferEnvelope(string PackageId, string ContentSchema,
    string Fingerprint, TransferBlobDescriptor Catalog, ImmutableArray<TransferBlobDescriptor> Payloads)
{
    internal int EnvelopeVersion { get; init; } = 1;
    internal TransferBlobDescriptor? PrivateMetadata { get; init; }
    internal ImmutableArray<byte> PrivateMetadataBytes { get; init; } = ImmutableArray<byte>.Empty;
    internal ImmutableArray<byte> CatalogBytes { get; init; } = ImmutableArray<byte>.Empty;
    internal ImmutableDictionary<string, TransferMeasuredBlob> MeasuredPayloads { get; init; }
        = ImmutableDictionary<string, TransferMeasuredBlob>.Empty;
}
internal enum TransferEnvelopeError
{
    None, UnreadableStream, ContainerLimit, UnsupportedZip, EntryLimit, DirectoryLimit,
    InvalidZip, InvalidEntry, DuplicateEntry, ManifestLimit, InvalidManifest,
    UnsupportedVersion, UnsupportedFeature, InvalidReference, PayloadLimit,
    LengthMismatch, HashMismatch, ReadFailure
}
internal sealed record TransferEnvelopeInspection(NativeTransferEnvelope? Envelope, TransferEnvelopeError Error)
{
    internal bool IsEnvelopeValid => Envelope != null && Error == TransferEnvelopeError.None;
}

internal sealed record TransferEnvelopeLimits
{
    internal long ContainerBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    internal int Entries { get; init; } = 10_000;
    internal int DirectoryBytes { get; init; } = 4 * 1024 * 1024;
    internal int ManifestBytes { get; init; } = 1024 * 1024;
    internal int CatalogBytes { get; init; } = 16 * 1024 * 1024;
    internal int PrivateMetadataBytes { get; init; } = 16 * 1024 * 1024;
    internal long PayloadBytes { get; init; } = 1024L * 1024 * 1024;
    internal long TotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    internal bool IsWithinSafetyCaps() => ContainerBytes is > 0 and <= 2L * 1024 * 1024 * 1024
        && Entries is >= 2 and <= 10_000 && DirectoryBytes is > 0 and <= 4 * 1024 * 1024
        && ManifestBytes is > 0 and <= 1024 * 1024 && CatalogBytes is > 0 and <= 16 * 1024 * 1024
        && PrivateMetadataBytes is > 0 and <= 16 * 1024 * 1024
        && PayloadBytes is > 0 and <= 1024L * 1024 * 1024 && TotalBytes is > 0 and <= 4L * 1024 * 1024 * 1024;
}
