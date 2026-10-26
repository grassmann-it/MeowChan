using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// These exercise the admission coordinator used by GalleryVideoPlayer itself. No Avalonia
// initialization, native runtime or codec is needed. Signals fix the race ordering; deadlines
// only stop a broken test from hanging the suite.
public sealed class VideoPlaybackLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task IdleStopsStaySynchronousBeforeAndAfterARealDrain()
    {
        int cleanups = 0, engines = 0;
        VideoPlaybackLifetime lifetime = new(_ => { cleanups++; return Task.CompletedTask; },
            () => { engines++; return Task.CompletedTask; });
        for (int i = 0; i < 1000; i++) Assert.True(lifetime.StopAsync().IsCompletedSuccessfully);
        Assert.Equal(1000, lifetime.NoOpStops);
        Assert.Equal(0, cleanups);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(show);
        await lifetime.StopAsync().WaitAsync(Deadline);
        for (int i = 0; i < 1000; i++) Assert.True(lifetime.StopAsync().IsCompletedSuccessfully);
        Assert.Equal(2000, lifetime.NoOpStops);
        Assert.Equal(1, lifetime.FullStops);
        Assert.Equal(1, cleanups);
        await lifetime.DisposeAsync().WaitAsync(Deadline);
        Assert.Equal(1, engines);
    }

    [Fact]
    public async Task AcceptedShowCannotBeMisclassifiedAsIdleBeforeItsWorkerStarts()
    {
        TaskCompletionSource nativeEntered = Signal();
        VideoPlaybackLifetime lifetime = new(_ => { nativeEntered.TrySetResult(); return Task.CompletedTask; }, () => Task.CompletedTask);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        // The worker has not run yet. Stop must include this accepted show in its barrier.
        Task stop = lifetime.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(nativeEntered.Task.IsCompleted);
        Assert.True(show.Token.IsCancellationRequested);
        Assert.False(lifetime.IsCurrent(show.Generation));
        lifetime.FinishShow(show);
        await stop.WaitAsync(Deadline);
        Assert.True(nativeEntered.Task.IsCompletedSuccessfully);
        Assert.Equal(0, lifetime.NoOpStops);
        await lifetime.DisposeAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task DisposalClosesAdmissionBeforeAnAcceptedShowCanResume()
    {
        bool engineReleased = false;
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask,
            () => { engineReleased = true; return Task.CompletedTask; });
        var old = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        Task disposing = lifetime.DisposeAsync();
        Assert.Same(disposing, lifetime.DisposeAsync());
        Assert.False(disposing.IsCompleted);
        Assert.False(engineReleased);
        Assert.False(lifetime.IsCurrent(old.Generation));
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Null(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(old); // Resuming the older show cannot acquire a newer generation/token.
        await disposing.WaitAsync(Deadline);
        Assert.True(engineReleased);
        Assert.Null(lifetime.AcceptShow(CancellationToken.None));
        Assert.False(lifetime.IsCurrent(old.Generation));
    }

    [Fact]
    public async Task StopsCoalesceAndRepeatedDisposeWaitsForActualNativeAndEngineReturn()
    {
        TaskCompletionSource nativeEntered = Signal(), releaseNative = Signal(), engineEntered = Signal(), releaseEngine = Signal();
        int cleanups = 0, engineReleases = 0;
        bool streamOwned = true, drawableOwned = true;
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            Interlocked.Increment(ref cleanups); nativeEntered.TrySetResult();
            await releaseNative.Task;
            drawableOwned = streamOwned = false;
        }, async () =>
        {
            Interlocked.Increment(ref engineReleases); engineEntered.TrySetResult(); await releaseEngine.Task;
        });
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(show);
        Task stop = lifetime.StopAsync();
        try
        {
            await nativeEntered.Task.WaitAsync(Deadline);
            for (int i = 0; i < 10000; i++) Assert.Same(stop, lifetime.StopAsync());
            Task dispose = lifetime.DisposeAsync();
            for (int i = 0; i < 10000; i++) Assert.Same(dispose, lifetime.DisposeAsync());
            Assert.Same(dispose, lifetime.StopAsync());
            Assert.False(stop.IsCompleted); Assert.False(dispose.IsCompleted);
            Assert.True(streamOwned); Assert.True(drawableOwned);
            Assert.False(engineEntered.Task.IsCompleted);
            Assert.Null(lifetime.AcceptShow(CancellationToken.None));
            Assert.Equal(1, lifetime.FullStops);
            releaseNative.TrySetResult();
            await stop.WaitAsync(Deadline);
            await engineEntered.Task.WaitAsync(Deadline);
            Assert.False(streamOwned); Assert.False(drawableOwned);
            Assert.False(dispose.IsCompleted);
            releaseEngine.TrySetResult();
            await dispose.WaitAsync(Deadline);
            Assert.Same(dispose, lifetime.DisposeAsync());
            Assert.Equal(1, cleanups); Assert.Equal(1, engineReleases);
        }
        finally { releaseNative.TrySetResult(); releaseEngine.TrySetResult(); }
    }

    [Fact]
    public async Task NewShowWaitsForPriorDrainAndStaleCallbacksCannotStopIt()
    {
        TaskCompletionSource nativeEntered = Signal(), release = Signal();
        VideoPlaybackLifetime lifetime = new(async _ => { nativeEntered.TrySetResult(); await release.Task; }, () => Task.CompletedTask);
        var old = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(old);
        Task stop = lifetime.StopAsync();
        try
        {
            await nativeEntered.Task.WaitAsync(Deadline);
            var next = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
            Assert.Same(stop, next.PreviousCleanup);
            Assert.False(next.PreviousCleanup.IsCompleted);
            long current = lifetime.Generation;
            Assert.Null(lifetime.StopCurrent(old.Generation, out _)); // delayed EncounteredError/EndReached/cancel
            await lifetime.StopAsync(old.Generation);
            Assert.Equal(current, lifetime.Generation);
            Assert.True(lifetime.IsCurrent(next.Generation));
            Assert.False(lifetime.IsCurrent(old.Generation));
            release.TrySetResult();
            await next.PreviousCleanup.WaitAsync(Deadline);
            Assert.True(lifetime.IsCurrent(next.Generation));
            lifetime.FinishShow(next);
            await lifetime.StopAsync().WaitAsync(Deadline);
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task StopCanCancelANewerShowWaitingOnTheSameDrainWithoutAWaitCycle()
    {
        TaskCompletionSource nativeEntered = Signal(), release = Signal();
        int cleanups = 0;
        VideoPlaybackLifetime lifetime = new(async _ => { cleanups++; nativeEntered.TrySetResult(); await release.Task; }, () => Task.CompletedTask);
        var first = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(first);
        Task stop = lifetime.StopAsync();
        try
        {
            await nativeEntered.Task.WaitAsync(Deadline);
            var next = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
            Task waiting = next.PreviousCleanup.WaitAsync(next.Token);
            Assert.Same(stop, lifetime.StopAsync());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
            lifetime.FinishShow(next);
            for (int i = 0; i < 1000; i++) Assert.Same(stop, lifetime.StopAsync());
            release.TrySetResult();
            await stop.WaitAsync(Deadline);
            Assert.InRange(cleanups, 1, 2); // one native pass plus at most one combined follow-up
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CanceledExternalLoadInvalidatesCallbacksAndDrainsTheAcceptedShow()
    {
        using CancellationTokenSource canceled = new();
        TaskCompletionSource cleaned = Signal();
        VideoPlaybackLifetime lifetime = new(_ => { cleaned.TrySetResult(); return Task.CompletedTask; }, () => Task.CompletedTask);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(canceled.Token));
        await canceled.CancelAsync();
        Assert.False(lifetime.IsCurrent(show.Generation));
        Assert.False(cleaned.Task.IsCompleted);
        lifetime.FinishShow(show);
        await cleaned.Task.WaitAsync(Deadline);
        await lifetime.StopAsync().WaitAsync(Deadline);
        await lifetime.DisposeAsync().WaitAsync(Deadline);
        Assert.Null(lifetime.AcceptShow(CancellationToken.None));
    }

    [Fact]
    public async Task FailedNativeReleaseIsSharedAndNeverRetriesOrReleasesTheEngine()
    {
        int attempts = 0, engines = 0;
        var failure = new InvalidOperationException("fake native release failure");
        VideoPlaybackLifetime lifetime = new(_ => { attempts++; throw failure; }, () => { engines++; return Task.CompletedTask; });
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(show);
        Task stop = lifetime.StopAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => stop.WaitAsync(Deadline)));
        Assert.Null(lifetime.AcceptShow(CancellationToken.None));
        Task dispose = lifetime.DisposeAsync();
        Assert.Same(dispose, lifetime.DisposeAsync());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => dispose.WaitAsync(Deadline)));
        Assert.True(lifetime.Cleanup.Evidence.OwnershipRetained);
        Assert.Equal(1, attempts); Assert.Equal(0, engines);
    }

    [Fact]
    public async Task StopAndAdmissionHaveOneTotalOrderUnderConcurrentCalls()
    {
        for (int iteration = 0; iteration < 40; iteration++)
        {
            VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
            using Barrier start = new(2);
            VideoPlaybackLifetime.ShowRequest? accepted = null;
            Task? stopping = null;
            long stoppedAt = 0;
            Task accepting = Task.Run(() => { start.SignalAndWait(Deadline); accepted = lifetime.AcceptShow(CancellationToken.None); });
            Task stopper = Task.Run(() => { start.SignalAndWait(Deadline); stopping = lifetime.StopAsync(); stoppedAt = lifetime.Generation; });
            await Task.WhenAll(accepting, stopper).WaitAsync(Deadline);
            var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(accepted);
            if (show.Generation < stoppedAt)
            {
                Assert.False(lifetime.IsCurrent(show.Generation));
                Assert.False(stopping!.IsCompleted); // the accepted but unstarted show is still owned
            }
            lifetime.FinishShow(show);
            await stopping!.WaitAsync(Deadline);
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
    }
}

public sealed class VideoCleanupWatchTests
{
    [Fact]
    public void DeadmanReportsOncePerPhaseAndNeverClaimsResourceRelease()
    {
        long clock = 100;
        VideoCleanupWatch watch = new("test.video.cleanup", () => clock, deadline: 5, schedule: false);
        List<VideoCleanupEvidence> reports = new();
        watch.Pending += reports.Add;
        Assert.True(watch.Begin(42));
        watch.Phase("native-stop");
        Assert.False(watch.Begin(43)); // one timer/owner; a second observer cannot replace it
        clock += 4; watch.CheckDeadman(); Assert.Empty(reports);
        clock += 1;
        for (int i = 0; i < 1000; i++) watch.CheckDeadman();
        VideoCleanupEvidence evidence = Assert.Single(reports);
        Assert.Equal("native-stop", evidence.Phase); Assert.Equal("accepted", evidence.LastCompleted);
        Assert.Equal(42, evidence.Generation); Assert.Equal(5, evidence.ElapsedTicks);
        Assert.True(evidence.Pending); Assert.True(evidence.Overdue); Assert.True(evidence.OwnershipRetained);
        watch.Phase("player-dispose"); clock += 5; watch.CheckDeadman();
        Assert.Equal(2, reports.Count);
        Assert.Equal("native-stop", reports[1].LastCompleted);
        watch.Complete(); clock += 100; watch.CheckDeadman();
        Assert.Equal(2, reports.Count);
        Assert.False(watch.Evidence.Pending); Assert.False(watch.Evidence.OwnershipRetained);
    }

    [Fact]
    public void FailedReleaseRetainsOwnershipAndAThrowingDiagnosticCannotChangeIt()
    {
        long clock = 0;
        VideoCleanupWatch watch = new("test.video.cleanup", () => clock, deadline: 5, schedule: false);
        watch.Pending += _ => throw new InvalidOperationException("diagnostic observer");
        watch.Begin(7); watch.Phase("player-dispose"); clock = 5; watch.CheckDeadman();
        Assert.True(watch.Evidence.Pending);
        watch.Fail();
        Assert.True(watch.Evidence.OwnershipRetained);
        Assert.False(watch.Evidence.Pending);
        Assert.Equal("player-dispose:failed", watch.Evidence.Phase);
    }
}
