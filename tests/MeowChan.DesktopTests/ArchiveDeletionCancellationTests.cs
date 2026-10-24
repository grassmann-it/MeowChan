using System.Collections;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.DesktopTests;

public sealed class ArchiveDeletionCancellationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAfterConfirmedDeleteReturnsActualIdsAndCleansOnlyThoseIds(int threadCount)
    {
        using TempDirectory temp = new();
        ResourceId[] ids = Enumerable.Range(0, threadCount).Select(index => new ResourceId("4chan", "g", (900 + index).ToString())).ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            MediaRatingStore ratings = new(temp.Path, lease);
            FavoriteStore favorites = new(temp.Path, lease);
            foreach (ResourceId id in ids)
            {
                await catalog.RegisterThreadAsync(new(id, $"https://boards.4chan.org/g/thread/{id.Thread}", id.Thread!,
                    Path.Combine(temp.Path, "missing-media-" + id.Thread), true));
                ratings.Set(Key(id), 4);
                favorites.SetFavorite(new(Key(id), 12, "image.png"), true);
            }
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync(ids);
        Assert.All(plan.Files.Directories, directory => Assert.Empty(directory.AssetSha256));
        using CancellationTokenSource cancellation = new();
        CancelOnEnumeration cancellationPoint = new(cancellation);
        // Hashes are first enumerated after Catalog.DeleteThreadsAsync returned and
        // the confirmed ID was recorded. This is a synchronous boundary, not a timer.
        ArchiveDirectoryPlan[] directories = plan.Files.Directories.Select(directory => new ArchiveDirectoryPlan(
            directory.Directory, directory.Generation, directory.Files, directory.Leftovers, directory.Blocker,
            cancellationPoint)).ToArray();
        plan = plan with { Files = new ArchiveFilesPlan(plan.Files.ProfileDirectory, directories) };

        ArchivePermanentDeletionResult? result = null;
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            result = await host.DeleteArchivePermanentlyAsync(plan, cancellation.Token);
        });
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, cancellationPoint.Enumerations);
        Assert.Null(failure);
        Assert.NotNull(result);
        ResourceId deleted = Assert.Single(result.DeletedThreads);
        Assert.Contains(deleted, ids);
        Assert.Null(result.RatingCleanupThreads);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        foreach (ResourceId id in ids)
        {
            if (id == deleted)
            {
                Assert.Null(await reader.ReadThreadAsync(id));
                Assert.Null(host.GetMediaRating(Key(id)));
                Assert.DoesNotContain(host.GetFavorites(), entry => entry.Key == Key(id));
            }
            else
            {
                Assert.NotNull(await reader.ReadThreadAsync(id));
                Assert.Equal(4, host.GetMediaRating(Key(id)));
                Assert.Contains(host.GetFavorites(), entry => entry.Key == Key(id));
            }
        }
        if (threadCount > 1) Assert.NotEmpty(result.Errors);
        else Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CancellationBeforeFirstCommitKeepsCatalogRatingsAndFavorites()
    {
        using TempDirectory temp = new();
        ResourceId id = new("4chan", "g", "999");
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            await catalog.RegisterThreadAsync(new(id, "https://boards.4chan.org/g/thread/999", "999", Path.Combine(temp.Path, "missing"), true));
            new MediaRatingStore(temp.Path, lease).Set(Key(id), 4);
            new FavoriteStore(temp.Path, lease).SetFavorite(new(Key(id), 12, "image.png"), true);
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([id]);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DeleteArchivePermanentlyAsync(plan, cancellation.Token));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.NotNull(await reader.ReadThreadAsync(id));
        Assert.Equal(4, host.GetMediaRating(Key(id)));
        Assert.Contains(host.GetFavorites(), entry => entry.Key == Key(id));
    }

    private static FavoriteAssetKey Key(ResourceId id) => new(id, "image", "original", new string('a', 64));

    private sealed class CancelOnEnumeration(CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        internal int Enumerations { get; private set; }
        public int Count => 0;
        public string this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator()
        {
            Enumerations++;
            cancellation.Cancel();
            return Enumerable.Empty<string>().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
