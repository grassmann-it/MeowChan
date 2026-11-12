using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

/// <summary>
/// Independent B2 invariant checks: post-id/URL parsing edges, multi-budget paging over more than
/// five pages, high-water/dedupe, restart continuation, empty/unordered pages and query purity.
/// Synthetic data only.
/// </summary>
public sealed class DanbooruWatcherInvariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("HTTPS://DANBOORU.DONMAI.US/posts/42", "42")]
    [InlineData("https://danbooru.donmai.us/posts/42.json", "42")]
    [InlineData("https://danbooru.donmai.us/posts/42.json/", "42")]
    [InlineData("\t\r\n 7 \n", "7")]
    // System.Uri resolves dot segments before we see them; the result is still a pinned canonical post URL.
    [InlineData("https://danbooru.donmai.us/posts/42/../43", "43")]
    public void ParseAcceptsOnlyCanonicalShapes(string input, string id)
    {
        Assert.True(DanbooruPostReference.TryParse(input, out DanbooruPostReference? r, out DanbooruPostReferenceError e));
        Assert.Equal(DanbooruPostReferenceError.None, e);
        Assert.Equal(id, r!.PostId);
    }

    [Theory]
    [InlineData(null, "Empty")]
    [InlineData("", "Empty")]
    [InlineData(" \t\r\n", "Empty")]
    [InlineData("12a", "InvalidUrl")]
    [InlineData("42.json", "InvalidUrl")]
    [InlineData(" 42", "InvalidUrl")]
    [InlineData("-5", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us/posts/42.JSON", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us/posts/42//", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us/posts/", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us/posts/.json", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us:8443/posts/42", "InvalidUrl")]
    [InlineData("https://danbooru.donmai.us.evil.example/posts/42", "InvalidUrl")]
    [InlineData("ftp://danbooru.donmai.us/posts/42", "InvalidUrl")]
    [InlineData("//danbooru.donmai.us/posts/42", "InvalidUrl")]
    public void ParseFailsClosedWithAStableErrorKind(string? input, string expected)
    {
        Assert.False(DanbooruPostReference.TryParse(input, out DanbooruPostReference? r, out DanbooruPostReferenceError e));
        Assert.Null(r);
        Assert.Equal(expected, e.ToString());
    }

    [Fact]
    public void PostIdComparisonIsNumericOverflowFreeAndRejectsInvalidIds()
    {
        Assert.True(DanbooruPostReference.ComparePostIds("9", "10") < 0);
        Assert.True(DanbooruPostReference.ComparePostIds("999999999999999999", "99999999999999999") > 0);
        Assert.Equal(0, DanbooruPostReference.ComparePostIds("123", "123"));
        Assert.Throws<ArgumentException>(() => DanbooruPostReference.ComparePostIds("0", "1"));
        Assert.Throws<ArgumentException>(() => DanbooruPostReference.ComparePostIds("1", "01"));
        Assert.Throws<ArgumentException>(() => DanbooruPostReference.ComparePostIds("1", ""));
        Assert.False(DanbooruPostReference.TryCreateFromId(null, out _, out DanbooruPostReferenceError e));
        Assert.Equal(DanbooruPostReferenceError.InvalidId, e);
    }

    [Theory]
    [InlineData("cat")]
    [InlineData("cat -bird")]
    [InlineData("rating:explicit cat")]
    [InlineData("rating:g cat")]
    public void QueryIsTheUsersOwnTextAndNeverGainsRatingOrOrderTokens(string query)
    {
        Assert.True(DanbooruWatcher.TryCreate("w", query, Now, out DanbooruWatcher? w, out _));
        string[] userTokens = query.Split(' ');
        string[] canonical = w!.CanonicalQuery.Split(' ');
        Assert.Equal(userTokens.OrderBy(t => t, StringComparer.Ordinal), canonical.OrderBy(t => t, StringComparer.Ordinal));
        Assert.DoesNotContain("g,s", w.CanonicalQuery, StringComparison.Ordinal);
        if (!query.Contains("rating:", StringComparison.Ordinal))
            Assert.DoesNotContain("rating", w.CanonicalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("order:", w.CanonicalQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQueryDoesNotBecomeAnImplicitSearch(string? query)
    {
        Assert.False(DanbooruWatcher.TryCreate("w", query, Now, out DanbooruWatcher? w, out DanbooruWatcherCreateError e));
        Assert.Null(w);
        Assert.Equal(DanbooruWatcherCreateError.InvalidQuery, e);
    }

    [Fact]
    public void TwelvePagesAcrossThreeBudgetRunsEmitEachPostOnceAndSurviveRestartsBetweenRuns()
    {
        // 24 new posts 124..101 above baseline 100, 2 per page => 12 pages, budget 5 => runs of 5+5+2.
        DanbooruWatcher w = Baseline(pageSize: 2, "100");
        List<string> emitted = new();
        int page = 1;
        int runs = 0;
        while (true)
        {
            DanbooruWatcherTransition begin = DanbooruWatcher.BeginCheck(w, Now.AddMinutes(++runs));
            Assert.True(begin.Accepted);
            Assert.Equal(page, begin.RequestedPage);
            Assert.Equal("100", begin.Watcher.CheckStartHighWaterMark);
            w = begin.Watcher;
            bool done = false;
            for (int i = 0; i < DanbooruWatcher.MaximumPageBudget && !done; i++, page++)
            {
                string[] ids = Enumerable.Range(0, 2).Select(k => (124 - (page - 1) * 2 - k).ToString()).ToArray();
                bool last = page == 12;
                // Last page is delivered as the provider's end page.
                DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(w,
                    new(DanbooruPostReference.ProviderId, "cat", page, ids, last), Now);
                Assert.True(t.Accepted);
                emitted.AddRange(t.AddedPostIds);
                w = t.Watcher;
                done = w.State != DanbooruWatcherState.Checking;
                if (!done) Assert.Equal(ids[^1], w.CursorPostId);
            }
            if (w.Progress.NextPage == 0) break;
            Assert.True(w.Progress.BudgetLimited);
            Assert.Equal(DanbooruWatcherState.Active, w.State);
            // Simulate a process restart between runs: state is Active already, recovery is a no-op.
            Assert.Same(w, DanbooruWatcher.RecoverAfterRestart(w));
        }

        Assert.Equal(3, runs);
        Assert.Equal(Enumerable.Range(101, 24).Reverse().Select(i => i.ToString()), emitted);
        Assert.Equal(emitted.Count, emitted.Distinct().Count());
        Assert.Equal("124", w.HighWaterMark);
        Assert.Equal(25, w.KnownPostIds.Count);
        Assert.Equal(w.KnownPostIds.OrderBy(i => int.Parse(i)), w.KnownPostIds);
        Assert.Null(w.CheckStartHighWaterMark);
        Assert.Null(w.CursorPostId);
    }

    [Fact]
    public void KilledMidRunRestartsAtTheCursorAndDoesNotReemitAcceptedPages()
    {
        DanbooruWatcher w = Baseline(pageSize: 2, "100");
        w = DanbooruWatcher.BeginCheck(w, Now).Watcher;
        DanbooruWatcherTransition p1 = DanbooruWatcher.ApplyPage(w, P(1, false, "110", "109"), Now);
        DanbooruWatcherTransition p2 = DanbooruWatcher.ApplyPage(p1.Watcher, P(2, false, "108", "107"), Now);
        Assert.Equal(DanbooruWatcherState.Checking, p2.Watcher.State);

        DanbooruWatcher restarted = DanbooruWatcher.RecoverAfterRestart(p2.Watcher);
        DanbooruWatcherTransition resumed = DanbooruWatcher.BeginCheck(restarted, Now.AddMinutes(1));
        Assert.Equal(3, resumed.RequestedPage);
        Assert.Equal("107", resumed.RequestedCursorBeforePostId);
        Assert.Equal("100", resumed.Watcher.CheckStartHighWaterMark);
        DanbooruWatcherTransition p3 = DanbooruWatcher.ApplyPage(resumed.Watcher, P(3, true, "106", "100"), Now);
        Assert.Equal(new[] { "106" }, p3.AddedPostIds);
        // Replaying an already accepted page after restart never re-emits: it is a fenced error.
        DanbooruWatcherTransition replay = DanbooruWatcher.ApplyPage(resumed.Watcher, P(3, true, "109", "108"), Now);
        Assert.False(replay.Accepted);
        Assert.Equal("cursor_did_not_advance", replay.RejectionCode);
    }

    [Fact]
    public void HighWaterMarkNeverRegressesAndStaleKnownPostsAreNotNew()
    {
        DanbooruWatcher w = Baseline(pageSize: 5, "100", "90");
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(w, Now).Watcher, P(1, true, "95", "90"), Now);
        Assert.Empty(t.AddedPostIds);
        Assert.Equal("100", t.Watcher.HighWaterMark);
        // An older, unknown id below the fence is recorded as known but never emitted (no backfill).
        Assert.Contains("95", t.Watcher.KnownPostIds);
    }

    [Fact]
    public void DuplicateIdsInsideOnePageAreEmittedOnce()
    {
        DanbooruWatcher w = Baseline(pageSize: 5, "100");
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(w, Now).Watcher, P(1, true, "102", "102", "101"), Now);
        Assert.True(t.Accepted);
        Assert.Equal(new[] { "102", "101" }, t.AddedPostIds);
    }

    [Theory]
    [InlineData("page_not_descending", new[] { "101", "102" })]
    [InlineData("invalid_post_id", new[] { "102", "0101" })]
    [InlineData("invalid_post_id", new[] { "102", "abc" })]
    [InlineData("invalid_post_id", new[] { "102", "1000000000000000000" })]
    [InlineData("page_limit_exceeded", new[] { "105", "104", "103", "102", "101", "100" })]
    public void UnorderedInvalidOrOversizedPagesFailClosedWithoutEmittingOrAdvancing(string code, string[] ids)
    {
        DanbooruWatcher w = DanbooruWatcher.BeginCheck(Baseline(pageSize: 5, "50"), Now).Watcher;
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(w, P(1, true, ids), Now);
        Assert.False(t.Accepted);
        Assert.Equal(code, t.RejectionCode);
        Assert.Equal(DanbooruWatcherState.Error, t.Watcher.State);
        Assert.Empty(t.AddedPostIds);
        Assert.Equal("50", t.Watcher.HighWaterMark);
        Assert.Equal(w.KnownPostIds, t.Watcher.KnownPostIds);
    }

    [Theory]
    [InlineData("other", "cat", 1)]
    [InlineData("danbooru", "dog", 1)]
    [InlineData("danbooru", "cat", 2)]
    [InlineData("danbooru", "cat", 0)]
    public void PageBindingMismatchFailsClosed(string source, string query, int page)
    {
        DanbooruWatcher w = DanbooruWatcher.BeginCheck(Baseline(pageSize: 5, "50"), Now).Watcher;
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(w,
            new(source, query, page, new[] { "60" }, true), Now);
        Assert.False(t.Accepted);
        Assert.Equal("page_binding_mismatch", t.RejectionCode);
        Assert.Empty(t.AddedPostIds);
    }

    [Fact]
    public void ApplyPageWithoutARunningCheckIsRejectedAndLeavesTheWatcherUntouched()
    {
        DanbooruWatcher w = Baseline(pageSize: 5, "50");
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(w, P(1, true, "60"), Now);
        Assert.False(t.Accepted);
        Assert.Equal("check_not_running", t.RejectionCode);
        Assert.Same(w, t.Watcher);
    }

    [Fact]
    public void EmptyEndPageCompletesAnEstablishedTraversalAndEmptyBaselineStaysEmpty()
    {
        DanbooruWatcher w = DanbooruWatcher.BeginCheck(Baseline(pageSize: 2, "100"), Now).Watcher;
        DanbooruWatcherTransition p1 = DanbooruWatcher.ApplyPage(w, P(1, false, "104", "103"), Now);
        DanbooruWatcherTransition end = DanbooruWatcher.ApplyPage(p1.Watcher, P(2, true), Now);
        Assert.True(end.Accepted);
        Assert.Empty(end.AddedPostIds);
        Assert.Equal(DanbooruWatcherState.Active, end.Watcher.State);
        Assert.Equal(0, end.Watcher.Progress.NextPage);
        Assert.Null(end.Watcher.CursorPostId);
        Assert.Equal("104", end.Watcher.HighWaterMark);

        DanbooruWatcher fresh = Make(5);
        DanbooruWatcherTransition baseline = DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(fresh, Now).Watcher, P(1, true), Now);
        Assert.True(baseline.Accepted);
        Assert.True(baseline.Watcher.BaselineEstablished);
        Assert.Null(baseline.Watcher.HighWaterMark);
    }

    [Fact]
    public void BaselineIsOnePageOnlyEvenWhenTheProviderReportsMore()
    {
        DanbooruWatcherTransition t = DanbooruWatcher.ApplyPage(
            DanbooruWatcher.BeginCheck(Make(5), Now).Watcher, P(1, false, "30", "20"), Now);
        Assert.True(t.Accepted);
        Assert.Empty(t.AddedPostIds);
        Assert.Equal(DanbooruWatcherState.Active, t.Watcher.State);
        Assert.Equal(0, t.Watcher.Progress.NextPage);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(21, 5)]
    [InlineData(20, 0)]
    [InlineData(20, 6)]
    public void PageSizeAndBudgetCannotExceedTheAcceptedBounds(int size, int budget)
    {
        Assert.False(DanbooruWatcher.TryCreate("w", "cat", Now, out _, out DanbooruWatcherCreateError e, size, budget));
        Assert.NotEqual(DanbooruWatcherCreateError.None, e);
    }

    private static DanbooruWatcher Make(int pageSize)
    {
        Assert.True(DanbooruWatcher.TryCreate("w", "cat", Now, out DanbooruWatcher? w, out _, pageSize));
        return w!;
    }

    private static DanbooruWatcher Baseline(int pageSize, params string[] ids) =>
        DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(Make(pageSize), Now).Watcher, P(1, true, ids), Now).Watcher;

    private static DanbooruSearchPageObservation P(int page, bool end, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, "cat", page, ids, end);
}
