using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoTimelineInteractionTests
{
    private static VideoTimelineInteraction Timeline(long generation = 1)
    {
        VideoTimelineInteraction timeline = new();
        timeline.Reset(generation);
        return timeline;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyActiveGestureProtectsTarget_ReleaseResumesObservedClock(bool pointer)
    {
        var timeline = Timeline();
        Assert.True(timeline.Observe(1, timeline.Revision, 100));
        timeline.BeginGesture(pointer);
        var seek = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 500, 10000, true));
        Assert.Equal(5000, timeline.DisplayPosition);
        Assert.True(timeline.Complete(seek, 4900));
        Assert.True(timeline.Observe(1, timeline.Revision, 5100));
        Assert.Equal(5000, timeline.DisplayPosition);
        timeline.EndGesture(pointer); // hover or keyboard focus can remain; neither owns state
        Assert.False(timeline.HasGesture);
        Assert.Equal(5100, timeline.DisplayPosition);
        Assert.True(timeline.Observe(1, timeline.Revision, 5400));
        Assert.Equal(5400, timeline.DisplayPosition);
    }

    [Fact]
    public void ReleasedGestureStillProtectsNewestPendingTargetFromOldAcknowledgements()
    {
        var timeline = Timeline();
        long oldObservation = timeline.Revision;
        var first = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 200, 10000, true));
        var latest = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 800, 10000, true));
        Assert.False(timeline.Observe(1, oldObservation, 100));
        Assert.False(timeline.Complete(first, 2000));
        Assert.False(timeline.Observe(1, timeline.Revision, 2100));
        Assert.Equal(8000, timeline.DisplayPosition);
        Assert.True(timeline.Complete(latest, 7900));
        Assert.Equal(7900, timeline.DisplayPosition);
        Assert.False(timeline.Observe(1, first.Revision, 2200));
        Assert.False(timeline.Observe(1, latest.Revision, 2100)); // queued before readback, delivered afterward
        Assert.Equal(7900, timeline.DisplayPosition);
    }

    [Fact]
    public void PausedSeekUsesBackendReadbackAndNeverInventsProgress()
    {
        var timeline = Timeline();
        var request = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 500, 10000, true));
        Assert.True(timeline.Complete(request, 4990));
        for (int i = 0; i < 1000; i++) Assert.Equal(4990, timeline.DisplayPosition);
        Assert.True(timeline.Observe(1, timeline.Revision, 4990));
        Assert.Equal(4990, timeline.DisplayPosition);
    }

    [Fact]
    public void CancelAndMediaReplacementInvalidateQueuedSeeksAndTheirCompletions()
    {
        var timeline = Timeline();
        timeline.Observe(1, timeline.Revision, 100);
        timeline.BeginGesture(true);
        timeline.BeginGesture(false);
        var cancelled = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 500, 10000, true));
        timeline.Cancel(); // Escape, focus loss, deactivation, stop or detach
        Assert.False(timeline.HasGesture);
        Assert.False(timeline.IsLatest(cancelled));
        Assert.False(timeline.Complete(cancelled, 5000));
        Assert.Equal(100, timeline.DisplayPosition);
        var old = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 800, 10000, true));
        timeline.Reset(2);
        Assert.False(timeline.Complete(old, 8000));
        Assert.False(timeline.Observe(1, timeline.Revision, 9000));
        Assert.Null(timeline.Request(1, 100, 10000, true));
        Assert.Equal(0, timeline.DisplayPosition);
    }

    [Theory]
    [InlineData(0, true, 500)]
    [InlineData(-1, true, 500)]
    [InlineData(1000, false, 500)]
    [InlineData(1000, true, double.NaN)]
    [InlineData(1000, true, double.PositiveInfinity)]
    public void UnknownDurationAndNonSeekableInputsDoNotQueue(long length, bool seekable, double value)
    {
        var timeline = Timeline();
        Assert.Null(timeline.Request(1, value, length, seekable));
        Assert.Equal(0, timeline.DisplayPosition);
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(1500, 10000)]
    [InlineData(1000, 10000)]
    public void RequestsAreClampedIncludingEndOfMedia(double value, long expected)
    {
        var timeline = Timeline();
        var request = Assert.IsType<VideoSeekRequest>(timeline.Request(1, value, 10000, true));
        Assert.Equal(expected, request.Position);
        Assert.Equal(10000, request.Length); // retained if a terminal backend no longer exposes its input length
    }

    [Fact]
    public async Task RapidSeekSeriesHasOneWorkerAndOnlyTheLatestPendingTarget()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<long> executed = new();
        int concurrent = 0, maximum = 0;
        VideoLatestSeekQueue queue = new(async request =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref concurrent));
            executed.Add(request.Position);
            if (request.Revision == 1) { entered.TrySetResult(); await release.Task; }
            Interlocked.Decrement(ref concurrent);
        });
        Task drain = queue.Enqueue(new(1, 1, 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 2; i <= 10000; i++) Assert.Same(drain, queue.Enqueue(new(1, i, i)));
        }
        finally { release.TrySetResult(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new long[] { 1, 10000 }, executed);
        Assert.Equal(1, maximum);
        await queue.Enqueue(new(1, 10001, 10001));
        Assert.Equal(10001, executed[^1]);
    }
    [Fact]
    public async Task SeekWaitingForNativeLockRejectsOldRevisionAndReplacementGeneration()
    {
        using SemaphoreSlim operations = new(1, 1);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var first = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(first);
        var timeline = Timeline(first.Generation);
        object playback = new();
        long playbackGeneration = first.Generation;
        List<long> applied = new();
        VideoLatestSeekQueue queue = new(request => VideoPlaybackCommands.RunAsync(operations, lifetime,
            request.Generation, () => playback, _ => playbackGeneration, _ =>
            {
                if (timeline.IsLatest(request)) applied.Add(request.Position);
            }));
        await operations.WaitAsync();
        Task drain;
        try
        {
            var old = Assert.IsType<VideoSeekRequest>(timeline.Request(first.Generation, 200, 10000, true));
            drain = queue.Enqueue(old);
            var latest = Assert.IsType<VideoSeekRequest>(timeline.Request(first.Generation, 800, 10000, true));
            _ = queue.Enqueue(latest);
        }
        finally { operations.Release(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new long[] { 8000 }, applied);

        await operations.WaitAsync();
        try
        {
            var old = Assert.IsType<VideoSeekRequest>(timeline.Request(first.Generation, 900, 10000, true));
            drain = queue.Enqueue(old);
            var next = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
            playbackGeneration = next.Generation;
            timeline.Reset(next.Generation);
            lifetime.FinishShow(next);
        }
        finally { operations.Release(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new long[] { 8000 }, applied);
        await lifetime.DisposeAsync();
    }

    [Fact]
    public void SeekEventIsOnlyForUserGestureStart_NotKeyRepeatReadbackOrMediaLifecycle()
    {
        var timeline = Timeline();
        int started = 0;
        timeline.SeekInteractionStarted += () => started++;
        timeline.Observe(1, timeline.Revision, 100);
        var request = Assert.IsType<VideoSeekRequest>(timeline.Request(1, 500, 10000, true));
        timeline.Complete(request, 5000);
        Assert.Equal(0, started); // programmatic/accessibility Value changes are not pointer/key gestures
        timeline.BeginGesture(false);
        timeline.BeginGesture(false); // held arrow auto-repeat
        timeline.BeginGesture(true); // overlapping pointer input is still the same interaction
        Assert.Equal(1, started);
        timeline.EndGesture(false);
        Assert.True(timeline.HasGesture);
        timeline.EndGesture(true);
        timeline.BeginGesture(true);
        Assert.Equal(2, started);
        timeline.Cancel();
        timeline.Reset(2);
        Assert.Equal(2, started);
        timeline.BeginGesture(false);
        Assert.Equal(3, started);
    }

}
