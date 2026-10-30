using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class DesktopSessionUserPriorityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MultipleExactWatchesCanBeBoostedWhilePausedWithoutStartingNetworkOrPersistingBoost()
    {
        using TempDirectory directory = new();
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false,
            DownloadPath = Path.Combine(directory.Path, "media"), MaxParallelDownloads = 1000 }.SaveConfig(directory.Path);
        using NoNetwork handler = new();
        using HttpClient client = new(handler);
        await using DesktopSessionHost host = new(directory.Path, [new FourChanImageBoard(client)]);
        await host.StartAsync().WaitAsync(Deadline);
        var first = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Deadline);
        var second = await host.AddAsync("https://boards.4chan.org/g/thread/456").WaitAsync(Deadline);
        long[] ids = [first.WatchId!.Value, second.WatchId!.Value];
        foreach (long id in ids) Assert.True(await host.SetUserThreadPriorityAsync(id, true).WaitAsync(Deadline));
        DesktopSessionProjection projection = await host.CaptureProjectionAsync().WaitAsync(Deadline);
        Assert.Equal(ids.Order(), projection.HighPriorityWatches.Order());
        Assert.True(host.IsPaused);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1000, projection.MediaConcurrency.Configured);
        Assert.Equal(32, projection.MediaConcurrency.Effective);
        Assert.Equal(0, projection.MediaConcurrency.Active);
        Assert.Equal(1000, Config.LoadConfig(directory.Path).MaxParallelDownloads);
        Assert.True(await host.SetUserThreadPriorityAsync(ids[0], false).WaitAsync(Deadline));
        Assert.Equal(ids[1], Assert.Single(host.Projection.HighPriorityWatches));
        Assert.False(await host.SetUserThreadPriorityAsync(long.MaxValue, true).WaitAsync(Deadline));
        await host.RemoveThreadAsync(ids[1]).WaitAsync(Deadline);
        Assert.False(await host.SetUserThreadPriorityAsync(ids[1], true).WaitAsync(Deadline));
        var replacement = await host.AddAsync("https://boards.4chan.org/g/thread/456").WaitAsync(Deadline);
        Assert.NotEqual(ids[1], replacement.WatchId);
        Assert.Empty((await host.CaptureProjectionAsync().WaitAsync(Deadline)).HighPriorityWatches);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PriorityCommandBeforeReadyIsRefused()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetUserThreadPriorityAsync(1, true));
        Assert.Empty(host.Projection.HighPriorityWatches);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Paused priority commands must not send network requests.");
        }
    }
}
