using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// P13-05: active attachment presence follows accepted/reused attachments, not rejected
// conflict input. The fixture is synthetic and contains no private profile data.
public sealed class NativeMediaMergeAttachmentPresenceRegressionTests
{
    [Fact]
    public async Task RejectedRetainedConflictCannotPromoteAttachmentPresence()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(catalog, files);
        await AddTargetRetainedBindingAsync(catalog, files);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost previous = before.Posts.Single(post => post.PostId == "16");
        Assert.False(previous.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Empty(previous.Attachments!);

        byte[] conflictingBytes = Bytes.ToArray();
        conflictingBytes[0] ^= 1;
        TransferAttachment retainedIdentity = Attachment(postId: "16");
        // Keep provider/revision identity unchanged; only measured incoming bytes conflict.
        TransferAttachment conflict = retainedIdentity with { PayloadSha256 = Hash(conflictingBytes) };
        await using NativeTransferImportStage stage = await StagePayloadsAsync(files,
            CatalogWithPosts(WithAttachments(previous, conflict)), conflictingBytes);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        Assert.Equal(1, plan.Conflicts);
        Assert.Empty(plan.SafeKeys);
        Assert.Empty(plan.UpdatedPosts);
        Assert.Empty(plan.AddedAttachments);
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.UpdatedPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(0, result.NewBindings);

        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost actual = after.Posts.Single(post => post.PostId == "16");
        Assert.False(actual.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Empty(actual.Attachments!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedNewBindingOrExactRetainedReuseStillPromotesMissingAttachmentPresence(bool reuseRetained)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(catalog, files);
        if (reuseRetained) await AddTargetRetainedBindingAsync(catalog, files);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost previous = before.Posts.Single(post => post.PostId == "16");
        TransferAttachment accepted = Attachment(postId: "16");
        MediaAssetSnapshot[] snapshots = await Task.WhenAll(before.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));
        Assert.False(previous.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Empty(previous.Attachments!);
        ResolvedMediaAttachment? old = MediaAttachmentResolver.Resolve(snapshots, Id, "16", accepted.Metadata);
        Assert.Equal(reuseRetained, old != null);

        await using NativeTransferImportStage stage = await StagePayloadsAsync(files,
            CatalogWithPosts(WithAttachments(previous, accepted)), Bytes);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Equal(reuseRetained ? 0 : 1, plan.SafeKeys.Length);
        Assert.Equal(previous.Fields | PostFieldPresence.Attachments, Assert.Single(plan.UpdatedPosts).Fields);
        Assert.Single(plan.AddedAttachments);

        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.UpdatedPosts);
        Assert.Equal(1, result.NewAttachments);
        Assert.Equal(reuseRetained ? 0 : 1, result.NewBindings);
        Assert.Equal(0, result.Conflicts);

        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost actual = after.Posts.Single(post => post.PostId == "16");
        Assert.True(actual.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Equal(accepted.Metadata, Assert.Single(actual.Attachments!));
        MediaAssetSnapshot[] active = await Task.WhenAll(after.MediaDirectories.Select(root => MediaAssetReader.OpenAsync(root)));
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(active, Id, "16", Assert.Single(actual.Attachments!)));
        Assert.Equal(Hash(Bytes), resolved.Binding.Actual.Sha256);
        Assert.Equal(before.MediaDirectories.Count + (reuseRetained ? 0 : 1), after.MediaDirectories.Count);
        if (reuseRetained)
        {
            Assert.Equal(old!.Binding, resolved.Binding);
            Assert.Equal(old.Asset, resolved.Asset);
            Assert.Equal(old.Snapshot.Directory, resolved.Snapshot.Directory);
        }
    }

    [Fact]
    public async Task RejectedConflictDoesNotBlockIndependentAcceptedNewPostAttachment()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(catalog, files);
        await AddTargetRetainedBindingAsync(catalog, files);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost previous = before.Posts.Single(post => post.PostId == "16");
        byte[] conflictingBytes = Bytes.ToArray();
        conflictingBytes[0] ^= 1;
        TransferAttachment conflict = Attachment(postId: "16") with { PayloadSha256 = Hash(conflictingBytes) };
        TransferAttachment accepted = Attachment(postId: "17");
        TransferPost added = new(new("17", 2, PostFieldPresence.All, At, "", "", "new", false,
            [accepted.Metadata]), [accepted]);
        await using NativeTransferImportStage stage = await StagePayloadsAsync(files,
            CatalogWithPosts(WithAttachments(previous, conflict), added), conflictingBytes, Bytes);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        Assert.Equal(1, plan.Conflicts);
        Assert.Single(plan.AddedPosts);
        Assert.Empty(plan.UpdatedPosts);
        Assert.Single(plan.SafeKeys);
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.NewPosts);
        Assert.Equal(1, result.NewAttachments);
        Assert.Equal(1, result.NewBindings);
        Assert.Equal(1, result.Conflicts);

        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost unchanged = after.Posts.Single(post => post.PostId == "16");
        Assert.False(unchanged.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Empty(unchanged.Attachments!);
        CapturedPost addedPost = after.Posts.Single(post => post.PostId == "17");
        Assert.True(addedPost.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Equal(accepted.Metadata, Assert.Single(addedPost.Attachments!));
    }

    [Fact]
    public async Task ExplicitEmptyObservationStillPromotesMissingPresenceWithoutInventingAnAttachment()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await SeedTargetAsync(catalog, files);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost previous = before.Posts.Single(post => post.PostId == "16");
        Assert.False(previous.Fields.HasFlag(PostFieldPresence.Attachments));
        NativeTransferCatalog content = CatalogWithPosts(WithAttachments(previous));
        content = content with { Threads = [content.Threads[0] with
            { RetainedBindings = [new("900", Attachment(postId: "900"))] }] };
        await using NativeTransferImportStage stage = await Stage(files, content);
        NativeMediaMergePreparation preparation = await catalog.PrepareNativeMediaMergeAsync(stage);
        Assert.Equal(NativeMediaMergeError.None, preparation.Error);
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(preparation.Plan);
        Assert.Equal(previous.Fields | PostFieldPresence.Attachments, Assert.Single(plan.UpdatedPosts).Fields);
        Assert.Empty(plan.AddedPosts);
        Assert.Empty(plan.AddedAttachments);
        Assert.Single(plan.SafeKeys);
        Assert.Equal(0, plan.Conflicts);
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.UpdatedPosts);
        Assert.Equal(0, result.NewAttachments);
        Assert.Equal(1, result.NewBindings);
        Assert.Equal(0, result.Conflicts);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        CapturedPost actual = after.Posts.Single(post => post.PostId == "16");
        Assert.True(actual.Fields.HasFlag(PostFieldPresence.Attachments));
        Assert.Empty(actual.Attachments!);
        Assert.DoesNotContain(after.Posts, post => post.PostId == "900");
    }

    private static TransferPost WithAttachments(CapturedPost previous, params TransferAttachment[] attachments)
        => new(previous with
        {
            Fields = previous.Fields | PostFieldPresence.Attachments,
            Attachments = attachments.Select(attachment => attachment.Metadata).ToArray()
        }, attachments.ToImmutableArray());

    private static NativeTransferCatalog CatalogWithPosts(params TransferPost[] posts)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0] with { Posts = posts.ToImmutableArray() };
        return content with { Threads = [thread] };
    }

    private static async Task SeedTargetAsync(ArchiveCatalog catalog, Workspace files)
    {
        // Standard contained-media import: post 15 has media; post 16 is Html-only.
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await catalog.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
    }

    private static async Task AddTargetRetainedBindingAsync(ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, CatalogWithRetained("16"));
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore())).Status);
    }

    private static NativeTransferCatalog CatalogWithRetained(string postId)
    {
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0] with
        {
            Posts = [],
            RetainedBindings = [new(postId, Attachment(postId: postId))]
        };
        return content with { Threads = [thread] };
    }

    private static async Task<NativeTransferImportStage> StagePayloadsAsync(Workspace files,
        NativeTransferCatalog content, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes =>
            new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, content, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }
}
