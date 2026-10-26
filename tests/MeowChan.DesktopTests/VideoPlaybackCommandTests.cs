using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Exercise the same worker, semaphore, target snapshot and authorization used by the real
// GalleryVideoPlayer.CommandAsync. Only native targets are faked; no VLC or dispatcher is needed.
public sealed class VideoPlaybackCommandTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Target(long generation)
    {
        internal long Generation { get; } = generation;
        internal int Commands;
        internal bool Released;
        internal void Invoke()
        {
            if (Released) throw new InvalidOperationException("Command reached a released fake player.");
            Interlocked.Increment(ref Commands);
        }
    }

    [Fact]
    public async Task ResumeCommandRejectsRetiringPlaybackWhileItsCleanupWaitsForTheNativeLock()
    {
        using SemaphoreSlim operations = new(1, 1);
        TaskCompletionSource cleanupEntered = Signal(), allowCleanup = Signal();
        Target? playback = null;
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            cleanupEntered.TrySetResult();
            await allowCleanup.Task; // Hold cleanup before it acquires the production native-operation lock.
            await operations.WaitAsync();
            try
            {
                if (playback != null) playback.Released = true;
                playback = null;
            }
            finally { operations.Release(); }
        }, () => Task.CompletedTask);
        var first = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        Target old = new(first.Generation);
        playback = old;
        lifetime.FinishShow(first);
        Task stopping = lifetime.StopAsync(); // Suspend A.
        Task? resuming = null;
        try
        {
            await cleanupEntered.Task.WaitAsync(Deadline);
            var next = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None)); // Resume B.
            Target replacement = new(next.Generation);
            async Task ResumeAsync()
            {
                try
                {
                    await next.PreviousCleanup.WaitAsync(next.Token);
                    await operations.WaitAsync();
                    try { if (lifetime.IsCurrent(next.Generation)) playback = replacement; }
                    finally { operations.Release(); }
                }
                catch (OperationCanceledException) when (next.Token.IsCancellationRequested) { }
                finally { lifetime.FinishShow(next); }
            }
            resuming = ResumeAsync();
            Assert.Same(stopping, next.PreviousCleanup);
            Assert.True(lifetime.IsCurrent(next.Generation));
            Assert.False(resuming.IsCompleted);
            Assert.Same(old, playback);
            // B is current, but the adapter slot still contains retiring A. This is the P2 ordering.
            await VideoPlaybackCommands.RunAsync(operations, lifetime, next.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(Deadline);
            Assert.Equal(0, old.Commands);
            Assert.False(old.Released);
            Assert.False(stopping.IsCompleted);
            Assert.False(allowCleanup.Task.IsCompleted);
            allowCleanup.TrySetResult();
            await resuming.WaitAsync(Deadline);
            Assert.True(old.Released);
            Assert.Same(replacement, playback);
            // Late A input must also be rejected after B is published.
            await VideoPlaybackCommands.RunAsync(operations, lifetime, first.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(Deadline);
            await VideoPlaybackCommands.RunAsync(operations, lifetime, next.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(Deadline);
            Assert.Equal(0, old.Commands);
            Assert.Equal(1, replacement.Commands);
        }
        finally
        {
            allowCleanup.TrySetResult();
            if (resuming != null) await resuming.WaitAsync(Deadline);
            await lifetime.DisposeAsync().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task CommandQueuedDuringReplacementUsesTheTargetPublishedUnderTheNativeLock()
    {
        using SemaphoreSlim operations = new(1, 1);
        Target? playback = null;
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            await operations.WaitAsync();
            try { if (playback != null) playback.Released = true; playback = null; }
            finally { operations.Release(); }
        }, () => Task.CompletedTask);
        var first = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        Target old = new(first.Generation);
        playback = old;
        lifetime.FinishShow(first);
        var next = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        Target replacement = new(next.Generation);
        // Model ShowCore replacing its owned slot while commands wait for its native lock.
        await operations.WaitAsync();
        Task queued = VideoPlaybackCommands.RunAsync(operations, lifetime, next.Generation, () => playback,
            static target => target.Generation, static target => target.Invoke());
        try
        {
            Assert.False(queued.IsCompleted);
            old.Released = true;
            playback = replacement;
            lifetime.FinishShow(next);
        }
        finally { operations.Release(); }
        try
        {
            await queued.WaitAsync(Deadline);
            Assert.Equal(0, old.Commands);
            Assert.Equal(1, replacement.Commands);
            await VideoPlaybackCommands.RunAsync(operations, lifetime, first.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(Deadline);
            Assert.Equal(1, replacement.Commands);
        }
        finally { await lifetime.DisposeAsync().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task CommandFailureRetainsTheExistingErrorCallbackAndReleasesTheNativeLock()
    {
        using SemaphoreSlim operations = new(1, 1);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        Target playback = new(show.Generation);
        lifetime.FinishShow(show);
        Exception? observed = null;
        InvalidOperationException failure = new("fake native command failed");
        try
        {
            await VideoPlaybackCommands.RunAsync(operations, lifetime, show.Generation, () => playback,
                static target => target.Generation, _ => throw failure, exception => observed = exception).WaitAsync(Deadline);
            Assert.Same(failure, observed);
            Assert.Equal(1, operations.CurrentCount);
            await VideoPlaybackCommands.RunAsync(operations, lifetime, show.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(Deadline);
            Assert.Equal(1, playback.Commands);
        }
        finally { await lifetime.DisposeAsync().WaitAsync(Deadline); }
    }
}
