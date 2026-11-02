using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Internal new-resource-only slice; none of these records grants source/download authority.
internal enum NativeMediaImportError { None, UnsupportedPackage, MissingPageUrl, ResourceExists }
internal enum NativeMediaImportCommitStatus { Committed, StalePlan, ReconciliationRequired, Inactive, Unknown }
internal enum NativeMediaImportPhase
{
    PreparingSaved, OperationsParentCreated, CandidateDirectoryCreated, CandidateReady, PreparedSaved,
    CandidateVerified, BeforeSqlCommit, SqlCommitted
}
internal enum NativeMediaImportOperationState { Preparing, Prepared, Committed }
internal enum NativeMediaImportFilesystemStatus { MissingParent, EmptyParent, Inspected, Invalid }
internal sealed record NativeMediaImportPreparation(NativeMediaImportPlan? Plan, NativeMediaImportError Error);
internal sealed record NativeMediaImportCommitResult(NativeMediaImportCommitStatus Status,
    int NewThreads = 0, int NewPosts = 0, int NewAttachments = 0, bool IsReplay = false, string? Diagnostic = null);
internal sealed record NativeMediaImportOperation(Guid OperationId, string RequestSha256, string PackageFingerprint,
    string CatalogInstance, long CatalogGeneration, ResourceId Resource, string OperationsParent,
    NativeMediaImportOperationState State, DateTimeOffset ImportedAtUtc, string? CandidateDirectory,
    string? ManifestGeneration, int PostCount, int AttachmentCount);
internal sealed record NativeMediaImportOperationPage(ImmutableArray<NativeMediaImportOperation> Items, Guid? NextOperationId);
internal sealed record NativeMediaImportChildInspection(string Path, PassiveMediaCandidateStatus Status,
    bool MatchesOperation = false, bool IsEmpty = false, bool JournalMissing = false);
internal sealed record NativeMediaImportOperationInspection(NativeMediaImportOperation? Operation,
    NativeMediaImportFilesystemStatus FilesystemStatus, ImmutableArray<NativeMediaImportChildInspection> Children,
    NativeMediaImportCommitResult? Receipt = null);
internal sealed record NativeMediaImportAttachment(string PostId, string RevisionKey,
    TransferAttachment Incoming, MediaProviderMetadata Provider);

internal sealed class NativeMediaImportPlan
{
    internal NativeMediaImportPlan(ArchiveCatalog owner, NativeTransferImportStage stage, NativeTransferPackage package,
        string instance, long generation, Guid operation, string request, string parent,
        ImmutableArray<NativeMediaImportAttachment> attachments)
    {
        Owner = owner; Stage = stage; Package = package; Instance = instance; Generation = generation;
        OperationId = operation; RequestSha256 = request; OperationsParent = parent; Attachments = attachments;
        ImportedAtUtc = DateTimeOffset.UtcNow;
        RevisionKeysJson = JsonSerializer.Serialize(attachments.Select(a => a.RevisionKey).OrderBy(k => k, StringComparer.Ordinal));
    }
    internal ArchiveCatalog Owner { get; }
    internal NativeTransferImportStage Stage { get; }
    // Frozen validated metadata; never re-inspect the package or caller's input after planning.
    internal NativeTransferPackage Package { get; }
    internal TransferThread Thread => Package.Catalog.Threads[0];
    internal ResourceId Resource => Thread.Id;
    internal string Instance { get; }
    internal long Generation { get; }
    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string OperationsParent { get; }
    internal DateTimeOffset ImportedAtUtc { get; }
    internal ImmutableArray<NativeMediaImportAttachment> Attachments { get; }
    internal string RevisionKeysJson { get; }
}
