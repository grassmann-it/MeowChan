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
    public async Task FiveHundredMixedWatchesCompleteWithinGlobalAndPerSourceBudgets()
    {
        MetadataAdmission admission = new();
        int active = 0;
        int peak = 0;
        int[] sourceActive = new int[8];
        int manual = 0, board = 0;
        Task[] watches = Enumerable.Range(0, 500).Select(async i =>
        {
            int source = i % sourceActive.Length;
            DownloadPriority priority = i % 3 == 0 ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery;
            using IDisposable slot = await admission.AcquireAsync($"source{source}", priority, default);
            Assert.Equal(1, Interlocked.Increment(ref sourceActive[source]));
            int current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref peak); }
            while (current > previous && Interlocked.CompareExchange(ref peak, current, previous) != previous);
            await Task.Yield();
            if (priority == DownloadPriority.Manual) Interlocked.Increment(ref manual); else Interlocked.Increment(ref board);
            Interlocked.Decrement(ref active);
            Interlocked.Decrement(ref sourceActive[source]);
        }).ToArray();
        await Task.WhenAll(watches).WaitAsync(TestWait.Timeout);
        Assert.InRange(peak, 1, 4);
        Assert.Equal(167, manual);
        Assert.Equal(333, board);
        Assert.Equal((0, 0), admission.Snapshot);
    }

    [Fact]
    public async Task PauseKeepsAlreadyWaitingMetadataOutOfFreedSlotsUntilResume()
    {
        DownloadQueue queue = new(1, 1024);
        using IDisposable held = await queue.AcquireMetadataAsync("one", DownloadPriority.Manual);
        Task<IDisposable> pending = queue.AcquireMetadataAsync("one", DownloadPriority.BoardDiscovery);
        await queue.SetPausedAsync(true);
        held.Dispose();
        Assert.Equal((0, 1), queue.GetMetadataCapacitySnapshot());
        Assert.False(pending.IsCompleted);
        await queue.SetPausedAsync(false);
        using IDisposable next = await pending.WaitAsync(TestWait.Timeout);
        await queue.StopAllAsync();
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
