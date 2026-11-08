using System;
using System.Collections.Immutable;
using System.IO;
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
    int NewThreads = 0, int NewPosts = 0, int NewAttachments = 0, bool IsReplay = false, string? Diagnostic = null,
    int EvidenceAdditions = 0, int DerivedDescriptorAdditions = 0, int ContainedDerivedAdditions = 0,
    int OriginalBindingCount = 0);
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
        OperationId = operation; RequestSha256 = request; OperationsParent = parent; _legacyAttachments = attachments;
        ImportedAtUtc = DateTimeOffset.UtcNow;
        _legacyRevisionKeysJson = JsonSerializer.Serialize(attachments.Select(a => a.RevisionKey).OrderBy(k => k, StringComparer.Ordinal));
    }
    internal NativeMediaImportPlan(ArchiveCatalog owner, NativeMediaMaterializationPlan materialization,
        string instance, long generation, Guid operation, string request, string parent)
    {
        Owner = owner; Stage = materialization.Stage; Package = materialization.Package;
        Instance = instance; Generation = generation; OperationId = operation; RequestSha256 = request;
        OperationsParent = parent; ImportedAtUtc = DateTimeOffset.UtcNow;
        Representation = new(materialization, new(2, operation, request, Package.Envelope.Fingerprint,
            instance, generation, materialization.Resource, parent, ImportedAtUtc));
    }
    private readonly ImmutableArray<NativeMediaImportAttachment> _legacyAttachments;
    private readonly string? _legacyRevisionKeysJson;
    internal int PlanVersion => Representation is null ? 1 : 2;
    internal NativeMediaImportV2State? Representation { get; }
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
    internal ImmutableArray<NativeMediaImportAttachment> Attachments => PlanVersion == 1
        ? _legacyAttachments : throw new InvalidOperationException("Representation plans have semantic members, not legacy attachments.");
    internal string RevisionKeysJson => _legacyRevisionKeysJson
        ?? throw new InvalidOperationException("Representation plans have semantic members, not legacy keys.");
}

// The immutable v2 branch of the existing owner-issued handle. This object and persisted
// DTOs carry evidence only: the Catalog's exact issued-instance registry grants admission.
internal sealed class NativeMediaImportV2State
{
    internal NativeMediaImportV2State(NativeMediaMaterializationPlan materialization, NativeMediaPersistedIdentity identity)
    {
        if (identity.PlanVersion != 2 || materialization.Resource != identity.Resource
            || materialization.Package.Envelope.Fingerprint != identity.PackageFingerprint
            || materialization.FullSelection.IsEmpty)
            throw new InvalidDataException("Invalid representation import identity.");
        Materialization = materialization; Identity = identity;
        Inventory = Selection.CreateInventory(identity.OperationId, identity.PackageFingerprint, identity.ImportedAtUtc);
        TransferThread thread = materialization.Package.Catalog.Threads[0];
        Counts = new(thread.Posts.Length, thread.Posts.Sum(p => p.Attachments.Length),
            Inventory.OriginalEvidence.Length, Inventory.DerivedRepresentations.Length,
            Inventory.DerivedRepresentations.Count(d => d.IncomingStatus == MediaDerivedStatus.Contained), Inventory.Bindings.Length);
        // These are the authentic bytes validated by the retained stage, not a catalog
        // reconstructed from lossy selection descriptors or a fabricated envelope.
        CatalogBytes = materialization.Package.Envelope.CatalogBytes;
    }
    internal NativeMediaMaterializationPlan Materialization { get; }
    internal NativeMediaPersistedIdentity Identity { get; }
    internal NativeMediaImportCounts Counts { get; }
    internal ImmutableArray<byte> CatalogBytes { get; }
    internal NativeMediaCandidateSelection Selection => Materialization.FullSelection;
    internal NativeMediaCandidateInventory Inventory { get; }

    internal bool MatchesOperation(NativeMediaPersistedImportOperation operation, NativeMediaImportOperationState state)
    {
        NativeMediaPersistedRoot full = operation.Full;
        if (operation.Identity != Identity || operation.State != state || operation.Counts != Counts
            || !operation.CatalogBytes.AsSpan().SequenceEqual(CatalogBytes.AsSpan())
            || full.LegacyKeysJson != "[]" || !full.LegacyKeys.IsEmpty || full.LegacyBindingsSha256 != null
            || full.Selection?.CanonicalJson != Selection.CanonicalJson || full.SelectionSha256 != Selection.Sha256
            || full.MemberCount != Selection.Members.Length) return false;
        return state == NativeMediaImportOperationState.Preparing
            ? full.Directory == null && full.ManifestGeneration == null && full.InventorySha256 == null
            : full.Directory != null && full.ManifestGeneration is { Length: 64 } && full.InventorySha256 == Inventory.Sha256;
    }

    internal bool MatchesCandidate(PassiveMediaImportCandidate candidate)
        => candidate.PlanVersion == 2 && !candidate.IsLegacyCandidate
            && candidate.OperationId == Identity.OperationId && candidate.Resource == Identity.Resource
            && candidate.PackageFingerprint == Identity.PackageFingerprint && candidate.ImportedAtUtc == Identity.ImportedAtUtc
            && candidate.Selection?.CanonicalJson == Selection.CanonicalJson && candidate.Selection.Sha256 == Selection.Sha256
            && candidate.InventorySha256 == Inventory.Sha256
            && candidate.OriginalEvidence.SequenceEqual(Inventory.OriginalEvidence)
            && candidate.DerivedRepresentations.SequenceEqual(Inventory.DerivedRepresentations)
            && candidate.Bindings.SequenceEqual(Inventory.Bindings);

    internal NativeMediaImportCommitResult CommittedResult(bool replay = false)
        => new(NativeMediaImportCommitStatus.Committed, 1, Counts.PostCount, Counts.AttachmentCount, replay,
            EvidenceAdditions: Counts.EvidenceAdditions, DerivedDescriptorAdditions: Counts.DerivedDescriptorAdditions,
            ContainedDerivedAdditions: Counts.ContainedDerivedAdditions, OriginalBindingCount: Counts.OriginalBindingCount);
}
