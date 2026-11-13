using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>media-ratings.json: damaged, newer, oversized and not-a-file cases.</summary>
internal sealed class MediaRatingStoreAdapter : StoreAdapter
{
    // PF-R3: a damaged backup is set aside as .bad; a backup of a newer version stays and stops the save.
    internal override bool BackupBlocksWrite(string variant, bool stillParses) => variant == "future-version";
    internal override bool SetsDamagedBackupAside => true;
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
}

public sealed class MediaRatingStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new MediaRatingStoreAdapter();

    // A repeated property is damage: the store refuses it and writes nothing, so the good copies stay.
    [Fact]
    public void ARepeatedEntriesPropertyNeverSilentlyEmptiesTheRatings() =>
        CorruptionScenarios.DamagedPrimary(new MediaRatingStoreAdapter(), "duplicate-array-property-different-value", null, "duplicate");

    [Fact]
    public void ARepeatedPropertyInsideAnEntryIsDamageToo()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStoreAdapter adapter = new();
        adapter.Seed(temp.Path, lease);
        string path = adapter.PrimaryPath(temp.Path);
        byte[] before = File.ReadAllBytes(path);
        // A second "Value" in the first entry: the serializer would keep 1 instead of the stored rating.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Value\":", "\"Value\":1,\"Value\":", StringComparison.Ordinal));
        Assert.NotEqual(before, File.ReadAllBytes(path));
        Assert.Equal(ReadKind.Unusable, adapter.Read(temp.Path, lease).Kind);
    }

    [Fact]
    public void FilesWrittenByTheStoreStillLoad()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        MediaRatingStoreAdapter adapter = new();
        adapter.Seed(temp.Path, lease);
        adapter.Mutate(temp.Path, lease);
        Assert.Equal(ReadKind.Data, adapter.Read(temp.Path, lease).Kind);
    }
}
