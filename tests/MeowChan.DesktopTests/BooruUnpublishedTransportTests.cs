using System.Net.Http;
using System.Reflection;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// F1: a recovered original can be accepted by the shared queue while its Booru composition is still unpublished.
// This keeps the real queue/catalog/profile lease in the path; only the provider wire is the in-memory stub.
public sealed class BooruUnpublishedTransportTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task PendingRecoveryAcceptedBeforePublicationKeepsTransportUntilQueueDrainAndStopsIdempotently()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new(7301));

        // First leave a real, durable Requested original behind by stopping the shared queue before it can run.
        using (DanbooruTestBinding seedBinding = new(stub))
        {
            DesktopSessionHost seed = await BooruCompositionHostTests.StartAsync(
                profile.Path, stub, () => seedBinding.Binding, paused: true);
            try
            {
                BooruCallResult<BooruPostView> read = await seed.BooruServices!.Search
                    .ReadPostAsync(post.PostId, CancellationToken.None).WaitAsync(Deadline);
                BooruLoadResult admitted = await seed.BooruServices.Queue
                    .LoadOriginalAsync(read.Value!, BooruLoadOrigin.OneShot, CancellationToken.None)
                    .WaitAsync(Deadline);
                Assert.Equal(BooruLoadStatus.Queued, admitted.Status);
            }
            finally
            {
                await seed.StopAsync().WaitAsync(Deadline);
                await seed.DisposeAsync();
            }
        }

        // Let the recovered queue job actually reach the CDN handler, but keep that transfer in flight. This
        // makes early disposal observable: the request must remain alive until the shared queue is stopped.
        stub.HoldOriginals();
        DesktopSessionHost? host = null;
        BooruSessionComposition? unpublished = null;
        TaskCompletionSource schedulerEntered = NewSignal();
        TaskCompletionSource schedulerRelease = NewSignal();
        TaskCompletionSource<string> recoveryAccepted = NewSignal<string>();
        TaskCompletionSource recoveryRelease = NewSignal();
        TaskCompletionSource<bool> beforeQueueDrain = NewSignal<bool>();
        TaskCompletionSource beforeQueueRelease = NewSignal();
        FirstClockCallBarrier clock = new();

        using DanbooruTestBinding binding = new(stub, enabled: false);
        try
        {
            // The second session's queue is running. Keeping the binding disabled lets the test install the
            // first-clock and scheduler barriers before the composition can start.
            host = await BooruCompositionHostTests.StartAsync(
                profile.Path, stub, () => binding.Binding, paused: false);
            host.BooruClockForTests = clock;
            host.AfterBooruStoppedForTests = queueStopping =>
            {
                // This callback is before _downloadQueue.StopAllAsync and therefore before transport release.
                beforeQueueDrain.TrySetResult(queueStopping);
                beforeQueueRelease.Task.GetAwaiter().GetResult();
            };

            binding.SetEnabled(true);
            await WaitAsync(() => TransportOwners(host).Length == 1, "unpublished Booru transport owner");
            unpublished = TransportOwners(host).Single();

            // Scheduler.Load is held before its recovery task is created. The recovery admission hook is therefore
            // installed before any pending original can be offered to the shared queue.
            await clock.Entered.Task.WaitAsync(Deadline);
            int changedCalls = 0;
            unpublished.WatcherHost.Scheduler.Changed += () =>
            {
                if (Interlocked.Exchange(ref changedCalls, 1) == 0)
                {
                    schedulerEntered.TrySetResult();
                    schedulerRelease.Task.GetAwaiter().GetResult();
                }
            };
            unpublished.Originals.AfterQueueAcceptedForTests = postId =>
            {
                recoveryAccepted.TrySetResult(postId);
                return recoveryRelease.Task;
            };
            clock.Release.TrySetResult();

            Assert.Equal(post.PostId, await recoveryAccepted.Task.WaitAsync(Deadline));
            await schedulerEntered.Task.WaitAsync(Deadline);
            await WaitAsync(() => stub.OriginalRequests == 1, "recovered original transfer");

            // The shared queue has accepted the recovered work, but StartAsync is still inside the deterministic
            // scheduler barrier, so the composition has not been published through host.BooruComposition.
            Assert.Null(host.BooruComposition);
            Assert.Same(Field<DownloadQueue>(host, "_downloadQueue"), Field<DownloadQueue>(unpublished.Originals, "_queue"));
            HttpClient mediaClient = Field<HttpClient>(unpublished, "_mediaClient");

            // Begin shutdown at exactly this point. The synchronous close marks the unpublished start terminal before
            // schedulerRelease lets composition.StartAsync return; the normal publication branch can never win.
            Task shutdown = host.StopAsync();
            await WaitAsync(() => Field<bool>(host, "_booruClosed"), "Booru shutdown ingress");
            Assert.Null(host.BooruComposition);
            Assert.False(shutdown.IsCompleted, "shutdown must still be waiting for the blocked unpublished start");

            // Release only the start barrier. Recovery is still held after queue acceptance, so composition cleanup
            // and the session shutdown must drain the accepted work before the queue is allowed to stop.
            schedulerRelease.TrySetResult();
            recoveryRelease.TrySetResult();
            Assert.False(await beforeQueueDrain.Task.WaitAsync(Deadline), "the queue must not be stopping during Booru drain");
            Assert.Equal(1, unpublished.Originals.ActiveCount); // The already accepted transfer still belongs to the queue.
            Assert.Equal(1, stub.OriginalRequests); // Neither restart recovery nor shutdown duplicated it.

            // StopAsync has completed Booru start/stop drain, but is blocked just before the actual shared-queue
            // stop. A live request here proves the unpublished composition was not disposed early.
            using (CancellationTokenSource probeCancellation = new(TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediaClient.GetAsync(post.OriginalUrl, probeCancellation.Token));

            beforeQueueRelease.TrySetResult();
            await shutdown.WaitAsync(Deadline);

            // Only after queue drain may the owner release its media client. The queue-stop outcome stays pending
            // for restart recovery; an early composition Dispose must not turn accepted work into a silent loss.
            Assert.Contains(unpublished.Originals.GetPendingOriginals(), item => item.PostId == post.PostId);
            Assert.Equal(0, unpublished.Originals.ActiveCount); // Physical queue drain, not just cancellation requested.
            Assert.Empty(TransportOwners(host));
            Assert.Null(host.BooruComposition);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => mediaClient.GetAsync(post.OriginalUrl));
            await host.StopAsync().WaitAsync(Deadline);
            await unpublished.StopAsync().WaitAsync(Deadline);
            unpublished.ReleaseTransport();
        }
        finally
        {
            // Unblock every deterministic seam before cleanup so a failed assertion cannot strand a worker.
            clock.Release.TrySetResult();
            schedulerRelease.TrySetResult();
            recoveryRelease.TrySetResult();
            beforeQueueRelease.TrySetResult();
            stub.ReleaseOriginals();
            if (host is not null)
            {
                try { await host.StopAsync().WaitAsync(Deadline); }
                catch { /* preserve the assertion that led here; cleanup is best effort */ }
                try { await host.DisposeAsync(); }
                catch { }
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static BooruSessionComposition[] TransportOwners(DesktopSessionHost host)
    {
        lock (Field<object>(host, "_booruSync"))
            return Field<List<BooruSessionComposition>>(host, "_booruTransportOwners").ToArray();
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        using CancellationTokenSource deadline = new(Deadline);
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
            if (deadline.IsCancellationRequested) throw new TimeoutException("Timed out waiting for " + what);
        }
    }

    private sealed class FirstClockCallBarrier : TimeProvider
    {
        private int _entered;

        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return DateTimeOffset.UtcNow;
        }
    }
}
