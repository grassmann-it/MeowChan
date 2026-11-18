using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoPlaybackObservationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Target(long Generation);

    [Fact]
    public async Task CurrentSuccessorCannotObserveRetiringPredecessorWhileCleanupIsPending()
    {
        using SemaphoreSlim operations = new(1, 1);
        TaskCompletionSource cleanupEntered = Signal(), releaseCleanup = Signal();
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
        }, () => Task.CompletedTask);
        var first = lifetime.AcceptShow(CancellationToken.None)!;
        Target playback = new(first.Generation);
        lifetime.FinishShow(first);
        Task stopping = lifetime.StopAsync();
        await cleanupEntered.Task.WaitAsync(Deadline);
        var next = lifetime.AcceptShow(CancellationToken.None)!;
        int reads = 0;
        try
        {
            string result = await VideoPlaybackObservation.RunAsync(operations, lifetime, next.Generation,
                () => playback, p => p.Generation, _ => { reads++; return "Playing"; }, state => state).WaitAsync(Deadline);
            Assert.Equal("Retired", result);
            Assert.Equal(0, reads);
            Assert.False(stopping.IsCompleted);
            releaseCleanup.TrySetResult();
            await next.PreviousCleanup.WaitAsync(Deadline);
            playback = new(next.Generation);
            result = await VideoPlaybackObservation.RunAsync(operations, lifetime, next.Generation,
                () => playback, p => p.Generation, _ => { reads++; return "Playing"; }, state => state).WaitAsync(Deadline);
            Assert.Equal("Playing", result);
            Assert.Equal(1, reads);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            lifetime.FinishShow(next);
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ObserverQueuedBeforeReplacementCannotReadSuccessorUnderOldRequest()
    {
        using SemaphoreSlim operations = new(1, 1);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var first = lifetime.AcceptShow(CancellationToken.None)!;
        Target playback = new(first.Generation);
        lifetime.FinishShow(first);
        int reads = 0;
        await operations.WaitAsync();
        Task<string> queued = VideoPlaybackObservation.RunAsync(operations, lifetime, first.Generation,
            () => playback, p => p.Generation, _ => { reads++; return "Playing"; }, state => state);
        var next = lifetime.AcceptShow(CancellationToken.None)!;
        playback = new(next.Generation);
        lifetime.FinishShow(next);
        operations.Release();
        try
        {
            Assert.Equal("Retired", await queued.WaitAsync(Deadline));
            Assert.Equal(0, reads);
            Assert.Equal(1, operations.CurrentCount);
        }
        finally { await lifetime.DisposeAsync().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task AbandonedQueuedObserverNeverEntersNativeReadAfterGateIsReleased()
    {
        using SemaphoreSlim operations = new(1, 1);
        using CancellationTokenSource cancel = new();
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var show = lifetime.AcceptShow(CancellationToken.None)!;
        lifetime.FinishShow(show);
        int reads = 0;
        await operations.WaitAsync();
        Task<string> queued = VideoPlaybackObservation.RunAsync(operations, lifetime, show.Generation,
            () => new Target(show.Generation), p => p.Generation, _ => { reads++; return "Playing"; }, state => state, cancel.Token);
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
            Assert.Equal(0, reads);
            Assert.Equal(0, operations.CurrentCount); // Observer cancellation must not release someone else's lease.
        }
        finally
        {
            operations.Release();
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
        Assert.Equal(0, reads);
        Assert.Equal(1, operations.CurrentCount);
    }

    [Fact]
    public async Task RetirementDuringNativeReadDiscardsResultButKeepsOwnershipUntilNativeReturns()
    {
        using SemaphoreSlim operations = new(1, 1);
        using ManualResetEventSlim releaseRead = new();
        using CancellationTokenSource cancel = new();
        TaskCompletionSource reading = Signal(), cleaned = Signal();
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            await operations.WaitAsync();
            try { cleaned.TrySetResult(); }
            finally { operations.Release(); }
        }, () => Task.CompletedTask);
        var show = lifetime.AcceptShow(CancellationToken.None)!;
        lifetime.FinishShow(show);
        Task<string> observing = VideoPlaybackObservation.RunAsync(operations, lifetime, show.Generation,
            () => new Target(show.Generation), p => p.Generation, _ =>
            {
                reading.TrySetResult();
                if (!releaseRead.Wait(Deadline)) throw new TimeoutException("Test did not release native read.");
                return "Playing";
            }, state => state, cancel.Token);
        try
        {
            await reading.Task.WaitAsync(Deadline);
            Task stopping = lifetime.StopAsync();
            cancel.Cancel();
            Assert.False(observing.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.False(cleaned.Task.IsCompleted);
            Assert.Equal(0, operations.CurrentCount);
            releaseRead.Set();
            Assert.Equal("Retired", await observing.WaitAsync(Deadline));
            await stopping.WaitAsync(Deadline);
            Assert.True(cleaned.Task.IsCompletedSuccessfully);
        }
        finally
        {
            releaseRead.Set();
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
    }
}
