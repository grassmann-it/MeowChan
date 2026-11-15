using System;

namespace MeowChan.Desktop;

/// <summary>
/// Who may write the reader list's scroll offset for the selected card. A reader jump (keyboard, find, "Show in thread") aligns its
/// target itself and owns the offset while it runs; the selection anchor and the re-fit after a frame change of the focused card stand
/// back then. The reader taking over (a press, a wheel turn, a paging key: each bumps the interaction revision) ends that ownership.
/// Pure state: no control, no layout pass.
/// </summary>
internal sealed class ReaderScrollOwner
{
    private int _jumps;
    private int _jumpInteraction;
    private long _fitUntil = long.MinValue;
    private int _fitInteraction;

    /// <summary>Starts a jump at the reader's current interaction revision; disposing the result ends it (also when it was superseded).</summary>
    internal Jump BeginJump(int interaction)
    {
        _jumps++;
        _jumpInteraction = interaction;
        return new Jump(this);
    }

    /// <summary>True while a jump runs and the reader has not taken over since the latest one began.</summary>
    internal bool JumpOwns(int interaction) => _jumps > 0 && _jumpInteraction == interaction;

    /// <summary>
    /// Opens the window in which a size change of the focused card's frame (the decoded aspect replacing the reserved one) may still
    /// re-fit the view: until <paramref name="now"/> plus <paramref name="windowMilliseconds"/>, and only as long as the reader does not
    /// take over (<paramref name="interaction"/> is the revision the selection was made at).
    /// </summary>
    internal void OpenFitWindow(long now, int interaction, int windowMilliseconds)
    {
        _fitUntil = now + Math.Max(0, windowMilliseconds);
        _fitInteraction = interaction;
    }

    /// <summary>Whether a pending re-fit may still move the list: inside the window, no jump owns the offset, the reader did not take over.</summary>
    internal bool MayRefit(long now, int interaction) => now <= _fitUntil && interaction == _fitInteraction && !JumpOwns(interaction);

    internal readonly struct Jump : IDisposable
    {
        private readonly ReaderScrollOwner? _owner;
        internal Jump(ReaderScrollOwner owner) => _owner = owner;
        public void Dispose() { if (_owner != null) _owner._jumps = Math.Max(0, _owner._jumps - 1); }
    }
}
