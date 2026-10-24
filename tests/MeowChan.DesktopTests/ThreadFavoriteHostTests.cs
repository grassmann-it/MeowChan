using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ThreadFavoriteHostTests
{
    private static ResourceId Thread => new("source", "board", "42");

    [Fact]
    public async Task CompleteThreadSnapshotSurvivesRestartAndIsProfileOwnedWithoutAssets()
    {
        using TempDirectory temp = new();
        string fingerprint;
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            ThreadFavoriteLoadSnapshot empty = host.GetThreadFavoriteSnapshot();
            Assert.Equal(ThreadFavoriteLoadState.Ready, empty.State); Assert.Empty(empty.Snapshot!.Threads);
            ResourceId[] ids = [Thread, Thread with { Source = "other" }, Thread with { Board = "other" }, Thread with { Thread = "43" }];
            foreach (ResourceId id in ids) await host.SetThreadFavoriteAsync(id, true);
            ThreadFavoriteLoadSnapshot marked = host.GetThreadFavoriteSnapshot();
            Assert.Equal(ids, marked.Snapshot!.Threads); Assert.Equal(4, marked.Snapshot.Revision);
            fingerprint = marked.Snapshot.Fingerprint;
            Assert.Equal(ids, host.GetThreadFavorites()); Assert.Empty(host.GetFavorites());
        }
        await using (DesktopSessionHost restored = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await restored.StartAsync();
            Assert.Equal(fingerprint, restored.GetThreadFavoriteSnapshot().Snapshot!.Fingerprint);
        }
        using TempDirectory other = new();
        await using DesktopSessionHost separate = new(other.Path, Array.Empty<IImageBoard>());
        await separate.StartAsync(); Assert.Empty(separate.GetThreadFavorites());
    }

    [Fact]
    public async Task FailedWriteDoesNotPublishRequestedMarkAndRetryCommits()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        var committed = await host.SetThreadFavoriteAsync(Thread, true);
        string backup = Path.Combine(temp.Path, "thread-favorites.json.bak");
        Directory.CreateDirectory(backup);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.SetThreadFavoriteAsync(Thread, false));
        Assert.Equal(committed.Snapshot.Fingerprint, host.GetThreadFavoriteSnapshot().Snapshot!.Fingerprint);
        Assert.Equal(Thread, Assert.Single(host.GetThreadFavorites()));
        Directory.Delete(backup);
        Assert.True((await host.SetThreadFavoriteAsync(Thread, false)).Changed);
        Assert.Empty(host.GetThreadFavorites());
    }

    [Fact]
    public async Task FreshProtectionQueryRejectsFutureOrDamagedIndexInsteadOfReturningCachedOrEmptySet()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync(); await host.SetThreadFavoriteAsync(Thread, true);
        string file = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] valid = File.ReadAllBytes(file);
        foreach (string damaged in new[] { "{bad", "{\"Version\":99,\"Revision\":2,\"Threads\":[]}" })
        {
            File.WriteAllText(file, damaged);
            ThreadFavoriteLoadSnapshot unavailable = host.GetThreadFavoriteSnapshot();
            Assert.Equal(ThreadFavoriteLoadState.Unavailable, unavailable.State);
            Assert.Null(unavailable.Snapshot); Assert.False(string.IsNullOrWhiteSpace(unavailable.Error));
            Assert.Throws<InvalidDataException>(() => host.GetThreadFavorites());
            await Assert.ThrowsAsync<InvalidDataException>(() => host.SetThreadFavoriteAsync(Thread, false));
            Assert.Equal(damaged, File.ReadAllText(file));
        }
        File.WriteAllBytes(file, valid);
        Assert.Equal(ThreadFavoriteLoadState.Ready, host.GetThreadFavoriteSnapshot().State);
        Assert.Equal(Thread, Assert.Single(host.GetThreadFavorites()));
    }

    [Fact]
    public async Task CanceledAndStoppedHostCannotChangeThreadMarks()
    {
        using TempDirectory temp = new();
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.SetThreadFavoriteAsync(Thread, true, new CancellationToken(true)));
        Assert.Empty(host.GetThreadFavorites());
        await host.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetThreadFavoriteAsync(Thread, true));
        Assert.Empty(host.GetThreadFavorites());
    }
}
