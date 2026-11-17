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
