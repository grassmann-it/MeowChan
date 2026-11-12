using System;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

/// <summary>B5 pre-flight contract: pure due selection only; no transport, storage or timers.</summary>
public sealed class DanbooruWatcherScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public void NewActiveWatcherIsDueImmediatelyForItsBaselineCheck()
    {
        DanbooruWatcher w = Create("w1", T0);
        Assert.Equal(T0, DanbooruWatcherSchedule.NextCheckAtUtc(w, Interval, T0));
        Assert.Same(w, DanbooruWatcherSchedule.SelectNext(new[] { w }, T0, Interval));
    }

    [Fact]
    public void CheckedWatcherIsDueExactlyOneIntervalAfterItsLastCheck()
    {
        DanbooruWatcher w = Checked("w1", T0);
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { w }, T0 + Interval - TimeSpan.FromTicks(1), Interval));
        Assert.Same(w, DanbooruWatcherSchedule.SelectNext(new[] { w }, T0 + Interval, Interval));
    }

    [Fact]
    public void PausedErrorRemovedAndCheckingWatchersAreNeverSelectedAutomatically()
    {
        DanbooruWatcher active = Checked("w", T0);
        DanbooruWatcher paused = DanbooruWatcher.Pause(active).Watcher;
        DanbooruWatcher removed = DanbooruWatcher.Remove(active).Watcher;
        DanbooruWatcher error = DanbooruWatcher.ApplyError(
            DanbooruWatcher.BeginCheck(active, T0).Watcher, "provider_http_500", T0).Watcher;
        DanbooruWatcher checking = DanbooruWatcher.BeginCheck(active, T0).Watcher;

        foreach (DanbooruWatcher w in new[] { paused, removed, error, checking })
        {
            Assert.Null(DanbooruWatcherSchedule.NextCheckAtUtc(w, Interval, T0.AddDays(30)));
            Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { w }, T0.AddDays(30), Interval));
        }
    }

    [Fact]
    public void CooldownWatcherIsRetriedAtTheProviderTimeNotTheRegularInterval()
    {
        DanbooruWatcher w = DanbooruWatcher.ApplyDeferred(
            DanbooruWatcher.BeginCheck(Checked("w", T0), T0 + Interval).Watcher,
            T0 + Interval + TimeSpan.FromMinutes(2), "booru-cooldown", T0 + Interval).Watcher;
        Assert.Equal(DanbooruWatcherState.Cooldown, w.State);

        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { w }, T0 + Interval + TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1), Interval));
        Assert.Same(w, DanbooruWatcherSchedule.SelectNext(new[] { w }, T0 + Interval + TimeSpan.FromMinutes(2), Interval));
    }

    [Fact]
    public void ActiveProviderCooldownBlocksEveryWatcherUntilItEnds()
    {
        DanbooruWatcher a = Create("a", T0);
        DanbooruWatcher b = Checked("b", T0 - Interval);
        DateTimeOffset until = T0.AddSeconds(90);

        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { a, b }, T0.AddSeconds(89), Interval, until));
        Assert.NotNull(DanbooruWatcherSchedule.SelectNext(new[] { a, b }, until, Interval, until));
    }

    [Fact]
    public void OnlyOneCheckMayBeInFlightAcrossWatchers()
    {
        DanbooruWatcher a = Create("a", T0);
        DanbooruWatcher b = Create("b", T0);

        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { a, b }, T0, Interval, null, new[] { "a" }));

        DanbooruWatcher started = DanbooruWatcher.BeginCheck(a, T0).Watcher;
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { started, b }, T0, Interval));
    }

    [Fact]
    public void WatcherLeftInCheckingByACrashIsRecoveredBeforeItCanBlockOrRun()
    {
        DanbooruWatcher crashed = DanbooruWatcher.BeginCheck(Checked("a", T0), T0 + Interval).Watcher;
        DanbooruWatcher other = Create("b", T0);
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { crashed, other }, T0 + Interval, Interval));

        DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(crashed);
        Assert.Equal(DanbooruWatcherState.Active, recovered.State);
        Assert.NotNull(DanbooruWatcherSchedule.SelectNext(new[] { recovered, other }, T0 + Interval, Interval));
    }

    [Fact]
    public void SelectedWatcherCannotBeStartedTwiceBecauseTheDomainRejectsASecondCheck()
    {
        DanbooruWatcher w = Create("a", T0);
        DanbooruWatcher picked = DanbooruWatcherSchedule.SelectNext(new[] { w }, T0, Interval)!;
        DanbooruWatcherTransition first = DanbooruWatcher.BeginCheck(picked, T0);
        DanbooruWatcherTransition second = DanbooruWatcher.BeginCheck(first.Watcher, T0);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal("check_already_running", second.RejectionCode);
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { first.Watcher }, T0.AddDays(1), Interval));
    }

    [Fact]
    public void OldestDueWatcherRunsFirstAndTiesBreakDeterministically()
    {
        DanbooruWatcher late = Checked("late", T0);
        DanbooruWatcher early = Checked("early", T0 - TimeSpan.FromMinutes(10));
        DateTimeOffset now = T0 + Interval + TimeSpan.FromMinutes(5);
        Assert.Same(early, DanbooruWatcherSchedule.SelectNext(new[] { late, early }, now, Interval));

        DanbooruWatcher b = Create("b", T0);
        DanbooruWatcher a = Create("a", T0);
        Assert.Same(a, DanbooruWatcherSchedule.SelectNext(new[] { b, a }, T0, Interval));
        Assert.Same(a, DanbooruWatcherSchedule.SelectNext(new[] { a, b }, T0, Interval));
    }

    [Fact]
    public void BudgetLimitedWatcherContinuesWithoutWaitingButCannotStarveADueWatcher()
    {
        DanbooruWatcher partial = BudgetLimited("busy", T0 + TimeSpan.FromMinutes(5));
        Assert.True(partial.Progress.BudgetLimited);
        Assert.True(partial.Progress.NextPage > 0);

        // Resume at once although far less than one interval passed.
        DateTimeOffset now = T0 + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(2);
        Assert.Same(partial, DanbooruWatcherSchedule.SelectNext(new[] { partial }, now, Interval));

        // A watcher that has been due for longer goes first; the partial one queues behind it.
        DanbooruWatcher waiting = Checked("waiting", T0 - TimeSpan.FromMinutes(30));
        Assert.Same(waiting, DanbooruWatcherSchedule.SelectNext(new[] { partial, waiting }, now, Interval));
    }

    [Fact]
    public void FutureLastCheckTimeIsTreatedAsDueSoItCannotStallTheWatcher()
    {
        DanbooruWatcher w = Checked("w", T0 + TimeSpan.FromDays(365));
        Assert.Equal(T0, DanbooruWatcherSchedule.NextCheckAtUtc(w, Interval, T0));
        Assert.Same(w, DanbooruWatcherSchedule.SelectNext(new[] { w }, T0, Interval));
    }

    [Fact]
    public void IntervalBelowTheProviderStartSpacingIsRejected()
    {
        DanbooruWatcher w = Create("w", T0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DanbooruWatcherSchedule.NextCheckAtUtc(w, TimeSpan.FromMilliseconds(999), T0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DanbooruWatcherSchedule.SelectNext(new[] { w }, T0, TimeSpan.Zero));
        Assert.NotNull(DanbooruWatcherSchedule.NextCheckAtUtc(w, DanbooruWatcherSchedule.MinimumInterval, T0));
    }

    [Fact]
    public void DuplicateWatcherIdsAndEmptyInputNeverProduceTwoSelections()
    {
        DanbooruWatcher w = Create("same", T0);
        Assert.Null(DanbooruWatcherSchedule.SelectNext(Array.Empty<DanbooruWatcher>(), T0, Interval));
        Assert.Same(w, DanbooruWatcherSchedule.SelectNext(new[] { w, w }, T0, Interval));
    }

    [Fact]
    public void RemovedWatcherKeepsItsKnownPostsAndHighWaterMarkButIsNeverScheduled()
    {
        DanbooruWatcher w = Checked("w", T0);
        DanbooruWatcher removed = DanbooruWatcher.Remove(w).Watcher;

        Assert.Equal(DanbooruWatcherState.Removed, removed.State);
        Assert.Equal(w.HighWaterMark, removed.HighWaterMark);
        Assert.Equal(w.KnownPostIds, removed.KnownPostIds);
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { removed }, T0.AddDays(2), Interval));
        Assert.False(DanbooruWatcher.BeginCheck(removed, T0.AddDays(2), manual: true).Accepted);
    }

    // B5 gap G6 (fixed): a manual check of a Paused watcher that ends in Cooldown/Error used to lose the
    // pause, because BeginCheck derived the return state from State (Cooldown => Active) instead of the
    // last deliberately chosen ResumeState. Verified failing before the fix: the watcher ended Active.
    [Fact]
    public void ManualCheckOfAPausedWatcherThatEndsInCooldownStillReturnsToPaused()
    {
        DanbooruWatcher paused = DanbooruWatcher.Pause(Checked("w", T0)).Watcher;
        DanbooruWatcher running = DanbooruWatcher.BeginCheck(paused, T0.AddHours(1), manual: true).Watcher;
        DanbooruWatcher deferred = DanbooruWatcher.ApplyDeferred(running, T0.AddHours(2), "provider-throttled", T0.AddHours(1)).Watcher;
        DanbooruWatcher afterCooldown = DanbooruWatcher.BeginCheck(deferred, T0.AddHours(3), manual: true).Watcher;
        DanbooruWatcher cancelled = DanbooruWatcher.CancelCheck(afterCooldown).Watcher;
        Assert.Equal(DanbooruWatcherState.Paused, cancelled.State);
    }

    [Fact]
    public void PausedWatcherWhoseManualCheckWasDeferredIsNeverRetriedAutomatically()
    {
        DanbooruWatcher paused = DanbooruWatcher.Pause(Checked("w", T0)).Watcher;
        DanbooruWatcher running = DanbooruWatcher.BeginCheck(paused, T0.AddHours(1), manual: true).Watcher;
        DanbooruWatcher deferred = DanbooruWatcher.ApplyDeferred(running, T0.AddHours(2), "booru-cooldown", T0.AddHours(1)).Watcher;
        Assert.Equal(DanbooruWatcherState.Cooldown, deferred.State);
        Assert.Equal(DanbooruWatcherState.Paused, deferred.ResumeState);

        Assert.Null(DanbooruWatcherSchedule.NextCheckAtUtc(deferred, Interval, T0.AddDays(1)));
        Assert.Null(DanbooruWatcherSchedule.SelectNext(new[] { deferred }, T0.AddDays(1), Interval));
    }

    [Fact]
    public void BudgetLimitedProgressIsDueAtOnceWhateverItsPageOrdinal()
    {
        DanbooruWatcher partial = BudgetLimited("busy", T0 + TimeSpan.FromMinutes(5)); // budget 1 => next page 2
        DateTimeOffset now = T0 + TimeSpan.FromMinutes(6);
        Assert.Equal(2, partial.Progress.NextPage);
        Assert.NotNull(partial.CursorPostId);
        Assert.Same(partial, DanbooruWatcherSchedule.SelectNext(new[] { partial }, now, Interval));

        // Continuations go below the cursor, so a deep traversal is never parked behind a page limit.
        DanbooruWatcher deep = partial with { Progress = partial.Progress with { NextPage = 5_000 } };
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), DanbooruWatcherSchedule.NextCheckAtUtc(deep, Interval, now.AddDays(1)));
        Assert.Same(deep, DanbooruWatcherSchedule.SelectNext(new[] { deep }, now.AddDays(1), Interval));
    }

    private static DanbooruWatcher Create(string id, DateTimeOffset createdAt, int pageBudget = 5)
    {
        Assert.True(DanbooruWatcher.TryCreate(id, "cat", createdAt, out DanbooruWatcher? w, out _, pageBudget: pageBudget));
        return w!;
    }

    /// <summary>Baseline established, Active, last checked at <paramref name="checkedAt"/>.</summary>
    private static DanbooruWatcher Checked(string id, DateTimeOffset checkedAt)
    {
        DanbooruWatcher created = Create(id, checkedAt - TimeSpan.FromDays(1));
        DanbooruWatcher started = DanbooruWatcher.BeginCheck(created, checkedAt).Watcher;
        DanbooruWatcher done = DanbooruWatcher.ApplyPage(started, Page(1, true, "100", "99"), checkedAt).Watcher;
        Assert.Equal(DanbooruWatcherState.Active, done.State);
        return done;
    }

    private static DanbooruWatcher BudgetLimited(string id, DateTimeOffset checkedAt)
    {
        DanbooruWatcher baseline = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Create(id, T0 - TimeSpan.FromDays(1), pageBudget: 1), T0).Watcher,
            Page(1, true, "100"), T0).Watcher;
        DanbooruWatcher started = DanbooruWatcher.BeginCheck(baseline, checkedAt).Watcher;
        return DanbooruWatcher.ApplyPage(started, Page(1, false, "120", "119"), checkedAt).Watcher;
    }

    private static DanbooruSearchPageObservation Page(int page, bool isEnd, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, "cat", page, ids, isEnd);
}
