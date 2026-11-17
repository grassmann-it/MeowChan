namespace MeowChan.Desktop;

internal enum AdmissionRevealStep { Idle, Wait, ShowThread, ShowBoard, Drop }

/// <summary>
/// DL-02: after an Add the Downloads page selects the new watch's row and scrolls it into view. The rows follow the session in slices,
/// so the row often does not exist yet when the Add returns: the request waits for the next completed row frame, for a short while.
/// This is the decision only (nothing here touches a control): <see cref="Next"/> says what the window does now.
/// </summary>
internal sealed class AdmissionReveal
{
    private long? _watchId;
    private long _deadline;

    /// <summary>The watch whose row is still to be shown, or null.</summary>
    internal long? WatchId => _watchId;

    /// <summary>Asks for the row of <paramref name="watchId"/> (null: nothing to show) for the next <paramref name="windowMilliseconds"/>.</summary>
    internal void Request(long? watchId, long nowMilliseconds, long windowMilliseconds)
    {
        _watchId = watchId;
        _deadline = nowMilliseconds + windowMilliseconds;
    }

    internal void Cancel() => _watchId = null;

    /// <summary>
    /// What to do now. <see cref="AdmissionRevealStep.Wait"/> keeps the request for the next completed frame; the two Show steps and
    /// <see cref="AdmissionRevealStep.Drop"/> end it. A row the search filter hides (<paramref name="visible"/> false), a page the user left
    /// (<paramref name="pageReady"/> false) and an expired request are dropped: the selection the user made since is never overwritten later.
    /// </summary>
    internal AdmissionRevealStep Next(long nowMilliseconds, bool pageReady, bool rowsApplying, bool threadRow, bool boardRow, bool visible)
    {
        if (_watchId == null) return AdmissionRevealStep.Idle;
        if (!pageReady || nowMilliseconds > _deadline)
        {
            _watchId = null;
            return AdmissionRevealStep.Drop;
        }
        if (rowsApplying || (!threadRow && !boardRow)) return AdmissionRevealStep.Wait;
        _watchId = null;
        if (!visible) return AdmissionRevealStep.Drop;
        return threadRow ? AdmissionRevealStep.ShowThread : AdmissionRevealStep.ShowBoard;
    }
}
