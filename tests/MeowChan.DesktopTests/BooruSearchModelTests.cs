using MeowChan.PluginSdk;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruSearchModelTests : IDisposable
{
    private readonly BooruUiHarness _h = new();
    private readonly BooruSearchModel _model;

    public BooruSearchModelTests() =>
        _model = new BooruSearchModel(_h.Search, _h.Queue, _h.Previews, _h.Clock);

    public void Dispose() => _model.Dispose();

    // ---- nothing is requested without an explicit action ----------------------------------------

    [Fact]
    public void EditingTheFieldAndSwitchingModeSendNothing()
    {
        _model.SetInput("cat blue_eyes");
        _model.SetMode(BooruInputMode.Post);
        _model.SetMode(BooruInputMode.Tags);
        _model.Tick();
        Assert.Equal(0, _h.ProviderRequests);
        Assert.Equal(BooruSearchStatus.Idle, _model.View.Status);
    }

    [Fact]
    public async Task InvalidAndEmptyInputAreExplainedWithoutARequest()
    {
        await BooruTestSupport.SearchAsync(_model, "   ");
        Assert.Equal(BooruSearchStatus.Idle, _model.View.Status);

        await BooruTestSupport.SearchAsync(_model, "a b c");
        Assert.Equal(BooruSearchStatus.InvalidInput, _model.View.Status);
        Assert.Equal(BooruInputIssue.TooManyTags, _model.View.Parsed.Issue);

        await BooruTestSupport.SearchAsync(_model, "cat rating:z");
        Assert.Equal(BooruInputIssue.Rating, _model.View.Parsed.Issue);

        await BooruTestSupport.SearchAsync(_model, "007", BooruInputMode.Post);
        Assert.Equal(BooruInputIssue.PostId, _model.View.Parsed.Issue);

        await BooruTestSupport.SearchAsync(_model, "https://danbooru.donmai.us/posts/12345?q=cat", BooruInputMode.Post);
        Assert.Equal(BooruInputIssue.PostLink, _model.View.Parsed.Issue);
        await BooruTestSupport.SearchAsync(_model, "https://example.com/posts/12345", BooruInputMode.Tags);
        Assert.Equal(BooruInputIssue.PostLink, _model.View.Parsed.Issue);
        Assert.Equal(0, _h.ProviderRequests);
    }

    // ---- no hidden rating filter ------------------------------------------------------------------

    [Theory]
    [InlineData("cat")]
    [InlineData("cat blue_eyes")]
    [InlineData("  CAT   blue_eyes ")]
    public async Task TheQuerySentIsTheCanonicalFieldWithNoRatingTermAdded(string input)
    {
        await BooruTestSupport.SearchAsync(_model, input);
        string sent = Assert.Single(_h.Search.Searches).Query;
        Assert.Equal(_model.View.Parsed.Canonical, sent);
        Assert.DoesNotContain("rating", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-", sent);
        Assert.Equal(sent, _model.View.ExecutedQuery);
    }

    [Fact]
    public async Task ARatingTermTheUserTypedIsSentOnceAndNeverAdditionalOnes()
    {
        await BooruTestSupport.SearchAsync(_model, "cat rating:g");
        string sent = Assert.Single(_h.Search.Searches).Query;
        Assert.Equal(1, sent.Split(' ').Count(term => term.StartsWith("rating:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NextPageRepeatsTheDisplayedQueryNotUnsentEdits()
    {
        _h.Serve(pages: 3);
        await BooruTestSupport.SearchAsync(_model, "cat");
        _model.SetInput("dog");
        await _model.GoToPageAsync(2);
        Assert.Equal(("cat", 2), _h.Search.Searches[1]);
    }

    // ---- bounded paging ------------------------------------------------------------------------------

    [Fact]
    public async Task PagingIsBoundedToTheHostLimitAndStopsAtTheProvidersEnd()
    {
        _h.Serve(pages: 10);
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.False(_model.View.HasPreviousPage);
        for (int page = 2; page <= BooruSearchModel.MaximumPage; page++)
        {
            Assert.True(_model.View.HasNextPage);
            await _model.GoToPageAsync(page);
        }
        Assert.Equal(BooruSearchModel.MaximumPage, _model.View.Page);
        Assert.False(_model.View.HasNextPage);
        Assert.True(_model.View.PageLimitReached);
        int requests = _h.Search.Searches.Count;
        await _model.GoToPageAsync(BooruSearchModel.MaximumPage + 1);
        Assert.Equal(requests, _h.Search.Searches.Count);

        // The provider's own end also ends the paging, without hitting the bound.
        _h.Serve(pages: 2);
        await BooruTestSupport.SearchAsync(_model, "dog");
        await _model.GoToPageAsync(2);
        Assert.False(_model.View.HasNextPage);
        Assert.False(_model.View.PageLimitReached);
        Assert.True(_model.View.HasPreviousPage);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task APageAfterOneIsNeverADeadEndAndRecoveryRepeatsTheExactQueryAndPage(string kind)
    {
        _h.Serve(pages: 10);
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.GoToPageAsync(2);
        await _model.GoToPageAsync(3);
        TaskCompletionSource<BooruCallResult<BooruResultPage>> gate = new();
        bool fail = true;
        _h.Search.OnSearch = (query, page, _) => kind == "cancelled" && fail ? gate.Task
            : Task.FromResult(kind != "cancelled" && fail
                ? BooruCallResult<BooruResultPage>.Of(kind == "offline" ? BooruCallStatus.Offline : BooruCallStatus.Failed, "x")
                : BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, false, BooruUiFixtures.Post("7"))));
        Task running = _model.GoToPageAsync(4);
        if (kind == "cancelled")
        {
            await BooruTestSupport.WaitAsync(() => _model.View.IsBusy, "busy");
            _model.Cancel();
            gate.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 4, false, BooruUiFixtures.Post("late"))));
        }
        await running;
        Assert.Empty(_model.View.Rows); // no stale or late result published
        Assert.Equal(4, _model.View.Page);
        Assert.True(_model.View.HasPreviousPage);
        int requests = _h.Search.Searches.Count;
        _model.Tick();
        Assert.Equal(requests, _h.Search.Searches.Count); // never automatic

        fail = false;
        await _model.RetryAsync();
        Assert.Equal(("cat", 4), _h.Search.Searches[^1]);
        Assert.Equal(BooruSearchStatus.Results, _model.View.Status);

        fail = true;
        if (kind != "cancelled") await _model.GoToPageAsync(5);
        fail = false;
        await _model.GoToPageAsync(3);
        Assert.Equal(("cat", 3), _h.Search.Searches[^1]);
        Assert.Equal(BooruSearchStatus.Results, _model.View.Status);
    }

    [Fact]
    public async Task PreviousFromAFailedPageStaysBoundedAndNextIsNotOffered()
    {
        _h.Serve(pages: 10);
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.GoToPageAsync(2);
        _h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Offline, "x"));
        await _model.GoToPageAsync(3);
        Assert.False(_model.View.HasNextPage);
        int requests = _h.Search.Searches.Count;
        await _model.GoToPageAsync(4);
        await _model.GoToPageAsync(0);
        Assert.Equal(requests, _h.Search.Searches.Count);
    }

    [Fact]
    public async Task AnEmptyLaterPageOffersPreviousAndNoNext()
    {
        _h.Serve(pages: 10);
        await BooruTestSupport.SearchAsync(_model, "cat");
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(
            BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true)));
        await _model.GoToPageAsync(2);
        Assert.Equal(BooruSearchStatus.Empty, _model.View.Status);
        Assert.True(_model.View.HasPreviousPage);
        Assert.False(_model.View.HasNextPage);
    }

    [Fact]
    public async Task RetryIsOfferedOnlyForAnExecutedSearchOrLookupNeverForTheInitialIdle()
    {
        Assert.False(_model.View.CanRetry);
        int requests = _h.Search.Requests;
        await _model.RetryAsync();
        Assert.Equal(requests, _h.Search.Requests);

        TaskCompletionSource<BooruCallResult<BooruResultPage>> gate = new();
        _h.Search.OnSearch = (_, _, _) => gate.Task;
        Task running = BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.WaitAsync(() => _model.View.IsBusy, "busy");
        Assert.False(_model.View.CanRetry); // busy
        _model.Cancel();
        Assert.True(_model.View.CanRetry);
        gate.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true, BooruUiFixtures.Post("late"))));
        await running;
        Assert.Empty(_model.View.Rows);
        _h.Serve(pages: 3);
        await _model.RetryAsync();
        Assert.Equal(("cat", 1), _h.Search.Searches[^1]);
        Assert.False(_model.View.CanRetry); // Results
    }

    [Fact]
    public async Task RetryIsRefusedWhileAProviderPauseIsInForce()
    {
        _h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Cooldown, "c"));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.False(_model.View.CanRetry);
        int requests = _h.Search.Requests;
        await _model.RetryAsync();
        Assert.Equal(requests, _h.Search.Requests);
    }

    [Fact]
    public async Task APageAnswerForAnotherQueryOrPageIsNotShown()
    {
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page("somethingelse", page, true, BooruUiFixtures.Post("1"))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(BooruSearchStatus.Failed, _model.View.Status);
        Assert.Empty(_model.View.Rows);
    }

    // ---- states ------------------------------------------------------------------------------------------

    [Fact]
    public async Task EmptyResultIsItsOwnState()
    {
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(BooruSearchStatus.Empty, _model.View.Status);
        Assert.True(_model.View.IsEnd);
        Assert.False(_model.View.HasNextPage);
    }

    [Theory]
    [InlineData("Offline", "Offline")]
    [InlineData("Denied", "Denied")]
    [InlineData("NotFound", "NotFound")]
    [InlineData("Unavailable", "Unavailable")]
    [InlineData("Failed", "Failed")]
    public async Task OfflineRestrictedAndErrorStatesAreDistinctAndShowNoRows(string callName, string expectedName)
    {
        BooruCallStatus call = Enum.Parse<BooruCallStatus>(callName);
        BooruSearchStatus expected = Enum.Parse<BooruSearchStatus>(expectedName);
        _h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(call, "code"));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(expected, _model.View.Status);
        Assert.Empty(_model.View.Rows);
        Assert.False(_model.IsPaused);
    }

    [Fact]
    public async Task RateLimitPausesTheSearchWithoutAnyRequestUntilTheTimeHasPassed()
    {
        DateTimeOffset retryAt = _h.Clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        _h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(BooruCallStatus.RateLimited, "provider-throttled", retryAt, 429));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(BooruSearchStatus.RateLimited, _model.View.Status);
        Assert.Equal(retryAt, _model.View.RetryAt);
        Assert.True(_model.IsPaused);
        Assert.False(_model.CanSubmit);

        // Pressing search, retry or paging during the pause is refused locally.
        int requests = _h.Search.Requests;
        await _model.SubmitAsync();
        await _model.RetryAsync();
        await _model.GoToPageAsync(2);
        Assert.Equal(requests, _h.Search.Requests);

        // The pause running out only re-enables the button; it starts nothing on its own.
        _h.Clock.Advance(TimeSpan.FromMinutes(3));
        _model.Tick();
        Assert.False(_model.IsPaused);
        Assert.True(_model.CanSubmit);
        Assert.Equal(requests, _h.Search.Requests);
    }

    [Theory]
    [InlineData("RateLimited", "RateLimited")]
    [InlineData("Cooldown", "Cooldown")]
    public async Task APauseWithoutAUsableTimeFallsBackToAtLeastSixtySeconds(string callName, string expectedName)
    {
        BooruCallStatus call = Enum.Parse<BooruCallStatus>(callName);
        BooruSearchStatus expected = Enum.Parse<BooruSearchStatus>(expectedName);
        _h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(call, "x", _h.Clock.GetUtcNow() - TimeSpan.FromSeconds(5)));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(expected, _model.View.Status);
        Assert.True(_model.View.RetryAt >= _h.Clock.GetUtcNow() + TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task OfflineRetryRepeatsExactlyWhatWasShownAndOnlyAfterTheExplicitAction()
    {
        bool offline = true;
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(offline
            ? BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Offline, "http-network")
            : BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("9"))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(BooruSearchStatus.Offline, _model.View.Status);
        offline = false;
        _model.SetInput("something else"); // editing never retries
        Assert.Equal(1, _h.Search.Requests);
        await _model.RetryAsync();
        Assert.Equal(("cat", 1), _h.Search.Searches[1]);
        Assert.Equal(BooruSearchStatus.Results, _model.View.Status);
    }

    [Fact]
    public async Task AThrowingPortBecomesAnErrorStateWithoutLeakingTheMessage()
    {
        _h.Search.OnSearch = (_, _, _) => throw new InvalidOperationException("secret provider text");
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Equal(BooruSearchStatus.Failed, _model.View.Status);
        Assert.DoesNotContain("secret", _model.View.Code ?? string.Empty);
    }

    [Fact]
    public async Task ASlowSearchCanBeCancelledAndItsLateAnswerIsDiscarded()
    {
        TaskCompletionSource<BooruCallResult<BooruResultPage>> gate = new();
        _h.Search.OnSearch = (_, _, _) => gate.Task;
        Task running = BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.WaitAsync(() => _model.View.IsBusy, "busy");
        Assert.False(_model.CanSubmit);
        _model.Cancel();
        Assert.Equal(BooruSearchStatus.Idle, _model.View.Status);
        gate.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true, BooruUiFixtures.Post("1"))));
        await running;
        Assert.Empty(_model.View.Rows);
    }

    // ---- rows -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RowsKeepTheProvidersRatingAsProviderMetadataAndTheSourceTags()
    {
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
            BooruUiFixtures.Post("1", rating: BooruRating.Explicit, tags: new[] { "cat", "solo", "some_artist" }, score: 77))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        BooruPostView post = Assert.Single(_model.View.Rows).Post;
        Assert.Equal(BooruRating.Explicit, post.Rating);
        Assert.Equal(77, post.Score);
        Assert.Equal(3, post.Tags.Length);
        Assert.Equal(new Uri("https://danbooru.donmai.us/posts/1"), post.PostUrl);
        Assert.Equal(BooruOriginalAccess.Available, post.OriginalAccess);
    }

    [Fact]
    public async Task PreviewsLoadThroughThePortAndAFailedPreviewDoesNotBreakTheRow()
    {
        _h.Previews.OnPreview = post => post.PostId == "2" ? null : BooruUiFixtures.Png(1);
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
            BooruUiFixtures.Post("1"), BooruUiFixtures.Post("2"), BooruUiFixtures.Post("3", preview: false))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.SettleAsync(_model);
        Assert.Equal(BooruPreviewState.Ready, _model.View.Rows[0].Preview);
        Assert.Equal(BooruPreviewState.Unavailable, _model.View.Rows[1].Preview);
        Assert.Equal(BooruPreviewState.None, _model.View.Rows[2].Preview);
        Assert.Equal(2, _h.Previews.Requests);
        Assert.NotNull(_model.View.Rows[0].PreviewBytes);
    }

    [Fact]
    public async Task LocalLibraryStateIsShownAndFollowsTheQueueWithoutADuplicateLoad()
    {
        _h.Serve(perPage: 2);
        _h.Queue.States["101"] = BooruLocalState.Loaded;
        await BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.WaitAsync(() => _model.View.Rows[0].Load == BooruRowLoad.AlreadyLoaded, "local state");
        Assert.False(_model.View.Rows[0].CanLoad);
        await _model.LoadOriginalAsync("101");
        Assert.Empty(_h.Queue.Loads);

        _h.Queue.States["102"] = BooruLocalState.Queued;
        _h.Queue.RaiseLocalStatesChanged();
        await BooruTestSupport.WaitAsync(() => _model.View.Rows[1].Load == BooruRowLoad.AlreadyQueued, "queue state");
    }

    // ---- load original ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOlderLocalStateReadCannotOverwriteFailureOrSuccess()
    {
        _h.Serve(perPage: 2);
        _h.Queue.States["101"] = BooruLocalState.Queued;
        _h.Queue.States["102"] = BooruLocalState.Queued;
        await BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.WaitAsync(() => _model.View.Rows.All(row => row.Load == BooruRowLoad.AlreadyQueued), "initial queue states");

        TaskCompletionSource olderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyDictionary<string, BooruLocalState>> older = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Queue.OnGetStates = () =>
        {
            olderStarted.TrySetResult();
            return older.Task;
        };
        Task pending = _model.RefreshLocalStatesAsync();
        await olderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _h.Queue.OnGetStates = null;
        _h.Queue.States["101"] = BooruLocalState.DownloadFailed;
        _h.Queue.States["102"] = BooruLocalState.Loaded;
        await _model.RefreshLocalStatesAsync();

        older.SetResult(new Dictionary<string, BooruLocalState>
        {
            ["101"] = BooruLocalState.Queued,
            ["102"] = BooruLocalState.Queued
        });
        await pending;
        Assert.Equal(BooruRowLoad.DownloadFailed, _model.View.Rows[0].Load);
        Assert.True(_model.View.Rows[0].CanRetry);
        Assert.Equal(BooruRowLoad.AlreadyLoaded, _model.View.Rows[1].Load);
        Assert.False(_model.View.Rows[1].CanRetry);
    }

    [Fact]
    public async Task LoadOriginalHandsOverExactlyOnceAndAnotherPressDoesNotQueueAgain()
    {
        _h.Serve(perPage: 1);
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.Empty(_h.Queue.Loads); // searching never loads anything
        await _model.LoadOriginalAsync("101");
        await _model.LoadOriginalAsync("101");
        var load = Assert.Single(_h.Queue.Loads);
        Assert.Equal(("101", BooruLoadOrigin.Search), load);
        Assert.Equal(BooruRowLoad.Queued, _model.View.Rows[0].Load);
    }

    [Fact]
    public async Task QueuedOriginalCanBeCancelledAndExplicitlyRetriedThroughTheQueuePort()
    {
        _h.Serve(perPage: 1);
        _h.Queue.OnCancel = _ => Task.FromResult(true);
        _h.Queue.OnRetry = _ => Task.FromResult(new BooruLoadResult(BooruLoadStatus.Queued));
        await BooruTestSupport.SearchAsync(_model, "cat");

        await _model.LoadOriginalAsync("101");
        Assert.True(_model.View.Rows[0].CanCancel);
        await _model.CancelOriginalAsync("101");
        Assert.Equal(new[] { "101" }, _h.Queue.Cancellations);
        Assert.Equal(BooruRowLoad.Failed, _model.View.Rows[0].Load);
        Assert.Equal(BooruCodes.Cancelled, _model.View.Rows[0].LoadCode);
        Assert.True(_model.View.Rows[0].CanRetry);

        await _model.RetryOriginalAsync("101");
        Assert.Equal(new[] { "101" }, _h.Queue.Retries);
        Assert.Equal(BooruRowLoad.Queued, _model.View.Rows[0].Load);
    }

    [Fact]
    public async Task CancelAndRetryMutationsAreSingleFlightForOneRow()
    {
        _h.Serve(perPage: 1);
        TaskCompletionSource<bool> cancel = new();
        _h.Queue.OnCancel = _ => cancel.Task;
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.LoadOriginalAsync("101");

        Task first = _model.CancelOriginalAsync("101");
        await BooruTestSupport.WaitAsync(() => _model.View.Rows[0].Load == BooruRowLoad.Handing, "cancelling");
        Task second = _model.CancelOriginalAsync("101");
        cancel.SetResult(true);
        await Task.WhenAll(first, second);
        Assert.Single(_h.Queue.Cancellations);
    }

    [Theory]
    [InlineData("AlreadyQueued", "AlreadyQueued")]
    [InlineData("AlreadyLoaded", "AlreadyLoaded")]
    [InlineData("NoOriginal", "NoOriginal")]
    [InlineData("Restricted", "Restricted")]
    [InlineData("Offline", "Offline")]
    [InlineData("Failed", "Failed")]
    public async Task EveryQueueOutcomeHasItsOwnRowState(string statusName, string expectedName)
    {
        BooruLoadStatus status = Enum.Parse<BooruLoadStatus>(statusName);
        BooruRowLoad expected = Enum.Parse<BooruRowLoad>(expectedName);
        _h.Serve(perPage: 1);
        _h.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(status, "code"));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.LoadOriginalAsync("101");
        Assert.Equal(expected, _model.View.Rows[0].Load);
    }

    [Fact]
    public async Task OfflineAndFailedLoadsCanBeRepeatedOnlyByPressingAgain()
    {
        _h.Serve(perPage: 1);
        int calls = 0;
        _h.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(++calls == 1 ? BooruLoadStatus.Offline : BooruLoadStatus.Queued));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.LoadOriginalAsync("101");
        Assert.True(_model.View.Rows[0].CanLoad);
        Assert.Equal(1, calls);
        await _model.LoadOriginalAsync("101");
        Assert.Equal(BooruRowLoad.Queued, _model.View.Rows[0].Load);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("NotOffered", "NoOriginal")]
    [InlineData("Restricted", "Restricted")]
    public async Task APostWithoutAUsableOriginalNeverReachesTheQueue(string accessName, string expectedName)
    {
        BooruOriginalAccess access = Enum.Parse<BooruOriginalAccess>(accessName);
        BooruRowLoad expected = Enum.Parse<BooruRowLoad>(expectedName);
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("5", original: access))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        Assert.False(_model.View.Rows[0].CanLoad);
        await _model.LoadOriginalAsync("5");
        Assert.Empty(_h.Queue.Loads);
        Assert.Equal(expected, _model.View.Rows[0].Load);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ADeletedOrBannedPostHasNoLoadableOriginal(bool deleted, bool banned)
    {
        _h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("6", deleted: deleted, banned: banned))));
        await BooruTestSupport.SearchAsync(_model, "cat");
        await _model.LoadOriginalAsync("6");
        Assert.Empty(_h.Queue.Loads);
        Assert.Equal(BooruOriginalAccess.Restricted, _model.View.Rows[0].Post.OriginalAccess);
    }

    // ---- one-shot (post link) --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts/12345", "Post")]
    [InlineData("https://danbooru.donmai.us/posts/12345.json", "Post")]
    [InlineData("12345", "Post")]
    [InlineData("https://danbooru.donmai.us/posts/12345", "Tags")]
    public async Task APostLinkOrIdIsAOneShotLookupAndNeverASearch(string input, string modeName)
    {
        BooruInputMode mode = Enum.Parse<BooruInputMode>(modeName);
        await BooruTestSupport.SearchAsync(_model, input, mode);
        Assert.Empty(_h.Search.Searches);
        Assert.Equal("12345", Assert.Single(_h.Search.PostReads));
        Assert.True(_model.View.IsPostResult);
        Assert.Equal(BooruInputMode.Post, _model.View.Mode); // a pasted link switches the mode visibly
        BooruResultRowView row = Assert.Single(_model.View.Rows);
        Assert.Equal("12345", row.Post.PostId);
        Assert.False(_model.View.HasNextPage);
        Assert.False(_model.View.HasPreviousPage);
    }

    [Fact]
    public async Task OneShotLoadOriginalCarriesTheOneShotOrigin()
    {
        await BooruTestSupport.SearchAsync(_model, "https://danbooru.donmai.us/posts/77");
        await _model.LoadOriginalAsync("77");
        Assert.Equal(("77", BooruLoadOrigin.OneShot), Assert.Single(_h.Queue.Loads));
    }

    [Fact]
    public async Task AnAnswerForAnotherPostIsRejectedAndMissingPostsAreReported()
    {
        _h.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(BooruUiFixtures.Post("999")));
        await BooruTestSupport.SearchAsync(_model, "12345", BooruInputMode.Post);
        Assert.Equal(BooruSearchStatus.Failed, _model.View.Status);

        _h.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Of(BooruCallStatus.NotFound, "not-found", httpStatus: 404));
        await BooruTestSupport.SearchAsync(_model, "12346", BooruInputMode.Post);
        Assert.Equal(BooruSearchStatus.NotFound, _model.View.Status);
    }

    [Fact]
    public async Task ADisposedModelIgnoresLateAnswers()
    {
        TaskCompletionSource<BooruCallResult<BooruResultPage>> gate = new();
        _h.Search.OnSearch = (_, _, _) => gate.Task;
        Task running = BooruTestSupport.SearchAsync(_model, "cat");
        await BooruTestSupport.WaitAsync(() => _model.View.IsBusy, "busy");
        _model.Dispose();
        gate.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("cat", 1, true, BooruUiFixtures.Post("1"))));
        await running;
        Assert.Empty(_model.View.Rows);
    }
}
