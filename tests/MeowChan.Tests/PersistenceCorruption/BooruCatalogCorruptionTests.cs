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

    internal override IReadOnlyDictionary<string, string> KnownBugs => new Dictionary<string, string>
    {
        ["primary:duplicate-array-property-different-value"] =
            "BUG: BooruCatalogStore accepts a repeated posts property and keeps the last one, so a duplicate silently empties "
            + "the catalog instead of being rejected."
    };
}

public sealed class BooruCatalogCorruptionTests(ITestOutputHelper log) : StoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new BooruCatalogAdapter();

    [Fact(Skip = "BUG: booru-catalog.json is parsed without a duplicate-property check, so a document whose posts property appears twice "
        + "reads as the last (empty) one. The store reports the emptied catalog as good data although it has no backup and is "
        + "documented to fail closed, and the next write persists it. Its sibling stores reject this (FavoriteStore.CheckObject at "
        + "libmeowchan/Favorites/FavoriteStore.cs:334-345, SavedArchiveSearchStore.CheckDuplicates at "
        + "libmeowchan/Persistence/SavedArchiveSearchStore.cs:279-284).")]
    public void ARepeatedPostsPropertyNeverSilentlyEmptiesTheCatalog() =>
        CorruptionScenarios.DamagedPrimary(new BooruCatalogAdapter(), "duplicate-array-property-different-value", null, "bug");
}
