using Avalonia.Input;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// One scroll owner per reader jump (review 2026-11-15, A1): while a jump aligns its card, the selection anchor and the frame re-fit
// stand back; the reader taking over (the interaction revision moves) ends that at once.
public sealed class ReaderScrollOwnerTests
{
    [Fact]
    public void AJumpOwnsTheScrollUntilItEnds()
    {
        ReaderScrollOwner owner = new();
        Assert.False(owner.JumpOwns(3));
        using (owner.BeginJump(3))
        {
            Assert.True(owner.JumpOwns(3));
        }
        Assert.False(owner.JumpOwns(3));
    }

    [Fact]
    public void TheReaderTakingOverEndsTheJumpsOwnership()
    {
        ReaderScrollOwner owner = new();
        using ReaderScrollOwner.Jump jump = owner.BeginJump(7);
        // A press, a wheel turn or a paging key bumps the revision: a pointer selection made now is anchored again.
        Assert.False(owner.JumpOwns(8));
    }

    [Fact]
    public void ASupersededJumpDoesNotEndTheNewerOne()
    {
        ReaderScrollOwner owner = new();
        ReaderScrollOwner.Jump older = owner.BeginJump(1);
        ReaderScrollOwner.Jump newer = owner.BeginJump(2);
        older.Dispose(); // the stale jump returns on its next pass
        Assert.True(owner.JumpOwns(2));
        Assert.False(owner.JumpOwns(1));
        newer.Dispose();
        Assert.False(owner.JumpOwns(2));
        // A default (never begun) jump disposes harmlessly.
        default(ReaderScrollOwner.Jump).Dispose();
        Assert.False(owner.JumpOwns(2));
    }

    // A7: the re-fit after a late frame change of the focused card respects the reader taking over and its time window.
    [Fact]
    public void ReFitOnlyInsideItsWindowAndWhileTheReaderDidNotTakeOver()
    {
        ReaderScrollOwner owner = new();
        Assert.False(owner.MayRefit(0, 0)); // no selection yet
        owner.OpenFitWindow(1000, 4, 1500);
        Assert.True(owner.MayRefit(1000, 4));
        Assert.True(owner.MayRefit(2500, 4));
        Assert.False(owner.MayRefit(2501, 4)); // the window is over
        Assert.False(owner.MayRefit(1200, 5)); // a wheel turn right after the selection cancels the pending re-fit
    }

    [Fact]
    public void ReFitStandsBackWhileAJumpAlignsTheCard()
    {
        ReaderScrollOwner owner = new();
        owner.OpenFitWindow(0, 2, 1500);
        using (owner.BeginJump(2)) Assert.False(owner.MayRefit(10, 2));
        Assert.True(owner.MayRefit(10, 2));
    }

    // Review finding 4: the list no longer scrolls on focus by itself; keyboard focus of a control inside a card does so explicitly.
    [Theory]
    [InlineData(NavigationMethod.Tab, true)]
    [InlineData(NavigationMethod.Directional, true)]
    [InlineData(NavigationMethod.Pointer, false)]
    [InlineData(NavigationMethod.Unspecified, false)]
    public void OnlyKeyboardNavigationScrollsAFocusedControlIntoView(NavigationMethod method, bool scrolls)
    {
        ReaderScrollOwner owner = new();
        Assert.Equal(scrolls, owner.FocusMayScroll(method, 5));
    }

    [Fact]
    public void AJumpStillOwnsTheOffsetWhenTheKeyboardMovesTheFocus()
    {
        ReaderScrollOwner owner = new();
        using (owner.BeginJump(5))
        {
            Assert.False(owner.FocusMayScroll(NavigationMethod.Tab, 5));
            // The reader took over meanwhile (the revision moved): the focus may scroll again.
            Assert.True(owner.FocusMayScroll(NavigationMethod.Tab, 6));
        }
        Assert.True(owner.FocusMayScroll(NavigationMethod.Tab, 5));
    }
}
