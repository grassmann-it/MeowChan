using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Existing-resource merge coverage. The target is first created through the real passive
// media-import producer and is intentionally NotFound, so these tests exercise the 404 path
// without manufacturing SQL rows or media manifests.
public sealed class NativeMediaMergeTests
{
    [Fact]
    public async Task Existing404IdenticalMergeIsNoOpAndReplayable()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed404Target(lease, catalog, files);

        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));

        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult committed = await CommitMergeAsync(admission, catalog, plan);

        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
        Assert.False(committed.IsReplay);
        Assert.Equal(0, committed.NewPosts);
        Assert.Equal(0, committed.UpdatedPosts);
        Assert.Equal(0, committed.NewAttachments);
        Assert.Equal(0, committed.NewBindings);
        Assert.Equal(0, committed.Conflicts);

        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal(before.MediaDirectories, after.MediaDirectories);
        Assert.Equal(before.PostCount, after.PostCount);
        IReadOnlyList<CapturedPost> actualPosts = after.GetPosts(0, Math.Max(1, after.PostCount));
        foreach (CapturedPost post in before.GetPosts(0, Math.Max(1, before.PostCount)))
        {
            CapturedPost actual = Assert.Single(actualPosts, value => value.PostId == post.PostId);
            Assert.Equal(post with { Attachments = null }, actual with { Attachments = null });
            Assert.Equal(post.Attachments, actual.Attachments);
        }

        NativeMediaMergeResult replay = await CommitMergeAsync(admission, catalog, plan);
        Assert.Equal(committed with { IsReplay = true }, replay);
    }

    [Fact]
    public async Task Existing404ConflictKeepsTargetAndPersistsConflictReceipt()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed404Target(lease, catalog, files);

        NativeTransferCatalog conflicting = ContentWithChangedExistingPost("incoming-conflict");
        await using NativeTransferImportStage stage = await Stage(files, conflicting);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult committed = await CommitMergeAsync(admission, catalog, plan);

        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
        Assert.Equal(0, committed.NewPosts);
        Assert.Equal(0, committed.UpdatedPosts);
        Assert.Equal(0, committed.NewAttachments);
        Assert.Equal(0, committed.NewBindings);
        Assert.Equal(1, committed.Conflicts);

        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(before.Summary, after.Summary);
        Assert.Equal("synthetic-media-import-needle", after.Summary.Title);
        Assert.Equal("synthetic-media-import-needle",
            after.GetPosts(0, after.PostCount).Single(p => p.PostId == "15").Html);

        NativeMediaMergeResult replay = await CommitMergeAsync(admission, catalog, plan);
        Assert.Equal(committed with { IsReplay = true }, replay);
    }

    [Fact]
    public async Task CommittedMergeReplaysAfterTargetDeleteAndDifferentRecreate()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed404Target(lease, catalog, files);

        await using NativeTransferImportStage stage = await Stage(files, ContentWithAddedPost("17", "merge-added-post"));
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        NativeMediaMergeResult committed;
        using (ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync())
            committed = await CommitMergeAsync(admission, catalog, plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
        Assert.Equal(1, committed.NewPosts);
        Assert.Equal(0, committed.NewAttachments);
        Assert.Equal(0, committed.NewBindings);
        Assert.Equal(0, committed.Conflicts);

        Assert.Single(await catalog.DeleteThreadsAsync([Id]));
        await catalog.RegisterThreadAsync(new(Id, Url, "replacement",
            Path.Combine(files.Profile, "replacement"), false, ThreadSourceState.NotFound));
        Dictionary<string, string> beforeReplay = Files(files.Profile);

        // Delete/recreate ran after the first complete merge released its owner.
        using ProfileAdmissionToken replayAdmission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult replay = await CommitMergeAsync(replayAdmission, catalog, plan);

        Assert.Equal(committed with { IsReplay = true }, replay);
        Assert.Equal(beforeReplay, Files(files.Profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView replacement = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal("replacement", replacement.Summary.Title);
        Assert.Empty(replacement.GetPosts(0, Math.Max(1, replacement.PostCount)));
    }

    [Fact]
    public async Task UncommittedMergeAgainstDeleteAndRecreateIsStaleWithoutPublication()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed404Target(lease, catalog, files);

        await using NativeTransferImportStage stage = await Stage(files, ContentWithAddedPost("18", "stale-merge"));
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.False(Directory.Exists(plan.OperationsParent));

        Assert.Single(await catalog.DeleteThreadsAsync([Id]));
        await catalog.RegisterThreadAsync(new(Id, Url, "replacement",
            Path.Combine(files.Profile, "replacement"), false, ThreadSourceState.NotFound));
        Dictionary<string, string> before = Files(files.Profile);

        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        NativeMediaMergeResult stale = await CommitMergeAsync(admission, catalog, plan);

        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, stale.Status);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Equal(before, Files(files.Profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView replacement = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal("replacement", replacement.Summary.Title);
        Assert.Empty(replacement.GetPosts(0, Math.Max(1, replacement.PostCount)));
    }

    // The caller retains the same real Exclusive through the complete producer/reconciliation.
    private static async Task<NativeMediaMergeResult> CommitMergeAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaMergePlan plan,
        ProfileExclusiveDisposition expectedDisposition = ProfileExclusiveDisposition.SafeToResume,
        Action? beforeAdmission = null, Action<NativeMediaImportPhase>? checkpoint = null)
    {
        ProfileExclusiveOutcome<NativeMediaMergeResult> outcome = await catalog.CommitNativeMediaMergeAdmittedAsync(
            admission, plan, new MediaStore(), beforeAdmission: beforeAdmission, checkpoint: checkpoint)
            .ConfigureAwait(false);
        Assert.Equal(expectedDisposition, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission);
        return outcome.Result;
    }

    private static async Task Seed404Target(ProfileLease lease, ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var outcome = await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, new MediaStore());
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        catalog.EnsureTransferAdmission(admission);
        NativeMediaImportCommitResult result = outcome.Result;
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
    }

    // The seed import owns Exclusive until its complete admitted operation returns.
    // Merge ownership is acquired separately after the seed releases its token.
    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }

    private static NativeTransferCatalog ContentWithChangedExistingPost(string html)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost first = thread.Posts[0] with
        {
            Metadata = thread.Posts[0].Metadata with { Html = html }
        };
        return content with { Threads = [thread with { Posts = [first, thread.Posts[1]] }] };
    }

    private static NativeTransferCatalog ContentWithAddedPost(string postId, string html)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        TransferPost added = new(new(postId, 2, PostFieldPresence.Html, Html: html),
            ImmutableArray<TransferAttachment>.Empty);
        return content with { Threads = [thread with { Posts = thread.Posts.Add(added) }] };
    }
}
