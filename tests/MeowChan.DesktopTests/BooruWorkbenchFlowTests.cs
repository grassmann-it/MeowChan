using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>The three user flows end to end over the fakes: search, one-shot and watcher.</summary>
public sealed class BooruWorkbenchFlowTests : IDisposable
{
    private readonly BooruUiHarness _h = new();
    private readonly BooruWorkbenchModel _model;

    public BooruWorkbenchFlowTests() => _model = new BooruWorkbenchModel(_h.Services);

    public void Dispose() => _model.Dispose();

    [Fact]
    public async Task OpeningTheWorkbenchReadsTheWatcherListOnly()
    {
        await _model.InitializeAsync();
        Assert.Equal(new[] { "list" }, _h.Watchers.Calls);
        Assert.Equal(0, _h.Search.Requests);
        Assert.Empty(_h.Queue.Loads);
        Assert.Equal(0, _h.Previews.Requests);
    }

    [Fact]
    public async Task SearchThenSelectThenLoadOriginal()
    {
        _h.Serve(pages: 2, perPage: 3);
        await _model.InitializeAsync();

        await BooruTestSupport.SearchAsync(_model.Search, "cat blue_eyes");
        await BooruTestSupport.SettleAsync(_model.Search);
        BooruSearchView view = _model.Search.View;
        Assert.Equal(BooruSearchStatus.Results, view.Status);
        Assert.Equal("cat blue_eyes", view.ExecutedQuery);
        Assert.Equal(3, view.Rows.Length);
        Assert.Equal("Danbooru", view.ProviderName);
        Assert.Empty(_h.Queue.Loads);

        await _model.Search.GoToPageAsync(2);
        Assert.Equal(2, _model.Search.View.Page);
        Assert.True(_model.Search.View.HasPreviousPage);

        string selected = _model.Search.View.Rows[1].Post.PostId;
        await _model.Search.LoadOriginalAsync(selected);
        Assert.Equal((selected, BooruLoadOrigin.Search), Assert.Single(_h.Queue.Loads));
        Assert.Equal(BooruRowLoad.Queued, _model.Search.View.Rows[1].Load);
        Assert.Equal(BooruRowLoad.Idle, _model.Search.View.Rows[0].Load);
    }

    [Fact]
    public async Task PostUrlThenOneShotLoad()
    {
        await _model.InitializeAsync();
        await BooruTestSupport.SearchAsync(_model.Search, "https://danbooru.donmai.us/posts/4242", BooruInputMode.Post);
        Assert.Equal("4242", Assert.Single(_h.Search.PostReads));
        Assert.True(_model.Search.View.IsPostResult);
        Assert.False(_model.Search.CanCreateWatcher); // a single post can never become a watcher
        await _model.Search.LoadOriginalAsync("4242");
        Assert.Equal(("4242", BooruLoadOrigin.OneShot), Assert.Single(_h.Queue.Loads));
        Assert.Empty(_h.Watchers.Calls.Where(call => call.StartsWith("create:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task QueryThenWatcherThenCheckNowThenPauseResumeRemove()
    {
        _h.Serve();
        await _model.InitializeAsync();
        await BooruTestSupport.SearchAsync(_model.Search, "cat");
        Assert.True(_model.Search.CanCreateWatcher);

        // Create: stored from now, no check, no download of existing posts.
        Assert.True(await _model.CreateWatcherAsync());
        Assert.Contains("create:cat", _h.Watchers.Calls);
        Assert.Equal(0, _h.Watchers.Checks);
        Assert.Empty(_h.Queue.Loads);
        Assert.Equal("booru.ui.watcher.created", _model.Search.View.Notice!.Key);
        BooruWatcherRowView row = Assert.Single(_model.Watchers.View.Rows);
        Assert.Equal("cat", row.Watcher.Query);
        Assert.False(row.Watcher.BaselineEstablished);
        Assert.Null(row.Watcher.LastCheckedAt);
        string id = row.WatcherId;

        // The same search is not watched twice.
        Assert.False(await _model.CreateWatcherAsync());
        Assert.Equal("booru.ui.watcher.duplicate", _model.Search.View.Notice!.Key);
        Assert.Single(_h.Watchers.Calls, call => call.StartsWith("create:", StringComparison.Ordinal));

        await _model.Watchers.CheckNowAsync(id);
        Assert.Equal(1, _h.Watchers.Checks);
        Assert.True(_model.Watchers.View.Rows[0].Watcher.BaselineEstablished);
        Assert.NotNull(_model.Watchers.View.Rows[0].Watcher.LastCheckedAt);

        await _model.Watchers.PauseAsync(id);
        Assert.Null(await _model.Watchers.CheckNowAsync(id));
        Assert.Equal(1, _h.Watchers.Checks);

        await _model.Watchers.ResumeAsync(id);
        await _model.Watchers.CheckNowAsync(id);
        Assert.Equal(2, _h.Watchers.Checks);

        _model.Watchers.RequestRemoval(id);
        await _model.Watchers.ConfirmRemovalAsync(id);
        Assert.Empty(_model.Watchers.View.Rows);
        Assert.Equal(BooruWatcherListStatus.Empty, _model.Watchers.View.Status);
        Assert.Null(await _model.Watchers.CheckNowAsync(id));
        Assert.Equal(2, _h.Watchers.Checks);
        // Removal never touched the download side.
        Assert.Empty(_h.Queue.Loads);
    }

    [Fact]
    public async Task CreatingAWatcherNeverAddsARatingTerm()
    {
        await BooruTestSupport.SearchAsync(_model.Search, "cat blue_eyes");
        await _model.CreateWatcherAsync();
        string call = Assert.Single(_h.Watchers.Calls, c => c.StartsWith("create:", StringComparison.Ordinal));
        Assert.Equal("create:cat blue_eyes", call);
        Assert.DoesNotContain("rating", call);
    }

    [Fact]
    public async Task AnInvalidOrPostInputCannotCreateAWatcher()
    {
        _model.Search.SetInput("a b c");
        Assert.False(await _model.CreateWatcherAsync());
        Assert.Equal("booru.ui.watcher.createNeedsTags", _model.Search.View.Notice!.Key);
        _model.Search.SetMode(BooruInputMode.Post);
        _model.Search.SetInput("123");
        Assert.False(await _model.CreateWatcherAsync());
        Assert.DoesNotContain(_h.Watchers.Calls, call => call.StartsWith("create:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedCreationIsReportedAndLeavesNoWatcher()
    {
        _h.Watchers.OnCreate = _ => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "store_failed");
        await BooruTestSupport.SearchAsync(_model.Search, "cat");
        Assert.False(await _model.CreateWatcherAsync());
        Assert.Equal("booru.ui.watcher.createFailed", _model.Search.View.Notice!.Key);
        Assert.Empty(_model.Watchers.View.Rows);
    }

    [Fact]
    public async Task NoPortEverSeesATimerDrivenRequest()
    {
        _h.Serve();
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.InitializeAsync();
        await BooruTestSupport.SearchAsync(_model.Search, "dog");
        int requests = _h.ProviderRequests;
        for (int index = 0; index < 20; index++)
        {
            _h.Clock.Advance(TimeSpan.FromHours(1));
            _model.Search.Tick();
            _model.Watchers.Tick();
        }
        await Task.Delay(50);
        Assert.Equal(requests, _h.ProviderRequests);
    }
}
