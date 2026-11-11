using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

public sealed class PrivateTagArchiveHostTests
{
    private const string FileName = "private-tags.json";

    private static PrivateMediaTagTarget Media(ResourceId thread)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(thread, "100", 0, "att-v2:" + new string('a', 64)),
            "media-1", "original", TransferMediaRole.Original, new string('c', 64),
            out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    [Fact]
    public async Task ThreadAndMediaTagsProtectExactlyTheirThreadFromArchiveCleanup()
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root);
        ResourceId[] ids = Enumerable.Range(0, 3).Select(index => Id("tagged-" + index)).ToArray();
        foreach (ResourceId id in ids) Directory.CreateDirectory(DirectoryFor(root, id));
        await RegisterAsync(profile, root, ids);
        TagId tag;
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            PrivateTagStore store = new(profile, lease);
            tag = store.Define("keep").Tag;
            store.SetThread(tag, ids[0], true);
            store.SetMedia(tag, Media(ids[1]), true);
        }
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();

        ArchiveCleanupPreview preview = await host.PrepareArchiveCleanupAsync();

        Assert.True(preview.Complete); Assert.False(preview.IsStale);
        foreach (ResourceId id in ids[..2])
        {
            ArchiveCleanupPreviewRow row = Assert.Single(preview.Rows, row => row.Id == id);
            Assert.Equal(ArchiveCleanupEligibility.Protected, row.Eligibility);
            Assert.Equal(ArchiveCleanupReason.PrivateTag, Assert.Single(row.Reasons));
        }
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(preview.Rows, row => row.Id == ids[2]).Eligibility);
        Assert.False(string.IsNullOrWhiteSpace(ArchiveCleanupText.Reason(ArchiveCleanupReason.PrivateTag)));
        Assert.NotEqual(ArchiveCleanupText.Reason(ArchiveCleanupReason.CatalogChanged), ArchiveCleanupText.Reason(ArchiveCleanupReason.PrivateTag));

        // A tag added after the preview changes the protection evidence, so an old plan cannot run.
        await host.SetThreadTagAsync(tag, ids[2], true);
        ArchiveCleanupPreview retagged = await host.PrepareArchiveCleanupAsync();
        Assert.NotEqual(preview.ProtectionToken, retagged.ProtectionToken);
        Assert.Equal(ArchiveCleanupEligibility.Protected, Assert.Single(retagged.Rows, row => row.Id == ids[2]).Eligibility);

        // An unreadable tag index never counts as "no tags": nothing is selectable.
        File.WriteAllText(Path.Combine(profile, FileName), "{bad");
        ArchiveCleanupPreview unknown = await host.PrepareArchiveCleanupAsync();
        Assert.False(unknown.Complete);
        Assert.All(unknown.Rows, row => Assert.Equal(ArchiveCleanupEligibility.Unknown, row.Eligibility));
    }

    [Fact]
    public async Task PermanentDeletionRemovesTheTagAssignmentsOfDeletedThreadsOnly()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        ResourceId gone = new("4chan", "g", "42"), kept = new("4chan", "g", "43");
        TagId thread, media;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            await catalog.RegisterThreadAsync(new(gone, "https://boards.4chan.org/g/thread/42", "42", Path.Combine(temp.Path, "missing-media"), true));
            PrivateTagStore store = new(temp.Path, lease);
            thread = store.Define("thread tag").Tag;
            media = store.Define("media tag").Tag;
            store.SetThread(thread, gone, true); store.SetThread(thread, kept, true);
            store.SetMedia(media, Media(gone), true); store.SetMedia(media, Media(kept), true);
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([gone]);

        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(plan);

        Assert.Equal(new[] { gone }, result.DeletedThreads);
        Assert.DoesNotContain(result.Errors, error => error.Contains("tags", StringComparison.Ordinal));
        PrivateTagStoreSnapshot published = host.GetPublishedPrivateTagSnapshot().Snapshot!;
        PrivateTagStoreSnapshot fresh = host.GetPrivateTagSnapshot().Snapshot!;
        Assert.Equal(published.Fingerprint, fresh.Fingerprint);
        Assert.Equal(new[] { thread, media }, fresh.Tags.Select(tag => tag.Id));
        Assert.Empty(fresh.TagsOf(gone));
        Assert.Empty(fresh.TagsOf(Media(gone)));
        Assert.Equal(thread, Assert.Single(fresh.TagsOf(kept)).Id);
        Assert.Equal(media, Assert.Single(fresh.TagsOf(Media(kept))).Id);
    }

    [Fact]
    public async Task FailedTagCleanupIsReportedWithoutUndoingOrHidingTheDeletion()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        ResourceId gone = new("4chan", "g", "42");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            await catalog.RegisterThreadAsync(new(gone, "https://boards.4chan.org/g/thread/42", "42", Path.Combine(temp.Path, "missing-media"), true));
            PrivateTagStore store = new(temp.Path, lease);
            store.SetThread(store.Define("tag").Tag, gone, true);
            File.Delete(Path.Combine(temp.Path, FileName + ".bak"));
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([gone]);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        Directory.CreateDirectory(Path.Combine(temp.Path, FileName + ".bak"));

        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(plan);

        Assert.Equal(new[] { gone }, result.DeletedThreads);
        Assert.Contains(result.Errors, error => error.Contains("tags", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Null(await reader.ReadThreadAsync(gone));
    }
}
