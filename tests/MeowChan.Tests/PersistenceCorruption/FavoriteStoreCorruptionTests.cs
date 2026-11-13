using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>media-favorites.json (v1/v2 with the private-batch marker fence): damaged, newer, oversized and not-a-file cases.</summary>
internal sealed class FavoriteStoreAdapter : StoreAdapter
{
    internal override string Name => "favorites";
    internal override string PrimaryName => "media-favorites.json";
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => ProfileStoreLimits.FavoriteFileCeilingBytes;

    internal static FavoriteEntry Entry(int index) => new(
        new FavoriteAssetKey(new ResourceId("s", "b", "1"), "m" + index, "original", new string('a', 64)), 3, "name-" + Secret + "-" + index + ".png");

    internal override void Seed(string directory, ProfileLease lease)
    {
        FavoriteStore store = new(directory, lease);
        store.SetFavorite(Entry(1), true);
        store.SetFavorite(Entry(2), true);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
        string.Join("|", new FavoriteStore(directory, lease).List().Select(entry => entry.Key.MediaIdentifier)) + "#");

    internal override void Mutate(string directory, ProfileLease lease) => new FavoriteStore(directory, lease).SetFavorite(Entry(3), true);
}

public sealed class FavoriteStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new FavoriteStoreAdapter();
}
