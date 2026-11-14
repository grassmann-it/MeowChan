using System;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed class DanbooruWatcherDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalQueryIsDeterministicAndNeverInjectsRatingOrPrivateTags()
    {
        DanbooruWatcher watcher = Create(" 	cat\r\n-dog ");

        Assert.Equal("cat -dog", watcher.CanonicalQuery);
        Assert.DoesNotContain("rating:", watcher.CanonicalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("g,s", watcher.CanonicalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", watcher.CanonicalQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(watcher.CanonicalQuery, Create("cat -dog").CanonicalQuery);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("999999999999999999", true)]
    [InlineData("0", false)]
    [InlineData("01", false)]
    [InlineData("1000000000000000000", false)]
    [InlineData("+1", false)]
    [InlineData("1.0", false)]
    [InlineData("١", false)]
    public void PostIdRecognitionHonorsStrictAsciiBoundaries(string input, bool valid)
    {
        bool actual = DanbooruPostReference.TryParse(input, out DanbooruPostReference? reference, out _);
        Assert.Equal(valid, actual);
        if (valid)
        {
            Assert.NotNull(reference);
            Assert.Equal(input, reference!.PostId);
            Assert.Equal($"https://danbooru.donmai.us/posts/{input}", reference.CanonicalUrl.AbsoluteUri);
        }
        else Assert.Null(reference);
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts/123", "123")]
    [InlineData("https://danbooru.donmai.us/posts/123.json", "123")]
    [InlineData(" https://danbooru.donmai.us:443/posts/123/\t", "123")]
    public void DanbooruUrlRecognitionProducesOneCanonicalPostUrl(string input, string expectedId)
    {
        Assert.True(DanbooruPostReference.TryParse(input, out DanbooruPostReference? reference, out _));
        Assert.Equal(expectedId, reference!.PostId);
        Assert.Equal($"https://danbooru.donmai.us/posts/{expectedId}", reference.CanonicalUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://danbooru.donmai.us/posts/123")]
    [InlineData("https://testbooru.donmai.us/posts/123")]
    [InlineData("https://danbooru.donmai.us/post/123")]
    [InlineData("https://danbooru.donmai.us/posts/123/comments")]
    [InlineData("https://danbooru.donmai.us/posts/123?x=1")]
    [InlineData("https://danbooru.donmai.us/posts/123#fragment")]
    [InlineData("https://danbooru.donmai.us/posts/0")]
    [InlineData("https://danbooru.donmai.us/posts/01")]
    [InlineData("https://danbooru.donmai.us/posts/1234567890123456789")]
    [InlineData("https://user:pass@danbooru.donmai.us/posts/123")]
    public void UrlRecognitionFailsClosedAtHostPathAndIdBoundaries(string input)
    {
        Assert.False(DanbooruPostReference.TryParse(input, out DanbooruPostReference? reference, out _));
        Assert.Null(reference);
    }

    [Fact]
    public void FirstCheckEstablishesFromNowBaselineWithoutHistoricalBackfill()
    {
        DanbooruWatcher watcher = Create("cat");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition result = DanbooruWatcher.ApplyPage(started.Watcher,
            Page("cat", 1, false, "105", "104", "104"), Now.AddSeconds(1));

        Assert.True(result.Accepted);
        Assert.Equal(DanbooruWatcherState.Active, result.Watcher.State);
        Assert.True(result.Watcher.BaselineEstablished);
        Assert.Equal("105", result.Watcher.HighWaterMark);
        Assert.Empty(result.AddedPostIds);
        Assert.Equal(new[] { "104", "105" }, result.Watcher.KnownPostIds);
        Assert.Equal(0, result.Watcher.Progress.NextPage);
    }

    [Fact]
    public void NewPostsAreEmittedOnceAndKnownPostProgressIsNumeric()
    {
        DanbooruWatcher watcher = EstablishBaseline("100000000000000000", "99999999999999999");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now.AddMinutes(1));
        DanbooruWatcherTransition result = DanbooruWatcher.ApplyPage(started.Watcher,
            Page("cat", 1, true, "100000000000000002", "100000000000000001", "100000000000000000"), Now.AddMinutes(1));

        Assert.Equal(new[] { "100000000000000002", "100000000000000001" }, result.AddedPostIds);
        Assert.Equal("100000000000000002", result.Watcher.HighWaterMark);

        DanbooruWatcherTransition repeatStart = DanbooruWatcher.BeginCheck(result.Watcher, Now.AddMinutes(2));
        DanbooruWatcherTransition repeat = DanbooruWatcher.ApplyPage(repeatStart.Watcher,
            Page("cat", 1, true, "100000000000000002", "100000000000000001"), Now.AddMinutes(2));
        Assert.Empty(repeat.AddedPostIds);
        Assert.Equal(4, repeat.Watcher.KnownPostIds.Count);
    }

    [Fact]
    public void FivePageBudgetLeavesAResumableCursorInsteadOfClaimingCompletion()
    {
        DanbooruWatcher watcher = CreateWithLimits("cat", pageSize: 1, pageBudget: 5);
        DanbooruWatcherTransition baselineStart = DanbooruWatcher.BeginCheck(watcher, Now);
        watcher = DanbooruWatcher.ApplyPage(baselineStart.Watcher,
            Page("cat", 1, true, "100"), Now).Watcher;
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now.AddMinutes(1));
        Assert.Equal(1, started.RequestedPage);
        DanbooruWatcher current = started.Watcher;
        for (int page = 1; page <= 5; page++)
        {
            DanbooruWatcherTransition applied = DanbooruWatcher.ApplyPage(current,
                Page("cat", page, false, (100 + 6 - page).ToString()), Now.AddMinutes(page));
            current = applied.Watcher;
        }

        Assert.Equal(DanbooruWatcherState.Active, current.State);
        Assert.True(current.Progress.BudgetLimited);
        Assert.Equal(6, current.Progress.NextPage);
        DanbooruWatcherTransition restart = DanbooruWatcher.BeginCheck(current, Now.AddMinutes(6));
        Assert.Equal(6, restart.RequestedPage);
    }

    [Fact]
    public void PauseResumeAndCancelPreserveTheNextPageWithoutARequest()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition paused = DanbooruWatcher.Pause(started.Watcher);
        Assert.Equal(DanbooruWatcherState.Paused, paused.Watcher.State);
        Assert.Equal(1, paused.Watcher.Progress.NextPage);
        Assert.False(DanbooruWatcher.BeginCheck(paused.Watcher, Now).Accepted);

        DanbooruWatcherTransition manual = DanbooruWatcher.BeginCheck(paused.Watcher, Now, manual: true);
        Assert.Equal(DanbooruWatcherState.Checking, manual.Watcher.State);
        DanbooruWatcherTransition cancelled = DanbooruWatcher.CancelCheck(manual.Watcher);
        Assert.Equal(DanbooruWatcherState.Paused, cancelled.Watcher.State);
        Assert.Equal(1, cancelled.Watcher.Progress.NextPage);
        Assert.True(DanbooruWatcher.Resume(cancelled.Watcher, Now).Accepted);
    }

    [Fact]
    public void CooldownAndErrorKeepWatcherAndBlockOnlyTheRelevantCheck()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition deferred = DanbooruWatcher.ApplyDeferred(started.Watcher,
            Now.AddMinutes(1), "provider_cooldown", Now);
        Assert.Equal(DanbooruWatcherState.Cooldown, deferred.Watcher.State);
        Assert.False(DanbooruWatcher.BeginCheck(deferred.Watcher, Now.AddSeconds(30)).Accepted);
        Assert.True(DanbooruWatcher.BeginCheck(deferred.Watcher, Now.AddMinutes(1)).Accepted);

        DanbooruWatcherTransition error = DanbooruWatcher.ApplyError(
            DanbooruWatcher.BeginCheck(deferred.Watcher, Now.AddMinutes(1)).Watcher,
            "provider_error", Now.AddMinutes(1));
        Assert.Equal(DanbooruWatcherState.Error, error.Watcher.State);
        Assert.Equal("provider_error", error.Watcher.ErrorCode);
        Assert.Equal(DanbooruWatcherState.Active,
            DanbooruWatcher.Resume(error.Watcher, Now.AddMinutes(1)).Watcher.State);
    }

    [Fact]
    public void ResetProviderCooldownPreservesProgressAndPersonalPause()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition deferred = DanbooruWatcher.ApplyDeferred(started.Watcher,
            Now.AddDays(2), "provider_cooldown", Now);
        DanbooruWatcherTransition reset = DanbooruWatcher.ResetProviderCooldown(deferred.Watcher);

        Assert.True(reset.Accepted);
        Assert.Equal(DanbooruWatcherState.Active, reset.Watcher.State);
        Assert.Null(reset.Watcher.CooldownUntilUtc);
        Assert.Null(reset.Watcher.ErrorCode);
        Assert.Equal(deferred.Watcher.HighWaterMark, reset.Watcher.HighWaterMark);
        Assert.Equal(deferred.Watcher.KnownPostIds, reset.Watcher.KnownPostIds);
        Assert.Equal(deferred.Watcher.Progress, reset.Watcher.Progress);

        DanbooruWatcherTransition paused = DanbooruWatcher.Pause(deferred.Watcher);
        DanbooruWatcherTransition pausedReset = DanbooruWatcher.ResetProviderCooldown(paused.Watcher);
        Assert.True(pausedReset.Accepted);
        Assert.Equal(DanbooruWatcherState.Paused, pausedReset.Watcher.State);
        Assert.Equal(DanbooruWatcherState.Paused, pausedReset.Watcher.ResumeState);
        Assert.Null(pausedReset.Watcher.CooldownUntilUtc);
    }

    [Fact]
    public void RestartRecoveryResumesAnInterruptedPartialPageAndDoesNotDuplicatePosts()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition firstPage = DanbooruWatcher.ApplyPage(started.Watcher,
            Page("cat", 1, false, "102", "101"), Now);
        // Page 1 is accepted and the serialized state is still Checking for a bounded continuation.
        Assert.Equal(DanbooruWatcherState.Checking, firstPage.Watcher.State);
        Assert.Equal(2, firstPage.Watcher.Progress.NextPage);
        Assert.Equal("101", firstPage.Watcher.CursorPostId);
        Assert.Equal(2, firstPage.RequestedPage);

        DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(firstPage.Watcher);
        Assert.Equal(DanbooruWatcherState.Active, recovered.State);
        DanbooruWatcherTransition resumed = DanbooruWatcher.BeginCheck(recovered, Now.AddMinutes(1));
        Assert.Equal(2, resumed.RequestedPage);
        Assert.Equal("101", resumed.Watcher.CursorPostId);
        DanbooruWatcherTransition secondPage = DanbooruWatcher.ApplyPage(resumed.Watcher,
            Page("cat", 2, true, "100"), Now.AddMinutes(1));
        Assert.Empty(secondPage.AddedPostIds);
        Assert.Equal(new[] { "100", "101", "102" }, secondPage.Watcher.KnownPostIds.OrderBy(id => int.Parse(id)));
    }

    [Fact]
    public void NonTerminalEmptyPageFailsClosedInsteadOfRepeatingTheSameCursor()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition result = DanbooruWatcher.ApplyPage(started.Watcher,
            Page("cat", 1, false), Now);

        Assert.False(result.Accepted);
        Assert.Equal(DanbooruWatcherState.Error, result.Watcher.State);
        Assert.Equal("empty_page_did_not_advance", result.RejectionCode);
    }

    [Fact]
    public void CursorRejectsARepeatedOrNonAdvancingPage()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        DanbooruWatcherTransition first = DanbooruWatcher.ApplyPage(started.Watcher,
            Page("cat", 1, false, "102", "101"), Now);
        DanbooruWatcherTransition repeated = DanbooruWatcher.ApplyPage(first.Watcher,
            Page("cat", 2, false, "102", "101"), Now.AddSeconds(1));

        Assert.False(repeated.Accepted);
        Assert.Equal(DanbooruWatcherState.Error, repeated.Watcher.State);
        Assert.Equal("cursor_did_not_advance", repeated.RejectionCode);
    }

    [Fact]
    public void RemoveIsTerminalAndDoesNotEraseKnownLocalIdentity()
    {
        DanbooruWatcher watcher = EstablishBaseline("100");
        DanbooruWatcherTransition removed = DanbooruWatcher.Remove(watcher);
        Assert.Equal(DanbooruWatcherState.Removed, removed.Watcher.State);
        Assert.False(DanbooruWatcher.BeginCheck(removed.Watcher, Now).Accepted);
        Assert.Equal(watcher.KnownPostIds, removed.Watcher.KnownPostIds);
    }

    private static DanbooruWatcher Create(string query) => CreateWithLimits(query);

    private static DanbooruWatcher CreateWithLimits(string query, int pageSize = DanbooruWatcher.DefaultPageSize,
        int pageBudget = DanbooruWatcher.DefaultPageBudget)
    {
        Assert.True(DanbooruWatcher.TryCreate("watcher-1", query, Now, out DanbooruWatcher? watcher, out _,
            pageSize, pageBudget));
        return watcher!;
    }

    private static DanbooruWatcher EstablishBaseline(params string[] ids)
    {
        DanbooruWatcher watcher = Create("cat");
        DanbooruWatcherTransition started = DanbooruWatcher.BeginCheck(watcher, Now);
        return DanbooruWatcher.ApplyPage(started.Watcher, Page("cat", 1, true, ids), Now).Watcher;
    }

    private static DanbooruSearchPageObservation Page(string query, int page, bool isEnd, params string[] ids) =>
        new(DanbooruPostReference.ProviderId, query, page, ids, isEnd);
}
