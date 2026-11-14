using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Tom's E3 addition (G1): in the tagging mode the slideshow may run; it waits while the tag panel is worked in and goes on afterwards
// without skipping the shown medium. The viewer holds the controller (SetAdvanceHeld) while TaggingSlideshowHold.Holds is true and calls
// RenewAfterHold before it releases the hold; these tests pin the controller's half with a manual clock.
public sealed class GallerySlideshowTagHoldTests
{
    [Fact]
    public void AnImageHeldWhileTaggingGetsAFullPeriodAfterTheHold()
    {
        Fixture fixture = new();
        fixture.BeginImage(1);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4.5)); // 0.5 s left of 5
        fixture.Controller.SetAdvanceHeld(1, true); // the person types a tag
        fixture.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, fixture.Next); // the hold never moves on
        fixture.Controller.RenewAfterHold(1);
        fixture.Controller.SetAdvanceHeld(1, false);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, fixture.Next); // not the 0.5 s that were left: the medium just tagged is not skipped
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, fixture.Next);
        Assert.True(fixture.Controller.IsRunning);
    }

    [Fact]
    public void WithoutTheRenewalAHoldKeepsTheRestAsBefore()
    {
        Fixture fixture = new();
        fixture.BeginImage(1);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.SetAdvanceHeld(1, true); // a menu, a drag: the old rule (the rest of the period)
        fixture.Advance(TimeSpan.FromMinutes(1));
        fixture.Controller.SetAdvanceHeld(1, false);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void ARenewalUnderAnotherHoldWaitsForThatHoldToo()
    {
        Fixture fixture = new();
        fixture.BeginImage(1);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4.9));
        fixture.Controller.SetAdvanceHeld(1, true);
        fixture.Controller.RenewAfterHold(1); // the panel is done, but a menu is still open
        fixture.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.SetAdvanceHeld(1, false);
        fixture.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void AVideoThatEndedWhileTaggingStaysOnePeriodInsteadOfMovingOnAtOnce()
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.BeginMedia(1, video: true, hasNext: true);
        fixture.Controller.VideoReady(1);
        fixture.Controller.Start();
        fixture.Controller.SetAdvanceHeld(1, true);
        fixture.Controller.VideoEnded(1); // ends while the person tags
        fixture.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, fixture.Next);
        fixture.Controller.RenewAfterHold(1);
        fixture.Controller.SetAdvanceHeld(1, false);
        fixture.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fixture.Next);
        fixture.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void AVideoStillPlayingKeepsItsOwnRuleAfterTheHold()
    {
        Fixture fixture = new();
        fixture.Controller.SetMode(GalleryPlaybackMode.Slideshow);
        fixture.Controller.BeginMedia(1, video: true, hasNext: true);
        fixture.Controller.VideoReady(1);
        fixture.Controller.Start();
        fixture.Controller.SetAdvanceHeld(1, true);
        fixture.Controller.RenewAfterHold(1);
        fixture.Controller.SetAdvanceHeld(1, false);
        fixture.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, fixture.Next); // a playing video moves on at its end, never by the image clock
        fixture.Controller.VideoEnded(1);
        Assert.Equal(1, fixture.Next);
    }

    [Fact]
    public void AStaleOrConsumedGenerationIsNotRenewed()
    {
        Fixture fixture = new();
        fixture.BeginImage(2);
        fixture.Controller.Start();
        fixture.Advance(TimeSpan.FromSeconds(4.5));
        fixture.Controller.RenewAfterHold(1); // an older medium
        fixture.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(1, fixture.Next);
        fixture.Controller.RenewAfterHold(2); // already moved on: nothing comes back
        fixture.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fixture.Next);
    }

    private sealed class Fixture
    {
        public ManualTime Clock { get; } = new();
        public int Next { get; private set; }
        public GallerySlideshowController Controller { get; }
        public Fixture() => Controller = new GallerySlideshowController(() => Next++, Clock);
        public void BeginImage(long generation)
        {
            Controller.SetMode(GalleryPlaybackMode.Slideshow);
            Controller.BeginMedia(generation, video: false, hasNext: true);
            Controller.ImageReady(generation);
        }
        public void Advance(TimeSpan elapsed) { Clock.Advance(elapsed); Controller.Tick(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("The caller owns Tick; slideshow must not create timers.");
        public void Advance(TimeSpan duration) => _timestamp = checked(_timestamp + duration.Ticks);
    }
}
