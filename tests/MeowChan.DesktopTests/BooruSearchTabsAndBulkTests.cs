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
    public async Task SwitchingTabsRestoresEachSearchWithoutAnyRequest()
    {
        BooruSearchTabs tabs = new(_model, () => _limit);
        await BooruTestSupport.SearchAsync(_model, "cat");
        int requests = _h.ProviderRequests;
        int second = tabs.Add(copyCurrent: false);
        Assert.True(second > 0);
        Assert.Equal(string.Empty, _model.View.Input);
        await BooruTestSupport.SearchAsync(_model, "dog");
        requests = _h.ProviderRequests;
        Assert.True(tabs.Select(BooruSearchTabs.PrimaryId));
        Assert.Equal("cat", _model.View.ExecutedQuery);
        Assert.True(tabs.Select(second));
        Assert.Equal("dog", _model.View.ExecutedQuery);
        Assert.Equal(requests, _h.ProviderRequests);
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
