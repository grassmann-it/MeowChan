using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

// AP-01b real-host path: loose legacy media tagged through the session and the host, read back after recreating it.
public sealed class LocalMediaTagHostTests
{
    private const string FileName = "private-tags.json";
    private static readonly byte[] Same = Encoding.UTF8.GetBytes("same bytes");

    private sealed record Fixture(string Profile, string Root, ResourceId Loose, ResourceId Other, string Directory);

    private static async Task<Fixture> SeedAsync(TempDirectory temp)
    {
        string profile = Path.Combine(temp.Path, "profile"), root = Path.Combine(temp.Path, "downloads");
        Config(profile, root);
        ResourceId loose = Id("loose"), other = Id("other");
        string directory = DirectoryFor(root, loose);
        await AssetsAsync(directory, new Asset("m1", "original", "a.png", "a.png", Same), new Asset("m1", "original", "b.png", "b.png", Same));
        await AssetsAsync(DirectoryFor(root, other), new Asset("m2", "original", "c.png", "c.png", Encoding.UTF8.GetBytes("other")));
        await RegisterAsync(profile, root, loose, other);
        return new(profile, root, loose, other, directory);
    }

    private static async Task<IReadOnlyList<GalleryEntry>> GalleryAsync(DesktopSessionHost host, ResourceId thread)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        ArchivedThreadView view = (await reader.ReadThreadAsync(thread))!;
        List<MediaAssetSnapshot> manifests = new();
        foreach (string directory in await reader.GetMediaDirectoriesAsync(thread)) manifests.Add(await MediaAssetReader.OpenAsync(directory));
        return GallerySelection.Build(view, manifests, allSavedVersions: false, includeOtherMedia: true).Entries;
    }

    private static async Task<MediaTagSubject> SubjectAsync(DesktopSessionHost host, ResourceId thread, string file) =>
        MediaTagSubjects.Of((await GalleryAsync(host, thread)).Single(entry => entry.Asset.RequestedFileName == file))!;

    private static MediaTagSession Session(DesktopSessionHost host, MediaTagSubject subject) =>
        new(subject, host.GetPublishedPrivateTagSnapshot, host.DefineAndSetMediaSubjectTagAsync, host.SetMediaSubjectTagAsync);

    private static PrivateMediaTagTarget BoundFor(ResourceId thread)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(thread, "100", 0, "att-v2:" + new string('a', 64)),
            "media-1", "original", TransferMediaRole.Original, new string('c', 64), out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    [Fact]
    public async Task ALooseLegacyFileIsTaggedThroughTheHostAndReadsBackTheSameAfterTwoRestarts()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        // Pre-existing v1 content: a thread tag and a bound media tag that must survive unchanged.
        TagId keep;
        using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile))
        {
            PrivateTagStore store = new(fixture.Profile, lease);
            keep = store.Define("keep").Tag;
            store.SetThread(keep, fixture.Other, true);
            store.SetMedia(keep, BoundFor(fixture.Other), true);
        }
        PrivateTagStoreSnapshot v1;
        using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile)) v1 = new PrivateTagStore(fixture.Profile, lease).ReadSnapshot();
        Assert.Equal(1, v1.FormatVersion);
        LocalMediaTagTarget target;
        await using (DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            MediaTagSession session = Session(host, await SubjectAsync(host, fixture.Loose, "a.png"));
            Assert.True(session.IsLocal);
            Assert.Empty(session.Assigned);

            await session.AddAsync("loose");
            await session.AddExistingAsync(keep);

            Assert.Null(session.MessageKey);
            Assert.Equal(new[] { "keep", "loose" }, session.Assigned.Select(tag => tag.Name.Display));
            target = Assert.IsType<MediaTagSubject.Local>(session.Subject).Expected!.Value; // Frozen after the first write.
            // Equal bytes in the same folder are another occurrence and stay untagged.
            Assert.Empty(host.GetPublishedPrivateTagSnapshot().Snapshot!.TagsOf(await SubjectAsync(host, fixture.Loose, "b.png")));
        }
        for (int restart = 0; restart < 2; restart++)
        {
            await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            PrivateTagStoreSnapshot snapshot = host.GetPrivateTagSnapshot().Snapshot!;
            MediaTagSubject fresh = await SubjectAsync(host, fixture.Loose, "a.png");
            Assert.Equal(target, snapshot.ResolveLocal(Assert.IsType<MediaTagSubject.Local>(fresh).Observation).Target);
            MediaTagSession reopened = Session(host, fresh);
            Assert.Equal(new[] { "keep", "loose" }, reopened.Assigned.Select(tag => tag.Name.Display));
            Assert.Equal(target, Assert.IsType<MediaTagSubject.Local>(reopened.Subject).Expected);
            Assert.Equal(v1.Tags.Select(tag => tag.Id), snapshot.Tags.Select(tag => tag.Id).Take(v1.Tags.Count));
            Assert.Equal(v1.ThreadAssignments, snapshot.ThreadAssignments);
            Assert.Equal(v1.MediaAssignments, snapshot.MediaAssignments);
            Assert.Empty(snapshot.TagsOf(fixture.Loose)); // No thread tag appeared from the media tags.
            Assert.Equal(2, snapshot.FormatVersion);
            Assert.Single(snapshot.LocalRegistry.Occurrences);
        }
    }

    [Fact]
    public async Task TheHostRefusesToAddToAChangedFileButStillRemovesItsVisibleTag()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        MediaTagSession session = Session(host, await SubjectAsync(host, fixture.Loose, "a.png"));
        await session.AddAsync("first");
        TagId first = Assert.Single(session.Assigned).Id;

        // Same length, other bytes: an external replacement the manifest does not vouch for.
        await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, "a.png"), Encoding.UTF8.GetBytes("SAME BYTES"));
        string path = Path.Combine(fixture.Profile, FileName);
        byte[] before = File.ReadAllBytes(path);
        await session.AddAsync("second");

        Assert.Equal("tags.media.error.unavailable", session.MessageKey);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(host.GetPrivateTagSnapshot().Snapshot!.Tags, tag => tag.Name.Display == "second");
        await Assert.ThrowsAsync<PrivateTagStoreException>(() =>
            host.SetMediaSubjectTagAsync(first, session.Subject, true));

        await session.RemoveAsync(first);
        Assert.Null(session.MessageKey);
        Assert.Empty(session.Assigned);
        Assert.Empty(host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments);
    }

    [Fact]
    public async Task ATaggedLooseFileProtectsItsThreadFromCleanupAndAV2IndexIsValidEvidence()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchiveCleanupPreview before = await host.PrepareArchiveCleanupAsync();
        Assert.True(before.Complete);
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(before.Rows, row => row.Id == fixture.Loose).Eligibility);

        await host.DefineAndSetMediaSubjectTagAsync("keep", await SubjectAsync(host, fixture.Loose, "a.png"));
        ArchiveCleanupPreview after = await host.PrepareArchiveCleanupAsync();

        Assert.True(after.Complete);
        Assert.NotEqual(before.ProtectionToken, after.ProtectionToken);
        ArchiveCleanupPreviewRow row = Assert.Single(after.Rows, item => item.Id == fixture.Loose);
        Assert.Equal(ArchiveCleanupEligibility.Protected, row.Eligibility);
        Assert.Equal(ArchiveCleanupReason.PrivateTag, Assert.Single(row.Reasons));
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(after.Rows, item => item.Id == fixture.Other).Eligibility);

        // A dangling local assignment is a damaged index: protection is unknown, nothing is selectable.
        string path = Path.Combine(fixture.Profile, FileName);
        string damaged = File.ReadAllText(path).Replace("\"Occurrence\":\"", "\"Occurrence\":\"0", StringComparison.Ordinal);
        File.WriteAllText(path, damaged);
        ArchiveCleanupPreview unknown = await host.PrepareArchiveCleanupAsync();
        Assert.False(unknown.Complete);
        Assert.All(unknown.Rows, item => Assert.Equal(ArchiveCleanupEligibility.Unknown, item.Eligibility));
    }

    [Fact]
    public async Task PermanentDeletionDropsOnlyTheOwnersLocalTagsAndAFailedCleanupIsVisibleAndRetryable()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await host.DefineAndSetMediaSubjectTagAsync("tag", await SubjectAsync(host, fixture.Loose, "a.png"));
        await host.DefineAndSetMediaSubjectTagAsync("tag", await SubjectAsync(host, fixture.Other, "c.png"));
        string path = Path.Combine(fixture.Profile, FileName);
        File.Delete(path + ".bak");
        Directory.CreateDirectory(path + ".bak"); // The next tag write cannot rotate its backup.
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([fixture.Loose]);

        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(plan);

        Assert.Equal(new[] { fixture.Loose }, result.DeletedThreads);
        Assert.Contains(result.Errors, error => error.Contains("tags", StringComparison.Ordinal));
        Assert.Equal(new[] { fixture.Loose }, result.TagCleanupThreads);
        Assert.Equal(2, host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments.Count); // Nothing lost, nothing half-removed.

        Directory.Delete(path + ".bak");
        await host.RetryPrivateTagCleanupAsync(result.TagCleanupThreads!);
        await host.RetryPrivateTagCleanupAsync(result.TagCleanupThreads!); // Idempotent.

        PrivateTagStoreSnapshot snapshot = host.GetPrivateTagSnapshot().Snapshot!;
        Assert.Equal("tag", Assert.Single(snapshot.Tags).Name.Display); // Definitions stay.
        LocalMediaOccurrence remaining = Assert.Single(snapshot.LocalRegistry.Occurrences);
        Assert.Equal(fixture.Other, remaining.Locator.Thread);
        Assert.Single(snapshot.LocalAssignments);
        Assert.Equal(new[] { fixture.Other }, PrivateTagStore.LocalAssignmentOwners(snapshot));
    }

    [Fact]
    public async Task OrphansOfMissingFilesAreInspectedAndRemovedExplicitlyThroughTheHost()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await host.DefineAndSetMediaSubjectTagAsync("tag", await SubjectAsync(host, fixture.Loose, "a.png"));
        await host.DefineAndSetMediaSubjectTagAsync("tag", await SubjectAsync(host, fixture.Loose, "b.png"));
        File.Delete(Path.Combine(fixture.Directory, "b.png"));
        TagManagerSession manager = new(host.GetPublishedPrivateTagSnapshot, host.RenamePrivateTagAsync, host.DeletePrivateTagAsync,
            host.InspectLocalMediaTagOrphansAsync, host.RemoveLocalMediaTagAssignmentsAsync);
        Assert.True(manager.CanInspectOrphans);
        Assert.Equal(2, Assert.Single(manager.Rows).Media);

        await manager.CheckOrphansAsync();
        LocalMediaTagInspection inspection = manager.Inspection!;
        Assert.Single(inspection.Orphans);
        Assert.Equal(1, inspection.OrphanedFiles);

        // The orphan is still counted and visible until it is explicitly removed; the tag stays.
        await manager.RemoveOrphansAsync();
        Assert.Equal("tags.manage.orphansRemoved", manager.MessageKey);
        Assert.Null(manager.Inspection);
        Assert.Equal(1, Assert.Single(manager.Rows).Media);
        PrivateTagStoreSnapshot snapshot = host.GetPrivateTagSnapshot().Snapshot!;
        Assert.Single(snapshot.LocalAssignments);
        Assert.Equal(2, snapshot.LocalRegistry.Occurrences.Count);
    }

    [Fact]
    public async Task AStaleFrozenSubjectIsRefusedInsteadOfRedirectedAndTheDialogExplainsIt()
    {
        using TempDirectory temp = new();
        Fixture fixture = await SeedAsync(temp);
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        MediaTagSubject subject = await SubjectAsync(host, fixture.Loose, "a.png");
        await host.DefineAndSetMediaSubjectTagAsync("one", subject);
        MediaTagSession stale = Session(host, subject); // frozen to the current registration
        await host.RetryPrivateTagCleanupAsync([fixture.Loose]); // drops the registration (as a deletion would)
        await host.DefineAndSetMediaSubjectTagAsync("two", subject); // a new, independent registration of the same place
        host.GetPrivateTagSnapshot();

        await stale.AddAsync("three");

        Assert.Equal("tags.media.error.changed", stale.MessageKey);
        Assert.DoesNotContain(host.GetPrivateTagSnapshot().Snapshot!.Tags, tag => tag.Name.Display == "three");
        Assert.False(string.IsNullOrWhiteSpace(Texts.T("tags.media.error.changed")));
    }

    [Fact]
    public void ExportAndTransferSurfacesStateThatOwnTagsAreNotTransferred()
    {
        Assert.Equal(new[] { NativePrivateMetadataOmission.PrivateTags }, NativePrivateMetadataSnapshot.Omissions);
        string line = Assert.Single(PrivateTransferDisclosure.Lines());
        Assert.Equal(Texts.T("tags.export.omitted"), line);
        Assert.False(string.IsNullOrWhiteSpace(line));
    }
}
