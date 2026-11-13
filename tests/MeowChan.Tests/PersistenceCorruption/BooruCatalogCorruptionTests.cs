using MeowChan.Archive;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>booru-catalog.json keeps no backup by design and fails closed: invalid or newer data never becomes an empty catalog.</summary>
internal sealed class BooruCatalogAdapter : StoreAdapter
{
    internal override string Name => "booru-catalog";
    internal override string PrimaryName => BooruCatalogStore.FileName;
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => BooruCatalogStore.MaximumDocumentBytes;
    internal override bool HasBackup => false;

    private static BooruPostClaim Post(string id) => new("danbooru", id, BooruPostClaimPresence.Tags,
        $"https://danbooru.donmai.us/posts/{id}", Tags: [new BooruPostClaimTag("tag_" + Secret, BooruPostClaimTagCategory.General)]);

    internal override void Seed(string directory, ProfileLease lease)
    {
        BooruCatalogStore store = new(directory, lease);
        DateTimeOffset observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        store.UpsertPost(Post("1"), observed);
        store.UpsertPost(Post("2"), observed);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
    {
        BooruCatalogSnapshot snapshot = new BooruCatalogStore(directory, lease).Read();
        return string.Join("|", snapshot.Posts.Select(post => post.SourceId + "/" + post.PostId)) + "#" + snapshot.Watchers.Count;
    });

    internal override void Mutate(string directory, ProfileLease lease) =>
        new BooruCatalogStore(directory, lease).UpsertPost(Post("3"), DateTimeOffset.Parse("2026-11-12T13:00:00Z"));

}

public sealed class BooruCatalogCorruptionTests(ITestOutputHelper log) : StoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new BooruCatalogAdapter();

    // A repeated property fails closed: the read throws and the catalog is never written over.
    [Fact]
    public void ARepeatedPostsPropertyNeverSilentlyEmptiesTheCatalog() =>
        CorruptionScenarios.DamagedPrimary(new BooruCatalogAdapter(), "duplicate-array-property-different-value", null, "duplicate");

    [Fact]
    public void FilesWrittenByTheStoreStillLoad()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogAdapter adapter = new();
        adapter.Seed(temp.Path, lease);
        adapter.Mutate(temp.Path, lease);
        Assert.Equal(ReadKind.Data, adapter.Read(temp.Path, lease).Kind);
    }
}
