using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Booru;

/// <summary>
/// Pure, side-effect-free due-selection rules for the B5 watcher scheduler. It reads only the public
/// state of <see cref="DanbooruWatcher"/>; it never starts a check, sends a request, persists or sleeps.
/// The executor stays responsible for <see cref="DanbooruWatcher.BeginCheck"/>, the host transport
/// (which owns the 1 s start spacing and the single active metadata send) and persistence.
/// </summary>
internal static class DanbooruWatcherSchedule
{
    /// <summary>Floor for the scheduler interval; the default is a product decision outside this type.</summary>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The earliest time an automatic check may start, or <c>null</c> when the watcher is never
    /// started automatically (Paused, Error, Removed or already Checking). Error and Paused need an
    /// explicit user action; a manual check bypasses this schedule but not the provider cooldown.
    /// </summary>
    internal static DateTimeOffset? NextCheckAtUtc(DanbooruWatcher watcher, TimeSpan interval, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ValidateInterval(interval);
        DateTimeOffset now = nowUtc.ToUniversalTime();
        // A paused watcher whose manual check was deferred keeps Paused as its resume target; the
        // provider retry time must not turn that into an automatic request.
        if (watcher.State == DanbooruWatcherState.Cooldown && watcher.ResumeState == DanbooruWatcherState.Paused)
            return null;
        // A last-check time in the future (clock moved back) is not trustworthy: treat the watcher as due
        // now, so the next real check rewrites LastCheckedAtUtc instead of stalling it for a long time.
        DateTimeOffset? last = watcher.LastCheckedAtUtc;
        if (last > now && watcher.State is DanbooruWatcherState.Active) return now;
        switch (watcher.State)
        {
            case DanbooruWatcherState.Active:
                // Budget-limited partial progress continues below its cursor without waiting for a full
                // interval; a cursor has no page limit, so a backlog of any size is caught up check by check.
                // Fairness comes from ordering by due time: a just-checked watcher sorts last.
                if (watcher.Progress.NextPage > 0) return last ?? watcher.CreatedAtUtc;
                return last is { } checkedAt ? checkedAt + interval : watcher.CreatedAtUtc;
            case DanbooruWatcherState.Cooldown:
                // The provider-authorised retry time wins over the regular interval.
                return watcher.CooldownUntilUtc ?? (last ?? watcher.CreatedAtUtc) + interval;
            default:
                return null;
        }
    }

    /// <summary>
    /// Chooses at most one watcher to start now. Returns <c>null</c> while the provider cooldown is
    /// active, while any check is in flight (explicit set or a watcher still in Checking, e.g. before
    /// <see cref="DanbooruWatcher.RecoverAfterRestart"/>), or when nothing is due. One check at a time
    /// is the conservative scheduler form of "at most one active metadata send".
    /// </summary>
    internal static DanbooruWatcher? SelectNext(IEnumerable<DanbooruWatcher> watchers, DateTimeOffset nowUtc,
        TimeSpan interval, DateTimeOffset? providerCooldownUntilUtc = null,
        IReadOnlyCollection<string>? inFlightWatcherIds = null)
    {
        ArgumentNullException.ThrowIfNull(watchers);
        ValidateInterval(interval);
        DateTimeOffset now = nowUtc.ToUniversalTime();
        if (providerCooldownUntilUtc is { } cooldown && now < cooldown) return null;
        if (inFlightWatcherIds is { Count: > 0 }) return null;

        List<DanbooruWatcher> distinct = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (DanbooruWatcher watcher in watchers)
            if (seen.Add(watcher.WatcherId)) distinct.Add(watcher);
        if (distinct.Any(watcher => watcher.State == DanbooruWatcherState.Checking)) return null;

        return distinct
            .Select(watcher => (Watcher: watcher, Due: NextCheckAtUtc(watcher, interval, now)))
            .Where(item => item.Due is { } due && due <= now)
            .OrderBy(item => item.Due!.Value)
            .ThenBy(item => item.Watcher.CreatedAtUtc)
            .ThenBy(item => item.Watcher.WatcherId, StringComparer.Ordinal)
            .Select(item => item.Watcher)
            .FirstOrDefault();
    }

    private static void ValidateInterval(TimeSpan interval)
    {
        if (interval < MinimumInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), "The watcher interval is below the provider start spacing.");
    }
}
