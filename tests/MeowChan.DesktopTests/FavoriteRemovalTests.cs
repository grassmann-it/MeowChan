using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class FavoriteRemovalTests
{
    [Fact]
    public async Task RemovingUnavailableFavoritePreservesOtherIdentitiesAndLocalFilesAcrossRestart()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path); // Establish valid settings before any other profile evidence.
        FavoriteEntry missing = new(new FavoriteAssetKey(new ResourceId("4chan", "g", "100"), "old", "original", new string('a', 64)), 7, "gone.png");
        FavoriteEntry keep = missing with { Key = missing.Key with { Sha256 = new string('b', 64) } };
        string original = Path.Combine(temp.Path, "keep.png");
        await File.WriteAllTextAsync(original, "keep bytes");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            store.SetFavorite(missing, true);
            store.SetFavorite(keep, true);
        }
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            Assert.True(await host.SetFavoriteAsync(missing, false));
            Assert.False(await host.SetFavoriteAsync(missing, false));
            Assert.Equal(new[] { keep }, host.GetFavorites());
            Assert.Equal("keep bytes", await File.ReadAllTextAsync(original));
        }
        using ProfileLease restored = ProfileLease.Acquire(temp.Path);
        Assert.Equal(new[] { keep }, new FavoriteStore(temp.Path, restored).List());
    }
}
