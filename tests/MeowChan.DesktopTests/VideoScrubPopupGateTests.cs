using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Hide triggers of the seek-bar hover preview popup. The popup is a separate top-level window, so every way the
/// owner can go away (minimise, deactivate, hide to tray, detach, close) must close it and keep it closed until the
/// pointer is over the timeline again.
/// </summary>
public sealed class VideoScrubPopupGateTests
{
    private static VideoScrubPopupGate Hovering()
    {
        VideoScrubPopupGate gate = new();
        gate.ApplyEnvironment(VideoScrubEnvironment.Normal);
        Assert.True(gate.OnPointerEntered());
        return gate;
    }

    [Fact]
    public void NothingShowsBeforeTheEnvironmentIsKnownOrThePointerIsSeen()
    {
        VideoScrubPopupGate gate = new();
        Assert.False(gate.MayShow);
        Assert.False(gate.OnPointerEntered()); // environment unknown: not attached/active/visible yet
        gate.ApplyEnvironment(VideoScrubEnvironment.Normal);
        Assert.True(gate.MayShow); // the pointer was already seen over the timeline
        VideoScrubPopupGate fresh = new();
        fresh.ApplyEnvironment(VideoScrubEnvironment.Normal);
        Assert.False(fresh.MayShow);
        Assert.True(fresh.OnPointerMoved());
    }

    [Fact]
    public void PointerLeavingHidesAndAMoveBringsItBack()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.OnPointerExited();
        Assert.False(gate.MayShow);
        Assert.True(gate.OnPointerMoved());
    }

    [Theory]
    [InlineData(true, false, false, true)] // window deactivated
    [InlineData(true, true, true, true)] // window minimised
    [InlineData(false, true, false, true)] // timeline detached from the visual tree
    [InlineData(true, true, false, false)] // window hidden to the tray
    public void LosingTheWindowEnvironmentHidesAndDemandsFreshPointerEvidence(bool attached, bool active, bool minimized, bool visible)
    {
        VideoScrubPopupGate gate = Hovering();
        VideoScrubEnvironment lost = new(attached, active, minimized, visible);
        Assert.False(gate.ApplyEnvironment(lost));
        Assert.False(gate.MayShow);

        // Moves while the window is still unusable never count.
        Assert.False(gate.OnPointerMoved());

        // Restored, but the popup stays hidden until the pointer is over the timeline again.
        Assert.False(gate.ApplyEnvironment(VideoScrubEnvironment.Normal));
        Assert.True(gate.OnPointerMoved());
    }

    [Fact]
    public void ARestoredWindowShowsNothingWithoutAPointerEventEvenIfThePointerWasOverTheTimelineBefore()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.ApplyEnvironment(new VideoScrubEnvironment(true, true, true, true));
        Assert.False(gate.ApplyEnvironment(VideoScrubEnvironment.Normal));
        Assert.False(gate.MayShow);
    }

    [Fact]
    public void ARepeatedUnusableEnvironmentDoesNotChangeTheLatches()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.ApplyEnvironment(new VideoScrubEnvironment(true, false, false, true));
        gate.ApplyEnvironment(new VideoScrubEnvironment(true, false, true, true));
        gate.ApplyEnvironment(VideoScrubEnvironment.Normal);
        Assert.False(gate.MayShow);
        Assert.True(gate.OnPointerEntered());
    }

    [Fact]
    public void EscapeHidesUntilThePointerLeavesAndEntersAgain()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.OnEscape();
        Assert.False(gate.MayShow);
        // Moving the mouse on the timeline does not undo the key press.
        Assert.False(gate.OnPointerMoved());
        Assert.False(gate.OnPointerMoved());
        gate.OnPointerExited();
        Assert.False(gate.MayShow);
        Assert.True(gate.OnPointerEntered());
    }

    [Fact]
    public void EscapeLatchSurvivesAWindowRoundTripButAFreshEnterClearsIt()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.OnEscape();
        gate.ApplyEnvironment(new VideoScrubEnvironment(true, false, false, true));
        gate.ApplyEnvironment(VideoScrubEnvironment.Normal);
        Assert.False(gate.OnPointerMoved());
        Assert.True(gate.OnPointerEntered());
    }

    [Fact]
    public void ChangingTheMediumForgetsThePointer()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.OnMediaChanged();
        Assert.False(gate.MayShow);
        Assert.True(gate.OnPointerMoved());
    }

    [Fact]
    public void ARetiredGateNeverShowsAgain()
    {
        VideoScrubPopupGate gate = Hovering();
        gate.Retire();
        Assert.False(gate.MayShow);
        Assert.False(gate.OnPointerEntered());
        Assert.False(gate.OnPointerMoved());
        Assert.False(gate.ApplyEnvironment(VideoScrubEnvironment.Normal));
    }

    [Fact]
    public void AnUnknownEnvironmentIsUnusable()
    {
        Assert.False(VideoScrubEnvironment.Unknown.IsUsable);
        Assert.True(VideoScrubEnvironment.Normal.IsUsable);
        Assert.False(new VideoScrubEnvironment(true, true, true, true).IsUsable);
        Assert.False(new VideoScrubEnvironment(true, true, false, false).IsUsable);
    }
}
