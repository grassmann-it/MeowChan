using System;
using System.Collections.Immutable;
using MeowChan.Archive;

namespace MeowChan.Transfer;

// INTERNAL technical slice: no media import, no Desktop offer, no active download capability.
internal enum NativeTextImportError { None, InvalidPackage, MediaNotSupported, MissingPageUrl, TargetBudgetExceeded }
internal enum NativeTextImportCommitStatus { Committed, StalePlan }
internal sealed record NativeTextImportPreparation(NativeTextImportPlan? Plan, NativeTextImportError Error);
internal sealed record NativeTextImportCommitResult(NativeTextImportCommitStatus Status,
    int NewThreads = 0, int NewPosts = 0, int UpdatedPosts = 0, int ConflictingThreads = 0, bool IsReplay = false);
internal sealed record NativeTextImportCandidate(string Sha256, DateTimeOffset ImportedAtUtc,
    DateTimeOffset ExportedAtUtc, bool HasConflicts, ImmutableArray<byte> CatalogBytes);
internal sealed record NativeTextImportCandidatePage(ImmutableArray<NativeTextImportCandidate> Items, string? NextSha256);
internal sealed record NativeTextImportDelta(TransferThread Incoming, bool IsNew,
    ImmutableArray<CapturedPost> Added, ImmutableArray<CapturedPost> Updated, bool HasConflicts,
    ImmutableArray<byte> CatalogBytes, string Sha256);

// Immutable owned data, bound to one live ArchiveCatalog instance as well as a durable catalog fence.
// Stable operation identity recovers an unknown commit outcome, even after target deletion.
internal sealed class NativeTextImportPlan
{
    internal NativeTextImportPlan(ArchiveCatalog owner, string instance, long generation,
        DateTimeOffset exportedAtUtc, ImmutableArray<NativeTextImportDelta> deltas, string requestSha256)
    { Owner = owner; Instance = instance; Generation = generation; ExportedAtUtc = exportedAtUtc; Deltas = deltas; RequestSha256 = requestSha256; OperationId = Guid.NewGuid(); }
    internal ArchiveCatalog Owner { get; }
    internal string Instance { get; }
    internal long Generation { get; }
    internal DateTimeOffset ExportedAtUtc { get; }
    internal ImmutableArray<NativeTextImportDelta> Deltas { get; }
    internal int ThreadCount => Deltas.Length;
    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
}
