using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryMediaTagTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");

    private sealed record Fixture(MediaAssetSnapshot Snapshot, MediaAssetReference Asset, CapturedAttachment Attachment,
        MediaAttachmentBinding Binding, string RevisionKey);

    private static Fixture Bound(string post, int position, string file, string variant = "original", byte seed = 1)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gallery-media-tag-fixture");
        byte[] bytes = { seed, 2, 3 };
        var provider = new MediaProviderMetadata(file, variant, "https://offline.invalid/" + file, file, 3, null, null);
        var actual = new MediaActualIntegrity(3, Convert.ToBase64String(MD5.HashData(bytes)),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file + variant))).ToLowerInvariant();
        // Upper-case digest in the manifest view must still give the canonical lower-case target.
        var asset = new MediaAssetReference(assetId, file, variant, file, Path.Combine(directory, file),
            3, actual.Sha256.ToUpperInvariant(), provider.Url, file, true) { Md5 = actual.Md5 };
        string revision = MediaRevisionKeys.CreateAttachmentKey(Thread, post, position, provider);
        var binding = new MediaAttachmentBinding(new(Thread, post, position, revision),
            MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), assetId, provider, actual,
            new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch, "fixture", "owner", Thread.Source, 1));
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [revision] = binding });
        var attachment = new CapturedAttachment(position, file, variant, provider.Url, file, ExpectedSize: 3)
        { AttachmentRevisionKey = revision };
        return new(snapshot, asset, attachment, binding, revision);
    }

    private static GalleryEntry Entry(Fixture fixture, ResolvedMediaAttachment? resolution, ResourceId? thread = null) =>
        new(new GalleryAssetKey(thread ?? Thread, fixture.Snapshot.Directory, fixture.Asset.AssetId), fixture.Snapshot,
            fixture.Asset, null, fixture.Attachment, false, false) { AttachmentResolution = resolution };

    [Fact]
    public void BoundEntryResolvesToItsExactVersionAndRole()
    {
        Fixture original = Bound("100", 0, "a.png");
        GalleryEntry entry = Entry(original, new ResolvedMediaAttachment(original.Snapshot, original.Asset, original.Binding, false));

        PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(entry, out GalleryMediaTagUnavailable reason);

        Assert.Equal(GalleryMediaTagUnavailable.None, reason);
        Assert.NotNull(target);
        Assert.Equal(new AttachmentRevisionReference(Thread, "100", 0, original.RevisionKey), target!.Reference);
        Assert.Equal("a.png", target.MediaId);
        Assert.Equal("original", target.Variant);
        Assert.Equal(TransferMediaRole.Original, target.Role);
        Assert.Equal(original.Asset.Sha256.ToLowerInvariant(), target.Sha256);

        Fixture thumb = Bound("100", 0, "a.png", variant: "thumbnail");
        PrivateMediaTagTarget? preview = GalleryMediaTagTarget.Resolve(
            Entry(thumb, new ResolvedMediaAttachment(thumb.Snapshot, thumb.Asset, thumb.Binding, false)), out _);
        Assert.Equal(TransferMediaRole.Preview, preview!.Role);
        Assert.NotEqual(target, preview);
    }

    [Fact]
    public void UnboundLegacyAndMismatchedEntriesAreNotTaggable()
    {
        Fixture fixture = Bound("100", 0, "a.png");
        Fixture other = Bound("101", 0, "b.png", seed: 9);

        Assert.Null(GalleryMediaTagTarget.Resolve(null, out GalleryMediaTagUnavailable none));
        Assert.Equal(GalleryMediaTagUnavailable.NoPostVersion, none);
        Assert.Null(GalleryMediaTagTarget.Resolve(Entry(fixture, null), out GalleryMediaTagUnavailable unassigned));
        Assert.Equal(GalleryMediaTagUnavailable.NoPostVersion, unassigned);
        Assert.Null(GalleryMediaTagTarget.Resolve(Entry(fixture,
            new ResolvedMediaAttachment(fixture.Snapshot, fixture.Asset, null, true)), out GalleryMediaTagUnavailable legacy));
        Assert.Equal(GalleryMediaTagUnavailable.NoPostVersion, legacy);
        // A binding for a different asset, or from another thread, is never adopted.
        Assert.Null(GalleryMediaTagTarget.Resolve(Entry(fixture,
            new ResolvedMediaAttachment(fixture.Snapshot, fixture.Asset, other.Binding, false)), out GalleryMediaTagUnavailable foreign));
        Assert.Equal(GalleryMediaTagUnavailable.Mismatch, foreign);
        Assert.Null(GalleryMediaTagTarget.Resolve(Entry(fixture,
            new ResolvedMediaAttachment(fixture.Snapshot, fixture.Asset, fixture.Binding, false), new ResourceId("4chan", "g", "999")), out GalleryMediaTagUnavailable thread));
        Assert.Equal(GalleryMediaTagUnavailable.Mismatch, thread);
    }

    [Fact]
    public void LooseFilesAndFavoriteEntriesWithoutResolutionNeverBorrowAnExistingBinding()
    {
        // Favorites galleries and loose thread files build entries from (thread, asset) only. Even though
        // the manifest holds an exact post binding for these bytes, the entry must not adopt it: several
        // bindings may share one asset, and no contract picks one. These stay untaggable until tags v3.
        Fixture fixture = Bound("100", 0, "a.png");
        GalleryEntry favoriteStyle = Entry(fixture, null);
        Assert.Contains(fixture.RevisionKey, fixture.Snapshot.AttachmentBindings.Keys);
        Assert.Null(GalleryMediaTagTarget.Resolve(favoriteStyle, out GalleryMediaTagUnavailable reason));
        Assert.Equal(GalleryMediaTagUnavailable.NoPostVersion, reason);
    }

    [Fact]
    public void BoundEntryWithoutUsableDigestIsRejectedAsInvalidIdentity()
    {
        Fixture fixture = Bound("100", 0, "a.png");
        MediaAssetReference noDigest = fixture.Asset with { Sha256 = "" };
        GalleryEntry entry = new(new GalleryAssetKey(Thread, fixture.Snapshot.Directory, noDigest.AssetId), fixture.Snapshot,
            noDigest, null, fixture.Attachment, false, false)
        { AttachmentResolution = new ResolvedMediaAttachment(fixture.Snapshot, noDigest, fixture.Binding, false) };

        Assert.Null(GalleryMediaTagTarget.Resolve(entry, out GalleryMediaTagUnavailable reason));
        Assert.Equal(GalleryMediaTagUnavailable.InvalidIdentity, reason);
    }

    private static MediaTagSession Session(PrivateTagStore store, PrivateMediaTagTarget target) => new(new MediaTagSubject.Bound(target),
        () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
        (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
        (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));

    private static PrivateMediaTagTarget Target(string post = "100", byte seed = 1)
    {
        Fixture fixture = Bound(post, 0, "f" + post + ".png", seed: seed);
        return GalleryMediaTagTarget.Resolve(Entry(fixture,
            new ResolvedMediaAttachment(fixture.Snapshot, fixture.Asset, fixture.Binding, false)), out _)!;
    }

    [Fact]
    public async Task SessionAddsExistingAndNewTagsAndRemovesThemOnlyFromItsTarget()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId red = store.Define("red").Tag, blue = store.Define("blue").Tag;
        PrivateMediaTagTarget mine = Target(), other = Target("101", 9);
        store.SetMedia(blue, other, true);
        store.SetThread(red, Thread, true);
        MediaTagSession session = Session(store, mine);

        // Thread tags are not media tags: the thread's red tag is offered, not shown as assigned.
        Assert.Empty(session.Assigned);
        Assert.Equal(new[] { red, blue }, session.Available.Select(tag => tag.Id));

        await session.AddExistingAsync(red);
        await session.AddAsync("  /RPT/ ");
        await session.AddAsync("/rpt/");

        Assert.Equal(new[] { "red", "/RPT/" }, session.Assigned.Select(tag => tag.Name.Display));
        Assert.Equal(new[] { blue }, session.Available.Select(tag => tag.Id));
        Assert.True(session.Changed);
        Assert.Null(session.MessageKey);

        await session.RemoveAsync(red);
        PrivateTagStoreSnapshot stored = store.ReadSnapshot();
        Assert.Equal(new[] { "/RPT/" }, stored.TagsOf(mine).Select(tag => tag.Name.Display));
        Assert.Equal(blue, Assert.Single(stored.TagsOf(other)).Id);
        Assert.Equal(red, Assert.Single(stored.TagsOf(Thread)).Id);
    }

    [Fact]
    public async Task InvalidNamesAndVanishedTagsAreExplainedAndUnavailableIndexIsReadOnly()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId gone = store.Define("gone").Tag;
        MediaTagSession session = Session(store, Target());
        store.Delete(gone);

        await session.AddAsync("tab\tname");
        Assert.Equal("tags.error.invalidName", session.MessageKey);
        await session.AddExistingAsync(gone);
        Assert.Equal("tags.manage.error.gone", session.MessageKey);
        Assert.Empty(session.Available);
        Assert.False(session.Changed);

        int calls = 0;
        MediaTagSession unavailable = new(new MediaTagSubject.Bound(Target()),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Unavailable, null, "damaged"),
            (_, _, _) => { calls++; throw new InvalidOperationException(); },
            (_, _, _, _) => { calls++; throw new InvalidOperationException(); });
        await unavailable.AddAsync("x");
        await unavailable.RemoveAsync(gone);
        Assert.Null(unavailable.Snapshot);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HostMediaTagsProtectTheThreadAndSurviveRestart()
    {
        using TempDirectory temp = new();
        PrivateMediaTagTarget target = Target();
        await using (DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>())))
        {
            await host.StartAsync();
            MediaTagSession session = new(new MediaTagSubject.Bound(target), host.GetPublishedPrivateTagSnapshot,
                host.DefineAndSetMediaSubjectTagAsync, host.SetMediaSubjectTagAsync);
            await session.AddAsync("keep");
            Assert.Equal("keep", Assert.Single(session.Assigned).Name.Display);
        }
        await using DesktopSessionHost restored = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>()));
        await restored.StartAsync();
        PrivateTagStoreSnapshot snapshot = restored.GetPrivateTagSnapshot().Snapshot!;
        Assert.Equal("keep", Assert.Single(snapshot.TagsOf(target)).Name.Display);
        Assert.Empty(snapshot.TagsOf(Thread));
    }
}
