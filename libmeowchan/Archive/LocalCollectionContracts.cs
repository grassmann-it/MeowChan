using System;
using System.Collections.Immutable;

namespace MeowChan.Archive;

// Local identities are not provider identities or content hashes. default(struct)
// remains possible and must be rejected again at every consuming boundary.
internal readonly record struct LocalCollectionId
{
    internal LocalCollectionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A nonempty local UUID is required.", nameof(value));
        Value = value;
    }
    internal Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

internal readonly record struct LocalItemId
{
    internal LocalItemId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A nonempty local UUID is required.", nameof(value));
        Value = value;
    }
    internal Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

internal readonly record struct LocalRevisionId
{
    internal LocalRevisionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A nonempty local UUID is required.", nameof(value));
        Value = value;
    }
    internal Guid Value { get; }
    public override string ToString() => Value.ToString("D");
}

internal readonly record struct LocalMediaRevisionReference(
    LocalCollectionId CollectionId, LocalItemId ItemId, LocalRevisionId RevisionId);
internal readonly record struct LocalImportRecoveryKey(string CatalogInstance, Guid OperationId);
internal readonly record struct LocalImportReceiptKey(string CatalogInstance, Guid OperationId, string RequestSha256);

internal enum LocalMediaRole { Original = 1 }
internal enum LocalFieldPresence { Absent = 0, Present = 1 }
internal enum LocalClaimOmissionReason { Unsupported = 1, NotSelected = 2, UnsafeValue = 3, ExceedsBound = 4 }
internal sealed record LocalClaimOmission(string FieldCode, LocalClaimOmissionReason Reason);

/// <summary>Inert, immutable values. PublicSourceUrl requires trusted profile-specific classification at the codec boundary.</summary>
internal sealed record LocalSourceClaims(
    int Version, string ProfileId, int ProfileVersion, string? ClaimId, string? PublicSourceUrl,
    LocalFieldPresence TagsPresence, ImmutableArray<string> SourceTags, ImmutableArray<LocalClaimOmission> Omissions);

internal sealed record LocalCollectionSummary(LocalCollectionId Id, string Title, long CollectionRevision, int ItemCount);
internal sealed record LocalCollectionPage(long TotalCount, int Offset, ImmutableArray<LocalCollectionSummary> Items);
internal sealed record LocalRegisteredRevision(
    LocalMediaRevisionReference Reference, int Ordinal, LocalMediaRole Role, long Length, string Sha256);

/// <summary>Historical registration data, not proof of current payload availability or a read/write capability.</summary>
internal sealed record LocalCollectionImportReceipt(
    int Version, string CatalogInstance, Guid OperationId, string RequestSha256, LocalCollectionId CollectionId,
    long CollectionRevision, DateTimeOffset RegisteredAtUtc, ImmutableArray<LocalRegisteredRevision> Revisions);
