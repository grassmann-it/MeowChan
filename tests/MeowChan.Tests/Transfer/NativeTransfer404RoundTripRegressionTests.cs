using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransfer404RoundTripRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping404UnionReexportsAfterOfflineReopenWithoutReplacingRetainedIdentity(bool captureEnabled)
    {
        using Workspace files = new();
        ArchiveThreadSummary beforeSummary;
        Dictionary<string, Dictionary<string, string>> rootsBefore;
        MediaAssetReference retainedAsset;
        MediaAttachmentBinding retainedBinding;
        string retainedRoot;
        CapturedAttachment retainedMetadata = Attachment(postId: "900").Metadata;
        TransferRetainedBinding retained = new("900", new(retainedMetadata, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(Bytes), new("strict", At.AddDays(-2), false, false)));
        NativeTransferPackageWriter.TransferPackageBudget budget;

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using (NativeTransferImportStage seed = await Stage(files, ContentRange(1, 20)))
            {
                NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(seed);
                Assert.Equal(NativeMediaImportError.None, prepared.Error);
                using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
                Assert.Equal(NativeMediaImportCommitStatus.Committed,
                    (await CommitSeedWithExclusiveAsync(admission, catalog,
                        Assert.IsType<NativeMediaImportPlan>(prepared.Plan))).Status);
            }

            // Establish one genuinely historical target binding via the real passive producer;
            // new-thread import intentionally does not admit retained input. No fabricated SQL.
            NativeTransferCatalog history = ContentRange(1, 20);
            history = history with { Threads = [history.Threads[0] with { Posts = [], RetainedBindings = [retained] }] };
            await using (NativeTransferImportStage stage = await Stage(files, history))
            {
                NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
                Assert.Equal(NativeMediaMergeError.None, prepared.Error);
                using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
                Assert.Equal(NativeMediaImportCommitStatus.Committed,
                    (await CommitMergeWithExclusiveAsync(admission, catalog,
                        Assert.IsType<NativeMediaMergePlan>(prepared.Plan), ProfileExclusiveDisposition.SafeToResume)).Status);
            }
            await catalog.SetCaptureEnabledAsync(Id, captureEnabled);
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile))
            {
                ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
                beforeSummary = before.Summary;
                Assert.Equal(ThreadSourceState.NotFound, beforeSummary.SourceState);
                Assert.Equal(captureEnabled, beforeSummary.CaptureEnabled);
                rootsBefore = before.MediaDirectories.ToDictionary(root => root, Files, StringComparer.Ordinal);
                MediaAssetSnapshot[] snapshots = await Snapshots(before);
                ResolvedMediaAttachment resolved = await Resolve(snapshots, "900", retainedMetadata);
                retainedAsset = resolved.Asset;
                retainedBinding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
                retainedRoot = resolved.Snapshot.Directory;
            }

            NativeTransferCatalog incoming = ContentRange(15, 30);
            // An incoming live observation must not resurrect the local 404 or replace
            // its title/observation timestamps/nullable ordering contract.
            incoming = incoming with { Threads = [incoming.Threads[0] with {
                SourceState = ThreadSourceState.Active, Title = "incoming live title",
                FirstSeenAtUtc = At.AddDays(-10), CapturedAtUtc = At.AddDays(10), DecimalPostOrder = false
            }] };
            await using NativeTransferImportStage input = await Stage(files, incoming);
            NativeMediaMergePreparation preparation = await catalog.PrepareNativeMediaMergeAsync(input);
            Assert.Equal(NativeMediaMergeError.None, preparation.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
            budget = plan.ExportBudget;
            using ProfileAdmissionToken mergeAdmission = await EnterExclusiveAsync(lease);
            NativeMediaMergeResult result = await CommitMergeWithExclusiveAsync(mergeAdmission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
            Assert.Equal(10, result.NewPosts);
            Assert.Equal(0, result.UpdatedPosts);
            Assert.Equal(10, result.NewAttachments);
            Assert.Equal(10, result.NewBindings);
            // The incoming live observation conflicts with the existing local 404
            // metadata. It must be reported while the additive post/media union
            // still commits and the target's NotFound state remains authoritative.
            Assert.Equal(1, result.Conflicts);
        }

        // Neither a provider nor the writing owner/lease/stage is alive here.
        await using ArchiveReader reopened = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reopened.ReadThreadAsync(Id));
        Assert.Equal(beforeSummary with { PostCount = 30, Revision = beforeSummary.Revision + 1 }, after.Summary);
        Assert.True(after.DecimalPostOrder == true);
        Assert.Equal(Enumerable.Range(1, 30).Select(n => n.ToString()), after.Posts.Select(post => post.PostId));
        Assert.DoesNotContain(after.Posts, post => post.PostId == "900");
        Assert.Equal(rootsBefore.Count + 1, after.MediaDirectories.Count);
        foreach (var root in rootsBefore)
        {
            Assert.Contains(root.Key, after.MediaDirectories);
            Assert.Equal(root.Value, Files(root.Key));
        }
        MediaAssetSnapshot[] active = await Snapshots(after);
        foreach (CapturedPost post in after.Posts)
            await Resolve(active, post.PostId, Assert.Single(post.Attachments!));
        ResolvedMediaAttachment old = await Resolve(active, "900", retainedMetadata);
        Assert.Equal(retainedAsset, old.Asset);
        Assert.Equal(retainedBinding, old.Binding);
        Assert.Equal(retainedRoot, old.Snapshot.Directory);

        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reopened, Id, At.AddDays(1), Guid.NewGuid());
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = Assert.IsType<NativeTransferArchiveSnapshot>(frozen.Snapshot);
        using MemoryStream output = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        Assert.Equal(budget.ContainerBytes, output.Length);
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspection.IsPackageValid, inspection.EnvelopeError + "/" + inspection.CatalogError);
        NativeTransferPackage package = Assert.IsType<NativeTransferPackage>(inspection.Package);
        TransferThread exported = Assert.Single(package.Catalog.Threads);
        Assert.Equal(ThreadSourceState.NotFound, exported.SourceState);
        Assert.Equal(beforeSummary.Title, exported.Title);
        Assert.Equal(beforeSummary.FirstSeenAt, exported.FirstSeenAtUtc);
        Assert.Equal(beforeSummary.LastCapturedAt, exported.CapturedAtUtc);
        Assert.Equal(after.DecimalPostOrder, exported.DecimalPostOrder);
        Assert.Equal(after.Posts.Select(post => post.PostId), exported.Posts.Select(post => post.Metadata.PostId));
        Assert.Equal(retained, Assert.Single(exported.RetainedBindings));
        Assert.Equal(Hash(Bytes), Assert.Single(package.Envelope.Payloads).Sha256);
        // Re-admit the exported bytes and remeasure their payload, not just the DTO.
        output.Position = 0;
        NativeTransferStageResult restaged = await NativeTransferImportStage.CreateAsync(output, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, restaged.Error);
        await using NativeTransferImportStage finalStage = Assert.IsType<NativeTransferImportStage>(restaged.Stage);
        using MemoryStream payload = new();
        await finalStage.CopyPayloadToAsync(Hash(Bytes), payload);
        Assert.Equal(Bytes, payload.ToArray());
        foreach (var root in rootsBefore) Assert.Equal(root.Value, Files(root.Key));
    }

    private static NativeTransferCatalog ContentRange(int first, int last)
    {
        ImmutableArray<TransferPost> posts = Enumerable.Range(first, last - first + 1).Select(n =>
        {
            string id = n.ToString();
            TransferAttachment attachment = Attachment(postId: id);
            return new TransferPost(new(id, n, PostFieldPresence.All, At, "", "", $"post-{id}", false,
                [attachment.Metadata]), [attachment]);
        }).ToImmutableArray();
        return new(At, [new(Id, Url, "local 404", ThreadSourceState.NotFound, At, At, 9, true, posts)]);
    }

    private static Task<MediaAssetSnapshot[]> Snapshots(ArchivedThreadView view)
        => Task.WhenAll(view.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));

    // Direct synthetic storage owner via existing Core IVT, not public Registry startup.
    // Await initial store setup before Ready; retain the real token through reconciliation.
    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }

    private static async Task<NativeMediaMergeResult> CommitMergeWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaMergePlan plan,
        ProfileExclusiveDisposition expectedDisposition, Action<NativeMediaImportPhase>? checkpoint = null)
    {
        ProfileExclusiveOutcome<NativeMediaMergeResult> outcome =
            await catalog.CommitNativeMediaMergeAdmittedAsync(admission, plan, new MediaStore(), checkpoint: checkpoint);
        Assert.Equal(expectedDisposition, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // Borrowed authority survives all work and reconciliation.
        return outcome.Result;
    }

    private static async Task<NativeMediaImportCommitResult> CommitSeedWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaImportPlan plan)
    {
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome =
            await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission); // The consumer must leave its borrowed owner live.
        return outcome.Result;
    }

    private static async Task<ResolvedMediaAttachment> Resolve(IReadOnlyList<MediaAssetSnapshot> snapshots,
        string postId, CapturedAttachment metadata)
    {
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, postId, metadata));
        Assert.False(resolved.IsLegacy);
        await using Stream input = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new();
        await input.CopyToAsync(actual);
        Assert.Equal(Bytes, actual.ToArray());
        return resolved;
    }
}
