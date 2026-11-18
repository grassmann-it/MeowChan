using System;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

/// <summary>
/// Independent B2/B3 boundary review (2026-11-12). Synthetic pages only. Tests marked
/// <c>Skip</c> encode the CORRECT contract for a reproducible product gap documented in
/// review/booru-b2-boundary-2026-11-12.md; un-skip them together with the product fix.
/// </summary>
public sealed class DanbooruWatcherBoundaryReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    // MUST-1 (fixed): ApplyPage classifies "newer" against the stable CheckStartHighWaterMark,
    // not the live HighWaterMark that page 1 of the same traversal has already raised.
    [Fact]
    public void CatchUpAcrossSeveralPagesEmitsEveryPostNewerThanTheCheckStartBoundary()
    {
        DanbooruWatcher current = DanbooruWatcher.BeginCheck(Baseline("100"), Now.AddMinutes(1)).Watcher;
        string[] emitted = Array.Empty<string>();

        DanbooruWatcherTransition p1 = DanbooruWatcher.ApplyPage(current, Page(1, false, "120", "119"), Now);
        DanbooruWatcherTransition p2 = DanbooruWatcher.ApplyPage(p1.Watcher, Page(2, false, "118", "117"), Now);
        DanbooruWatcherTransition p3 = DanbooruWatcher.ApplyPage(p2.Watcher, Page(3, false, "101", "100"), Now);

        emitted = p1.AddedPostIds.Concat(p2.AddedPostIds).Concat(p3.AddedPostIds).ToArray();
        Assert.Equal(new[] { "120", "119", "118", "117", "101" }, emitted);
        Assert.Equal(DanbooruWatcherState.Active, p3.Watcher.State);
        Assert.Equal("120", p3.Watcher.HighWaterMark);
        Assert.Null(p3.Watcher.CheckStartHighWaterMark);
    }

    // MUST-1 regression: the stable fence also applies when an established watcher's check
    // started with an empty high-water mark, and a budget-limited run keeps its fence.
    [Fact]
    public void CatchUpFromEmptyBaselineEmitsEveryPostAcrossPagesAndKeepsFenceAcrossBudgetPause()
    {
        DanbooruWatcher empty = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Create(), Now).Watcher, Page(1, true), Now).Watcher;
        DanbooruWatcher w = DanbooruWatcher.BeginCheck(empty, Now.AddMinutes(1)).Watcher;

        DanbooruWatcherTransition p1 = DanbooruWatcher.ApplyPage(w, Page(1, false, "9", "8"), Now);
        DanbooruWatcherTransition p2 = DanbooruWatcher.ApplyPage(p1.Watcher, Page(2, true, "7", "6"), Now);

        Assert.Equal(new[] { "9", "8", "7", "6" }, p1.AddedPostIds.Concat(p2.AddedPostIds).ToArray());
        Assert.Equal(DanbooruWatcherState.Active, p2.Watcher.State);

        // Budget-limited run (budget 1): page 2 resumes against the original fence (100), so
        // the second page is still emitted and the boundary page completes the traversal.
        Assert.True(DanbooruWatcher.TryCreate("w-budget", "cat", Now, out DanbooruWatcher? created, out _, pageBudget: 1));
        DanbooruWatcher budgeted = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(created!, Now).Watcher, Page(1, true, "100"), Now).Watcher;
        DanbooruWatcher run1 = DanbooruWatcher.BeginCheck(budgeted, Now.AddMinutes(1)).Watcher;
        DanbooruWatcherTransition b1 = DanbooruWatcher.ApplyPage(run1, Page(1, false, "120", "119"), Now);
        Assert.Equal(new[] { "120", "119" }, b1.AddedPostIds);
        Assert.True(b1.Watcher.Progress.BudgetLimited);
        Assert.Equal("100", b1.Watcher.CheckStartHighWaterMark);
        DanbooruWatcher run2 = DanbooruWatcher.BeginCheck(b1.Watcher, Now.AddMinutes(2)).Watcher;
        DanbooruWatcherTransition b2 = DanbooruWatcher.ApplyPage(run2, Page(2, false, "101", "100"), Now);
        Assert.Equal(new[] { "101" }, b2.AddedPostIds);
        Assert.Null(b2.Watcher.CheckStartHighWaterMark);
        Assert.Equal(0, b2.Watcher.Progress.NextPage);
    }

    // MUST-2 (fixed): BooruWatcherRecord now carries the whole traversal contract
    // (CursorBeforePostId, CheckStartHighWaterMark + capture flag, ResumeState, PageSize, PageBudget,
    // PagesCheckedInRun, BudgetLimited), so a watcher rebuilt from exactly the persisted fields keeps
    // its fence and cursor instead of falsely completing a partial catch-up.
    [Fact]
    public void WatcherRebuiltFromPersistedFieldsOnlyDoesNotFalselyCompleteAPartialTraversal()
    {
        DanbooruWatcher live = DanbooruWatcher.BeginCheck(Baseline("100"), Now.AddMinutes(1)).Watcher;
        live = DanbooruWatcher.ApplyPage(live, Page(1, false, "120", "119"), Now).Watcher;
        Assert.Equal(DanbooruWatcherState.Checking, live.State);

        // Only the fields of BooruWatcherRecord survive the restart.
        DanbooruWatcher rebuilt = Create() with
        {
            State = DanbooruWatcherState.Active,
            BaselineEstablished = live.BaselineEstablished,
            HighWaterMark = live.HighWaterMark,
            KnownPostIds = live.KnownPostIds,
            Progress = new DanbooruWatcherProgress(live.Progress.NextPage, live.Progress.PagesCheckedInRun, false)
            {
                CursorBeforePostId = live.CursorPostId
            },
            CheckStartHighWaterMark = live.CheckStartHighWaterMark,
            ResumeState = live.ResumeState,
            PageSize = live.PageSize,
            PageBudget = live.PageBudget
        };
        // The rebuilt watcher keeps the fence that page 1 captured; without it the resumed
        // run would fall back to the live high-water mark and stop at the first page.
        Assert.Equal("100", rebuilt.CheckStartHighWaterMark);
        DanbooruWatcherTransition resumed = DanbooruWatcher.BeginCheck(rebuilt, Now.AddMinutes(2));
        DanbooruWatcherTransition p2 = DanbooruWatcher.ApplyPage(resumed.Watcher, Page(2, false, "118", "117"), Now);

        Assert.Equal(DanbooruWatcherState.Checking, p2.Watcher.State);
        Assert.Equal(new[] { "118", "117" }, p2.AddedPostIds);
    }

    // Green guard: cancel / error / restart during a partial traversal never reset the cursor,
    // never roll back the high-water mark and never claim completion.
    [Fact]
    public void CancelErrorAndRestartMidTraversalKeepCursorAndNeverClaimCompletion()
    {
        DanbooruWatcher w = DanbooruWatcher.BeginCheck(Baseline("100"), Now.AddMinutes(1)).Watcher;
        w = DanbooruWatcher.ApplyPage(w, Page(1, false, "120", "119"), Now).Watcher;

        DanbooruWatcher cancelled = DanbooruWatcher.CancelCheck(w).Watcher;
        Assert.Equal(DanbooruWatcherState.Active, cancelled.State);
        Assert.Equal("119", cancelled.CursorPostId);
        Assert.Equal("120", cancelled.HighWaterMark);

        DanbooruWatcher errored = DanbooruWatcher.ApplyError(w, "provider_error", Now).Watcher;
        DanbooruWatcher resumed = DanbooruWatcher.Resume(errored, Now).Watcher;
        DanbooruWatcherTransition retry = DanbooruWatcher.BeginCheck(resumed, Now);
        Assert.Equal(2, retry.RequestedPage);
        Assert.Equal("119", retry.RequestedCursorBeforePostId);

        DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(w);
        Assert.Equal(2, DanbooruWatcher.BeginCheck(recovered, Now).RequestedPage);
    }

    // Green guard: the first (baseline) check never emits and the empty baseline stays valid.
    [Fact]
    public void EmptyBaselineThenFirstPostIsNewButHistoricalPostsAreNot()
    {
        DanbooruWatcher empty = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Create(), Now).Watcher, Page(1, true), Now).Watcher;
        Assert.True(empty.BaselineEstablished);
        Assert.Null(empty.HighWaterMark);
        DanbooruWatcher second = DanbooruWatcher.BeginCheck(empty, Now.AddMinutes(1)).Watcher;
        DanbooruWatcherTransition result = DanbooruWatcher.ApplyPage(second, Page(1, true, "7"), Now);
        Assert.Equal(new[] { "7" }, result.AddedPostIds);
    }

    // Green guard: Removed is terminal for every mutation entry point.
    [Fact]
    public void RemovedWatcherRejectsEveryTransition()
    {
        DanbooruWatcher removed = DanbooruWatcher.Remove(Baseline("100")).Watcher;
        Assert.False(DanbooruWatcher.BeginCheck(removed, Now, manual: true).Accepted);
        Assert.False(DanbooruWatcher.Resume(removed, Now).Accepted);
        Assert.False(DanbooruWatcher.Pause(removed).Accepted);
        Assert.False(DanbooruWatcher.Remove(removed).Accepted);
        Assert.Equal(DanbooruWatcherState.Removed, DanbooruWatcher.RecoverAfterRestart(removed).State);
    }

    private static DanbooruWatcher Create()
    {
        Assert.True(DanbooruWatcher.TryCreate("w-review", "cat", Now, out DanbooruWatcher? w, out _));
        return w!;
    }

    private static DanbooruWatcher Baseline(params string[] ids) => DanbooruWatcher.ApplyPage(
        DanbooruWatcher.BeginCheck(Create(), Now).Watcher, Page(1, true, ids), Now).Watcher;

    private static DanbooruSearchPageObservation Page(int page, bool isEnd, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, "cat", page, ids, isEnd);
}
