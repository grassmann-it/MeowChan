namespace MeowChan.DesktopTests;

using MeowChan.Desktop;

public sealed class GallerySlideshowControllerTests
{
    [Fact]
    public void LateAvailabilityDoesNotRestartTheVisibleImageClock()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.UpdateHasNext(1, false);
        fixture.Controller.UpdateHasNext(0, true); // Stale list cannot restore Next.
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
        fixture.Controller.UpdateHasNext(1, true); // Consumed generation stays consumed.
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Next);
    }

    [Fact]
    public void NewlyAvailableSuccessorIsUsedWithoutRearmingCurrentVideo()
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.BeginMedia(1, video: true, hasNext: false);
        fixture.Controller.Start();
        fixture.Controller.UpdateHasNext(1, true);
        fixture.Controller.VideoEnded(1);
        fixture.Controller.VideoEnded(1);
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void SelectingSlideshowDoesNotStartOrRestoreRunningState()
    {
        Fixture fixture = new();
        fixture.Controller.BeginMedia(1, video: false, hasNext: true);
        fixture.Controller.ImageReady(1);
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
    }

    [Fact]
    public void StartBeforeReadyWaitsThenCountsAFullVisibleImagePeriod()
    {
        Fixture fixture = new();
        fixture.BeginImage(1);
        fixture.Controller.Start();
        Assert.True(fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.ImageReady(1);
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void ReadyBeforeStartDoesNotChargeTimeSpentStopped()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Advance(TimeSpan.FromHours(1));
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void DuplicateReadyAndStartAndSameModeCannotRestartCountdown()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(3));
        fixture.Controller.ImageReady(1);
        fixture.Controller.Start();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.SetImageDuration(TimeSpan.FromSeconds(5));
        fixture.Advance(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Controller.IsRunning);
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void PauseAndResumeRetainRemainingImageTimeAcrossRepeatedPauses()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(2));
        fixture.Controller.Pause();
        fixture.Controller.Pause();
        fixture.Advance(TimeSpan.FromHours(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(1));
        fixture.Controller.Pause();
        fixture.Advance(TimeSpan.FromHours(1));
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void ImageBecomingReadyDuringPauseGetsFullPeriodOnExplicitResume()
    {
        Fixture fixture = new();
        fixture.BeginImage(1);
        fixture.Controller.Start();
        fixture.Controller.Pause();
        fixture.Advance(TimeSpan.FromHours(1));
        fixture.Controller.ImageReady(1);
        fixture.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void PausingAtDeadlineDoesNotAdvanceUntilResumeAndTick()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.Controller.Pause();
        fixture.Controller.Tick();
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Start();
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Tick();
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void ManualSelectionInvalidatesOldReadyAndStartsNewTimeOnlyAfterNewReady()
    {
        Fixture fixture = new();
        fixture.BeginImage(10, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.BeginMedia(11, video: false, hasNext: true);
        fixture.Controller.ImageReady(10);
        fixture.Controller.VideoEnded(10);
        fixture.Advance(TimeSpan.FromHours(1));
        Assert.True(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
        fixture.Controller.ImageReady(11);
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void SelectionWhilePausedDiscardsOldRemainingTimeWithoutStartingTheNewImage()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.Pause();
        fixture.Controller.BeginMedia(2, video: false, hasNext: true);
        fixture.Controller.ImageReady(1);
        fixture.Controller.ImageReady(2);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void OldOrDuplicateBeginMediaCannotChangeCurrentTypeOrResetItsClock()
    {
        Fixture fixture = new();
        fixture.BeginImage(20, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(3));
        fixture.Controller.BeginMedia(19, video: true, hasNext: false);
        fixture.Controller.BeginMedia(20, video: true, hasNext: false);
        fixture.Controller.VideoEnded(19);
        fixture.Controller.VideoEnded(20);
        Assert.True(fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void VideoIgnoresTimeAndImageReadyAndConsumesOnlyOneCurrentEnd()
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.BeginMedia(2, video: true, hasNext: true);
        fixture.Controller.Start();
        fixture.Controller.ImageReady(2);
        fixture.Advance(TimeSpan.FromDays(1));
        fixture.Controller.VideoEnded(1);
        fixture.Controller.VideoEnded(3);
        Assert.Equal(0, fixture.Next);
        fixture.Controller.VideoEnded(2);
        fixture.Controller.VideoEnded(2);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, fixture.Next);
        fixture.Controller.Pause();
        fixture.Controller.Start();
        fixture.Controller.VideoEnded(2);
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(1, fixture.Next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndWhilePausedOrBeforeStartIsNotReplayedByStart(bool pause)
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.BeginMedia(1, video: true, hasNext: true);
        if (pause)
        {
            fixture.Controller.Start();
            fixture.Controller.Pause();
        }
        fixture.Controller.VideoEnded(1);
        fixture.Controller.Start();
        fixture.Controller.VideoEnded(1);
        fixture.Controller.Tick();
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
        // Explicit replay/new selection must carry a newer viewer request generation.
        fixture.Controller.BeginMedia(2, video: true, hasNext: true);
        fixture.Controller.Start();
        fixture.Controller.VideoEnded(2);
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void MixedImageVideoImageSequenceStopsOnLastItemWithoutLooping()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Next);
        fixture.Controller.BeginMedia(2, video: true, hasNext: true);
        fixture.Advance(TimeSpan.FromDays(1));
        fixture.Controller.VideoEnded(1);
        Assert.Equal(1, fixture.Next);
        fixture.Controller.VideoEnded(2);
        Assert.Equal(2, fixture.Next);
        fixture.Controller.BeginMedia(3, video: false, hasNext: false);
        fixture.Controller.ImageReady(3);
        fixture.Controller.Start();
        Assert.True(fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.True(fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(2, fixture.Next);
    }

    [Fact]
    public void InvalidationPrecedesReentrantNavigationAndNextMediaOwnsItsFullPeriod()
    {
        ManualTime clock = new();
        int next = 0;
        GallerySlideshowController controller = null!;
        controller = new GallerySlideshowController(() =>
        {
            next++;
            controller.Tick();
            controller.ImageReady(1);
            controller.VideoEnded(1);
            controller.Pause();
            controller.Start(); // Must not rearm the consumed first image.
            controller.Tick();
            if (next == 1)
            {
                controller.BeginMedia(2, video: false, hasNext: true);
                controller.ImageReady(2);
                controller.Start();
            }
        }, clock);
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.BeginMedia(1, video: false, hasNext: true);
        controller.ImageReady(1);
        controller.Start();
        clock.Advance(TimeSpan.FromSeconds(5));
        controller.Tick();
        Assert.Equal(1, next);
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Tick();
        Assert.Equal(1, next);
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Tick();
        Assert.Equal(2, next);
    }

    [Fact]
    public void CallbackFailureStopsAndCannotDuplicateConsumedAdvance()
    {
        ManualTime clock = new();
        int calls = 0;
        GallerySlideshowController controller = new(() => { calls++; throw new InvalidOperationException("fixture"); }, clock);
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.BeginMedia(1, video: true, hasNext: true);
        controller.Start();
        Assert.Throws<InvalidOperationException>(() => controller.VideoEnded(1));
        Assert.False(controller.IsRunning);
        controller.Start();
        controller.VideoEnded(1);
        controller.Tick();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyCannotStartAndSingleEntryFinishesWithoutNavigation(bool single)
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        if (single) fixture.Controller.BeginMedia(1, video: false, hasNext: false);
        fixture.Controller.ImageReady(1);
        fixture.Controller.Start();
        Assert.Equal(single, fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromDays(1));
        fixture.Controller.VideoEnded(1);
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(0, fixture.Next);
    }

    [Fact]
    public void LastVideoStopsRunningAndLateEndsNeverNavigate()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Controller.BeginMedia(2, video: true, hasNext: false);
        Assert.True(fixture.Controller.IsRunning);
        fixture.Controller.VideoEnded(1);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.True(fixture.Controller.IsRunning);
        fixture.Controller.VideoEnded(2);
        Assert.False(fixture.Controller.IsRunning);
        fixture.Controller.Start();
        fixture.Controller.VideoEnded(2);
        Assert.Equal(0, fixture.Next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingSlideshowStopsAndReturningRequiresExplicitStart(bool repeat)
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.SetMode(repeat ? GalleryPlaybackMode.Repeat : GalleryPlaybackMode.Manual);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.False(fixture.Controller.IsRunning);
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void StopCancelsOldCountdownAndRestartUsesFullPeriod()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.Stop();
        fixture.Controller.Stop();
        fixture.Advance(TimeSpan.FromDays(1));
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
        fixture.Controller.Stop();
        fixture.Controller.Start();
        fixture.Controller.ImageReady(1);
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.False(fixture.Controller.IsRunning);
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void ChangedDurationRestartsFullPeriodButNeverStartsPausedController()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.SetImageDuration(TimeSpan.FromSeconds(2));
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.Pause();
        fixture.Controller.SetImageDuration(TimeSpan.FromSeconds(3));
        fixture.Advance(TimeSpan.FromDays(1));
        Assert.False(fixture.Controller.IsRunning);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void WallClockChangesDoNotChangeMonotonicImageCountdown()
    {
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(2));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(3);
        fixture.Controller.Tick();
        Assert.Equal(0, fixture.Next);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(-6);
        fixture.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void StateChangedReportsActualRunningTransitionsOnlyAndAfterStateChanges()
    {
        Fixture fixture = new();
        List<bool> states = new();
        fixture.Controller.StateChanged += () => states.Add(fixture.Controller.IsRunning);
        fixture.BeginImage(1);
        fixture.Controller.Start();
        fixture.Controller.Start();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.Pause();
        fixture.Controller.Pause();
        fixture.Controller.Start();
        fixture.Controller.BeginMedia(2, video: false, hasNext: false);
        fixture.Controller.ImageReady(2);
        fixture.Advance(TimeSpan.FromSeconds(5));
        fixture.Controller.Stop();
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public void InvalidInputsFailWithoutChangingRunningState()
    {
        Assert.Throws<ArgumentNullException>(() => new GallerySlideshowController(null!));
        Fixture fixture = new();
        fixture.BeginImage(1, ready: true);
        fixture.Controller.Start();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Controller.SetImageDuration(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Controller.SetImageDuration(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Controller.SetMode((GalleryPlaybackMode)999));
        Assert.True(fixture.Controller.IsRunning);
        fixture.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Next);
    }

    private sealed class Fixture
    {
        public ManualTime Clock { get; } = new();
        public int Next { get; private set; }
        public GallerySlideshowController Controller { get; }
        public Fixture() => Controller = new GallerySlideshowController(() => Next++, Clock);
        public void BeginImage(long generation, bool ready = false)
        {
            Controller.SetMode(GalleryPlaybackMode.Slideshow);
            Controller.BeginMedia(generation, video: false, hasNext: true);
            if (ready) Controller.ImageReady(generation);
        }
        public void Advance(TimeSpan elapsed) { Clock.Advance(elapsed); Controller.Tick(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("The caller owns Tick; slideshow must not create timers.");
        public void Advance(TimeSpan duration) => _timestamp = checked(_timestamp + duration.Ticks);
    }
}
