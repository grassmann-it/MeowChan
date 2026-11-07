using System;
using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Passive, unauthenticated package observations. Never an active source/grant or a store command.
internal sealed record TransferMeasuredBlob(long Length, string Sha256, string Md5, string Sha1);
internal enum TransferMediaRole { Original, Preview }
internal enum TransferContentStatus { Contained, Omitted, Unknown, Unavailable, Failed, Cancelled }
internal sealed record TransferHistoricalIntegrity(string Kind, DateTimeOffset AcceptedAtUtc, bool SizeMismatch, bool Md5Mismatch);
// Recorded Actual evidence, not provider expectations or a promise that original bytes are present.
internal sealed record TransferOriginalByteVersion(long Length, string Sha256, string Md5, string? Sha1);
internal enum TransferDerivedRepresentationKind { Preview, VideoPosterPng }
// Source linkage is an untrusted assertion on receipt. A future producer must establish
// video/derivation evidence; neither this DTO nor a filename manufactures that evidence.
internal sealed record TransferDerivedRepresentation(TransferDerivedRepresentationKind Kind,
    string SourceRevisionKey, TransferOriginalByteVersion SourceByteVersion,
    TransferContentStatus Status, string? PayloadSha256);
internal sealed record TransferAttachment(CapturedAttachment Metadata, TransferMediaRole Role,
    TransferContentStatus Status, string? PayloadSha256, TransferHistoricalIntegrity? History)
{
    internal TransferOriginalByteVersion? OriginalByteVersion { get; init; }
    internal ImmutableArray<TransferDerivedRepresentation> DerivedRepresentations { get; init; }
        = ImmutableArray<TransferDerivedRepresentation>.Empty;
}
internal sealed record TransferRetainedBinding(string PostId, TransferAttachment Attachment);
internal sealed record TransferPost(CapturedPost Metadata, ImmutableArray<TransferAttachment> Attachments)
{
    // Wire null is distinct from an observed empty collection. Metadata.Attachments
    // retains null too; the empty projection here is only convenient enumeration.
    internal bool AttachmentsWereNull { get; init; }
}
internal sealed record TransferThread(ResourceId Id, string? PageUrl, string Title, ThreadSourceState SourceState,
    DateTimeOffset FirstSeenAtUtc, DateTimeOffset? CapturedAtUtc, long? CaptureRevision,
    bool? DecimalPostOrder, ImmutableArray<TransferPost> Posts)
{
    // Historical attachment surrogates, never captured post observations.
    internal ImmutableArray<TransferRetainedBinding> RetainedBindings { get; init; } = ImmutableArray<TransferRetainedBinding>.Empty;
}
internal sealed record NativeTransferCatalog(DateTimeOffset ExportedAtUtc, ImmutableArray<TransferThread> Threads);
internal enum TransferCatalogError
{
    None, CatalogLimit, InvalidJson, UnsupportedSchema, InvalidField, DuplicateIdentity,
    InvalidPresence, UnsafeUrl, UnsafeHtml, InvalidRevision, InvalidReference, InvalidHistory, BudgetExceeded,
    InvalidDerivedPayload
}
internal sealed record TransferCatalogInspection(NativeTransferCatalog? Catalog, TransferCatalogError Error)
{
    internal bool IsCatalogValid => Catalog != null && Error == TransferCatalogError.None;
}
