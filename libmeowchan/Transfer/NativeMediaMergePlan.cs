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
    bool IsReplay = false, string? Diagnostic = null);
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
        Target = target; Roots = roots; Inputs = inputs; AddedPosts = added; UpdatedPosts = updated;
        AddedAttachments = attachments; SafeKeys = safeKeys; Conflicts = conflicts;
        OperationId = operation; RequestSha256 = request; OperationsParent = parent; ImportedAtUtc = DateTimeOffset.UtcNow;
        ExportBudget = exportBudget;
        FullKeys = inputs.Select(a => a.RevisionKey).OrderBy(k => k, StringComparer.Ordinal).ToImmutableArray();
        CatalogBytes = ImmutableArray.CreateRange(NativeTransferCatalogSerializer.Serialize(Package.Catalog));
        RootsJson = JsonSerializer.Serialize(roots);
        FullKeysJson = JsonSerializer.Serialize(FullKeys); SafeKeysJson = JsonSerializer.Serialize(SafeKeys);
    }
    internal ArchiveCatalog Owner { get; }
    internal NativeTransferImportStage Stage { get; }
    internal NativeTransferPackage Package { get; }
    internal TransferThread Thread => Package.Catalog.Threads[0];
    internal ResourceId Resource => Thread.Id;
    internal string Instance { get; }
    internal long Generation { get; }
    internal ArchivedThreadView Target { get; }
    internal ImmutableArray<NativeMediaMergeRoot> Roots { get; }
    internal ImmutableArray<NativeMediaImportAttachment> Inputs { get; }
    internal ImmutableArray<CapturedPost> AddedPosts { get; }
    internal ImmutableArray<CapturedPost> UpdatedPosts { get; }
    internal ImmutableArray<NativeMediaMergeAddedAttachment> AddedAttachments { get; }
    internal ImmutableArray<string> FullKeys { get; }
    internal ImmutableArray<string> SafeKeys { get; }
    internal ImmutableArray<byte> CatalogBytes { get; }
    internal string RootsJson { get; }
    internal string FullKeysJson { get; }
    internal string SafeKeysJson { get; }
    internal int Conflicts { get; }
    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string OperationsParent { get; }
    internal DateTimeOffset ImportedAtUtc { get; }
    internal NativeTransferPackageWriter.TransferPackageBudget ExportBudget { get; }
    internal int NewAttachmentCount => AddedPosts.Sum(p => p.Attachments?.Count ?? 0) + AddedAttachments.Length;
    internal bool ReuseFullAsSafe => Conflicts == 0 && FullKeys.SequenceEqual(SafeKeys, StringComparer.Ordinal);
}
