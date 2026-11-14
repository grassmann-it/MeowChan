using System;
using System.IO;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

/// <summary>Explicit JSON null collections are not interchangeable with valid empty arrays.</summary>
public sealed class BooruCatalogNullCollectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T00:00:00Z");

    [Theory]
    [InlineData("posts")]
    [InlineData("watchers")]
    [InlineData("findings")]
    [InlineData("knownPostIds")]
    [InlineData("tags")]
    [InlineData("media")]
    public void ExplicitNullCollectionsFailClosedWithoutChangingTheOfflineCatalog(string collection)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteEmptyCollectionCatalog(store);

        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        SetCollection(root, collection, null);
        File.WriteAllText(store.FilePath, root.ToJsonString());
        byte[] damaged = File.ReadAllBytes(store.FilePath);

        BooruCatalogStore reopened = new(directory.Path, lease);
        Assert.Throws<InvalidDataException>(() => reopened.Read());
        // A failed parse must also block a later mutation instead of replacing the damaged file with an empty catalog.
        Assert.Throws<InvalidDataException>(() => reopened.UpsertPost(EmptyClaim("124"), Now));
        Assert.Equal(damaged, File.ReadAllBytes(store.FilePath));
        Assert.Single(Directory.GetFiles(directory.Path, BooruCatalogStore.FileName));
        Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
    }

    [Fact]
    public void EmptyRootAndNestedCollectionsRemainValid()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        File.WriteAllText(store.FilePath, "{\"schemaVersion\":1,\"posts\":[],\"watchers\":[]}");
        Assert.Empty(store.Read().Posts);
        Assert.Empty(store.Read().Watchers);

        WriteEmptyCollectionCatalog(store);
        BooruCatalogSnapshot snapshot = store.Read();
        BooruPostRecord post = Assert.Single(snapshot.Posts);
        Assert.Empty(post.Findings);
        Assert.Empty(post.Post.Tags);
        Assert.Empty(post.Post.Media);
        Assert.Empty(Assert.Single(snapshot.Watchers).KnownPostIds);
    }

    [Fact]
    public void OmittedClaimCollectionsKeepTheirExistingLegacyDefault()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteEmptyCollectionCatalog(store);
        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        JsonObject claim = root["posts"]![0]!["post"]!.AsObject();
        Assert.True(claim.Remove("tags"));
        Assert.True(claim.Remove("media"));
        File.WriteAllText(store.FilePath, root.ToJsonString());

        BooruPostClaim saved = Assert.Single(store.Read().Posts).Post;
        Assert.Empty(saved.Tags);
        Assert.Empty(saved.Media);
    }

    [Theory]
    [InlineData("findings")]
    [InlineData("knownPostIds")]
    public void MissingRequiredRecordCollectionsRemainRejected(string collection)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        WriteEmptyCollectionCatalog(store);
        JsonObject root = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        if (collection == "findings")
            Assert.True(root["posts"]![0]!.AsObject().Remove(collection));
        else
            Assert.True(root["watchers"]![0]!.AsObject().Remove(collection));
        File.WriteAllText(store.FilePath, root.ToJsonString());

        Assert.Throws<InvalidDataException>(() => new BooruCatalogStore(directory.Path, lease).Read());
    }

    private static void SetCollection(JsonObject root, string collection, JsonNode? value)
    {
        switch (collection)
        {
            case "posts":
                root["posts"] = value;
                break;
            case "watchers":
                root["watchers"] = value;
                break;
            case "findings":
                root["posts"]![0]!["findings"] = value;
                break;
            case "knownPostIds":
                root["watchers"]![0]!["knownPostIds"] = value;
                break;
            case "tags":
                root["posts"]![0]!["post"]!["tags"] = value;
                break;
            case "media":
                root["posts"]![0]!["post"]!["media"] = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(collection), collection, "Unknown catalog collection.");
        }
    }

    private static void WriteEmptyCollectionCatalog(BooruCatalogStore store)
    {
        store.UpsertPost(EmptyClaim("123"), Now);
        store.UpsertWatcher(new("w-empty", "danbooru", "cat", "Active", Now, KnownPostIds: []));
    }

    private static BooruPostClaim EmptyClaim(string id) => new("danbooru", id,
        BooruPostClaimPresence.Tags | BooruPostClaimPresence.Media,
        $"https://danbooru.donmai.us/posts/{id}", Tags: [], Media: []);

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "meowchan-booru-null-collections-" + Guid.NewGuid().ToString("N"));

        internal TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
