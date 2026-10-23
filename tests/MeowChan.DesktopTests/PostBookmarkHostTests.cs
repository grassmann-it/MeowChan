using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class PostBookmarkHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly PostBookmarkKey Key = new(new("4chan", "g", "123"), "124");

    [Fact]
    public async Task MissingOptionalStoreDoesNotWriteAndExplicitNoteSurvivesOwnerRestart()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "post-bookmarks.json");
        new Config { DownloadsPaused = true }.SaveConfig(temp.Path);
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync().WaitAsync(Timeout);
            Assert.Empty((await host.ReadPostBookmarksAsync()).Bookmarks);
            Assert.False(File.Exists(path));
            await host.SavePostBookmarkAsync(Key, "own\n🙂", 0, false).WaitAsync(Timeout);
            Assert.Empty(host.GetView().Threads); Assert.Empty(host.GetView().Boards);
        }
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync().WaitAsync(Timeout);
            Assert.Equal("own\n🙂", Assert.Single((await host.ReadPostBookmarksAsync()).Bookmarks).Note);
        }
    }

    [Fact]
    public async Task CorruptOptionalFeatureLeavesSessionReadyAndOriginalUntouched()
    {
        using TempDirectory temp = new();
        new Config { DownloadsPaused = true }.SaveConfig(temp.Path);
        string path = Path.Combine(temp.Path, "post-bookmarks.json");
        File.WriteAllText(path, "{bad user note file");
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Timeout);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReadPostBookmarksAsync());
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.Equal("{bad user note file", File.ReadAllText(path));
        Assert.Empty(host.GetView().Threads);
    }

    [Fact]
    public async Task QueuedEditorsCannotOverwriteAndStoppedOwnerCannotCommit()
    {
        using TempDirectory temp = new(); new Config { DownloadsPaused = true }.SaveConfig(temp.Path);
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Timeout);
        var first = host.SavePostBookmarkAsync(Key, "first", 0, false);
        var second = host.SavePostBookmarkAsync(Key, "second", 0, false);
        int successes = 0, conflicts = 0;
        foreach (var pending in new[] { first, second })
        {
            try { await pending.WaitAsync(Timeout); successes++; }
            catch (PostBookmarkConflictException) { conflicts++; }
        }
        Assert.Equal(1, successes); Assert.Equal(1, conflicts);
        var snapshot = await host.ReadPostBookmarksAsync();
        Assert.Single(snapshot.Bookmarks); Assert.Equal(1, snapshot.Revision);
        await host.StopAsync().WaitAsync(Timeout);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, "post-bookmarks.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SavePostBookmarkAsync(Key, "late", 1, true));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, "post-bookmarks.json")));
    }
}
