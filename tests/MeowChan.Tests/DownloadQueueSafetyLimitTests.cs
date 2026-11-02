using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed partial class DownloadQueueTests
{
    [Fact]
    public async Task SafetyLimitDefaultsTo32AndIsInstanceLocal()
    {
        DownloadQueue first = new(int.MaxValue, UnthrottledBandwidth);
        DownloadQueue second = new(100, UnthrottledBandwidth);
        try
        {
            Assert.Equal(32, DownloadQueue.ProtectiveParallelDownloadsCeiling);
            Assert.Equal(256, DownloadQueue.MaximumParallelDownloadsSafetyLimit);
            Assert.Equal(32, first.ParallelDownloadsSafetyLimit);
            Assert.Equal((int.MaxValue, 32, 0, int.MaxValue - 32), first.GetConcurrencySnapshot());
            first.UpdateParallelDownloadsSafetyLimit(256);
            Assert.Equal((int.MaxValue, 256, 0, int.MaxValue - 256), first.GetConcurrencySnapshot());
            Assert.Equal(32, second.ParallelDownloadsSafetyLimit);
            first.UpdateMaxParallelDownloads(3); // Existing API must respect the instance fence.
            Assert.Equal((3, 3, 0, 0), first.GetConcurrencySnapshot());
            first.UpdateParallelDownloadsSafetyLimit(1);
            Assert.Equal((3, 1, 0, 2), first.GetConcurrencySnapshot());
        }
        finally
        {
            await first.StopAllAsync().WaitAsync(TestWait.Timeout);
            await second.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(100, 32, 32)]
    [InlineData(100, 40, 40)]
    [InlineData(8, 3, 3)]
    [InlineData(3, 8, 3)]
    [InlineData(8, 1, 1)]
    public async Task SafetyLimitBoundsActualDispatchAboveAndBelowTheRequestedParallelism(int requested, int cap, int effective)
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(requested, UnthrottledBandwidth);
        try
        {
            if (cap != 32) queue.UpdateParallelDownloadsSafetyLimit(cap);
            int jobs = effective + 2;
            for (int id = 1; id <= jobs; id++)
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}.bin", $"safety-{id}")));
            int remaining = jobs;
            while (remaining > 0)
            {
                int wave = Math.Min(effective, remaining);
                await handler.WaitForStartsAsync(wave);
                Assert.InRange(handler.ActiveRequests, wave, effective);
                Assert.InRange(queue.GetConcurrencySnapshot().Active, wave, effective);
                Assert.Equal(effective, queue.GetConcurrencySnapshot().Effective);
                Assert.InRange(handler.MaximumActiveRequests, 1, effective);
                handler.Release(wave);
                remaining -= wave;
            }
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal((requested, effective, 0, requested - effective), queue.GetConcurrencySnapshot());
            Assert.Equal(cap, queue.ParallelDownloadsSafetyLimit);
            Assert.Equal(jobs, handler.RequestCount);
            Assert.Equal(effective, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
            Assert.Equal(jobs * Payload.Length, queue.ReceivedMediaBytes);
            string[] files = Directory.GetFiles(directory.Path, "*.bin", SearchOption.AllDirectories);
            Assert.Equal(jobs, files.Length);
            foreach (string file in files) Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task InvalidSafetyLimitDoesNotMutateEitherConcurrencySetting(int invalid)
    {
        DownloadQueue queue = new(7, UnthrottledBandwidth);
        try
        {
            queue.UpdateParallelDownloadsSafetyLimit(4);
            var before = queue.GetConcurrencySnapshot();
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.UpdateParallelDownloadsSafetyLimit(invalid));
            Assert.Equal(before, queue.GetConcurrencySnapshot());
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.UpdateConcurrencyLimits(99, invalid));
            Assert.Equal(before, queue.GetConcurrencySnapshot());
            Assert.Equal(4, queue.ParallelDownloadsSafetyLimit);
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.UpdateConcurrencyLimits(0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.UpdateConcurrencyLimits(-1, 16));
            Assert.Equal(before, queue.GetConcurrencySnapshot());
            Assert.Equal(4, queue.ParallelDownloadsSafetyLimit);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task LowerSafetyLimitDrainsActiveTransfersAndLatestUpdatesReserveOnlyAvailableSlots()
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(8, UnthrottledBandwidth);
        try
        {
            queue.UpdateParallelDownloadsSafetyLimit(5);
            for (int id = 1; id <= 10; id++)
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}.bin", $"drain-{id}")));
            await handler.WaitForStartsAsync(5);
            queue.UpdateParallelDownloadsSafetyLimit(2);
            Assert.Equal((8, 2, 5, 6), queue.GetConcurrencySnapshot());
            for (int active = 4; active >= 2; active--)
            {
                handler.Release(1);
                Uri returned = await handler.WaitForReturnAsync();
                await TestWait.UntilAsync(() => !queue.IsInQueue(returned.ToString()));
                Assert.Equal(active, queue.GetConcurrencySnapshot().Active);
                Assert.Equal(5, handler.RequestCount);
                Assert.Equal(0, handler.CancellationCount);
            }
            handler.Release(1);
            Uri replaced = await handler.WaitForReturnAsync();
            await TestWait.UntilAsync(() => !queue.IsInQueue(replaced.ToString()));
            await handler.WaitForStartsAsync(1);
            Assert.Equal(2, handler.ActiveRequests);
            Assert.Equal(6, handler.RequestCount);

            // Raising a fence dispatches queued work without waiting for an existing job.
            queue.UpdateParallelDownloadsSafetyLimit(4);
            await handler.WaitForStartsAsync(2);
            Assert.Equal(4, handler.ActiveRequests);
            queue.UpdateConcurrencyLimits(2, 8);
            Assert.Equal((2, 2, 4, 0), queue.GetConcurrencySnapshot());
            queue.UpdateMaxParallelDownloads(3);
            Assert.Equal((3, 3, 4, 0), queue.GetConcurrencySnapshot());
            queue.UpdateParallelDownloadsSafetyLimit(1); // Latest update wins while four jobs drain.
            Assert.Equal((3, 1, 4, 2), queue.GetConcurrencySnapshot());
            for (int active = 3; active >= 1; active--)
            {
                handler.Release(1);
                Uri returned = await handler.WaitForReturnAsync();
                await TestWait.UntilAsync(() => !queue.IsInQueue(returned.ToString()));
                Assert.Equal(active, queue.GetConcurrencySnapshot().Active);
                Assert.Equal(8, handler.RequestCount);
            }
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal(1, handler.ActiveRequests);
            Assert.Equal(9, handler.RequestCount);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal(1, handler.ActiveRequests);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal((3, 1, 0, 2), queue.GetConcurrencySnapshot());
            Assert.Equal(10, handler.RequestCount);
            Assert.Equal(5, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistryAppliesRequestedAndSafetyLimitsWithoutTransientExtraReservations(bool raiseRequested)
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(raiseRequested ? 2 : 8, UnthrottledBandwidth);
        queue.UpdateParallelDownloadsSafetyLimit(raiseRequested ? 8 : 2);
        MemoryWorkStateStore work = new();
        await using Registry registry = new([board], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        try
        {
            var thread = Thread(board, 500);
            var owner = work.CommitIntent(new WorkResource
            {
                Url = thread.Url, OutputRoot = directory.Path, Generation = 1,
                Intent = WorkIntent.Watching, IsThread = true
            });
            queue.BindWork(thread, owner);
            for (int id = 1; id <= 8; id++)
                Assert.True(queue.TryEnqueue(CreateItem(board, thread, directory, $"{id}.bin", $"atomic-{id}")));
            await handler.WaitForStartsAsync(2);
            int nextRequested = raiseRequested ? 8 : 2;
            int nextCap = raiseRequested ? 2 : 8;
            await registry.ApplyOptionsAsync(SafetyOptions(directory.Path, nextRequested, nextCap), persistSession: false);
            // Active counts reservations, including any not yet scheduled onto HTTP.
            Assert.Equal((nextRequested, 2, 2, nextRequested - 2), queue.GetConcurrencySnapshot());
            Assert.Equal(nextCap, queue.ParallelDownloadsSafetyLimit);
            for (int wave = 0; wave < 4; wave++)
            {
                if (wave != 0) await handler.WaitForStartsAsync(2);
                Assert.Equal(2, handler.ActiveRequests);
                Assert.InRange(handler.MaximumActiveRequests, 1, 2);
                handler.Release(2);
            }
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(8, handler.RequestCount);
            Assert.Equal(2, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
            // A later options application replaces the pair rather than retaining an old fence.
            await registry.ApplyOptionsAsync(SafetyOptions(directory.Path, 12, 6), persistSession: false);
            Assert.Equal((12, 6, 0, 6), queue.GetConcurrencySnapshot());
        }
        finally { await registry.StopAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task RegistryRejectsInvalidSafetyOptionsBeforeSettingsCallbackOrQueueMutation()
    {
        using TempDirectory directory = new();
        DownloadQueue queue = new(7, UnthrottledBandwidth);
        queue.UpdateParallelDownloadsSafetyLimit(4);
        await using Registry registry = new([], queue, directory.Path, TimeSpan.FromMinutes(1), 60, persistSession: false);
        bool committed = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.ApplyOptionsAsync(
            SafetyOptions(directory.Path, 99, 257), persistSession: false, commitSettings: () => committed = true));
        Assert.False(committed);
        Assert.Equal((7, 4, 0, 3), queue.GetConcurrencySnapshot());
        Assert.Equal(4, queue.ParallelDownloadsSafetyLimit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(256)]
    public void SessionOptionsAcceptBoundedSafetyLimitAndRetainCompatibleDefault(int cap)
    {
        Assert.Equal(32, new SessionOptions().ParallelDownloadsSafetyLimit);
        SafetyOptions(Path.GetTempPath(), int.MaxValue, cap).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void SessionOptionsRejectOutOfRangeSafetyLimit(int cap)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => SafetyOptions(Path.GetTempPath(), 5, cap).Validate());
        Assert.Equal(nameof(SessionOptions.ParallelDownloadsSafetyLimit), failure.ParamName);
    }

    private static SessionOptions SafetyOptions(string path, int requested, int cap) => new()
    {
        DownloadPath = path, MaxParallelDownloads = requested,
        ParallelDownloadsSafetyLimit = cap, BandwidthBytesPerSecond = UnthrottledBandwidth
    };
}
