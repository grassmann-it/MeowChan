using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B7 acceptance (fixtures only): what a linked Booru original offers after a restart without network, and
/// what the archive-wide media index (MediaPage/gallery/favorites source) does and does not know about it.
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task LinkedOriginalIsVerifiablyReadableAfterRestartButIsNotAnArchiveThreadMediaPageEntry()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(5);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("55", bytes, cdn);
        string assetId;
        await using (Session session = new(directory.Path, cdn))
        {
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            assetId = (await admission.Completion.WaitAsync(TestWait.Timeout)).AssetId!;
        }
        int requests = cdn.Requests.Count;

        string profile = Path.Combine(directory.Path, "profile");
        string mediaDir = Path.Combine(directory.Path, "media", BooruOriginalQueue.ProviderDirectoryName, "55");
        using (ProfileLease reopened = ProfileLease.Acquire(profile))
        {
            BooruPostRecord record = Assert.Single(new BooruCatalogStore(reopened.ProfileDirectory, reopened).Read().Posts);
            Assert.Equal(assetId, record.LocalAssetId);
            Assert.Equal(BooruOriginalIntent.Linked, record.OriginalIntent);

            // The linked asset is verifiably readable through the same manifest reader the gallery uses.
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(mediaDir);
            Assert.Equal(MediaManifestStatus.Available, manifest.Status);
            Assert.NotNull(manifest.FindById(assetId));
            await using Stream stream = await manifest.OpenVerifiedReadAsync(assetId);
            using MemoryStream copy = new();
            await stream.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());

            // Current contract gap (documented, not invented around): the media index is derived from
            // archived chan threads. A Booru post is not an archive thread and has no ResourceId, so
            // the MediaPage/gallery/favorite key (Thread, MediaId, Variant, Sha256) cannot address it.
            ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, reopened);
            await catalog.DisposeAsync();
            MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(
                token => ArchiveReader.OpenAsync(profile, token), MediaAssetReader.OpenAsync);
            Assert.True(index.Complete);
            Assert.Empty(index.Entries);
            Assert.Empty(index.Threads);
        }
        Assert.Equal(requests, cdn.Requests.Count);
        Assert.False(File.Exists(Path.Combine(profile, MeowChan.Tags.PrivateTagStore.FileName)));
    }
}
