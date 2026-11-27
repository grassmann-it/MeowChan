using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interop.Sidecars;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests;

public sealed class OriginalExportSidecarTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WritesOnlyExplicitlySelectedTagsOfTheExactOriginal(bool own, bool source)
    {
        using TempDirectory fixture = new();
        FavoriteExportAsset original = CreateOriginal(fixture.Path, BooruMediaIdentity.Thread("42"), BooruMediaIdentity.MediaIdentifier("42"));
        MediaAssetReference asset = original.Manifest.FindById(original.AssetId)!;
        PrivateTagStoreSnapshot privateTags = PrivateTags(original, asset);
        BooruCatalogSnapshot sourceTags = SourceTags(original, asset, [new("猫_é", BooruPostClaimTagCategory.Artist)]);
        OriginalExportSidecarOptions options = new(privateTags: own ? privateTags : null, sourceTags: source ? sourceTags : null);
        string destination = Path.Combine(fixture.Path, "export");

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([original], destination, null, options, default);
        Assert.Equal(1, result.Copied);
        string path = Assert.Single(result.Items).Path!;
        Assert.Equal(await File.ReadAllBytesAsync(asset.Path), await File.ReadAllBytesAsync(path));
        List<string> expected = new();
        if (own) expected.AddRange(["私のタグ", "loose_tag"]);
        if (source) expected.Add("artist:猫_é");
        SidecarParseResult parsed = PassiveSidecarCodec.Parse(await File.ReadAllBytesAsync(path + ".json"), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(parsed.Success);
        Assert.Equal(expected, parsed.Tags);
        if (!own && !source) Assert.Equal("{\"metadata\":{\"tags\":[]}}", await File.ReadAllTextAsync(path + ".json"));
        Assert.Equal(1, (await FavoriteOriginalExporter.ExportAsync([original], destination, null, options, default)).AlreadyExists);
        Assert.Equal(2, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task OffCopiesOnlyTheOriginalAndAnOccupiedSidecarIsPreserved()
    {
        using TempDirectory fixture = new();
        FavoriteExportAsset original = CreateOriginal(fixture.Path, new("test", "g", "42"), "first");
        string destination = Path.Combine(fixture.Path, "export");
        FavoriteExportResult plain = await FavoriteOriginalExporter.ExportAsync([original], destination);
        string path = Assert.Single(plain.Items).Path!;
        Assert.Equal(path, Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)));
        string occupied = path + ".json";
        await File.WriteAllTextAsync(occupied, "existing sidecar");
        File.Delete(path);

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([original], destination, null, new OriginalExportSidecarOptions(), default);
        Assert.Equal(1, result.Copied);
        string newPath = Assert.Single(result.Items).Path!;
        Assert.NotEqual(path, newPath);
        Assert.Equal("existing sidecar", await File.ReadAllTextAsync(occupied));
        Assert.True(File.Exists(newPath + ".json"));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingDuringPublicationRemovesTheSidecarAndKeepsPreviouslyExistingOriginals(bool preexisting)
    {
        using TempDirectory fixture = new();
        using CancellationTokenSource cancellation = new();
        FavoriteExportAsset original = CreateOriginal(fixture.Path, new("test", "g", "42"), "first");
        string destination = Path.Combine(fixture.Path, "export");
        string? existing = preexisting
            ? Assert.Single((await FavoriteOriginalExporter.ExportAsync([original], destination)).Items).Path : null;
        FavoriteOriginalExporter.Hooks hooks = new() { BeforePairCompletion = cancellation.Cancel };

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportWithHooksAsync([original], destination,
            hooks, filenameTemplate: null, sidecar: new OriginalExportSidecarOptions(), cancellationToken: cancellation.Token);

        Assert.Equal(1, result.Cancelled);
        string[] files = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
        if (preexisting) Assert.Equal(existing, Assert.Single(files));
        else Assert.Empty(files);
    }

    [Fact]
    public async Task LockedOriginalAndOriginalBeyondThePairLimitLeaveNoSidecars()
    {
        using TempDirectory fixture = new();
        ResourceId thread = new("test", "g", "42");
        FavoriteExportAsset locked = CreateOriginal(fixture.Path, thread, "locked");
        FavoriteExportAsset inLimit = CreateOriginal(fixture.Path, thread, "included");
        FavoriteExportAsset beyondLimit = CreateOriginal(fixture.Path, thread, "beyond");
        string destination = Path.Combine(fixture.Path, "export");
        using FileStream handle = new(locked.Manifest.FindById(locked.AssetId)!.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([locked, inLimit, beyondLimit], destination, null,
            new OriginalExportSidecarOptions(pairLimit: 2), default);

        Assert.Equal(2, result.Failed);
        string path = Assert.Single(result.Items, item => item.Status == FavoriteExportStatus.Copied).Path!;
        Assert.Equal(inLimit.Key, result.Items[1].Key);
        Assert.Equal(new[] { path, path + ".json" }, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OversizedChosenTagsLeaveNeitherFile()
    {
        using TempDirectory fixture = new();
        FavoriteExportAsset original = CreateOriginal(fixture.Path, BooruMediaIdentity.Thread("42"), BooruMediaIdentity.MediaIdentifier("42"));
        MediaAssetReference asset = original.Manifest.FindById(original.AssetId)!;
        BooruCatalogSnapshot tags = SourceTags(original, asset,
            [new(new string('a', PassiveSidecarCodec.MaxTagBytes), BooruPostClaimTagCategory.General)]);
        string destination = Path.Combine(fixture.Path, "export");

        FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync([original], destination, null,
            new OriginalExportSidecarOptions(sourceTags: tags), default);

        Assert.Equal(1, result.Failed);
        Assert.False(Directory.Exists(destination));
    }

    private static FavoriteExportAsset CreateOriginal(string root, ResourceId thread, string mediaId)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, mediaId)).FullName;
        string path = Path.Combine(directory, "photo.png");
        byte[] bytes = [2, 3, 5, 7];
        File.WriteAllBytes(path, bytes);
        string assetId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(mediaId)));
        MediaAssetReference asset = new(assetId, mediaId, "original", "photo.png", path, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "https://example.test/photo.png", "photo.png", true);
        return FavoriteExportAsset.From(thread, new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 1, null, [asset]), asset);
    }

    private static PrivateTagStoreSnapshot PrivateTags(FavoriteExportAsset original, MediaAssetReference asset)
    {
        PrivateTagDefinition ownThread = Tag("私のタグ");
        PrivateTagDefinition ownFile = Tag("loose_tag");
        PrivateTagDefinition other = Tag("another thread's tag");
        LocalMediaOccurrenceId occurrence = new(Guid.NewGuid());
        LocalMediaRevisionId revision = new(Guid.NewGuid());
        LocalMediaRegistry registry = new([new LocalMediaOccurrence(occurrence,
            new LocalMediaLocator(original.Key.Thread, original.Manifest.Directory, asset.RequestedFileName),
            [new LocalMediaRevision(revision, asset.AssetId, asset.Length, asset.Sha256)])]);
        return new PrivateTagStoreSnapshot(1, string.Empty, [ownThread, ownFile, other],
            [new(ownThread.Id, original.Key.Thread), new(other.Id, new ResourceId("different", "g", "42"))],
            [], registry, [new(ownFile.Id, new(occurrence, revision))]);
    }

    private static PrivateTagDefinition Tag(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), name!);
    }

    private static BooruCatalogSnapshot SourceTags(FavoriteExportAsset original, MediaAssetReference asset, IReadOnlyList<BooruPostClaimTag> tags)
    {
        Assert.True(BooruMediaIdentity.TryGetPostKey(original.Key.Thread, out var key));
        BooruPostClaim claim = new(key.SourceId, key.PostId, BooruPostClaimPresence.Tags, Tags: tags);
        BooruPostRecord linked = new(key.SourceId, key.PostId, claim, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            asset.AssetId, asset.Path, OriginalIntent: BooruOriginalIntent.Linked);
        BooruPostRecord other = linked with { PostId = "43", Post = claim with { PostId = "43", Tags = [new("unrelated", BooruPostClaimTagCategory.General)] } };
        return BooruCatalogSnapshot.Empty with { Posts = [linked, other] };
    }
}
