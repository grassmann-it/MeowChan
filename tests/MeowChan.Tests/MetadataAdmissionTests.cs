using MeowChan.Downloader;

namespace MeowChan.Tests;

public sealed class MetadataAdmissionTests
{
    [Fact]
    public async Task FourSourcesRunConcurrentlyButOneSourceIsSerialAndBothPrioritiesProgress()
    {
        MetadataAdmission admission = new();
        IDisposable[] held = new IDisposable[4];
        for (int i = 0; i < 4; i++) held[i] = await admission.AcquireAsync($"source{i}", DownloadPriority.Manual, default);
        Task<IDisposable> manual = admission.AcquireAsync("source0", DownloadPriority.Manual, default);
        Task<IDisposable> board = admission.AcquireAsync("source0", DownloadPriority.BoardDiscovery, default);
        Task<IDisposable> other = admission.AcquireAsync("other", DownloadPriority.Manual, default);
        Assert.Equal((4, 3), admission.Snapshot);
        held[1].Dispose();
        using IDisposable otherLease = await other.WaitAsync(TestWait.Timeout);
        Assert.False(manual.IsCompleted);
        Assert.False(board.IsCompleted);
        held[0].Dispose();
        // Both classes receive service; a continuously present manual request cannot starve board work.
        using (IDisposable boardLease = await board.WaitAsync(TestWait.Timeout)) Assert.False(manual.IsCompleted);
        using IDisposable manualLease = await manual.WaitAsync(TestWait.Timeout);
        held[2].Dispose(); held[3].Dispose();
        Assert.Equal((2, 0), admission.Snapshot);
    }

    [Fact]
    public async Task FiveHundredWatchesUseBoundedWaitersAndCancellationReleasesEverySlot()
    {
        MetadataAdmission admission = new();
        using IDisposable held = await admission.AcquireAsync("one", DownloadPriority.Manual, default);
        using CancellationTokenSource cancellation = new();
        Task<IDisposable>[] watches = Enumerable.Range(0, 500).Select(i => admission.AcquireAsync("one",
            i % 2 == 0 ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery, cancellation.Token)).ToArray();
        Assert.Equal((1, 256), admission.Snapshot);
        cancellation.Cancel();
        foreach (Task<IDisposable> watch in watches)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch.WaitAsync(TestWait.Timeout));
        Assert.Equal((1, 0), admission.Snapshot);
        held.Dispose();
        Assert.Equal((0, 0), admission.Snapshot);
    }

    [Fact]
    public async Task StopClosesPendingAndFutureMetadataCalls()
    {
        DownloadQueue queue = new(1, 1024);
        using IDisposable held = await queue.AcquireMetadataAsync("one", DownloadPriority.Manual);
        Task<IDisposable> pending = queue.AcquireMetadataAsync("one", DownloadPriority.BoardDiscovery);
        await queue.StopAllAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.AcquireMetadataAsync("two", DownloadPriority.Manual));
    }
}
