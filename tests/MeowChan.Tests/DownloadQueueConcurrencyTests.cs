using System.Diagnostics;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.Tests;

public sealed partial class DownloadQueueTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public async Task ExtremePreferenceBoundsLiveResponseBodiesAndCopyBuffers(int configured)
    {
        using TempDirectory directory = new();
        using BodyGateHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(configured, 10 * 1024 * 1024);
        try
        {
            IThread[] threads = Enumerable.Range(1, 25).Select(id => Thread(board, id)).ToArray();
            for (int id = 0; id < 50; id++)
                Assert.True(await queue.EnqueueAsync(CreateItem(board, threads[id % 25], directory,
                    $"{id}.bin", $"body-{id}")));
            for (int count = 0; count < DownloadQueue.ProtectiveParallelDownloadsCeiling; count++)
                await handler.Starts.Reader.ReadAsync().AsTask().WaitAsync(TestWait.Timeout);
            Assert.Equal(32, handler.LiveBodies);
            Assert.Equal(32, queue.GetConcurrencySnapshot().Active);
            Console.WriteLine($"C10203 body-held configured={configured} liveResponses={handler.LiveBodies} " +
                $"managedHeap={GC.GetTotalMemory(false)} copyBufferBytesUpperBound={32 * 81920}");
            handler.Release.TrySetResult();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(0, handler.LiveBodies);
            Assert.Equal(50 * Payload.Length, queue.ReceivedMediaBytes);
        }
        finally
        {
            handler.Release.TrySetResult();
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private sealed class BodyGateHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Threading.Channels.Channel<bool> Starts = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        internal int LiveBodies;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LiveBodies);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StreamContent(new Body(this)) });
        }

        private sealed class Body(BodyGateHandler owner) : Stream
        {
            private bool _read;
            private int _disposed;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_read) return 0;
                _read = true;
                owner.Starts.Writer.TryWrite(true);
                await owner.Release.Task.WaitAsync(cancellationToken);
                Payload.AsMemory().CopyTo(buffer);
                return Payload.Length;
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner.LiveBodies);
                base.Dispose(disposing);
            }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public async Task TwentyFiveThreadsUseBoundedEffectiveCapacityWithoutChangingPreference(int configured)
    {
        using TempDirectory directory = new();
        GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(configured, 10 * 1024 * 1024);
        int effective = Math.Min(configured, DownloadQueue.ProtectiveParallelDownloadsCeiling);
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            IThread[] threads = Enumerable.Range(1, 25).Select(id => Thread(board, id)).ToArray();
            // 250 jobs fits the unchanged 256 pending fence even at the smallest limit.
            foreach (int media in Enumerable.Range(0, 10))
                foreach (int thread in Enumerable.Range(0, 25))
                    Assert.True(await queue.EnqueueAsync(CreateItem(board, threads[thread], directory,
                        $"{thread}/{media}.bin", $"{thread}-{media}")));
            await handler.WaitForStartsAsync(effective);
            Assert.Equal(effective, handler.ActiveRequests);
            Assert.Equal((configured, effective, effective, configured - effective), queue.GetConcurrencySnapshot());
            Assert.Equal(configured, queue.MaxParallelDownloads);
            Assert.InRange(queue.GetCapacitySnapshot().Pending, 0, 256);
            long heldHeap = GC.GetTotalMemory(false);
            // Test scheduler progress, not whether this machine can durably publish all
            // 250 files within one TestWait.Timeout (especially on Windows CI). Keep
            // each wave held until every available slot has reached HTTP, then grant
            // exactly that wave's responses. No permits can leak into the next wave.
            // The first wave's start notifications were consumed above.
            int remaining = 250;
            while (remaining > 0)
            {
                int wave = Math.Min(effective, remaining);
                if (remaining != 250)
                    await handler.WaitForStartsAsync(wave);
                // A short final wave may overlap returning responses from the
                // preceding wave; full waves must occupy every effective slot.
                Assert.InRange(handler.ActiveRequests, wave, effective);
                Assert.Equal(configured, queue.MaxParallelDownloads);
                Assert.Equal(effective, queue.GetConcurrencySnapshot().Effective);
                Assert.InRange(handler.MaximumActiveRequests, 1, effective);
                handler.Release(wave);
                remaining -= wave;
            }
            // Only a capacity-bounded tail of publication/retirement remains, bounded
            // by the unchanged timeout; a stalled scheduler cannot pass silently.
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(250, handler.RequestCount);
            Assert.InRange(handler.MaximumActiveRequests, 1, effective);
            Assert.Equal(250 * Payload.Length, queue.ReceivedMediaBytes);
            string[] files = Directory.GetFiles(directory.Path, "*.bin", SearchOption.AllDirectories);
            Assert.Equal(250, files.Length);
            foreach (string file in files) Assert.Equal(Payload, await File.ReadAllBytesAsync(file));
            Console.WriteLine($"C10203 configured={configured} effective={effective} threads=25 jobs=250 " +
                $"peakHttp={handler.MaximumActiveRequests} heldManagedHeap={heldHeap} " +
                $"allocatedDelta={GC.GetTotalAllocatedBytes() - allocatedBefore} elapsedMs={elapsed.ElapsedMilliseconds}");
            queue.UpdateMaxParallelDownloads(int.MaxValue);
            Assert.Equal(int.MaxValue, queue.MaxParallelDownloads);
            Assert.Equal(DownloadQueue.ProtectiveParallelDownloadsCeiling, queue.GetConcurrencySnapshot().Effective);
            queue.UpdateMaxParallelDownloads(5);
            Assert.Equal((5, 5, 0, 0), queue.GetConcurrencySnapshot());
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.UpdateMaxParallelDownloads(0));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }
}
