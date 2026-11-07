using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Multiple resources in one profile, deliberately using the supported single-resource
// producers serially. A multi-thread package is valid transport, NOT import authority.
public sealed class NativeTransferMultiThreadMergeRegressionTests
{
    private static readonly ResourceId Other = new("4chan", "b", "7002");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollidingPostAndMediaIdsStayThreadScopedAcross404UnionReplayReopenAndWireRoundtrip(bool reverse)
    {
        using Workspace files = new();
        ResourceId[] order = reverse ? [Other, Id] : [Id, Other];
        Dictionary<ResourceId, ArchiveThreadSummary> summaries = new();
        Dictionary<ResourceId, ResolvedMediaAttachment> originals = new();
        Dictionary<string, Dictionary<string, string>> roots = new(StringComparer.Ordinal);
        Dictionary<ResourceId, NativeMediaMergePlan> plans = new();
        Dictionary<ResourceId, NativeMediaMergeResult> results = new();

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            foreach (ResourceId id in order)
            {
                await using NativeTransferImportStage seed = await Stage(files, Content(id, false, 1, 2));
                NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(seed);
                Assert.Equal(NativeMediaImportError.None, prepared.Error);
                using (ProfileAdmissionToken seedAdmission = await EnterExclusiveAsync(lease))
                {
                    Assert.Equal(NativeMediaImportCommitStatus.Committed,
                        (await CommitSeedWithExclusiveAsync(seedAdmission, catalog,
                            Assert.IsType<NativeMediaImportPlan>(prepared.Plan))).Status);
                } // Ordinary capture configuration is deliberately outside Exclusive.
                await catalog.SetCaptureEnabledAsync(id, id == Other);
            }
            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile))
            {
                foreach (ResourceId id in order)
                {
                    ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(id));
                    summaries.Add(id, view.Summary);
                    originals.Add(id, await Resolve(view, id, "2"));
                    foreach (string root in view.MediaDirectories) roots.Add(root, Files(root));
                }
            }
            Assert.NotEqual(originals[Id].Binding!.AcquisitionRevisionKey,
                originals[Other].Binding!.AcquisitionRevisionKey);
            Assert.NotEqual(originals[Id].Snapshot.Directory, originals[Other].Snapshot.Directory);

            foreach (ResourceId id in order)
            {
                // Same post 2, slot 0, media 111, URL and bytes in both resources;
                // post 3 belongs ONLY to 7001, post 4 ONLY to 7002.
                await using NativeTransferImportStage incoming = await Stage(files,
                    Content(id, true, 2, id == Id ? 3 : 4));
                NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(incoming);
                Assert.Equal(NativeMediaMergeError.None, prepared.Error);
                NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);
                NativeMediaMergeResult result = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
                Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
                Assert.False(result.IsReplay);
                Assert.Equal((1, 0, 1, 1, 1),
                    (result.NewPosts, result.UpdatedPosts, result.NewAttachments, result.NewBindings, result.Conflicts));
                plans.Add(id, plan);
                results.Add(id, result);
            }
            // Both stages have been disposed; receipt replay must not recopy either input.
            Dictionary<string, string> beforeReplay = Files(files.Profile);
            foreach (ResourceId id in order)
                Assert.Equal(results[id] with { IsReplay = true },
                    await CommitMergeWithExclusiveAsync(admission, catalog, plans[id], ProfileExclusiveDisposition.SafeToResume));
            Assert.Equal(beforeReplay, Files(files.Profile));
        }

        // New writing owner and lease: historical receipts survive independently of stages.
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            foreach (ResourceId id in order)
            {
                Assert.Equal(results[id] with { IsReplay = true },
                    await reopened.ReadNativeMediaMergeReceiptAsync(plans[id].OperationId, plans[id].RequestSha256));
                await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
                ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(id));
                // New operation for identical content: logical idempotence, not receipt replay.
                // An inactive full candidate is allowed by the existing journal contract.
                await using NativeTransferImportStage same = await Stage(files,
                    Content(id, false, 1, 2, id == Id ? 3 : 4));
                NativeMediaMergePreparation prepared = await reopened.PrepareNativeMediaMergeAsync(same);
                Assert.Equal(NativeMediaMergeError.None, prepared.Error);
                using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
                NativeMediaMergeResult noOp = await CommitMergeWithExclusiveAsync(admission, reopened,
                    Assert.IsType<NativeMediaMergePlan>(prepared.Plan), ProfileExclusiveDisposition.SafeToResume);
                Assert.Equal(NativeMediaImportCommitStatus.Committed, noOp.Status);
                Assert.False(noOp.IsReplay);
                Assert.Equal((0, 0, 0, 0, 0),
                    (noOp.NewPosts, noOp.UpdatedPosts, noOp.NewAttachments, noOp.NewBindings, noOp.Conflicts));
                ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(id));
                Assert.Equal(before.Summary, after.Summary);
                Assert.Equal(before.MediaDirectories.ToArray(), after.MediaDirectories.ToArray());
            }
        }

        // Offline reader after all writers/leases/stages closed. Resolve against the UNION
        // of both threads' roots too, so wrong-resource bindings cannot hide behind filtering.
        await using ArchiveReader offline = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView[] views = await Task.WhenAll(order.Select(async id =>
            Assert.IsType<ArchivedThreadView>(await offline.ReadThreadAsync(id))));
        MediaAssetSnapshot[] allRoots = await Task.WhenAll(views.SelectMany(v => v.MediaDirectories)
            .Select(root => MediaAssetReader.OpenAsync(root)));
        List<TransferThread> exported = [];
        foreach (ResourceId id in order)
        {
            ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await offline.ReadThreadAsync(id));
            Assert.Equal(summaries[id] with { PostCount = 3, Revision = summaries[id].Revision + 1 }, view.Summary);
            Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
            Assert.Equal(id == Other, view.Summary.CaptureEnabled);
            Assert.True(view.DecimalPostOrder == true);
            string[] expected = id == Id ? ["1", "2", "3"] : ["1", "2", "4"];
            Assert.Equal(expected, view.Posts.Select(p => p.PostId));
            Assert.Equal(2, view.MediaDirectories.Count);
            foreach (CapturedPost post in view.Posts)
            {
                Assert.Equal($"thread-{id.Thread}-post-{post.PostId}", post.Html);
                CapturedAttachment attachment = Assert.Single(post.Attachments!);
                Assert.Equal("111", attachment.MediaId);
                ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
                    MediaAttachmentResolver.Resolve(allRoots, id, post.PostId, attachment));
                Assert.Equal(id, resolved.Binding!.Reference.Resource);
                Assert.Equal(post.PostId, resolved.Binding.Reference.PostId);
                Assert.Null(MediaAttachmentResolver.Resolve(allRoots, id == Id ? Other : Id, post.PostId, attachment));
                await AssertBytes(resolved);
                if (post.PostId == "2")
                {
                    Assert.Equal(originals[id].Asset, resolved.Asset);
                    Assert.Equal(originals[id].Binding, resolved.Binding);
                    Assert.Equal(originals[id].Snapshot.Directory, resolved.Snapshot.Directory);
                }
            }

            TransferArchiveFreezeResult freeze = await NativeTransferArchiveExporter.FreezeAsync(offline, id, At.AddDays(1), Guid.NewGuid());
            Assert.True(freeze.IsFrozen, freeze.Error.ToString());
            using MemoryStream output = new();
            TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(
                Assert.IsType<NativeTransferArchiveSnapshot>(freeze.Snapshot), output);
            Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
            output.Position = 0;
            NativeTransferStageResult restaged = await NativeTransferImportStage.CreateAsync(output, files.StageRoot);
            Assert.Equal(NativeTransferStageError.None, restaged.Error);
            await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(restaged.Stage);
            TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
            Assert.Equal(id, thread.Id);
            Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
            Assert.Equal(summaries[id].Title, thread.Title);
            Assert.Equal(summaries[id].FirstSeenAt, thread.FirstSeenAtUtc);
            Assert.Equal(summaries[id].LastCapturedAt, thread.CapturedAtUtc);
            Assert.Equal(expected, thread.Posts.Select(p => p.Metadata.PostId));
            Assert.Equal(view.Posts.Select(p => p.Html), thread.Posts.Select(p => p.Metadata.Html));
            Assert.Empty(thread.RetainedBindings);
            exported.Add(thread);
            await AssertPayload(stage);
        }
        foreach (var root in roots) Assert.Equal(root.Value, Files(root.Key));

        // Real two-thread writer/validator/stage roundtrip, sharing ONE physical payload.
        // Do not split an admitted package behind the coordinator's back or invent a batch API.
        await using NativeTransferImportStage multi = await Stage(files, new(At.AddDays(1), exported.ToImmutableArray()));
        Assert.Equal(order, multi.Package.Catalog.Threads.Select(t => t.Id));
        Assert.Equal(6, multi.Package.Catalog.Threads.Sum(t => t.Posts.Length));
        Assert.Single(multi.Package.Envelope.Payloads);
        await AssertPayload(multi);
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            Dictionary<string, string> before = Files(files.Profile);
            NativeMediaImportPreparation import = await catalog.PrepareNativeMediaImportAsync(multi);
            Assert.Equal(NativeMediaImportError.UnsupportedPackage, import.Error);
            Assert.Null(import.Plan);
            NativeMediaMergePreparation merge = await catalog.PrepareNativeMediaMergeAsync(multi);
            Assert.Equal(NativeMediaMergeError.UnsupportedPackage, merge.Error);
            Assert.Null(merge.Plan);
            Assert.Equal(before, Files(files.Profile));
        }
    }

    private static NativeTransferCatalog Content(ResourceId id, bool incomingLive, params int[] numbers)
    {
        ImmutableArray<TransferPost> posts = numbers.Select(n =>
        {
            string postId = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TransferAttachment template = Attachment(postId: postId);
            CapturedAttachment metadata = template.Metadata;
            MediaProviderMetadata provider = new(metadata.MediaId!, metadata.Variant, metadata.Url!, metadata.OriginalFileName,
                metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256) { ExpectedSha1 = metadata.ExpectedSha1 };
            TransferAttachment attachment = template with { Metadata = metadata with {
                AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(id, postId, metadata.Position, provider) } };
            return new TransferPost(new(postId, n, PostFieldPresence.All, At, "", "",
                $"thread-{id.Thread}-post-{postId}", false, [attachment.Metadata]), [attachment]);
        }).ToImmutableArray();
        return new(At, [new(id, $"https://boards.4chan.org/b/thread/{id.Thread}",
            incomingLive ? "incoming live title" : $"local 404 {id.Thread}",
            incomingLive ? ThreadSourceState.Active : ThreadSourceState.NotFound,
            At, At, 9, true, posts)]);
    }

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

    private static async Task<ResolvedMediaAttachment> Resolve(ArchivedThreadView view, ResourceId id, string postId)
    {
        MediaAssetSnapshot[] snapshots = await Task.WhenAll(view.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));
        CapturedAttachment attachment = Assert.Single(view.Posts.Single(p => p.PostId == postId).Attachments!);
        return Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshots, id, postId, attachment));
    }

    private static async Task AssertBytes(ResolvedMediaAttachment resolved)
    {
        Assert.False(resolved.IsLegacy);
        await using Stream input = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new();
        await input.CopyToAsync(actual);
        Assert.Equal(Bytes, actual.ToArray());
    }

    private static async Task AssertPayload(NativeTransferImportStage stage)
    {
        using MemoryStream actual = new();
        await stage.CopyPayloadToAsync(Hash(Bytes), actual);
        Assert.Equal(Bytes, actual.ToArray());
    }
}
