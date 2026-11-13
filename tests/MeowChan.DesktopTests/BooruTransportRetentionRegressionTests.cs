using System.Net;
using System.Net.Http;
using System.Reflection;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// F03 review regression: DesktopSessionHost keeps every Booru composition that reached StartAsync as a transport
// owner until the shared queue and the Booru start/stop have drained (F1). The review claimed unbounded growth under
// repeated re-authorisation / AvailabilityChanged. These tests drive several real availability cycles through the
// production binding event and the production session shutdown, with the real queue/catalog/profile lease; only the
// provider wire is the in-memory stub.
//
// Not covered here (see BooruUnpublishedTransportTests): an unpublished recovery that already handed work to the
// shared queue.
public sealed class BooruTransportRetentionRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private const int Cycles = 3;

    [Fact]
    public async Task PublishedCompositionIsReusedAcrossAvailabilityCyclesAndKeepsActiveTransferTransportUntilQueueDrain()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new(7401));
        using DanbooruTestBinding binding = new(stub);
        TaskCompletionSource<bool> beforeQueueDrain = NewSignal<bool>();
        TaskCompletionSource beforeQueueRelease = NewSignal();
        DesktopSessionHost? host = null;
        try
        {
            DesktopSessionHost session = host = await BooruCompositionHostTests.StartAsync(
                profile.Path, stub, () => binding.Binding, paused: false);
            await WaitAsync(() => Field<Task>(session, "_booruStart").IsCompleted, "the Ready continuation of the Booru start");
            BooruSessionComposition composition = host.BooruComposition!;
            BooruWorkbenchServices services = host.BooruServices!;
            BooruOriginalQueue originals = composition.Originals;
            HttpClient mediaClient = Field<HttpClient>(composition, "_mediaClient");
            Assert.Same(composition, Assert.Single(TransportOwners(host)));

            // A real, accepted original transfer stays in flight through every cycle: the composition's media client
            // is the one the queue job uses, so it must neither be replaced nor released while the job is active.
            stub.HoldOriginals();
            BooruCallResult<BooruPostView> read = await services.Search
                .ReadPostAsync(post.PostId, CancellationToken.None).WaitAsync(Deadline);
            BooruLoadResult admitted = await services.Queue
                .LoadOriginalAsync(read.Value!, BooruLoadOrigin.OneShot, CancellationToken.None).WaitAsync(Deadline);
            Assert.Equal(BooruLoadStatus.Queued, admitted.Status);
            await WaitAsync(() => stub.OriginalRequests == 1, "the in-flight original transfer");
            Assert.Equal(1, originals.ActiveCount);

            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                DanbooruWatcherHost previousGeneration = composition.WatcherHost;

                // Plugin disabled at runtime (production lifecycle event): ports revoke synchronously, the watcher
                // generation drains, and the session's composition handler runs without composing anything.
                binding.SetEnabled(false);
                Assert.Null(host.BooruServices);
                await WaitAsync(() => Field<bool>(composition, "_watcherStopped") && LifecycleIdle(composition),
                    "the disabled watcher generation to drain");
                await EnsureComposedAsync(host, binding.Binding).WaitAsync(Deadline);

                // Re-enabled (re-authorised): the SAME composition re-opens with a new watcher generation only.
                binding.SetEnabled(true);
                await WaitAsync(() => composition.IsAvailable && !ReferenceEquals(previousGeneration, composition.WatcherHost)
                    && LifecycleIdle(composition), "the re-enabled watcher generation");
                // The handler the event scheduled, awaited deterministically: a published composition is reused.
                await EnsureComposedAsync(host, binding.Binding).WaitAsync(Deadline);

                // A repeated availability notification without a state change (re-authorisation re-notify) is a
                // no-op for both the session and the composition.
                DanbooruWatcherHost currentGeneration = composition.WatcherHost;
                binding.Binding.RaiseAvailabilityChanged();
                Assert.True(composition.IsAvailable);
                await EnsureComposedAsync(host, binding.Binding).WaitAsync(Deadline);
                Assert.Same(currentGeneration, composition.WatcherHost);

                Assert.Same(composition, host.BooruComposition);
                Assert.Same(services, host.BooruServices);
                Assert.Same(composition, Assert.Single(TransportOwners(host)));
                Assert.Same(mediaClient, Field<HttpClient>(composition, "_mediaClient"));
                Assert.Same(originals, composition.Originals);
                Assert.Equal(1, originals.ActiveCount);
                Assert.Equal(1, stub.OriginalRequests); // The active transfer was neither restarted nor duplicated.
                await AssertServesAsync(mediaClient, post);
            }

            // Terminal shutdown: hold the shutdown worker right before the shared queue stops.
            host.AfterBooruStoppedForTests = queueStopping =>
            {
                beforeQueueDrain.TrySetResult(queueStopping);
                beforeQueueRelease.Task.GetAwaiter().GetResult();
            };
            Task shutdown = host.StopAsync();
            Assert.False(await beforeQueueDrain.Task.WaitAsync(Deadline), "the queue must not be stopping during Booru drain");
            Assert.False(shutdown.IsCompleted);
            Assert.False(composition.IsAvailable); // Booru itself has stopped ...
            Assert.Null(host.BooruServices);
            Assert.Equal(1, originals.ActiveCount); // ... but the queue job is still active ...
            Assert.Same(composition, Assert.Single(TransportOwners(host)));
            await AssertServesAsync(mediaClient, post); // ... so its transport is still owned and usable.

            beforeQueueRelease.TrySetResult();
            await shutdown.WaitAsync(Deadline);

            // Only after the physical queue drain is the single owner released; the borrowed session transport is not.
            Assert.Equal(0, originals.ActiveCount);
            Assert.Empty(TransportOwners(host));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => mediaClient.GetAsync(post.PreviewUrl));
            await AssertServesAsync(binding.Binding.Transport, post);

            // Terminal release is idempotent.
            await host.StopAsync().WaitAsync(Deadline);
            await composition.StopAsync().WaitAsync(Deadline);
            composition.ReleaseTransport();
            Assert.Empty(TransportOwners(host));
        }
        finally
        {
            beforeQueueRelease.TrySetResult();
            stub.ReleaseOriginals();
            await CleanupAsync(host);
        }
    }

    [Fact]
    public async Task FailedStartsRetainOneOwnerPerAttemptOnlyAndPublicationStopsGrowthUntilTerminalRelease()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new(7402));
        // Disabled first, so every composition attempt below is caused by exactly one availability notification.
        using DanbooruTestBinding binding = new(stub, enabled: false);
        TaskCompletionSource<bool> beforeQueueDrain = NewSignal<bool>();
        TaskCompletionSource beforeQueueRelease = NewSignal();
        DesktopSessionHost? host = null;
        try
        {
            DesktopSessionHost session = host = await BooruCompositionHostTests.StartAsync(
                profile.Path, stub, () => binding.Binding, paused: false);
            // The Ready continuation must have subscribed and finished; otherwise it could add a second attempt.
            await WaitAsync(() => Field<Task>(session, "_booruStart").IsCompleted
                && Field<BooruProviderBinding?>(session, "_booruBinding") != null, "the Ready continuation of the Booru start");
            Assert.Empty(TransportOwners(host));

            List<BooruSessionComposition> failed = new();
            for (int attempt = 1; attempt <= Cycles; attempt++)
            {
                FailFirstClockCall clock = new();
                host.BooruClockForTests = clock;
                if (attempt == 1) binding.SetEnabled(true);
                else binding.Binding.RaiseAvailabilityChanged(); // Re-notify while enabled, as a lifecycle re-raise does.

                int expected = attempt;
                await WaitAsync(() => TransportOwners(session).Length == expected
                    && StopCompleted(TransportOwners(session)[expected - 1]) && Field<SemaphoreSlim>(session, "_booruGate").CurrentCount == 1,
                    "failed composition attempt " + attempt);
                Assert.True(clock.Failed, "the injected scheduler-load failure must be what failed this start");

                BooruSessionComposition[] owners = TransportOwners(host);
                Assert.Equal(attempt, owners.Length); // Exactly one retained owner per real attempt, no amplification.
                Assert.Equal(failed.ToArray(), owners[..^1]);
                BooruSessionComposition current = owners[^1];
                Assert.DoesNotContain(current, failed);
                Assert.False(current.IsAvailable);
                Assert.Equal(0, current.Originals.ActiveCount);
                Assert.Null(host.BooruComposition);
                Assert.Null(host.BooruServices);
                failed.Add(current);
            }

            // Recovery: the next notification composes successfully and is published.
            host.BooruClockForTests = null;
            binding.Binding.RaiseAvailabilityChanged();
            await WaitAsync(() => session.BooruComposition != null, "the published composition");
            BooruSessionComposition published = host.BooruComposition!;
            Assert.DoesNotContain(published, failed);
            Assert.NotNull(host.BooruServices);

            // Once published, further notifications and the awaited production handler never compose again.
            for (int repeat = 0; repeat < Cycles; repeat++)
            {
                binding.Binding.RaiseAvailabilityChanged();
                await EnsureComposedAsync(host, binding.Binding).WaitAsync(Deadline);
            }
            BooruSessionComposition[] retained = TransportOwners(host);
            Assert.Equal(Cycles + 1, retained.Length);
            Assert.Equal(failed.ToArray(), retained[..^1]);
            Assert.Same(published, retained[^1]);
            Assert.Same(published, host.BooruComposition);
            HttpClient[] mediaClients = retained.Select(owner => Field<HttpClient>(owner, "_mediaClient")).ToArray();
            Assert.Equal(retained.Length, mediaClients.Distinct().Count());

            host.AfterBooruStoppedForTests = queueStopping =>
            {
                beforeQueueDrain.TrySetResult(queueStopping);
                beforeQueueRelease.Task.GetAwaiter().GetResult();
            };
            Task shutdown = host.StopAsync();
            Assert.False(await beforeQueueDrain.Task.WaitAsync(Deadline), "the queue must not be stopping during Booru drain");
            Assert.False(shutdown.IsCompleted);

            // F1 contract: no owner (failed or published) is released before the shared queue has drained.
            Assert.Equal(retained, TransportOwners(host));
            foreach (HttpClient mediaClient in mediaClients) await AssertServesAsync(mediaClient, post);

            beforeQueueRelease.TrySetResult();
            await shutdown.WaitAsync(Deadline);

            // Terminal: every owner ever retained is released exactly by the session, the borrowed transport stays.
            Assert.Empty(TransportOwners(host));
            foreach (HttpClient mediaClient in mediaClients)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => mediaClient.GetAsync(post.PreviewUrl));
            await AssertServesAsync(binding.Binding.Transport, post);

            // A notification after shutdown composes nothing; terminal release is idempotent.
            binding.Binding.RaiseAvailabilityChanged();
            await EnsureComposedAsync(host, binding.Binding).WaitAsync(Deadline);
            await host.StopAsync().WaitAsync(Deadline);
            Assert.Empty(TransportOwners(host));
            Assert.Empty(stub.Violations);
        }
        finally
        {
            beforeQueueRelease.TrySetResult();
            await CleanupAsync(host);
        }
    }

    private static async Task CleanupAsync(DesktopSessionHost? host)
    {
        if (host is null) return;
        try { await host.StopAsync().WaitAsync(Deadline); }
        catch { /* preserve the assertion that led here; cleanup is best effort */ }
        try { await host.DisposeAsync(); }
        catch { }
    }

    private static async Task AssertServesAsync(HttpClient client, DanbooruStubPost post)
    {
        using HttpResponseMessage response = await client.GetAsync(post.PreviewUrl).WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(post.Preview, await response.Content.ReadAsByteArrayAsync());
    }

    // The production handler that OnBooruAvailabilityChanged schedules on the thread pool, awaited deterministically.
    private static Task EnsureComposedAsync(DesktopSessionHost host, BooruProviderBinding binding) =>
        (Task)typeof(DesktopSessionHost).GetMethod("EnsureBooruComposedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object[] { binding })!;

    private static bool LifecycleIdle(BooruSessionComposition composition) =>
        Field<SemaphoreSlim>(composition, "_lifecycle").CurrentCount == 1;

    private static bool StopCompleted(BooruSessionComposition composition) =>
        Field<TaskCompletionSource>(composition, "_stopCompletion").Task.IsCompleted;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static BooruSessionComposition[] TransportOwners(DesktopSessionHost host)
    {
        lock (Field<object>(host, "_booruSync"))
            return Field<List<BooruSessionComposition>>(host, "_booruTransportOwners").ToArray();
    }

    private static Task WaitAsync(Func<bool> condition, string what) => BooruCompositionHostTests.WaitAsync(condition, what);

    /// <summary>The first clock read of a composition is the scheduler load; failing it fails that start only.</summary>
    private sealed class FailFirstClockCall : TimeProvider
    {
        private int _calls;

        internal bool Failed => Volatile.Read(ref _calls) > 0;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) == 1) throw new InjectedStartFailure();
            return base.GetUtcNow();
        }
    }

    private sealed class InjectedStartFailure() : Exception("synthetic Danbooru scheduler-load failure");
}
