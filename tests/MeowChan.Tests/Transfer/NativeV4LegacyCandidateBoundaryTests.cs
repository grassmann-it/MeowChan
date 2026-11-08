using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Journal1 compatibility fences only: real admitted v1 operations, interrupted before
// Prepared. A self-consistent inactive Journal2 sibling deliberately shares their exact
// provenance and original Bindings, so the legacy matcher must reject its richer identity.
// No synthetic SQL rows, active-root edits, v4 Apply, or public recovery acceptance.
public sealed class NativeV4LegacyCandidateBoundaryTests
{
    [Fact]
    public async Task ImportDiscoveryAndVerifierCannotProjectJournal2OntoJournal1()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var interrupted = await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore(),
            checkpoint: StopAtReady);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, interrupted.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, interrupted.Result.Status);
        Assert.Equal(nameof(IOException), interrupted.Result.Diagnostic);

        PassiveMediaImportCandidate legacy = await OnlyReadyLegacyAsync(plan.OperationsParent);
        PassiveMediaImportCandidate v2 = await AddReadyV2SiblingAsync(stage, legacy);
        Dictionary<string, string> before = Files(files.Profile);
        for (int observation = 0; observation < 2; observation++)
        {
            var inspection = await catalog.InspectNativeMediaImportOperationAsync(
                plan.OperationId, plan.RequestSha256, new MediaStore());
            Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, inspection.FilesystemStatus);
            Assert.Equal(NativeMediaImportOperationState.Preparing, inspection.Operation!.State);
            Assert.Null(inspection.Operation.CandidateDirectory);
            Assert.Null(inspection.Operation.ManifestGeneration);
            AssertDiscovery(inspection.Children, legacy, v2);
            Assert.Null(inspection.Receipt);
        }
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile))
            Assert.Null(await reader.ReadThreadAsync(Id));

        await AssertVerifierBoundaryAsync(legacy, v2, (candidate, callback) => new MediaStore()
            .WithVerifiedPassiveImportCandidateAdmittedAsync(admission, plan, candidate, callback));
        Assert.Equal(before, Files(files.Profile)); // No repair, publication, or replacement child.
        Assert.Equal(2, Directory.GetDirectories(plan.OperationsParent).Length);
        catalog.EnsureTransferAdmission(admission);
    }

    [Fact]
    public async Task MergeDiscoveryAndVerifierCannotProjectJournal2OntoJournal1()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using (NativeTransferImportStage seedStage = await Stage(files, Content()))
        {
            NativeMediaImportPlan seed = Assert.IsType<NativeMediaImportPlan>(
                (await catalog.PrepareNativeMediaImportAsync(seedStage)).Plan);
            using ProfileAdmissionToken owner = await EnterExclusiveAsync(lease);
            var committed = await catalog.CommitNativeMediaImportAdmittedAsync(owner, seed, new MediaStore());
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        }
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Single(plan.FullKeys);
        Assert.Empty(plan.SafeKeys); // Same observation reuses the target's existing exact binding.
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var interrupted = await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(),
            checkpoint: StopAtReady);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, interrupted.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, interrupted.Result.Status);
        Assert.Equal(nameof(IOException), interrupted.Result.Diagnostic);

        PassiveMediaImportCandidate legacy = await OnlyReadyLegacyAsync(plan.OperationsParent);
        PassiveMediaImportCandidate v2 = await AddReadyV2SiblingAsync(stage, legacy);
        Dictionary<string, string> before = Files(files.Profile);
        for (int observation = 0; observation < 2; observation++)
        {
            var inspection = await catalog.InspectNativeMediaMergeOperationAsync(
                plan.OperationId, plan.RequestSha256, new MediaStore());
            Assert.Equal(NativeMediaImportFilesystemStatus.Inspected, inspection.FilesystemStatus);
            Assert.Equal(NativeMediaImportOperationState.Preparing, inspection.Operation!.State);
            Assert.Null(inspection.Operation.FullDirectory);
            Assert.Null(inspection.Operation.SafeDirectory);
            AssertDiscovery(inspection.Children, legacy, v2);
            Assert.Null(inspection.Receipt);
        }
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        MediaStore store = new();
        await AssertVerifierBoundaryAsync(legacy, v2, (candidate, callback) => store
            .WithPassiveMergeTargetRootsAdmittedAsync(admission, plan, snapshots =>
            {
                Assert.Equal(plan.Roots.Length, snapshots.Length);
                foreach (NativeMediaMergeRoot root in plan.Roots)
                    Assert.Equal(root.Generation,
                        Assert.Single(snapshots.Where(snapshot => snapshot.Directory == root.Directory)).Generation);
                return store.WithVerifiedPassiveMergeCandidatesAdmittedAsync(
                    admission, plan, candidate, null, callback);
            }));
        Assert.Equal(before, Files(files.Profile)); // Includes the original target, SQL, and both inactive roots.
        Assert.Equal(2, Directory.GetDirectories(plan.OperationsParent).Length);
        catalog.EnsureTransferAdmission(admission);
    }

    private static void StopAtReady(NativeMediaImportPhase phase)
    {
        if (phase == NativeMediaImportPhase.CandidateReady)
            throw new IOException("synthetic stop before Prepared");
    }

    private static async Task<PassiveMediaImportCandidate> OnlyReadyLegacyAsync(string parent)
    {
        string path = Assert.Single(Directory.GetDirectories(parent));
        var inspected = await new MediaStore().InspectPassiveImportCandidateAsync(path);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        var candidate = Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate);
        Assert.True(candidate.IsLegacyCandidate);
        Assert.Single(candidate.Bindings);
        return candidate;
    }

    private static void AssertDiscovery(ImmutableArray<NativeMediaImportChildInspection> children,
        PassiveMediaImportCandidate legacy, PassiveMediaImportCandidate v2)
    {
        Assert.Equal(2, children.Length);
        Assert.All(children, child => Assert.Equal(PassiveMediaCandidateStatus.Ready, child.Status));
        Assert.True(Assert.Single(children.Where(child => child.Path == legacy.Directory)).MatchesOperation);
        Assert.False(Assert.Single(children.Where(child => child.Path == v2.Directory)).MatchesOperation);
    }

    private static async Task AssertVerifierBoundaryAsync(PassiveMediaImportCandidate legacy,
        PassiveMediaImportCandidate v2, Func<PassiveMediaImportCandidate, Func<Task<bool>>, Task<bool>> verify)
    {
        int accepted = 0;
        Assert.True(await verify(legacy, () => { accepted++; return Task.FromResult(true); }));
        Assert.Equal(1, accepted); // A real Journal1 still verifies under the genuine borrowed owner.
        // DTO contamination uses the unchanged valid Journal1 path/generation/bindings. These
        // would reach the callback without the explicit fence, unlike a mere v2 JSON failure.
        PassiveMediaImportCandidate[] rejected =
        [
            v2,
            legacy with { PlanVersion = 2 },
            legacy with { PlanVersion = 999 },
            legacy with { Selection = v2.Selection },
            legacy with { InventorySha256 = v2.InventorySha256 },
            legacy with { OriginalEvidence = default },
            legacy with { OriginalEvidence = v2.OriginalEvidence },
            legacy with { DerivedRepresentations = default },
            legacy with { DerivedRepresentations = v2.DerivedRepresentations }
        ];
        foreach (PassiveMediaImportCandidate candidate in rejected)
        {
            Assert.False(candidate.IsLegacyCandidate);
            int callbacks = 0;
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => verify(candidate,
                () => { callbacks++; return Task.FromResult(true); }));
            Assert.Contains("original-only", error.Message);
            Assert.Contains("representation candidates", error.Message);
            Assert.Equal(0, callbacks);
        }
    }

    private static async Task<PassiveMediaImportCandidate> AddReadyV2SiblingAsync(
        NativeTransferImportStage stage, PassiveMediaImportCandidate legacy)
    {
        TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
        TransferPost post = Assert.Single(thread.Posts.Where(item => !item.Attachments.IsEmpty));
        TransferAttachment attachment = Assert.Single(post.Attachments);
        TransferMeasuredBlob measured = stage.Package.Envelope.MeasuredPayloads[attachment.PayloadSha256!];
        MediaOccurrence occurrence = new(thread.Id, post.Metadata.PostId, attachment.Metadata.Position,
            attachment.Metadata.AttachmentRevisionKey);
        TransferOriginalByteVersion version = new(measured.Length, measured.Sha256, measured.Md5, measured.Sha1);
        NativeMediaMemberId evidenceId = new(occurrence, version, NativeMediaMemberRole.OriginalEvidence);
        NativeMediaMemberDescriptor evidence = new(evidenceId, attachment.Metadata, false,
            TransferContentStatus.Contained, measured.Sha256, attachment.History, measured);
        byte[] preview = [7, 11, 13, 17]; // Passive preview bytes; no image decoder claim.
        TransferMeasuredBlob previewMeasured = new(preview.Length, Hash(preview), Md5(preview), Sha1(preview));
        NativeMediaCandidateSelection selection = NativeMediaCandidateSelection.Create(
        [
            new(evidence, false),
            new(evidence with { Id = evidenceId with { Role = NativeMediaMemberRole.OriginalPayload } }, false),
            new(new(evidenceId with { Role = NativeMediaMemberRole.Preview }, attachment.Metadata, false,
                TransferContentStatus.Contained, previewMeasured.Sha256, null, previewMeasured), false)
        ]);
        NativeMediaCandidateInventory inventory = selection.CreateInventory(
            legacy.OperationId, legacy.PackageFingerprint, legacy.ImportedAtUtc);
        Assert.Equal(legacy.Bindings.ToArray(), inventory.Bindings.ToArray());
        Assert.Single(inventory.OriginalEvidence);
        Assert.Equal(MediaDerivedStatus.Contained, Assert.Single(inventory.DerivedRepresentations).IncomingStatus);
        Assert.Equal(2, inventory.Payloads.Length);
        // Fault-injection fixture: clone only this real inactive child, then bind every v2
        // semantic field and Manifest6 generation consistently. No SQL rewriting or fake
        // inspector response. Identical fingerprint/keys/bindings is the regression trigger.
        string sibling = Path.Combine(Path.GetDirectoryName(legacy.Directory)!,
            ".meowchan-passive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        foreach (string path in Directory.GetFiles(legacy.Directory))
            File.Copy(path, Path.Combine(sibling, Path.GetFileName(path)));
        await File.WriteAllBytesAsync(Path.Combine(sibling, previewMeasured.Sha256 + ".derived.bin"), preview);
        string manifestPath = Path.Combine(sibling, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["Version"] = 6;
        manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(inventory.OriginalEvidence.ToArray());
        manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(inventory.DerivedRepresentations.ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await File.WriteAllBytesAsync(manifestPath, bytes);
        var journal = new
        {
            Version = 2, Phase = "ready", OperationId = legacy.OperationId.ToString("D"),
            legacy.PackageFingerprint, legacy.Resource, legacy.ImportedAtUtc,
            SelectionJson = selection.CanonicalJson, SelectionSha256 = selection.Sha256,
            InventorySha256 = inventory.Sha256, ManifestGeneration = Hash(bytes)
        };
        await File.WriteAllBytesAsync(Path.Combine(sibling, ".meowchan-import-candidate.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal));
        Dictionary<string, string> before = Files(sibling);
        var inspected = await new MediaStore().InspectPassiveImportCandidateAsync(sibling);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status); // Not a malformed negative fixture.
        var result = Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate);
        Assert.Equal(2, result.PlanVersion);
        Assert.False(result.IsLegacyCandidate);
        Assert.Equal(legacy.OperationId, result.OperationId);
        Assert.Equal(legacy.Resource, result.Resource);
        Assert.Equal(legacy.PackageFingerprint, result.PackageFingerprint);
        Assert.Equal(legacy.ImportedAtUtc, result.ImportedAtUtc);
        Assert.Equal(legacy.Bindings.ToArray(), result.Bindings.ToArray());
        Assert.Equal(selection.CanonicalJson, result.Selection!.CanonicalJson);
        Assert.Equal(inventory.Sha256, result.InventorySha256);
        Assert.Single(result.OriginalEvidence);
        Assert.Single(result.DerivedRepresentations);
        Assert.Equal(before, Files(sibling));
        return result;
    }

    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }
}
