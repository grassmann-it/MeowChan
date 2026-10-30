namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MeowChan.Interfaces;
using MeowChan.Persistence;

public partial class DownloadQueue
{
    private sealed record UserPriorityState(long Generation, string Url, UserDownloadPriority Priority);
    private readonly ConditionalWeakTable<IThread, UserPriorityState> _userPriorities = new();
    private int _userHighDispatchStreak;
    private int _userHighAdmissionStreak;

    // Host must resolve the exact frozen watch ID to this IThread + WorkResource.
    // A stale generation cannot alter its successor, even if IThread is reused.
    internal bool SetUserPriority(IThread thread, WorkResource owner, UserDownloadPriority priority)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (!OwnerStillCurrentLocked(thread, owner)) return false;
            _userPriorities.Remove(thread);
            if (priority == UserDownloadPriority.High)
                _userPriorities.Add(thread, new(owner.Generation, owner.Url, priority));
            PulseCapacityLocked();
            // ReserveWorkersLocked retains pause/stop/target/filter gates. This
            // does not enqueue, retry, cancel or touch any active worker.
            ready = ReserveWorkersLocked();
        }
        StartWorkers(ready);
        _changes.Signal();
        return true;
    }

    internal bool SetCurrentUserPriority(IThread thread, UserDownloadPriority priority)
    {
        WorkResource? owner;
        lock (_stateLock) _workOwners.TryGetValue(thread, out owner);
        return owner != null && SetUserPriority(thread, owner, priority);
    }

    internal UserDownloadPriority GetUserPriority(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        lock (_stateLock) return GetUserPriorityLocked(thread);
    }

    private UserDownloadPriority GetUserPriorityLocked(IThread thread)
        => _userPriorities.TryGetValue(thread, out UserPriorityState? state)
            && _workOwners.TryGetValue(thread, out WorkResource? owner)
            && state.Generation == owner.Generation && StringComparer.Ordinal.Equals(state.Url, owner.Url)
            && OwnerStillCurrentLocked(thread, owner)
                ? state.Priority : UserDownloadPriority.Normal;

    // Pure peeks: NextWaiterLocked can be called repeatedly before acceptance.
    private UserDownloadPriority? PreferredUserGroupLocked(bool high, bool normal, bool admission)
        => UserPriorityPolicy.Preferred(high, normal, admission ? _userHighAdmissionStreak : _userHighDispatchStreak);

    private void RecordUserReservationLocked(IThread thread, bool admission)
    {
        if (admission) _userHighAdmissionStreak = UserPriorityPolicy.Advance(_userHighAdmissionStreak, GetUserPriorityLocked(thread));
        else _userHighDispatchStreak = UserPriorityPolicy.Advance(_userHighDispatchStreak, GetUserPriorityLocked(thread));
    }
}
