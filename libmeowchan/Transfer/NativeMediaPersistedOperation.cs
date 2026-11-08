using System;
using System.Collections.Immutable;
using MeowChan.Archive;

namespace MeowChan.Transfer;

// Complete read-only SQL evidence. These records neither authorize Apply nor establish
// that a catalog, candidate directory, or a historical receipt is safe to replay.
internal sealed record NativeMediaPersistedIdentity(int PlanVersion, Guid OperationId, string RequestSha256,
    string PackageFingerprint, string CatalogInstance, long CatalogGeneration, ResourceId Resource,
    string OperationsParent, DateTimeOffset ImportedAtUtc);
internal sealed record NativeMediaPersistedRoot(string LegacyKeysJson, ImmutableArray<string> LegacyKeys,
    string? LegacyBindingsSha256, NativeMediaCandidateSelection? Selection, string? SelectionSha256,
    int MemberCount, string? InventorySha256, string? Directory, string? ManifestGeneration);
internal sealed record NativeMediaImportCounts(int PostCount, int AttachmentCount, int EvidenceAdditions,
    int DerivedDescriptorAdditions, int ContainedDerivedAdditions, int OriginalBindingCount);
internal sealed record NativeMediaMergeCounts(int NewPosts, int UpdatedPosts, int NewAttachments,
    int NewBindings, int Conflicts, int EvidenceAdditions, int DerivedDescriptorAdditions, int ContainedDerivedAdditions);
internal sealed record NativeMediaPersistedImportOperation(NativeMediaPersistedIdentity Identity,
    NativeMediaImportOperationState State, NativeMediaPersistedRoot Full, ImmutableArray<byte> CatalogBytes,
    NativeMediaImportCounts Counts);
internal sealed record NativeMediaPersistedMergeOperation(NativeMediaPersistedIdentity Identity,
    NativeMediaImportOperationState State, string RootsJson, ImmutableArray<NativeMediaMergeRoot> Roots,
    NativeMediaPersistedRoot Full, NativeMediaPersistedRoot Safe, ImmutableArray<byte> CatalogBytes,
    NativeMediaMergeCounts Counts);
internal sealed record NativeMediaPersistedPage<T>(ImmutableArray<T> Items, Guid? NextOperationId);
internal sealed record NativeMediaReceiptIdentity(Guid OperationId, string RequestSha256, int PlanVersion);
internal sealed record NativeMediaPersistedImportReceipt(NativeMediaReceiptIdentity Identity, NativeMediaImportCounts Counts);
internal sealed record NativeMediaPersistedMergeReceipt(NativeMediaReceiptIdentity Identity, NativeMediaMergeCounts Counts);
