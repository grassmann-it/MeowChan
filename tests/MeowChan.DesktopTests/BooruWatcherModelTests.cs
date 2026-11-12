using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruWatcherModelTests : IDisposable
{
    private readonly BooruUiHarness _h = new();
    private readonly BooruWatcherListModel _model;

    public BooruWatcherModelTests() => _model = new BooruWatcherListModel(_h.Watchers, _h.Clock);

    public void Dispose() => _model.Dispose();

    private BooruWatcherRowView Row(string id) => _model.View.Rows.Single(row => row.WatcherId == id);

    [Fact]
    public async Task LoadingTheListReadsStoredWatchersAndChecksNothing()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock, hits: 4, queued: 3));
        Assert.Equal(BooruWatcherListStatus.Loading, _model.View.Status);
        await _model.RefreshAsync();
        Assert.Equal(BooruWatcherListStatus.Ready, _model.View.Status);
        Assert.Equal(new[] { "list" }, _h.Watchers.Calls);
        Assert.Equal(0, _h.Watchers.Checks);
        BooruWatcherView watcher = Row("w1").Watcher;
        Assert.Equal("cat", watcher.Query);
        Assert.NotNull(watcher.LastCheckedAt);
        Assert.NotNull(watcher.NextCheckAt);
        Assert.Equal((4, 3), (watcher.HitCount, watcher.QueuedCount));
    }

    [Fact]
    public async Task EmptyAndFailedListsAreStates()
    {
        await _model.RefreshAsync();
        Assert.Equal(BooruWatcherListStatus.Empty, _model.View.Status);
        _h.Watchers.OnList = () => throw new IOException("disk detail");
        await _model.RefreshAsync();
        Assert.Equal(BooruWatcherListStatus.Failed, _model.View.Status);
        Assert.Equal("exception", _model.View.Code);
    }

    [Fact]
    public async Task CheckNowIsOneExplicitCheckWithItsResultShown()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.RefreshAsync();
        await _model.CheckNowAsync("w1");
        Assert.Equal(1, _h.Watchers.Checks);
        Assert.Equal("booru.ui.watcher.msg.checked", Row("w1").Message!.Key);
        Assert.Equal(new object?[] { 2, 2 }, Row("w1").Message!.Args);
        Assert.Equal(2, Row("w1").Watcher.HitCount);
        Assert.False(Row("w1").Busy);
    }

    [Fact]
    public async Task APausedWatcherSendsNothingEvenWhenCheckNowIsForced()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.RefreshAsync();
        await _model.PauseAsync("w1");
        Assert.Equal(BooruWatcherStatus.Paused, Row("w1").Watcher.Status);
        Assert.Equal(BooruWatcherBlock.Paused, _model.CheckBlock(Row("w1")));
        Assert.Null(Row("w1").Watcher.NextCheckAt);

        Assert.Null(await _model.CheckNowAsync("w1"));
        Assert.Null(await _model.PauseAsync("w1"));
        Assert.Equal(0, _h.Watchers.Checks);
        Assert.DoesNotContain("check:w1", _h.Watchers.Calls);

        await _model.ResumeAsync("w1");
        Assert.Equal(BooruWatcherStatus.Active, Row("w1").Watcher.Status);
        Assert.NotNull(Row("w1").Watcher.NextCheckAt);
        Assert.Equal(BooruWatcherBlock.None, _model.CheckBlock(Row("w1")));
        Assert.Equal(0, _h.Watchers.Checks); // resuming does not run a check either
    }

    [Fact]
    public async Task ResumeIsRefusedForAnActiveWatcher()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.RefreshAsync();
        Assert.Equal(BooruWatcherBlock.NotPaused, _model.ResumeBlock(Row("w1")));
        Assert.Null(await _model.ResumeAsync("w1"));
        Assert.DoesNotContain("resume:w1", _h.Watchers.Calls);
    }

    [Fact]
    public async Task RemovalNeedsConfirmationAndOnlyThenReachesTheScheduler()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock), BooruUiFixtures.Watcher("w2", "dog", clock: _h.Clock));
        await _model.RefreshAsync();
        Assert.Null(await _model.ConfirmRemovalAsync("w1")); // no confirmation open: nothing happens
        _model.RequestRemoval("w1");
        Assert.True(Row("w1").ConfirmingRemoval);
        Assert.DoesNotContain("remove:w1", _h.Watchers.Calls);
        _model.RequestRemoval("w2"); // only one confirmation at a time
        Assert.False(Row("w1").ConfirmingRemoval);
        _model.CancelRemoval("w2");
        Assert.DoesNotContain(_h.Watchers.Calls, call => call.StartsWith("remove:", StringComparison.Ordinal));

        _model.RequestRemoval("w1");
        BooruWatcherCommandResult? result = await _model.ConfirmRemovalAsync("w1");
        Assert.Equal(BooruWatcherCommandStatus.Done, result!.Status);
        Assert.DoesNotContain(_model.View.Rows, row => row.WatcherId == "w1");
        Assert.Equal("booru.ui.watcher.msg.removed", _model.View.Notice!.Key);
        Assert.Contains("remove:w1", _h.Watchers.Calls);
    }

    [Fact]
    public async Task ARemovedWatcherNeverStartsAHiddenRequest()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.RefreshAsync();
        _model.RequestRemoval("w1");
        await _model.ConfirmRemovalAsync("w1");
        _h.Watchers.Calls.Clear();

        Assert.Null(await _model.CheckNowAsync("w1"));
        Assert.Null(await _model.PauseAsync("w1"));
        Assert.Null(await _model.ResumeAsync("w1"));
        Assert.Null(await _model.ConfirmRemovalAsync("w1"));
        _h.Watchers.RaiseChanged(); // a later scheduler change only re-reads the list
        await BooruTestSupport.WaitAsync(() => _h.Watchers.Calls.Contains("list"), "list");
        Assert.All(_h.Watchers.Calls, call => Assert.Equal("list", call));
        Assert.Equal(0, _h.Watchers.Checks);
    }

    [Fact]
    public async Task ARejectedRemovalKeepsTheWatcherAndExplainsWhy()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        _h.Watchers.OnRemove = _ => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, "check_already_running");
        await _model.RefreshAsync();
        _model.RequestRemoval("w1");
        await _model.ConfirmRemovalAsync("w1");
        Assert.Contains(_model.View.Rows, row => row.WatcherId == "w1");
        Assert.False(Row("w1").ConfirmingRemoval);
        Assert.Equal("booru.ui.watcher.reject", Row("w1").Message!.Key);
        Assert.Equal(new object?[] { "check_already_running" }, Row("w1").Message!.Args);
    }

    [Theory]
    [InlineData("RateLimited", "booru.ui.watcher.msg.rateLimited")]
    [InlineData("Cooldown", "booru.ui.watcher.msg.cooldown")]
    [InlineData("Offline", "booru.ui.watcher.msg.offline")]
    [InlineData("Failed", "booru.ui.watcher.msg.failed")]
    public async Task CheckFailuresKeepTheWatcherAndShowTheirOwnMessage(string statusName, string key)
    {
        BooruWatcherCommandStatus status = Enum.Parse<BooruWatcherCommandStatus>(statusName);
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        _h.Watchers.OnCheck = _ => new BooruWatcherCommandResult(status, "x", RetryAt: _h.Clock.GetUtcNow() + TimeSpan.FromMinutes(5));
        await _model.RefreshAsync();
        await _model.CheckNowAsync("w1");
        Assert.Equal(key, Row("w1").Message!.Key);
        Assert.Contains(_model.View.Rows, row => row.WatcherId == "w1");
    }

    [Fact]
    public async Task ACoolingWatcherRefusesCheckNowUntilTheCooldownHasPassed()
    {
        DateTimeOffset until = _h.Clock.GetUtcNow() + TimeSpan.FromMinutes(10);
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", BooruWatcherStatus.Cooldown, _h.Clock, cooldownUntil: until));
        await _model.RefreshAsync();
        Assert.Equal(until, _model.ActiveCooldown(Row("w1").Watcher));
        Assert.Equal(BooruWatcherBlock.CoolingDown, _model.CheckBlock(Row("w1")));
        Assert.Null(await _model.CheckNowAsync("w1"));
        Assert.Equal(0, _h.Watchers.Checks);

        _h.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(_model.ActiveCooldown(Row("w1").Watcher));
        Assert.Equal(BooruWatcherBlock.None, _model.CheckBlock(Row("w1")));
        Assert.Equal(0, _h.Watchers.Checks); // the end of a cooldown starts nothing
    }

    [Fact]
    public async Task ErrorWatchersStayInTheListAndCanBeCheckedAgain()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", BooruWatcherStatus.Error, _h.Clock, errorCode: "http-network"));
        await _model.RefreshAsync();
        Assert.Equal("http-network", Row("w1").Watcher.ErrorCode);
        Assert.Equal(BooruWatcherBlock.None, _model.CheckBlock(Row("w1")));
        Assert.Equal(BooruWatcherBlock.None, _model.PauseBlock(Row("w1")));
    }

    [Fact]
    public async Task ACheckingWatcherCannotBeCheckedTwice()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", BooruWatcherStatus.Checking, _h.Clock));
        await _model.RefreshAsync();
        Assert.Equal(BooruWatcherBlock.Checking, _model.CheckBlock(Row("w1")));
        Assert.Null(await _model.CheckNowAsync("w1"));
        Assert.Equal(0, _h.Watchers.Checks);
    }

    [Fact]
    public async Task ABusyCommandBlocksADoubleClickWithoutASecondCall()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        TaskCompletionSource gate = new();
        _h.Watchers.OnCheck = _ => { gate.Task.Wait(); return BooruWatcherCommandResult.Success(); };
        await _model.RefreshAsync();
        Task first = Task.Run(() => _model.CheckNowAsync("w1"));
        await BooruTestSupport.WaitAsync(() => Row("w1").Busy, "busy");
        Assert.Equal(BooruWatcherBlock.Busy, _model.CheckBlock(Row("w1")));
        Assert.Null(await _model.CheckNowAsync("w1"));
        gate.SetResult();
        await first;
        Assert.Equal(1, _h.Watchers.Checks);
    }

    [Fact]
    public async Task ASchedulerChangeRefreshesTheListAndKeepsAnOpenConfirmation()
    {
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: _h.Clock));
        await _model.RefreshAsync();
        _model.RequestRemoval("w1");
        _h.Watchers.Seed(BooruUiFixtures.Watcher("w2", "dog", clock: _h.Clock));
        _h.Watchers.RaiseChanged();
        await BooruTestSupport.WaitAsync(() => _model.View.Rows.Length == 2, "refresh");
        Assert.True(Row("w1").ConfirmingRemoval);
        Assert.Equal(2, _model.Count);
    }
}
