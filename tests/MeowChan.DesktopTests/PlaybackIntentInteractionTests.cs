using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>Pure intent/generation checks: no Avalonia application, native player or GUI.</summary>
public sealed class PlaybackIntentInteractionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SelectedVideoPlaybackAndAutomaticProgressionAreIndependent(bool autoplay, bool running, bool play)
    {
        // The saved Slideshow choice cannot start automatic NEXT, even when its video autoplays.
        int next = 0;
        GallerySlideshowController controller = new(() => next++);
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.BeginMedia(1, video: true, hasNext: true);
        if (running) controller.Start();
        Assert.Equal(play, GalleryPlaybackPolicy.ShouldPlaySelection(autoplay, controller.IsRunning));
        controller.VideoEnded(1);
        Assert.Equal(running ? 1 : 0, next);
    }

    [Fact]
    public void TemporaryImageHoldPreservesRemainingTimeAndRunningIntent()
    {
        Clock clock = new();
        int next = 0;
        GallerySlideshowController controller = new(() => next++, clock);
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.BeginMedia(1, video: false, hasNext: true);
        controller.ImageReady(1);
        controller.Start();
        clock.Advance(2);
        controller.SetAdvanceHeld(1, true);
        clock.Advance(100);
        controller.SetAdvanceHeld(1, true); // Repeated focus/menu observations cannot restart the clock.
        controller.Tick();
        Assert.True(controller.IsRunning);
        Assert.Equal(0, next);
        controller.SetAdvanceHeld(1, false);
        clock.Advance(2);
        controller.Tick();
        Assert.Equal(0, next);
        clock.Advance(1);
        controller.Tick();
        Assert.Equal(1, next);
    }

    [Fact]
    public void VideoEndDuringPanelOrVolumeHoldAdvancesOnceOnlyAfterReleaseAndTick()
    {
        int next = 0;
        GallerySlideshowController controller = RunningVideo(() => next++);
        controller.SetAdvanceHeld(1, true);
        controller.VideoEnded(1);
        controller.VideoEnded(1);
        controller.Tick();
        Assert.True(controller.IsRunning);
        Assert.Equal(0, next);
        controller.SetAdvanceHeld(1, false);
        Assert.Equal(0, next); // Releasing a hold cannot reenter navigation from a UI callback.
        controller.Tick();
        controller.VideoEnded(1);
        controller.Tick();
        Assert.Equal(1, next);
    }

    [Fact]
    public void SeekInvalidatesDeferredEndWithoutPermanentlyPausingProgression()
    {
        int next = 0;
        GallerySlideshowController controller = RunningVideo(() => next++);
        controller.SetAdvanceHeld(1, true);
        controller.VideoEnded(1);
        controller.SeekStarted(1);
        controller.SetAdvanceHeld(1, false);
        controller.Tick();
        Assert.True(controller.IsRunning);
        Assert.Equal(0, next);
        controller.VideoEnded(1); // Only the new natural end may advance now.
        Assert.Equal(1, next);
    }

    [Fact]
    public void StaleHoldReleaseAndSeekCannotTouchTheNextGeneration()
    {
        int next = 0;
        GallerySlideshowController controller = RunningVideo(() => next++);
        controller.SetAdvanceHeld(1, true);
        controller.VideoEnded(1);
        controller.BeginMedia(2, video: true, hasNext: true);
        controller.SetAdvanceHeld(1, true);
        Assert.False(controller.IsAdvanceHeld);
        controller.SetAdvanceHeld(2, true);
        controller.VideoEnded(2);
        controller.SeekStarted(1);
        controller.SetAdvanceHeld(1, false);
        controller.Tick();
        Assert.True(controller.IsAdvanceHeld);
        Assert.Equal(0, next);
        controller.SetAdvanceHeld(2, false);
        controller.Tick();
        Assert.Equal(1, next);
    }

    [Fact]
    public void ExplicitPauseDuringHoldCannotBeUndoneByUiRelease()
    {
        int next = 0;
        GallerySlideshowController controller = RunningVideo(() => next++);
        controller.SetAdvanceHeld(1, true);
        controller.VideoEnded(1);
        controller.Pause();
        controller.SetAdvanceHeld(1, false);
        controller.Tick();
        controller.Start(); // Deferred pre-pause end is not an implicit replay request.
        Assert.False(controller.IsRunning);
        Assert.Equal(0, next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeekAndVolumeDoNotPublishOrChangeNativePlayPauseIntent(bool playing)
    {
        VideoPlaybackMode mode = playing ? VideoPlaybackMode.Playing : VideoPlaybackMode.Paused;
        VideoPlaybackIntent intent = new();
        VideoPlaybackIntentStamp old = intent.Set(1, mode);
        if (playing) intent.MarkPlaying(old);
        int userRequests = 0;
        intent.UserPlaybackRequested += _ => userRequests++;
        VideoTimelineInteraction timeline = new();
        timeline.Reset(1);
        timeline.SeekInteractionStarted += () => intent.Set(1, intent.Current.Mode);
        timeline.BeginGesture(pointer: true);
        VideoSeekRequest request = timeline.Request(1, 600, 10_000, seekable: true)!.Value;
        Assert.False(intent.TryAcceptEnd(old));
        VideoVolumeIntent volume = new();
        volume.Request(1, 40);
        volume.Request(1, 80);
        timeline.Complete(request, 6_000);
        timeline.EndGesture(pointer: true);
        Assert.Equal(mode, intent.Current.Mode);
        Assert.Equal(0, userRequests);
    }

    [Fact]
    public void LatestVolumeValueWinsOutOfOrderWorkAndReadbacks()
    {
        VideoVolumeIntent volume = new();
        var first = volume.Request(1, 15);
        var latest = volume.Request(1, 85);
        Assert.False(volume.IsCurrent(first));
        Assert.True(volume.IsCurrent(latest));
        var nextMedia = volume.Request(2, 85);
        Assert.False(volume.IsCurrent(latest));
        Assert.True(volume.IsCurrent(nextMedia));
        Assert.Equal(85, nextMedia.Volume);
        var sameValueNewer = volume.Request(2, 85);
        Assert.False(volume.IsCurrent(nextMedia));
        Assert.True(volume.IsCurrent(sameValueNewer));
    }

    [Fact]
    public void ConfigurationAutoplayAndUiHoldsDoNotPublishUserTransportRequests()
    {
        VideoPlaybackIntent intent = new();
        int requests = 0;
        intent.UserPlaybackRequested += _ => requests++;
        intent.Set(1, GalleryPlaybackPolicy.ShouldPlaySelection(true, false)
            ? VideoPlaybackMode.Playing : VideoPlaybackMode.Stopped);
        GallerySlideshowController controller = RunningVideo(() => { });
        controller.SetAdvanceHeld(1, true);
        controller.SetAdvanceHeld(1, false);
        Assert.Equal(VideoPlaybackMode.Playing, intent.Current.Mode);
        Assert.Equal(0, requests);
        intent.RequestUserPlayback(1, VideoPlaybackMode.Paused);
        Assert.Equal(1, requests);
        Assert.Equal(VideoPlaybackMode.Paused, intent.Current.Mode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExplicitSlideshowPauseBeforeMediaAdmissionOverridesSelectionAutoplay(bool autoplay, bool running)
        => Assert.False(GalleryPlaybackPolicy.ShouldPlaySelection(autoplay, running, explicitlyPaused: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeekingDoesNotInvalidateQueuedExplicitTransport(bool playing)
    {
        VideoPlaybackMode mode = playing ? VideoPlaybackMode.Playing : VideoPlaybackMode.Paused;
        VideoPlaybackIntent playback = new();
        VideoTransportIntent transport = new();
        var command = transport.Request(3, mode);
        var beforeSeek = playback.RequestUserPlayback(3, mode)!.Value;
        playback.Set(3, mode); // The production seek invalidates old terminal callbacks.
        Assert.False(playback.IsCurrent(beforeSeek));
        Assert.True(transport.IsCurrent(command)); // But the native command must still run.
        var newer = transport.Request(3, VideoPlaybackMode.Stopped);
        Assert.False(transport.IsCurrent(command));
        Assert.True(transport.IsCurrent(newer));
        var replacement = transport.Request(4, mode);
        Assert.False(transport.IsCurrent(newer));
        Assert.True(transport.IsCurrent(replacement));
    }

    [Fact]
    public async Task QueuedNativeTransportSurvivesSeekButRejectsSupersededAndReplacementCommands()
    {
        using SemaphoreSlim operations = new(0, 1);
        VideoPlaybackLifetime lifetime = new(_ => Task.CompletedTask, () => Task.CompletedTask);
        var selection = lifetime.AcceptShow(CancellationToken.None)!;
        lifetime.FinishShow(selection);
        var native = new NativeTransport(selection.Generation);
        VideoPlaybackIntent playback = new();
        VideoTransportIntent transport = new();
        int applied = 0;
        Task Queue(VideoPlaybackIntentStamp command) => VideoPlaybackCommands.RunAsync(operations, lifetime,
            command.Generation, () => native, value => value.Generation, value =>
            {
                if (!transport.IsCurrent(command)) return;
                value.Mode = command.Mode;
                applied++;
            });
        var old = transport.Request(selection.Generation, VideoPlaybackMode.Playing);
        Task first = Queue(old);
        var latest = transport.Request(selection.Generation, VideoPlaybackMode.Paused);
        playback.RequestUserPlayback(selection.Generation, VideoPlaybackMode.Paused);
        Task second = Queue(latest);
        playback.Set(selection.Generation, VideoPlaybackMode.Paused); // Seek starts before native admission.
        operations.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, applied);
        Assert.Equal(VideoPlaybackMode.Paused, native.Mode);
        await operations.WaitAsync();
        Task retiring = Queue(latest);
        var replacement = lifetime.AcceptShow(CancellationToken.None)!;
        native = new NativeTransport(replacement.Generation);
        lifetime.FinishShow(replacement);
        operations.Release();
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, applied);
        await lifetime.DisposeAsync();
    }

    private sealed class NativeTransport(long generation)
    {
        internal long Generation { get; } = generation;
        internal VideoPlaybackMode Mode { get; set; } = VideoPlaybackMode.Playing;
    }

    [Fact]
    public void SupersededTransportCannotRunEvenWhenTheNewModeMatchesTheOldOne()
    {
        VideoTransportIntent transport = new();
        var oldPause = transport.Request(1, VideoPlaybackMode.Paused);
        transport.Request(1, VideoPlaybackMode.Playing);
        var latestPause = transport.Request(1, VideoPlaybackMode.Paused);
        Assert.False(transport.IsCurrent(oldPause));
        Assert.True(transport.IsCurrent(latestPause));
    }

    [Fact]
    public void ReadyVolumeSnapshotTracksEditsAcceptedDuringLoadingAndSameValueRevisions()
    {
        VideoVolumeIntent volume = new();
        var initial = volume.Request(1, 70);
        var duringLoad = volume.Request(1, 20);
        Assert.Equal(duringLoad, volume.Current);
        Assert.False(volume.IsCurrent(initial));
        var configuredAgain = volume.Request(1, 20);
        Assert.NotEqual(duringLoad.Revision, volume.Current.Revision);
        Assert.Equal(configuredAgain, volume.Current);
        Assert.Equal(20, volume.Current.Volume);
    }

    [Fact]
    public void AdvancingConsumesBeforeReentrantCallbacksAndDoesNotOverwriteSuccessorHold()
    {
        int next = 0;
        GallerySlideshowController controller = null!;
        controller = RunningVideo(() =>
        {
            next++;
            controller.Tick();
            controller.VideoEnded(1);
            controller.BeginMedia(2, video: true, hasNext: true);
            controller.SetAdvanceHeld(2, true);
        });
        controller.VideoEnded(1);
        Assert.Equal(1, next);
        Assert.True(controller.IsRunning);
        Assert.True(controller.IsAdvanceHeld);
        controller.VideoEnded(2);
        controller.Tick();
        Assert.Equal(1, next);
        controller.SetAdvanceHeld(2, false);
        controller.Tick();
        Assert.Equal(2, next);
    }

    private static GallerySlideshowController RunningVideo(Action next)
    {
        GallerySlideshowController controller = new(next);
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.BeginMedia(1, video: true, hasNext: true);
        controller.Start();
        return controller;
    }

    private sealed class Clock : TimeProvider
    {
        private long _time;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _time;
        public void Advance(int seconds) => _time += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
