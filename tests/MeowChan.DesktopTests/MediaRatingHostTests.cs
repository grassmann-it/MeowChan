using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.DesktopTests;
public sealed class MediaRatingHostTests
{
    private static FavoriteAssetKey Key => new(new("4chan", "g", "42"), "image", "original", new string('a', 64));
    [Fact]
    public async Task CommitPublishesExactKeyAndFailurePreservesCacheAndOtherProfiles()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path); // Establish valid settings before any other profile evidence.
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            var result = await host.SetMediaRatingAsync(Key, 4);
            Assert.Equal(Key, result.Key); Assert.Equal(4, result.Value); Assert.Equal(4, host.GetMediaRating(Key));
            Assert.Null(host.GetMediaRating(Key with { Variant = "thumbnail" }));
            Directory.CreateDirectory(Path.Combine(temp.Path, "media-ratings.json.bak"));
            await Assert.ThrowsAsync<InvalidDataException>(() => host.SetMediaRatingAsync(Key, 2));
            Assert.Equal(4, host.GetMediaRating(Key));
            Directory.Delete(Path.Combine(temp.Path, "media-ratings.json.bak"));
            await host.SetMediaRatingAsync(Key, 2);
        }
        await using (DesktopSessionHost restored = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await restored.StartAsync(); Assert.Equal(2, restored.GetMediaRating(Key));
        }
        using TempDirectory other = new();
        await using DesktopSessionHost unrelated = new(other.Path, Array.Empty<IImageBoard>());
        await unrelated.StartAsync(); Assert.Empty(unrelated.GetMediaRatings());
    }
    [Fact]
    public async Task DeletionCleanupFailureReturnsFrozenIdsAndRetryNeverRepeatsArchiveDeletion()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path); // Establish valid settings before any other profile evidence.
        FavoriteAssetKey keep = Key with { Thread = new("4chan", "g", "43") };
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            await catalog.RegisterThreadAsync(new(Key.Thread, "https://boards.4chan.org/g/thread/42", "42", Path.Combine(temp.Path, "missing-media"), true));
            MediaRatingStore store = new(temp.Path, lease); store.Set(Key, 5); store.Set(keep, 3);
            File.Delete(Path.Combine(temp.Path, "media-ratings.json.bak"));
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([Key.Thread]);
        Assert.Equal(1, plan.RatingCount);
        Directory.CreateDirectory(Path.Combine(temp.Path, "media-ratings.json.bak"));
        ArchivePermanentDeletionResult result = await host.DeleteArchivePermanentlyAsync(plan);
        Assert.Equal(new[] { Key.Thread }, result.DeletedThreads);
        Assert.Equal(new[] { Key.Thread }, result.RatingCleanupThreads);
        Assert.Contains(result.Errors, error => error.Contains("ratings"));
        Assert.Equal(5, host.GetMediaRating(Key));
        Directory.Delete(Path.Combine(temp.Path, "media-ratings.json.bak"));
        await host.RetryMediaRatingCleanupAsync(result.RatingCleanupThreads!);
        await host.RetryMediaRatingCleanupAsync(result.RatingCleanupThreads!);
        Assert.Null(host.GetMediaRating(Key)); Assert.Equal(3, host.GetMediaRating(keep));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Null(await reader.ReadThreadAsync(Key.Thread));
    }
    [Fact]
    public async Task InvalidRatingIndexBlocksDeletionBeforeFilesAndDoesNotBlockFavorites()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path); // Establish valid settings before any other profile evidence.
        string file = Path.Combine(temp.Path, "media-ratings.json"); File.WriteAllText(file, "{bad");
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => host.PrepareArchiveDeletionAsync([Key.Thread]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetMediaRatingAsync(Key, 4));
        Assert.True(await host.SetFavoriteAsync(new FavoriteEntry(Key, 10, "gone.png"), true));
        Assert.Single(host.GetFavorites()); Assert.Equal("{bad", File.ReadAllText(file));
    }
}
