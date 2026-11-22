using MeowChan;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruSearchTabsAndBulkTests : IDisposable
{
    private readonly BooruUiHarness _h = new();
    private readonly BooruSearchModel _model;
    private int _limit = 3;

    public BooruSearchTabsAndBulkTests() => _model = new BooruSearchModel(_h.Search, _h.Queue, _h.Previews, _h.Clock);

    public void Dispose() => _model.Dispose();

    [Fact]
    public void TheSettingIsDefaultEightWithinOneAndThirtyTwo()
    {
        Assert.Equal((8, 1, 32), (new Config().BooruSearchTabs, Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSearchTabs = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSearchTabs = 33 }.Validate());
    }

    [Fact]
    public async Task SwitchingTabsRestoresExistingPreviewsAndDoesNotRestartPendingPreviews()
    {
        BooruSearchTabs tabs = new(_model, () => _limit);
        _h.Serve(perPage: 2);
        TaskCompletionSource previewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Previews.OnPreviewAsync = async (post, token) =>
        {
            if (post.PostId == "101") return BooruUiFixtures.Png(1);
            previewStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        };

        await BooruTestSupport.SearchAsync(_model, "cat");
        await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await BooruTestSupport.WaitAsync(() => _model.View.Rows[0].Preview == BooruPreviewState.Ready, "the first preview");
        byte[]? ready = _model.View.Rows[0].PreviewBytes;
        int requests = _h.Search.Requests;
        int previews = _h.Previews.Requests;
        int second = tabs.Add(copyCurrent: true);
        _h.Queue.States["101"] = BooruLocalState.Loaded;

        Assert.True(tabs.Select(BooruSearchTabs.PrimaryId));
        Assert.True(tabs.Select(second));
        await BooruTestSupport.WaitAsync(() => _model.View.Rows[0].Load == BooruRowLoad.AlreadyLoaded, "the restored queue state");
        Assert.Equal("cat", _model.View.ExecutedQuery);
        Assert.Same(ready, _model.View.Rows[0].PreviewBytes);
        Assert.Equal(BooruPreviewState.Unavailable, _model.View.Rows[1].Preview);
        Assert.Equal(requests, _h.Search.Requests);
        Assert.Equal(previews, _h.Previews.Requests);
        Assert.Empty(_h.Previews.SampleRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchTokenSurvivesItsUsersAndIsDisposedAfterCancellation(bool disposeModel)
    {
        BooruSearchTabs tabs = new(_model);
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Search.OnSearch = async (query, page, token) =>
        {
            started.SetResult(token);
            await release.Task;
            return BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true));
        };
        _model.SetInput("cat");
        Task search = _model.SubmitAsync();
        CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WaitHandle handle = token.WaitHandle;
        try
        {
            if (disposeModel) _model.Dispose();
            else tabs.Add(copyCurrent: false);
            Assert.True(token.IsCancellationRequested);
            Assert.Same(handle, token.WaitHandle);
        }
        finally { release.TrySetResult(); }
        await search;
        await BooruTestSupport.WaitAsync(
            () => Record.Exception(() => { _ = token.WaitHandle; }) is ObjectDisposedException, "retired search token disposal");
    }

    [Fact]
    public async Task OpenInNewTabCopiesTheSearchAndTheLimitHolds()
    {
        BooruSearchTabs tabs = new(_model, () => _limit);
        await BooruTestSupport.SearchAsync(_model, "cat");
        int copy = tabs.Add(copyCurrent: true);
        Assert.Equal("cat", _model.View.ExecutedQuery);
        Assert.Equal(copy, tabs.ActiveId);
        tabs.Add(false); tabs.Add(false);
        Assert.Equal(3, tabs.ExtraCount);
        Assert.False(tabs.CanAdd);
        Assert.Equal(-1, tabs.Add(false));
        Assert.Equal(3, tabs.ExtraCount);
    }

    [Fact]
    public async Task ClosingTheActiveTabActivatesItsLeftNeighbourAndTheSearchTabStays()
    {
        BooruSearchTabs tabs = new(_model, () => _limit);
        await BooruTestSupport.SearchAsync(_model, "cat");
        int a = tabs.Add(false);
        await BooruTestSupport.SearchAsync(_model, "dog");
        int b = tabs.Add(false);
        Assert.False(tabs.Close(BooruSearchTabs.PrimaryId));
        Assert.True(tabs.Close(b));
        Assert.Equal(a, tabs.ActiveId);
        Assert.Equal("dog", _model.View.ExecutedQuery);
        Assert.True(tabs.Close(a));
        Assert.Equal(BooruSearchTabs.PrimaryId, tabs.ActiveId);
        Assert.Equal("cat", _model.View.ExecutedQuery);
    }

    [Fact]
    public void ALimitBelowTheMinimumStillAllowsOneTab()
    {
        BooruSearchTabs tabs = new(_model, () => -5);
        Assert.Equal(1, tabs.Limit);
        Assert.True(tabs.Add(false) > 0);
        Assert.False(tabs.CanAdd);
    }

    [Fact]
    public async Task DownloadAllReadsAtMostFivePagesQueuesEachOriginalOnceAndNeverClaimsCompletion()
    {
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, false,
            Enumerable.Range(0, 20).Select(i => BooruUiFixtures.Post((page * 100 + i).ToString())).ToArray())));
        await BooruTestSupport.SearchAsync(_model, "cat");
        int before = _h.Search.Searches.Count;
        Assert.True(_model.CanDownloadAll);
        await _model.DownloadAllAsync();
        BooruBulkView bulk = _model.Bulk;
        Assert.Equal(5, _h.Search.Searches.Count - before);
        Assert.Equal((BooruBulkPhase.Finished, BooruBulkEnd.PageLimit, 5, 100, 100), (bulk.Phase, bulk.End, bulk.PagesRead, bulk.Found, bulk.Queued));
        Assert.Equal(100, _h.Queue.Loads.Count);
        Assert.All(_h.Queue.LoadQueries, query => Assert.Equal("cat", query));
        Assert.All(_h.Queue.Loads, load => Assert.Equal(BooruLoadOrigin.Search, load.Origin));
    }

    [Fact]
    public async Task DownloadAllCountsWithoutOriginalAndStopsAtTheSourceEndOrAnError()
    {
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(page == 1
            ? BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, false, BooruUiFixtures.Post("1"), BooruUiFixtures.Post("2", BooruOriginalAccess.NotOffered), BooruUiFixtures.Post("1")))
            : BooruCallResult<BooruResultPage>.Of(BooruCallStatus.RateLimited));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.DownloadAllAsync();
        BooruBulkView bulk = _model.Bulk;
        Assert.Equal((BooruBulkEnd.Blocked, 1, 2, 1, 1), (bulk.End, bulk.PagesRead, bulk.Found, bulk.Queued, bulk.NoOriginal));
    }

    [Fact]
    public async Task DownloadAllIsRefusedForAPostLookupAndWithoutAShownSearch()
    {
        Assert.False(_model.CanDownloadAll);
        await BooruTestSupport.SearchAsync(_model, "12345", BooruInputMode.Post);
        Assert.False(_model.CanDownloadAll);
        await _model.DownloadAllAsync();
        Assert.Equal(BooruBulkPhase.Idle, _model.Bulk.Phase);
    }
}
