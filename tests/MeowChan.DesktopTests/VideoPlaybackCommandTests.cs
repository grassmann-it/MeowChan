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

public sealed class VideoPlaybackIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitIntentRevokesPendingRepeatWithoutErasingLoop(bool stop)
    {
        VideoPlaybackIntent intent = new();
        intent.ToggleLoop();
        var ended = intent.Set(1, VideoPlaybackMode.Playing);
        Assert.True(intent.CanRepeat(ended));
        intent.Set(1, stop ? VideoPlaybackMode.Stopped : VideoPlaybackMode.Paused); // occurs before waiting for native Stop/command lock
        Assert.False(intent.CanRepeat(ended));
        Assert.False(intent.CanRepeat(intent.Current));
        Assert.True(intent.LoopEnabled);
        var resumed = intent.Set(1, VideoPlaybackMode.Playing);
        Assert.False(intent.CanRepeat(ended)); // resume must not revive an old callback
        Assert.True(intent.CanRepeat(resumed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrStopWhileRepeatNativeStopIsBlockedPreventsRepeatPlay(bool stop)
    {
        using SemaphoreSlim operations = new(1, 1);
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(show);
        VideoPlaybackIntent intent = new();
        intent.ToggleLoop();
        var ended = intent.Set(show.Generation, VideoPlaybackMode.Playing);
        object playback = new();
        int plays = 0;
        Task repeat = VideoPlaybackCommands.RunAsync(operations, lifetime, show.Generation, () => playback,
            _ => show.Generation, _ =>
            {
                if (!intent.CanRepeat(ended)) return;
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10))); // fake native Stop
                if (intent.CanRepeat(ended)) Interlocked.Increment(ref plays);
            });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            intent.Set(show.Generation, stop ? VideoPlaybackMode.Stopped : VideoPlaybackMode.Paused);
            Assert.True(intent.LoopEnabled);
        }
        finally { release.Set(); }
        await repeat.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, plays);
        await lifetime.DisposeAsync();
    }

    [Fact]
    public void MediaAndImageRetirementPreservesPreferenceButInvalidatesOldRepeat()
    {
        VideoPlaybackIntent intent = new();
        intent.ToggleLoop();
        var first = intent.Set(1, VideoPlaybackMode.Playing);
        intent.Set(1, VideoPlaybackMode.Stopped); // StopAsync retires video before displaying image
        Assert.True(intent.LoopEnabled);
        var next = intent.Set(3, VideoPlaybackMode.Playing);
        Assert.True(intent.LoopEnabled);
        Assert.False(intent.CanRepeat(first));
        Assert.True(intent.CanRepeat(next));
        intent.ToggleLoop();
        intent.Set(4, VideoPlaybackMode.Playing);
        Assert.False(intent.LoopEnabled);
        Assert.False(intent.CanRepeat(intent.Current));
        Assert.False(new VideoPlaybackIntent().LoopEnabled); // no cross-gallery/profile default change
    }

    [Fact]
    public void NewSeekEpochInvalidatesTerminalWorkWithinSameGeneration()
    {
        VideoPlaybackIntent intent = new();
        intent.ToggleLoop();
        var old = intent.Set(1, VideoPlaybackMode.Playing);
        var seek = intent.Set(1, intent.Current.Mode);
        Assert.False(intent.CanRepeat(old));
        Assert.True(intent.CanRepeat(seek));
        Assert.False(intent.IsCurrent(old));
    }
}

// The real player's frozen viewer events forward these same policies on its UI dispatcher.
public sealed class VideoPlaybackEventTests
{
    [Fact]
    public void PreferenceApplicationIsSilentAndUserToggleNotifiesOnceWithoutEcho()
    {
        VideoPlaybackIntent intent = new();
        List<bool> changed = new();
        intent.LoopChanged += value =>
        {
            changed.Add(value);
            Assert.Equal(value, intent.LoopEnabled);
            Assert.False(intent.SetLoopEnabled(value, notify: true)); // preference echo
        };
        Assert.True(intent.SetLoopEnabled(true)); // profile application before any media
        Assert.Empty(changed);
        intent.Set(1, VideoPlaybackMode.Playing);
        intent.ToggleLoop();
        Assert.Equal(new[] { false }, changed);
        Assert.True(intent.SetLoopEnabled(true, notify: true));
        Assert.Equal(new[] { false, true }, changed);
        intent.Set(1, VideoPlaybackMode.Stopped); // image/retirement is not a preference change
        Assert.True(intent.LoopEnabled);
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void ExplicitStartOverridesSilentAutoplayOff_RequestsAreNotBackendCallbacks()
    {
        VideoPlaybackIntent intent = new();
        List<bool> requested = new();
        intent.UserPlaybackRequested += requested.Add;
        intent.Set(1, VideoPlaybackMode.Stopped); // Show with autoplay off
        Assert.Empty(requested);
        var play = Assert.IsType<VideoPlaybackIntentStamp>(intent.RequestUserPlayback(1, VideoPlaybackMode.Playing));
        Assert.Equal(VideoPlaybackMode.Playing, play.Mode);
        Assert.Equal(new[] { true }, requested);
        intent.MarkPlaying(play);
        Assert.True(intent.TryAcceptEnd(play));
        Assert.True(intent.FinishNaturalEnd(play));
        Assert.Equal(new[] { true }, requested); // a native end is not a user Pause
        intent.RequestUserPlayback(1, VideoPlaybackMode.Playing); // Resume/replay
        intent.RequestUserPlayback(1, VideoPlaybackMode.Paused);
        intent.RequestUserPlayback(1, VideoPlaybackMode.Stopped);
        Assert.Equal(new[] { true, true, false, false }, requested);
        intent.Set(1, VideoPlaybackMode.Stopped); // StopAsync/DisposeAsync
        intent.Set(2, VideoPlaybackMode.Playing); // next media autoplay
        Assert.Equal(4, requested.Count);
    }

    [Fact]
    public void UserPlaybackObserverCannotRecursivelyRequestAnotherPlayerCommand()
    {
        VideoPlaybackIntent intent = new();
        int notifications = 0;
        intent.UserPlaybackRequested += _ =>
        {
            notifications++;
            Assert.Null(intent.RequestUserPlayback(1, VideoPlaybackMode.Playing));
            Assert.Null(intent.RequestUserPlayback(1, VideoPlaybackMode.Paused));
        };
        var request = Assert.IsType<VideoPlaybackIntentStamp>(intent.RequestUserPlayback(1, VideoPlaybackMode.Playing));
        Assert.Equal(1, notifications);
        Assert.Equal(request, intent.Current);
        Assert.NotNull(intent.RequestUserPlayback(1, VideoPlaybackMode.Paused));
        Assert.Equal(2, notifications); // guard is scoped only to synchronous event delivery
    }

    [Fact]
    public void NaturalEndRequiresActualStartAndNotifiesOnceAfterEnteringPausedState()
    {
        VideoPlaybackIntent intent = new();
        int ended = 0;
        intent.PlaybackEnded += () => { Assert.Equal(VideoPlaybackMode.Paused, intent.Current.Mode); ended++; };
        var play = intent.Set(1, VideoPlaybackMode.Playing);
        Assert.False(intent.TryAcceptEnd(play)); // late prior EndReached before new Playing
        Assert.True(intent.MarkPlaying(play));
        Assert.True(intent.TryAcceptEnd(play));
        Assert.False(intent.TryAcceptEnd(play));
        Assert.True(intent.FinishNaturalEnd(play));
        Assert.False(intent.FinishNaturalEnd(play));
        Assert.Equal(1, ended);
        var replay = Assert.IsType<VideoPlaybackIntentStamp>(intent.RequestUserPlayback(1, VideoPlaybackMode.Playing));
        Assert.False(intent.TryAcceptEnd(replay));
        Assert.False(intent.TryAcceptEnd(play));
        intent.MarkPlaying(replay);
        Assert.True(intent.TryAcceptEnd(replay));
        Assert.True(intent.FinishNaturalEnd(replay));
        Assert.Equal(2, ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPauseOrStopInvalidatesEvenAnAlreadyClaimedEnd(bool stop)
    {
        VideoPlaybackIntent intent = new();
        int ended = 0;
        intent.PlaybackEnded += () => ended++;
        var play = intent.Set(1, VideoPlaybackMode.Playing);
        intent.MarkPlaying(play);
        Assert.True(intent.TryAcceptEnd(play));
        intent.RequestUserPlayback(1, stop ? VideoPlaybackMode.Stopped : VideoPlaybackMode.Paused);
        Assert.False(intent.FinishNaturalEnd(play));
        Assert.False(intent.TryAcceptEnd(intent.Current));
        Assert.Equal(0, ended);
    }

    [Fact]
    public void RepeatNeverNotifiesAndMustRearmAfterStopBeforeNextNaturalCycle()
    {
        VideoPlaybackIntent intent = new();
        int ended = 0;
        intent.PlaybackEnded += () => ended++;
        intent.SetLoopEnabled(true);
        var play = intent.Set(1, VideoPlaybackMode.Playing);
        intent.MarkPlaying(play);
        Assert.True(intent.TryAcceptEnd(play));
        Assert.False(intent.FinishNaturalEnd(play));
        intent.SetLoopEnabled(false); // duplicate end during repeat Stop must not become navigation
        Assert.False(intent.TryAcceptEnd(play));
        Assert.Null(intent.BeginRepeat(play));
        intent.SetLoopEnabled(true);
        var repeat = Assert.IsType<VideoPlaybackIntentStamp>(intent.BeginRepeat(play));
        Assert.False(intent.TryAcceptEnd(play));
        Assert.False(intent.TryAcceptEnd(repeat));
        intent.MarkPlaying(repeat);
        Assert.True(intent.TryAcceptEnd(repeat));
        Assert.False(intent.FinishNaturalEnd(repeat));
        Assert.Equal(0, ended);
    }

    [Fact]
    public async Task RetiredGenerationCannotDeliverNaturalEndEvenIfItsIntentStillLooksCurrent()
    {
        using SemaphoreSlim operations = new(1, 1);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var show = Assert.IsType<VideoPlaybackLifetime.ShowRequest>(lifetime.AcceptShow(CancellationToken.None));
        lifetime.FinishShow(show);
        VideoPlaybackIntent intent = new();
        var play = intent.Set(show.Generation, VideoPlaybackMode.Playing);
        intent.MarkPlaying(play);
        int ended = 0;
        intent.PlaybackEnded += () => ended++;
        await lifetime.StopAsync(); // Post/command generation guard rejects before notification policy
        object target = new();
        await VideoPlaybackCommands.RunAsync(operations, lifetime, show.Generation, () => target,
            _ => show.Generation, _ =>
            {
                if (intent.TryAcceptEnd(play)) intent.FinishNaturalEnd(play);
            });
        Assert.Equal(0, ended);
        await lifetime.DisposeAsync();
    }
    [Fact]
    public void RepeatedExplicitPlayWhileRunningDoesNotRequireADuplicateNativePlayingEvent()
    {
        VideoPlaybackIntent intent = new();
        var play = intent.Set(1, VideoPlaybackMode.Playing);
        intent.MarkPlaying(play);
        var repeatedPlay = Assert.IsType<VideoPlaybackIntentStamp>(intent.RequestUserPlayback(1, VideoPlaybackMode.Playing));
        Assert.False(intent.TryAcceptEnd(play));
        Assert.True(intent.TryAcceptEnd(repeatedPlay));
        Assert.True(intent.FinishNaturalEnd(repeatedPlay));
    }

}
