using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Real package-writer merge round trips. These tests deliberately use only the existing
// producer fixture and the public reader/resolver path after the owner is reopened.
public sealed class NativeMediaMergeRoundtripTests
{
    [Fact]
    public async Task OverlappingPostsMergeIntoThirtyPostsAndResolveAfterOwnerRestart()
    {
        using Workspace files = new();
        ArchiveThreadSummary beforeSummary;
        string primaryRoot;
        Dictionary<string, string> primaryRootBefore;
        NativeMediaMergeResult committed;

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedTarget(lease, catalog, files, 1, 20);

            await using (ArchiveReader beforeReader = await ArchiveReader.OpenAsync(files.Profile))
            {
                ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await beforeReader.ReadThreadAsync(Id));
                beforeSummary = before.Summary;
                primaryRoot = Assert.Single(before.MediaDirectories);
                primaryRootBefore = Files(primaryRoot);
            }

            await using NativeTransferImportStage stage = await Stage(files, ContentForPosts(15, 30));
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);

            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
            committed = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            Assert.False(committed.IsReplay);
            Assert.Equal(10, committed.NewPosts);
            Assert.Equal(0, committed.UpdatedPosts);
            Assert.Equal(10, committed.NewAttachments);
            Assert.Equal(10, committed.NewBindings);
            Assert.Equal(0, committed.Conflicts);
        }

        // The normal reader is reopened after both the catalog and its profile lease have closed.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(beforeSummary with { PostCount = 30, Revision = beforeSummary.Revision + 1 }, after.Summary);
        Assert.Equal(beforeSummary.MediaDirectory, after.Summary.MediaDirectory);
        Assert.Equal(beforeSummary.SourceState, after.Summary.SourceState);
        Assert.Equal(beforeSummary.CaptureEnabled, after.Summary.CaptureEnabled);
        Assert.Equal(beforeSummary.FirstSeenAt, after.Summary.FirstSeenAt);
        Assert.Equal(beforeSummary.LastCapturedAt, after.Summary.LastCapturedAt);
        Assert.Equal(beforeSummary.PostCount + 10, after.PostCount);
        Assert.Equal(2, after.MediaDirectories.Count);
        Assert.Contains(primaryRoot, after.MediaDirectories);
        Assert.Equal(primaryRootBefore, Files(primaryRoot));

        IReadOnlyList<CapturedPost> posts = after.GetPosts(0, after.PostCount);
        Assert.Equal(Enumerable.Range(1, 30).Select(post => post.ToString()), posts.Select(post => post.PostId));
        Assert.Equal(30, posts.SelectMany(post => post.Attachments ?? Array.Empty<CapturedAttachment>())
            .Select(attachment => attachment.AttachmentRevisionKey)
            .Distinct(StringComparer.Ordinal).Count());

        MediaAssetSnapshot[] snapshots = await OpenSnapshots(after.MediaDirectories);
        foreach (CapturedPost post in posts)
            await AssertResolvedBytes(post, snapshots);
    }

    [Fact]
    public async Task SharedPayloadForExistingAndNewPostKeepsDistinctLogicalRevisionAndOldIdentity()
    {
        using Workspace files = new();
        string oldRoot;
        Dictionary<string, string> oldRootBefore;
        MediaAssetReference oldAsset;
        MediaAttachmentBinding oldBinding;
        NativeMediaMergeResult committed;

        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await SeedTarget(lease, catalog, files, 15, 15);

            await using (ArchiveReader beforeReader = await ArchiveReader.OpenAsync(files.Profile))
            {
                ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await beforeReader.ReadThreadAsync(Id));
                oldRoot = Assert.Single(before.MediaDirectories);
                oldRootBefore = Files(oldRoot);
                MediaAssetSnapshot oldSnapshot = await MediaAssetReader.OpenAsync(oldRoot);
                CapturedAttachment oldAttachment = Assert.Single(before.Posts.Single().Attachments!);
                ResolvedMediaAttachment oldResolved = Assert.IsType<ResolvedMediaAttachment>(
                    MediaAttachmentResolver.Resolve([oldSnapshot], Id, "15", oldAttachment));
                oldAsset = oldResolved.Asset;
                oldBinding = Assert.IsType<MediaAttachmentBinding>(oldResolved.Binding);
            }

            await using NativeTransferImportStage stage = await Stage(files, ContentForPostIds(15, 17));
            NativeMediaMergePreparation prepared = await catalog.PrepareNativeMediaMergeAsync(stage);
            Assert.Equal(NativeMediaMergeError.None, prepared.Error);
            NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(prepared.Plan);

            using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
            committed = await CommitMergeWithExclusiveAsync(admission, catalog, plan, ProfileExclusiveDisposition.SafeToResume);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            Assert.Equal(1, committed.NewPosts);
            Assert.Equal(0, committed.UpdatedPosts);
            Assert.Equal(1, committed.NewAttachments);
            Assert.Equal(1, committed.NewBindings);
            Assert.Equal(0, committed.Conflicts);
        }

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        MediaAssetSnapshot[] snapshots = await OpenSnapshots(after.MediaDirectories);

        CapturedAttachment existing = Assert.Single(after.Posts.Single(post => post.PostId == "15").Attachments!);
        ResolvedMediaAttachment resolvedExisting = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, "15", existing));
        Assert.Equal(oldAsset, resolvedExisting.Asset);
        Assert.Equal(oldAsset.Path, resolvedExisting.Asset.Path);
        Assert.Equal(oldBinding, resolvedExisting.Binding);
        Assert.Equal(oldRootBefore, Files(oldRoot));

        CapturedAttachment added = Assert.Single(after.Posts.Single(post => post.PostId == "17").Attachments!);
        ResolvedMediaAttachment resolvedAdded = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, "17", added));
        Assert.NotEqual(existing.AttachmentRevisionKey, added.AttachmentRevisionKey);
        Assert.NotEqual(oldAsset.AssetId, resolvedAdded.Asset.AssetId);
        Assert.NotEqual(oldAsset.Path, resolvedAdded.Asset.Path);
        Assert.NotEqual(oldBinding, resolvedAdded.Binding);
        await AssertBytes(resolvedExisting);
        await AssertBytes(resolvedAdded);
    }

    private static NativeTransferCatalog ContentForPosts(int firstPost, int lastPost)
        => ContentForPostIds(Enumerable.Range(firstPost, lastPost - firstPost + 1).ToArray());

    private static NativeTransferCatalog ContentForPostIds(params int[] postNumbers)
    {
        ImmutableArray<TransferPost> posts = postNumbers
            .Select(postNumber =>
            {
                string postId = postNumber.ToString();
                TransferAttachment attachment = Attachment(postId: postId);
                CapturedPost metadata = new(postId, postNumber, PostFieldPresence.All, At, "", "",
                    $"roundtrip-post-{postId}", false, [attachment.Metadata]);
                return new TransferPost(metadata, [attachment]);
            })
            .ToImmutableArray();

        TransferThread thread = new(Id, Url, "synthetic-media-import-needle", ThreadSourceState.NotFound,
            At, At, 9, false, posts);
        return new NativeTransferCatalog(At, [thread]);
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

    private static async Task SeedTarget(ProfileLease lease, ArchiveCatalog catalog, Workspace files, int firstPost, int lastPost)
    {
        await using NativeTransferImportStage stage = await Stage(files, ContentForPosts(firstPost, lastPost));
        NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.None, prepared.Error);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        NativeMediaImportCommitResult committed = await CommitSeedWithExclusiveAsync(admission, catalog, plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
    }

    private static async Task<MediaAssetSnapshot[]> OpenSnapshots(IReadOnlyList<string> directories)
        => await Task.WhenAll(directories.Select(directory => MediaAssetReader.OpenAsync(directory)));

    private static async Task AssertResolvedBytes(CapturedPost post, IReadOnlyList<MediaAssetSnapshot> snapshots)
    {
        CapturedAttachment attachment = Assert.Single(post.Attachments!);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshots, Id, post.PostId, attachment));
        Assert.False(resolved.IsLegacy);
        await AssertBytes(resolved);
    }

    private static async Task AssertBytes(ResolvedMediaAttachment resolved)
    {
        await using Stream verified = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        using MemoryStream actual = new();
        await verified.CopyToAsync(actual);
        Assert.Equal(Bytes, actual.ToArray());
    }
}
