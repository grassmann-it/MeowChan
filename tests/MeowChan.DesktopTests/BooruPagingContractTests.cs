using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruPagingContractTests
{
    [Fact]
    public async Task ForwardBackAndJumpsUseExecutedQueryAndNeverReplayOriginals()
    {
        BooruUiHarness h = new(); h.ServeFullPages(pages: 8);
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        Assert.Equal(20, m.Search.View.ResultCountLowerBound);
        await m.Search.LoadOriginalAsync("9220");
        m.Search.SetInput("dog");
        foreach (int page in new[] { 2, 1, 4, 5 })
        {
            await m.Search.GoToPageAsync(page);
            Assert.Equal(page * 20, m.Search.View.ResultCountLowerBound);
        }
        Assert.True(m.Search.View.PageLimitReached);
        Assert.False(m.Search.View.HasNextPage);
        foreach (int page in new[] { 0, 6, 5 }) await m.Search.GoToPageAsync(page);
        Assert.Equal(new[] { ("cat", 1), ("cat", 2), ("cat", 1), ("cat", 4), ("cat", 5) }, h.Search.Searches);
        Assert.Equal(new[] { ("9220", BooruLoadOrigin.Search) }, h.Queue.Loads);
        Assert.Empty(h.Queue.Retries);
        Assert.Empty(h.Queue.Cancellations);
        Assert.Equal(0, h.Samples.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task ProviderEndIsNotTheHostBoundOrAnAuthoritativeTotal(int lastSize)
    {
        BooruUiHarness h = new(); h.ServeFullPages(8);
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        h.Search.OnSearch = (q, p, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(q, p, true, BooruUiFixtures.FullPage(9200, lastSize))));
        await m.Search.GoToPageAsync(2);
        Assert.Equal(lastSize == 0 ? null : (int?)(20 + lastSize), m.Search.View.ResultCountLowerBound);
        Assert.False(m.Search.View.HasNextPage);
        Assert.False(m.Search.View.PageLimitReached);
        Assert.True(m.Search.View.HasPreviousPage);
        await m.Search.GoToPageAsync(3);
        Assert.Equal(2, h.Search.Requests);
        await m.Search.GoToPageAsync(1);
        Assert.Equal(("cat", 1), h.Search.Searches[^1]);
        Assert.Empty(h.Queue.Loads);
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Failed")]
    [InlineData("Denied")]
    [InlineData("NotFound")]
    [InlineData("Unavailable")]
    [InlineData("RateLimited")]
    [InlineData("Cooldown")]
    public async Task FailedJumpOffersBackAndExplicitRetryButHonorsProviderPause(string status)
    {
        BooruUiHarness h = new(); h.ServeFullPages(8);
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(
            Enum.Parse<BooruCallStatus>(status), "http-network", h.Clock.GetUtcNow().AddMinutes(2)));
        await m.Search.GoToPageAsync(4);
        Assert.False(m.Search.View.IsBusy);
        Assert.Null(m.Search.View.ResultCountLowerBound);
        Assert.True(m.Search.View.HasPreviousPage);
        Assert.False(m.Search.View.HasNextPage);
        Assert.False(m.Search.CanGoToPage(4));
        Assert.False(m.Search.CanGoToPage(5));
        Assert.Equal(!m.Search.IsPaused, m.Search.CanGoToPage(1));
        if (m.Search.IsPaused)
        {
            await m.Search.GoToPageAsync(1);
            Assert.Equal(2, h.Search.Requests);
            h.Clock.Advance(TimeSpan.FromMinutes(2)); m.Search.Tick();
        }
        m.Search.SetInput("dog");
        Assert.Equal(2, h.Search.Requests);
        Assert.True(m.Search.CanGoToPage(1));
        Assert.True(m.Search.CanRetry);
        h.ServeFullPages(8);
        await m.Search.RetryAsync();
        await m.Search.GoToPageAsync(1);
        Assert.Equal(new[] { ("cat", 1), ("cat", 4), ("cat", 4), ("cat", 1) }, h.Search.Searches);
        Assert.Empty(h.Queue.Loads);
        Assert.Empty(h.Queue.Retries);
    }

    [Fact]
    public async Task PageEligibilityMatchesRequestsAndNeverReplaysAfterInvalidSubmissionOrPostLookup()
    {
        BooruUiHarness h = new(); h.ServeFullPages(8);
        using BooruWorkbenchModel m = new(h.Services);
        Assert.False(m.Search.CanGoToPage(2));
        await m.Search.GoToPageAsync(2);
        Assert.Empty(h.Search.Searches);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        Assert.False(m.Search.CanGoToPage(0));
        Assert.False(m.Search.CanGoToPage(1));
        Assert.False(m.Search.CanGoToPage(6));
        Assert.True(m.Search.CanGoToPage(5));
        await m.Search.GoToPageAsync(5);
        Assert.True(m.Search.CanGoToPage(1));
        Assert.False(m.Search.CanGoToPage(5));
        m.Search.SetInput("a b c"); await m.Search.SubmitAsync();
        Assert.Null(m.Search.View.ResultCountLowerBound);
        Assert.False(m.Search.CanGoToPage(1));
        await m.Search.GoToPageAsync(1);
        Assert.Equal(2, h.Search.Requests);
        m.Search.SetMode(BooruInputMode.Post); m.Search.SetInput("123");
        await m.Search.SubmitAsync();
        Assert.Null(m.Search.View.ResultCountLowerBound);
        Assert.False(m.Search.CanGoToPage(2));
        await m.Search.GoToPageAsync(2);
        Assert.Equal(2, h.Search.Searches.Count);
        Assert.Equal(new[] { "123" }, h.Search.PostReads);
        Assert.Empty(h.Queue.Loads);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("page")]
    [InlineData("size")]
    public async Task MisboundPageFailsVisiblyWithoutCountOrOriginalAdmission(string mismatch)
    {
        BooruUiHarness h = new(); h.ServeFullPages(8);
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        h.Search.OnSearch = (q, p, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(mismatch == "query" ? "dog" : q, mismatch == "page" ? 3 : p, false,
                BooruUiFixtures.FullPage(9200, mismatch == "size" ? 21 : 20))));
        await m.Search.GoToPageAsync(4);
        Assert.Equal(BooruSearchStatus.Failed, m.Search.View.Status);
        Assert.Equal("page_binding", m.Search.View.Code);
        Assert.Empty(m.Search.View.Rows);
        Assert.Null(m.Search.View.ResultCountLowerBound);
        Assert.True(m.Search.CanRetry);
        Assert.True(m.Search.CanGoToPage(1));
        Assert.False(m.Search.CanGoToPage(5));
        Assert.Equal(2, h.Search.Requests);
        Assert.Empty(h.Queue.Loads);
    }

    [Fact]
    public void JumpControlUsesSharedEligibilityAndEditsNeverRequestAPage()
    {
        string source = SearchViewSource();
        Assert.Contains("_pageTarget.ValueChanged += (_, _) => OnModelChanged();", source);
        Assert.Contains("_jumpPage.IsEnabled = PageJumpTarget() is { } target && model.CanGoToPage(target);", source);
        Assert.Contains("if (PageJumpTarget() is { } page)", source);
        Assert.Contains("await _model.Search.GoToPageAsync(page);", source);
        Assert.Contains("page >= 1 && page <= BooruSearchModel.MaximumPage && page == decimal.Truncate(page)", source);
        Assert.Contains("AutomationProperties.SetName(_pageTarget, Texts.T(\"booru.ui.page.jump\"));", source);
        Assert.Contains("_resultCount.IsVisible = view.ResultCountLowerBound is not null;", source);
    }

    private static string SearchViewSource([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,
            "../../Desktop/Views/Booru/BooruWorkbenchView.Search.cs")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedJumpPublishesNoCountOrLateRowsAndRetryRepeatsOnlyThatPage(bool independent)
    {
        BooruUiHarness h = new(); h.ServeFullPages(8);
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        TaskCompletionSource<BooruCallResult<BooruResultPage>> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Search.OnSearch = (_, _, _) => independent ? throw new OperationCanceledException() : answer.Task;
        Task jump = m.Search.GoToPageAsync(4);
        if (!independent)
        {
            Assert.True(m.Search.View.IsBusy);
            Assert.Null(m.Search.View.ResultCountLowerBound);
            Assert.False(m.Search.CanGoToPage(1));
            Assert.False(m.Search.CanGoToPage(5));
            await m.Search.GoToPageAsync(3); // busy: no second request
            m.Search.Cancel();
            answer.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 4, false, BooruUiFixtures.Post("8"))));
        }
        await jump;
        Assert.Equal(BooruCodes.Canceled, m.Search.View.Code);
        Assert.Empty(m.Search.View.Rows);
        Assert.Null(m.Search.View.ResultCountLowerBound);
        Assert.True(m.Search.View.HasPreviousPage);
        Assert.True(m.Search.CanRetry);
        m.Search.SetInput("dog"); m.Search.Tick();
        Assert.Equal(2, h.Search.Requests);
        h.ServeFullPages(8); await m.Search.RetryAsync();
        Assert.Equal(new[] { ("cat", 1), ("cat", 4), ("cat", 4) }, h.Search.Searches);
        Assert.Equal(80, m.Search.View.ResultCountLowerBound);
        Assert.Empty(h.Queue.Loads);
    }
}
