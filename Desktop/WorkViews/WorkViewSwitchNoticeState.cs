using System;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// UI-thread-owned lifetime/identity of WV12 actions. Time is monotonic, not wall-clock time. Notification eviction,
/// dismissal, a new switch request and an applied view change all revoke old actions, even if a close callback is late.
/// Does not retain Config snapshots: Undo must change only WorkspaceMode through the normal save path.
/// </summary>
internal sealed class WorkViewSwitchNoticeState(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Notice? _active;
    private UndoRequest? _undo;
    private bool _closed;

    internal sealed record Notice(DiffSummary Diff, TimeSpan Duration, long Started);
    internal sealed record UndoRequest(WorkView From, WorkView To);

    /// <summary>Call before an accepted normal switch, not on a refused or no-op command.</summary>
    internal void BeginSwitch() { _active = null; _undo = null; }

    /// <summary>Consumes this occurrence once; a later request also revokes its narrow suppression ticket.</summary>
    internal UndoRequest? BeginUndo(Notice notice, WorkView current)
    {
        if (!TryTake(notice, current)) return null;
        return _undo = new UndoRequest(notice.Diff.To, notice.Diff.From);
    }

    internal void EndUndo(UndoRequest request)
    {
        if (ReferenceEquals(_undo, request)) _undo = null;
    }

    /// <summary>Observe every applied change, including footer/settings changes outside SetWorkViewAsync.</summary>
    internal Notice? Applied(WorkView from, WorkView to, int seconds, bool informationAllowed)
    {
        if (from == to) return null;
        bool ownUndo = _undo is { } undo && undo.From == from && undo.To == to;
        _active = null;
        _undo = null; // Suppress exactly the owned transition, never an entire async save interval.
        if (_closed || ownUndo || !informationAllowed || seconds <= 0) return null;
        if (seconds > Config.WorkViewSwitchNoticeSecondsMaximum) throw new ArgumentOutOfRangeException(nameof(seconds));
        return _active = new Notice(WorkViewPolicy.Diff(from, to), TimeSpan.FromSeconds(seconds), _clock.GetTimestamp());
    }

    internal bool IsCurrent(Notice notice, WorkView current) => !_closed && ReferenceEquals(_active, notice)
        && current == notice.Diff.To && _clock.GetElapsedTime(notice.Started) < notice.Duration;

    /// <summary>The host receives the captured pair and row/page diff, never a recomputation from the latest view.</summary>
    internal DiffSummary? TakeDetails(Notice notice, WorkView current) => TryTake(notice, current) ? notice.Diff : null;

    internal bool TryTake(Notice notice, WorkView current)
    {
        if (!IsCurrent(notice, current)) return false;
        _active = null;
        return true;
    }

    internal void Dismiss(Notice notice)
    {
        if (ReferenceEquals(_active, notice)) _active = null;
    }

    internal void Cancel() { _active = null; _undo = null; }
    internal void Close() { Cancel(); _closed = true; }
}
