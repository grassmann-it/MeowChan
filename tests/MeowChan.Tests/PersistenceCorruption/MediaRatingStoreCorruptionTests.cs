using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>media-ratings.json: damaged, newer, oversized and not-a-file cases.</summary>
internal sealed class MediaRatingStoreAdapter : StoreAdapter
{
    internal override string Name => "ratings";
    internal override string PrimaryName => "media-ratings.json";
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => ProfileStoreLimits.FavoriteFileCeilingBytes;

    private static FavoriteAssetKey Key(int index) =>
        new(new ResourceId("s", "b", "1"), "m-" + Secret + "-" + index, "original", new string('b', 64));

    internal override void Seed(string directory, ProfileLease lease)
    {
        MediaRatingStore store = new(directory, lease);
        store.Set(Key(1), 3);
        store.Set(Key(2), 5);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
        string.Join("|", new MediaRatingStore(directory, lease).List().Select(entry => entry.Key.MediaIdentifier + "=" + entry.Value)) + "#");

    internal override void Mutate(string directory, ProfileLease lease) => new MediaRatingStore(directory, lease).Set(Key(3), 4);

    private const string DuplicateProperty =
        "BUG: MediaRatingStore accepts a repeated \"Entries\" property and keeps the last one, so a duplicate silently empties "
        + "the rating list instead of being rejected as it is by FavoriteStore.CheckObject.";

    internal override IReadOnlyDictionary<string, string> KnownBugs => new Dictionary<string, string>
    {
        ["primary:duplicate-array-property-different-value"] = DuplicateProperty,
        ["primary-good-backup:duplicate-array-property-different-value"] = DuplicateProperty,
        ["both:duplicate-array-property-different-value"] = DuplicateProperty,
        ["backup:duplicate-array-property-different-value"] = DuplicateProperty
    };
}

public sealed class MediaRatingStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new MediaRatingStoreAdapter();

    [Fact(Skip = "BUG: media-ratings.json is parsed without a duplicate-property check, so a document whose \"Entries\" property appears "
        + "twice reads as the last (empty) one: every rating silently disappears and the store still reports the document as good, so "
        + "the next rating writes that empty list over the user's data. Its sibling stores reject this (FavoriteStore.CheckObject at "
        + "libmeowchan/Favorites/FavoriteStore.cs:334-345, ThreadFavoriteStore.CheckObject at libmeowchan/Favorites/ThreadFavoriteStore.cs:231-240).")]
    public void ARepeatedEntriesPropertyNeverSilentlyEmptiesTheRatings() =>
        CorruptionScenarios.DamagedPrimary(new MediaRatingStoreAdapter(), "duplicate-array-property-different-value", null, "bug");
}
