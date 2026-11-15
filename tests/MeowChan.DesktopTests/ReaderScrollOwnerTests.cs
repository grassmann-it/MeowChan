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
}
