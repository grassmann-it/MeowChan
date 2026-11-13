using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>thread-favorites.json: damaged, newer, oversized and not-a-file cases.</summary>
internal sealed class ThreadFavoriteStoreAdapter : StoreAdapter
{
    internal override string Name => "thread-favorites";
    internal override string PrimaryName => "thread-favorites.json";
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => ProfileStoreLimits.FavoriteFileCeilingBytes;

    private static ResourceId Thread(int index) => new("s-" + StoreAdapter.Secret, "b", index.ToString());

    internal override void Seed(string directory, ProfileLease lease)
    {
        ThreadFavoriteStore store = new(directory, lease);
        store.Set(Thread(1), true);
        store.Set(Thread(2), true);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
        string.Join("|", new ThreadFavoriteStore(directory, lease).ReadSnapshot().Threads.Select(thread => thread.Thread)) + "#");

    internal override void Mutate(string directory, ProfileLease lease) => new ThreadFavoriteStore(directory, lease).Set(Thread(3), true);
}

public sealed class ThreadFavoriteStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new ThreadFavoriteStoreAdapter();
}
