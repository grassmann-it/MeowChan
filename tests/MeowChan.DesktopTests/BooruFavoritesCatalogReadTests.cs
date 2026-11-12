using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// The Booru catalog is one JSON document that every read parses and validates whole. Resolving favorites must therefore
// read it once for all favorite provider posts, not once per favorite (each provider post is its own favorite group).
public sealed class BooruFavoritesCatalogReadTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    private static async Task SeedPostsAsync(string profile, params string[] postIds)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        BooruCatalogStore store = new(profile, lease);
        foreach (string id in postIds)
        {
            byte[] bytes = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, (byte)int.Parse(id) };
            string directory = Path.Combine(profile, "danbooru", id);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, id + ".png"), bytes);
            await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1,
                Entries = new Dictionary<string, object>
                {
                    [id + ".png"] = new
                    {
                        MediaIdentifier = BooruMediaIdentity.MediaIdentifier(id), Variant = "original",
                        SourceUrl = "https://cdn.donmai.us/original/" + id + ".png", FileName = id + ".png", OriginalFileName = id + ".png",
                        Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                    }
                }
            }));
            string assetId = (await MediaAssetReader.OpenAsync(directory)).GetPage().Single().AssetId;
            store.UpsertPost(new BooruPostClaim("danbooru", id, BooruPostClaimPresence.All, "https://danbooru.donmai.us/posts/" + id,
                Rating: BooruPostClaimRating.General,
                Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/" + id + ".png", ".png", bytes.Length, 64, 64)]), Observed);
            store.LinkLocalAsset("danbooru", id, assetId, Path.Combine(directory, id + ".png"));
        }
    }

    [Fact]
    public async Task ResolvingFavoritesReadsTheBooruCatalogOnceForAllFavoriteProviderPosts()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        string[] posts = { "11", "12", "13", "14" };
        await SeedPostsAsync(temp.Path, posts);

        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            readBooruCatalog: host.ReadBooruCatalog);
        Assert.Equal(posts.Length, snapshot.Entries.Count);
        foreach (GalleryEntry gallery in await MediaLibraryGallery.ResolveAsync(snapshot.Entries, host.OpenArchiveReaderAsync,
            MediaAssetReader.OpenAsync, default, host.ReadBooruCatalog))
            Assert.True(await host.SetFavoriteAsync(gallery, true));
        Assert.Equal(posts.Length, host.GetFavorites().Count);

        int before = host.BooruCatalogReads;
        FavoriteResolutionSnapshot resolved = await host.ResolveFavoriteAssetsAsync();
        Assert.Equal(posts.Length, resolved.Available.Count);
        Assert.Empty(resolved.Missing);
        Assert.Equal(1, host.BooruCatalogReads - before);

        // An unreadable catalog is read once as well and never turns a favorite into a removed one.
        File.WriteAllText(Path.Combine(temp.Path, BooruCatalogStore.FileName), "{ not json");
        before = host.BooruCatalogReads;
        FavoriteResolutionSnapshot degraded = await host.ResolveFavoriteAssetsAsync();
        Assert.Equal(1, host.BooruCatalogReads - before);
        Assert.Equal(posts.Length, host.GetFavorites().Count);
        Assert.Equal(posts.Length, degraded.Available.Count + degraded.Missing.Count);
    }
}
