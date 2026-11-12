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

    // MUST-1: ApplyPage classifies "newer" against the *live* HighWaterMark, which page 1 of
    // the same traversal has already raised. Posts on pages 2..5 of a catch-up are therefore
    // recorded as known but never reported as new, i.e. silently skipped by the download queue.
    [Fact(Skip = "MUST-1 product gap: pages 2..N of a catch-up never emit new posts (compares to live HighWaterMark instead of CheckStartHighWaterMark)")]
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
    }

    // MUST-2: BooruWatcherRecord persists NextPage/HighWaterMark/KnownPostIds but not the cursor
    // fence, CheckStartHighWaterMark or ResumeState. A watcher rebuilt from exactly the persisted
    // fields must never *complete* a traversal it has not finished.
    [Fact(Skip = "MUST-2 contract gap: persisted record lacks CursorBeforePostId/CheckStartHighWaterMark/ResumeState; rebuilt watcher falsely completes")]
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
            Progress = new DanbooruWatcherProgress(live.Progress.NextPage, 0, false)
        };
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
