using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Separate internal existing-resource consumer; old new-thread operation/receipt semantics do not change.
internal enum NativeMediaMergeError { None, UnsupportedPackage, MissingTarget, TargetUnavailable, TargetBudgetExceeded, StaleTarget }
internal sealed record NativeMediaMergePreparation(NativeMediaMergePlan? Plan, NativeMediaMergeError Error);
internal sealed record NativeMediaMergeResult(NativeMediaImportCommitStatus Status, int NewPosts = 0,
    int UpdatedPosts = 0, int NewAttachments = 0, int NewBindings = 0, int Conflicts = 0,
    bool IsReplay = false, string? Diagnostic = null, int EvidenceAdditions = 0,
    int DerivedDescriptorAdditions = 0, int ContainedDerivedAdditions = 0);
internal sealed record NativeMediaMergeRoot(string Directory, string Generation);
internal sealed record NativeMediaMergeAddedAttachment(string PostId, CapturedAttachment Attachment);
internal sealed record NativeMediaMergeOperation(Guid OperationId, string RequestSha256, ResourceId Resource,
    string OperationsParent, NativeMediaImportOperationState State, string? FullDirectory, string? SafeDirectory,
    string? FullManifestGeneration, string? SafeManifestGeneration, ImmutableArray<byte> CatalogBytes,
    ImmutableArray<string> FullKeys, ImmutableArray<string> SafeKeys);
internal sealed record NativeMediaMergeOperationPage(ImmutableArray<NativeMediaMergeOperation> Items, Guid? NextOperationId);
internal sealed record NativeMediaMergeInspection(NativeMediaMergeOperation? Operation,
    NativeMediaImportFilesystemStatus FilesystemStatus, ImmutableArray<NativeMediaImportChildInspection> Children,
    NativeMediaMergeResult? Receipt = null);

internal sealed class NativeMediaMergePlan
{
    internal NativeMediaMergePlan(ArchiveCatalog owner, NativeTransferImportStage stage, string instance, long generation,
        ArchivedThreadView target, ImmutableArray<NativeMediaMergeRoot> roots,
        ImmutableArray<NativeMediaImportAttachment> inputs, ImmutableArray<CapturedPost> added,
        ImmutableArray<CapturedPost> updated, ImmutableArray<NativeMediaMergeAddedAttachment> attachments,
        ImmutableArray<string> safeKeys, int conflicts, Guid operation, string request, string parent,
        NativeTransferPackageWriter.TransferPackageBudget exportBudget)
    {
        Owner = owner; Stage = stage; Package = stage.Package; Instance = instance; Generation = generation;
        Target = target; Roots = roots; _legacyInputs = inputs; AddedPosts = added; UpdatedPosts = updated;
        AddedAttachments = attachments; _legacySafeKeys = safeKeys; Conflicts = conflicts;
        OperationId = operation; RequestSha256 = request; OperationsParent = parent; ImportedAtUtc = DateTimeOffset.UtcNow;
        ExportBudget = exportBudget;
        _legacyFullKeys = inputs.Select(a => a.RevisionKey).OrderBy(k => k, StringComparer.Ordinal).ToImmutableArray();
        CatalogBytes = ImmutableArray.CreateRange(NativeTransferCatalogSerializer.Serialize(Package.Catalog));
        RootsJson = JsonSerializer.Serialize(roots);
        _legacyFullKeysJson = JsonSerializer.Serialize(FullKeys); _legacySafeKeysJson = JsonSerializer.Serialize(SafeKeys);
    }
    internal NativeMediaMergePlan(ArchiveCatalog owner, NativeMediaMaterializationPlan materialization,
        string instance, long generation, ArchivedThreadView target, ImmutableArray<NativeMediaMergeRoot> roots,
        ImmutableArray<CapturedPost> added, ImmutableArray<CapturedPost> updated,
        ImmutableArray<NativeMediaMergeAddedAttachment> attachments, NativeMediaCandidateSelection safeSelection,
        NativeMediaMergeCounts counts, Guid operation, string request, string parent,
        NativeTransferPackageWriter.TransferPackageBudget exportBudget, DateTimeOffset? importedAtUtc = null,
        ImmutableArray<NativeMediaConflictEntry> conflictLedger = default)
    {
        ConflictLedger = conflictLedger.IsDefault ? ImmutableArray<NativeMediaConflictEntry>.Empty : conflictLedger;
        Owner = owner; Stage = materialization.Stage; Package = materialization.Package;
        Instance = instance; Generation = generation; Target = target; Roots = roots;
        AddedPosts = added; UpdatedPosts = updated; AddedAttachments = attachments;
        Conflicts = counts.Conflicts; OperationId = operation; RequestSha256 = request;
        OperationsParent = parent; ImportedAtUtc = importedAtUtc ?? DateTimeOffset.UtcNow; ExportBudget = exportBudget;
        CatalogBytes = Package.Envelope.CatalogBytes; RootsJson = JsonSerializer.Serialize(roots);
        Representation = new(materialization, safeSelection,
            new(2, operation, request, Package.Envelope.Fingerprint, instance, generation,
                materialization.Resource, parent, ImportedAtUtc), roots, counts);
        if (counts.NewPosts != added.Length || counts.UpdatedPosts != updated.Length
            || counts.NewAttachments != NewAttachmentCount)
            throw new System.IO.InvalidDataException("The merge counters differ from the projected changes.");
    }
    private readonly ImmutableArray<NativeMediaImportAttachment> _legacyInputs;
    private readonly ImmutableArray<string> _legacyFullKeys, _legacySafeKeys;
    private readonly string? _legacyFullKeysJson, _legacySafeKeysJson;
    internal int PlanVersion => Representation is null ? 1 : 2;
    internal NativeMediaMergeV2State? Representation { get; }
    internal bool HasSafeCandidate => Representation is { } v2 ? !v2.SafeSelection.IsEmpty : !SafeKeys.IsEmpty;
    internal int NewBindingCount => Representation?.Counts.NewBindings ?? SafeKeys.Length;
    internal ArchiveCatalog Owner { get; }
    internal NativeTransferImportStage Stage { get; }
    internal NativeTransferPackage Package { get; }
    internal TransferThread Thread => Package.Catalog.Threads[0];
    internal ResourceId Resource => Thread.Id;
    internal string Instance { get; }
    internal long Generation { get; }
    internal ArchivedThreadView Target { get; }
    internal ImmutableArray<NativeMediaMergeRoot> Roots { get; }
    internal ImmutableArray<NativeMediaImportAttachment> Inputs => PlanVersion == 1 ? _legacyInputs
        : throw new InvalidOperationException("Versioned merges use semantic members, not legacy inputs.");
    internal ImmutableArray<CapturedPost> AddedPosts { get; }
    internal ImmutableArray<CapturedPost> UpdatedPosts { get; }
    internal ImmutableArray<NativeMediaMergeAddedAttachment> AddedAttachments { get; }
    internal ImmutableArray<string> FullKeys => PlanVersion == 1 ? _legacyFullKeys
        : throw new InvalidOperationException("Versioned merges use semantic full selection.");
    internal ImmutableArray<string> SafeKeys => PlanVersion == 1 ? _legacySafeKeys
        : throw new InvalidOperationException("Versioned merges use semantic safe selection.");
    internal ImmutableArray<byte> CatalogBytes { get; }
    internal string RootsJson { get; }
    internal string FullKeysJson => _legacyFullKeysJson
        ?? throw new InvalidOperationException("Versioned merges have no legacy full keys.");
    internal string SafeKeysJson => _legacySafeKeysJson
        ?? throw new InvalidOperationException("Versioned merges have no legacy safe keys.");
    internal int Conflicts { get; }
    /// <summary>Byte-level alternatives recorded in the conflict ledger when this merge commits.</summary>
    internal ImmutableArray<NativeMediaConflictEntry> ConflictLedger { get; } = ImmutableArray<NativeMediaConflictEntry>.Empty;
    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string OperationsParent { get; }
    internal DateTimeOffset ImportedAtUtc { get; }
    internal NativeTransferPackageWriter.TransferPackageBudget ExportBudget { get; }
    internal int NewAttachmentCount => AddedPosts.Sum(p => p.Attachments?.Count ?? 0) + AddedAttachments.Length;
    internal bool ReuseFullAsSafe => Representation is { } v2 ? v2.ReuseFullAsSafe
        : Conflicts == 0 && FullKeys.SequenceEqual(SafeKeys, StringComparer.Ordinal);
}
