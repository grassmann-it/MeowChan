using System;
using System.Collections.Generic;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal enum ArchiveCleanupEligibility { Candidate, Protected, Active, Kept, Unknown }
internal enum ArchiveCleanupReason
{
    ThreadFavorite, MediaFavorite, MediaRating, PostBookmark, ReaderGroup, ReadingPosition, OpenView,
    ActiveWatch, PendingWork, SharedDirectory, MediaUnknown, ProtectionUnknown, WorkUnknown,
    MoreThanTwoAssets, EvidenceChanged, CatalogChanged
}

internal sealed record ArchiveCleanupStorageAnchor(ResourceId Thread, string Directory,
    string ManifestGeneration, MediaAssetReference Asset, string ParentFingerprint);

internal sealed record ArchiveCleanupDirectoryEvidence(string Directory, string? ManifestGeneration,
    IReadOnlyList<MediaAssetReference> Assets, long VerifiedBytes, bool Missing,
    ArchiveCleanupStorageAnchor? StorageAnchor, string Fingerprint, string? Error)
{
    // Generation and verified asset fields already participate in the cleanup evidence token.
    [System.Text.Json.Serialization.JsonIgnore]
    public MediaAssetSnapshot? Manifest { get; init; }
}

internal sealed record ArchiveCleanupMediaEvidence(int? SavedAssetCount, long VerifiedBytes,
    IReadOnlyList<ArchiveCleanupDirectoryEvidence> Directories, string EvidenceToken, string? Error);

internal sealed record ArchiveCleanupPreviewRow(ArchiveThreadSummary Summary, ArchiveCleanupMediaEvidence Media,
    ArchiveCleanupEligibility Eligibility, IReadOnlyList<ArchiveCleanupReason> Reasons, string EvidenceToken)
{
    public ResourceId Id => Summary.Id;
    public string Title => Summary.Title;
    public int PostCount => Summary.PostCount;
}

/// <summary>Frozen complete-profile evidence. It is never itself permission to delete.</summary>
internal sealed record ArchiveCleanupPreview(string ProfileBinding, int PolicyVersion,
    IReadOnlyList<ArchiveCleanupPreviewRow> Rows, IReadOnlyList<ResourceId> OpenThreadIds,
    string EvidenceToken, string ProtectionToken, string WorkToken, bool Complete, bool IsStale,
    IReadOnlyList<string> Errors)
{
    public const int MaximumSavedAssets = 2;
}
