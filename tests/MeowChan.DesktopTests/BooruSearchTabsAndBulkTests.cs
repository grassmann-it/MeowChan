using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan;
using MeowChan.Desktop;
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
    public void BulkStatusFollowsTheTabAndResultsThatStartedIt() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness harness = new();
        harness.Serve(pages: 1, perPage: 1);
        using BooruWorkbenchView view = new(harness.Services);
        BooruSearchModel search = view.Model.Search;
        BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(search, "cat"));
        BooruComponentDispatcher.Wait(search.DownloadAllAsync());
        Dispatcher.UIThread.RunJobs();

        int original = Assert.Single(view.SearchTabs.Ids);
        Assert.True(Status().IsVisible);
        Assert.True(view.SearchTabs.Add(copyCurrent: true) > 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Status().IsVisible);
        Assert.True(view.SearchTabs.Select(original));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Status().IsVisible);
        BooruComponentDispatcher.Wait(search.SubmitAsync());
        Dispatcher.UIThread.RunJobs();
        Assert.False(Status().IsVisible);

        Border Status()
        {
            TabControl strip = Assert.IsType<TabControl>(view.Body);
            TabItem selected = Assert.IsType<TabItem>(strip.SelectedItem);
            Control content = Assert.IsAssignableFrom<Control>(selected.Content);
            return Assert.Single(content.GetLogicalDescendants().OfType<Border>(),
                control => AutomationProperties.GetAutomationId(control) == BooruWorkbenchView.BulkStatusName);
        }
    });

    [Theory]
    [InlineData(1, 2)]
    [InlineData(8, 9)]
    [InlineData(32, 33)]
    public void OldCapacityIsConvertedOnceThroughProfileAndTransfer(int legacy, int total)
    {
        Assert.Equal((9, 2, 33), (new Config().BooruSearchTabs, Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum));
        Config old = JsonSerializer.Deserialize($$"""{"BooruSearchTabs":{{legacy}}}""", ConfigJsonContext.Default.Config)!;
        Assert.Equal(total, old.BooruSearchTabs);
        old.Validate();
        var transfer = SettingsTransfer.Export(old);
        Assert.Equal(legacy, transfer[nameof(Config.BooruSearchTabs)]!.GetValue<int>());
        Assert.Equal(total, transfer.Deserialize(ConfigJsonContext.Default.Config)!.BooruSearchTabs);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSearchTabs = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruSearchTabs = 34 }.Validate());
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

        Assert.True(tabs.Select(tabs.Ids[0]));
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
        tabs.Add(false);
        Assert.Equal(3, tabs.Ids.Count);
        Assert.False(tabs.CanAdd);
        Assert.Equal(-1, tabs.Add(false));
        Assert.Equal(3, tabs.Ids.Count);
    }

    [Fact]
    public async Task ClosingTheActiveTabActivatesItsLeftNeighbourAndTheLastTabReturnsToEmpty()
    {
        BooruSearchTabs tabs = new(_model, () => _limit);
        await BooruTestSupport.SearchAsync(_model, "cat");
        int a = tabs.Add(false);
        await BooruTestSupport.SearchAsync(_model, "dog");
        int b = tabs.Add(false);
        int first = tabs.Ids[0];
        Assert.True(tabs.Close(b));
        Assert.Equal(a, tabs.ActiveId);
        Assert.Equal("dog", _model.View.ExecutedQuery);
        Assert.True(tabs.Close(a));
        Assert.Equal(first, tabs.ActiveId);
        Assert.Equal("cat", _model.View.ExecutedQuery);
        Assert.True(tabs.Close(first));
        Assert.Empty(tabs.Ids);
        Assert.Null(tabs.ActiveId);
        Assert.Empty(_model.View.Input);
        Assert.Null(_model.View.ExecutedQuery);
    }

    [Fact]
    public void ALimitBelowTheMinimumStillAllowsTwoTabs()
    {
        BooruSearchTabs tabs = new(_model, () => -5);
        Assert.Equal(2, tabs.Limit);
        Assert.True(tabs.Add(false) > 0);
        Assert.True(tabs.Add(false) > 0);
        Assert.False(tabs.CanAdd);
    }

    [Fact]
    public async Task DownloadAllReadsAtMostFivePagesQueuesEachOriginalOnceAndNeverClaimsCompletion()
    {
        _ = new BooruSearchTabs(_model);
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
        _ = new BooruSearchTabs(_model);
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(page == 1
            ? BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, false, BooruUiFixtures.Post("1"), BooruUiFixtures.Post("2", BooruOriginalAccess.NotOffered), BooruUiFixtures.Post("1")))
            : BooruCallResult<BooruResultPage>.Of(BooruCallStatus.RateLimited));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.DownloadAllAsync();
        BooruBulkView bulk = _model.Bulk;
        Assert.Equal((BooruBulkEnd.Blocked, 1, 2, 1, 1), (bulk.End, bulk.PagesRead, bulk.Found, bulk.Queued, bulk.NoOriginal));
    }

    [Fact]
    public async Task ClosingTheBulkOwnerCancelsEvenIfTheProviderCompletesAfterCancellation()
    {
        BooruSearchTabs tabs = new(_model);
        _h.Serve(pages: 1, perPage: 1);
        await BooruTestSupport.SearchAsync(_model, "cat");
        int owner = tabs.Ids[0];
        TaskCompletionSource<BooruCallResult<BooruResultPage>> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        _h.Search.OnSearch = (_, _, cancellation) => { token = cancellation; return response.Task; };
        Task bulk = _model.DownloadAllAsync();
        int second = tabs.Add(copyCurrent: true);
        Assert.Equal(owner, _model.Bulk.TabId);
        _model.CancelBulk();
        Assert.False(token.IsCancellationRequested);
        Assert.True(tabs.Close(owner));
        Assert.Equal(second, tabs.ActiveId);
        Assert.True(token.IsCancellationRequested);
        response.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true, BooruUiFixtures.Post("101"))));
        await bulk.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BooruBulkEnd.Canceled, _model.Bulk.End);
        Assert.Empty(_h.Queue.Loads);
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
