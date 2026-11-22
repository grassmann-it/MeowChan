using MeowChan.Booru;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>Host-owned façade for validated one-shot booru searches and post reads.</summary>
public sealed class BooruSearchService
{
    public const string InvalidQueryCode = "invalid_query";
    public const string InvalidQueryMessage = "The booru query is not accepted.";
    public const string InvalidPostIdCode = "post_id";
    public const string InvalidPostIdMessage = "The booru post id is not accepted.";
    public const string InvalidCursorCode = "invalid_cursor";
    public const string InvalidCursorMessage = "The booru cursor is not accepted.";
    /// <summary>The page size of a Danbooru search (the host ceiling); another source uses at most its profile's limit.</summary>
    public const int SearchLimit = BooruHostHttp.MaximumSearchLimit;

    /// <summary>
    /// The worker call was invalidated without the caller asking for it: the worker client was disposed or
    /// stopped while the call ran or before it started (plugin disabled at runtime, worker shut down). It is
    /// the worker connection's own code for an invalidated call, so the watcher search port treats it as
    /// "source unavailable" (no watcher state change) like every other worker-availability code.
    /// </summary>
    internal const string WorkerStoppedCode = "stopped";
    private const string WorkerStoppedMessage = "The booru worker stopped before it answered.";

    private readonly BooruHostServices _services;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;

    /// <summary>
    /// Host diagnostics: sees every worker failure (its code and the worker's bounded message, which names the
    /// exception type for "plugin-error" and "sdk-mismatch") before the result keeps only the code. Never throws into
    /// the call; the public result and its generic message are unchanged.
    /// </summary>
    internal Action<string, PluginWorkerException>? WorkerFailureObserved { get; init; }
    private readonly object _workerCallsSync = new();
    private readonly HashSet<Task> _workerCalls = [];
    private TaskCompletionSource _workerCallsIdle = CompletedSignal();

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public BooruSearchService(BooruHostServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sources = services.Sources;
    }

    /// <summary>
    /// Opens one operation scope (one host job: at most 20 sends and 120 s) that several pages of the same
    /// logical operation, e.g. one watcher check, can share. Dispose it when the operation ends.
    /// </summary>
    public BooruSearchScope OpenScope() => new(_services.OpenJob(), _services);

    /// <summary>One validated page in its own one-page scope.</summary>
    public async Task<BooruSearchResult> SearchAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, int page, CancellationToken cancellationToken = default)
    {
        using BooruSearchScope scope = OpenScope();
        return await SearchAsync(worker, sourceId, userQuery, page, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One validated page inside a caller-owned scope; the page shares the scope's send and time budget.</summary>
    public Task<BooruSearchResult> SearchAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, int page, BooruSearchScope scope, CancellationToken cancellationToken = default)
        => SearchCoreAsync(worker, sourceId, userQuery, page, null, scope, cancellationToken);

    /// <summary>
    /// One page of the posts below <paramref name="beforePostId"/> (a post id this source returned), newest
    /// first, in its own one-page scope. Only a source with decimal post order accepts a cursor; it is never
    /// limited to the five numbered pages because it names its position instead of counting pages.
    /// </summary>
    public async Task<BooruSearchResult> SearchBeforeAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, string beforePostId, CancellationToken cancellationToken = default)
    {
        if (beforePostId is null)
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidCursorCode, InvalidCursorMessage));
        using BooruSearchScope scope = OpenScope();
        return await SearchBeforeAsync(worker, sourceId, userQuery, beforePostId, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One cursor page inside a caller-owned scope; it shares the scope's send and time budget.</summary>
    public Task<BooruSearchResult> SearchBeforeAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, string beforePostId, BooruSearchScope scope, CancellationToken cancellationToken = default)
    {
        if (beforePostId is null)
            return Task.FromResult<BooruSearchResult>(new BooruSearchFailure(sourceId, new FailureDetail(InvalidCursorCode, InvalidCursorMessage)));
        return SearchCoreAsync(worker, sourceId, userQuery, 1, beforePostId, scope, cancellationToken);
    }

    private async Task<BooruSearchResult> SearchCoreAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, int page, string? beforePostId, BooruSearchScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!TryGetSource(sourceId, out SourceDescription? source, out FailureDetail? sourceFailure))
            return new BooruSearchFailure(sourceId, sourceFailure!);
        if (!TryGetProfile(sourceId, out BooruSourceProfile? profile, out FailureDetail? profileFailure))
            return new BooruSearchFailure(sourceId, profileFailure!);
        int maximumPage = BooruEndpointPolicy.MaximumPage(profile!);
        if (page < 1 || page > maximumPage)
            return new BooruSearchFailure(sourceId, new FailureDetail("invalid_page",
                "The booru page must be between 1 and " + maximumPage.ToString(System.Globalization.CultureInfo.InvariantCulture) + "."));
        if (beforePostId is not null && (source!.PostOrder != PostOrderKind.DecimalPostId
                || !IsValidPostId(new BooruPostId(sourceId, beforePostId), source)))
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidCursorCode, InvalidCursorMessage));
        if (userQuery is null)
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidQueryCode, InvalidQueryMessage));

        // Danbooru keeps its own restricted syntax; every other source is checked against its declared rules.
        if (!BooruEndpointPolicy.TryNormalizeQuery(sourceId, profile!, userQuery, out string providerQuery))
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidQueryCode, InvalidQueryMessage));

        BooruSearchRequest request = new(sourceId, providerQuery, page, PageSize(profile!), beforePostId);
        IReadOnlyList<ValidationIssue> requestIssues = SdkValidation.ValidateBooruSearchRequest(request, source!);
        if (requestIssues.Count > 0)
            return new BooruSearchFailure(sourceId, PublicFailure(requestIssues[0]));
        if (_services.TryGetCooldown(sourceId, out DateTimeOffset retryAt))
            return new BooruSearchDeferred(sourceId, retryAt, "booru-cooldown");

        ArgumentNullException.ThrowIfNull(worker);
        BooruJob job = scope.Job; // throws if the scope was disposed
        cancellationToken.ThrowIfCancellationRequested();
        if (JobFailure(job) is FailureDetail ended) return new BooruSearchFailure(sourceId, ended);
        if (!scope.TryBegin(request))
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidCursorCode, InvalidCursorMessage));
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Token);
        BooruSearchPage? acceptedPage = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            Task<BooruSearchResult> workerCall = worker.SearchBooruPostsAsync(request,
                job.Operation.BindBooruCall(request, lifetime.Token), lifetime.Token);
            TrackWorkerCall(workerCall);
            BooruSearchResult result = await workerCall.WaitAsync(lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (SearchJobEnd(job, sourceId) is BooruSearchResult stopped) return stopped;
            IReadOnlyList<ValidationIssue> resultIssues = SdkValidation.ValidateBooruSearchResult(request, result, source!);
            cancellationToken.ThrowIfCancellationRequested();
            if (SearchJobEnd(job, sourceId) is BooruSearchResult validationEnd) return validationEnd;
            if (resultIssues.Count != 0) return new BooruSearchFailure(sourceId, PublicFailure(resultIssues[0]));
            acceptedPage = (result as BooruSearchSnapshot)?.Page;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Job deadline/close (including a latched 429) outranks unrelated worker invalidation.
            // Only the caller's own cancellation escapes; every other cancellation is a terminal outcome.
            return SearchJobEnd(job, sourceId) ?? new BooruSearchFailure(sourceId, WorkerStopped());
        }
        catch (ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SearchJobEnd(job, sourceId) ?? new BooruSearchFailure(sourceId, WorkerStopped());
        }
        catch (PluginWorkerException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observe(sourceId, exception);
            return SearchJobEnd(job, sourceId) ?? new BooruSearchFailure(sourceId, WorkerFailure(exception));
        }
        catch (NotSupportedException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SearchJobEnd(job, sourceId) ?? new BooruSearchFailure(sourceId, new FailureDetail("capability", exception.Message));
        }
        finally
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            finally { scope.End(acceptedPage); }
        }
    }

    public async Task<BooruPostResult> ReadPostAsync(IPluginWorkerClient worker, BooruPostId post,
        CancellationToken cancellationToken = default)
    {
        if (post is null)
            return new BooruPostFailure(new BooruPostId(string.Empty, string.Empty), new FailureDetail("invalid_post", InvalidPostIdMessage));
        if (!TryGetSource(post.SourceId, out SourceDescription? source, out FailureDetail? sourceFailure))
            return new BooruPostFailure(post, sourceFailure!);
        if (!TryGetProfile(post.SourceId, out _, out FailureDetail? profileFailure))
            return new BooruPostFailure(post, profileFailure!);
        if (!IsValidPostId(post, source!))
            return new BooruPostFailure(post, new FailureDetail(InvalidPostIdCode, InvalidPostIdMessage));
        if (_services.TryGetCooldown(post.SourceId, out DateTimeOffset retryAt))
            return new BooruPostDeferred(post, retryAt, "booru-cooldown");

        ArgumentNullException.ThrowIfNull(worker);
        BooruPostRequest request = new(post);
        IReadOnlyList<ValidationIssue> requestIssues = SdkValidation.ValidateBooruPostRequest(request, source!);
        if (requestIssues.Count > 0)
            return new BooruPostFailure(post, PublicFailure(requestIssues[0]));

        BooruJob job = _services.OpenJob();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            Task<BooruPostResult> workerCall = worker.ReadBooruPostAsync(request, job.Operation.BindBooruCall(request, lifetime.Token), lifetime.Token);
            TrackWorkerCall(workerCall);
            BooruPostResult result = await workerCall.WaitAsync(lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (PostJobEnd(job, post) is BooruPostResult stopped) return stopped;
            IReadOnlyList<ValidationIssue> resultIssues = SdkValidation.ValidateBooruPostResult(request, result, source!);
            cancellationToken.ThrowIfCancellationRequested();
            if (PostJobEnd(job, post) is BooruPostResult validationEnd) return validationEnd;
            return resultIssues.Count == 0 ? result : new BooruPostFailure(post, PublicFailure(resultIssues[0]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PostJobEnd(job, post) ?? new BooruPostFailure(post, WorkerStopped());
        }
        catch (ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PostJobEnd(job, post) ?? new BooruPostFailure(post, WorkerStopped());
        }
        catch (PluginWorkerException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observe(post.SourceId, exception);
            return PostJobEnd(job, post) ?? new BooruPostFailure(post, WorkerFailure(exception));
        }
        catch (NotSupportedException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PostJobEnd(job, post) ?? new BooruPostFailure(post, new FailureDetail("capability", exception.Message));
        }
        finally
        {
            _services.CloseJob(job);
        }
    }

    /// <summary>
    /// Returns only after worker calls that outlived their host-side result wait have finished. The host may return a
    /// cancellation result before an uncooperative worker has consumed its token, but a watcher generation must not
    /// be reopened while that worker can still publish a late page.
    /// </summary>
    internal Task DrainWorkerCallsAsync()
    {
        lock (_workerCallsSync) return _workerCallsIdle.Task;
    }

    private void TrackWorkerCall(Task workerCall)
    {
        if (workerCall.IsCompleted) return;
        lock (_workerCallsSync)
        {
            if (workerCall.IsCompleted) return;
            if (_workerCalls.Count == 0)
                _workerCallsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workerCalls.Add(workerCall);
        }
        _ = workerCall.ContinueWith(completed =>
        {
            _ = completed.Exception; // Drain observes completion without creating an unobserved late-worker fault.
            lock (_workerCallsSync)
            {
                _workerCalls.Remove(completed);
                if (_workerCalls.Count == 0) _workerCallsIdle.TrySetResult();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static FailureDetail? JobFailure(BooruJob job)
    {
        if (!job.IsOpen) return new("booru-job", "The booru job is not live.");
        if (job.IsExpired) return new("booru-budget", "The booru job budget is exhausted.");
        if (job.Token.IsCancellationRequested) return new("booru-job", "The booru job is not live.");
        return null;
    }

    private static BooruSearchResult? SearchJobEnd(BooruJob job, string sourceId) =>
        job.CooldownStop is HostHttpDeferred deferred
            ? new BooruSearchDeferred(sourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode)
            : JobFailure(job) is FailureDetail failure ? new BooruSearchFailure(sourceId, failure) : null;

    private static BooruPostResult? PostJobEnd(BooruJob job, BooruPostId post) =>
        job.CooldownStop is HostHttpDeferred deferred
            ? new BooruPostDeferred(post, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode)
            : JobFailure(job) is FailureDetail failure ? new BooruPostFailure(post, failure) : null;

    private bool TryGetSource(string sourceId, out SourceDescription? source, out FailureDetail? failure)
    {
        if (_sources.TryGetValue(sourceId, out source) && source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
        {
            failure = null;
            return true;
        }
        source = null;
        failure = new FailureDetail("capability", "The source does not declare booru post search.");
        return false;
    }

    /// <summary>
    /// The page size the host requests from a source: the host ceiling, lowered by the source's profile. The watcher
    /// port reads <see cref="SearchLimit"/> for Danbooru, whose profile keeps the ceiling.
    /// </summary>
    internal int PageSize(string sourceId) =>
        _services.ProfileOf(sourceId) is BooruSourceProfile profile ? PageSize(profile) : SearchLimit;

    private static int PageSize(BooruSourceProfile profile) => BooruEndpointPolicy.MaximumLimit(profile);

    private bool TryGetProfile(string sourceId, out BooruSourceProfile? profile, out FailureDetail? failure)
    {
        profile = _services.ProfileOf(sourceId);
        failure = profile is null ? new FailureDetail("capability", "The source declares no usable booru endpoint profile.") : null;
        return profile is not null;
    }

    private static bool IsValidPostId(BooruPostId post, SourceDescription source)
    {
        // Every endpoint profile names post ids as canonical decimals (at most 18 digits), in the path or a parameter.
        if (BooruEndpointPolicy.IsDanbooru(source.SourceId) || source.PostOrder == PostOrderKind.DecimalPostId)
            return BooruEndpointPolicy.IsCanonicalPostId(post.PostId);
        return post.PostId.Length is > 0 and <= 256 && post.PostId.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static FailureDetail PublicFailure(ValidationIssue issue) =>
        new(issue.Code, issue.Message);

    private void Observe(string sourceId, PluginWorkerException exception)
    {
        try { WorkerFailureObserved?.Invoke(sourceId, exception); }
        catch (Exception) { } // Diagnostics only: an observer failure never changes the result.
    }

    private static FailureDetail WorkerFailure(PluginWorkerException exception) =>
        new(exception.Code, "The booru worker did not return a usable result.", exception.FailureInfo.HttpStatusCode is not null
            ? (int)exception.FailureInfo.HttpStatusCode : null, exception.FailureInfo.RetryAt);

    private static FailureDetail WorkerStopped() => new(WorkerStoppedCode, WorkerStoppedMessage);
}

/// <summary>
/// A host-owned operation scope shared by the pages of one logical booru operation. It wraps one
/// <see cref="BooruJob"/> (one <see cref="PluginOperationContext"/>/resource id, one send and time budget);
/// disposing it closes the job so late sends are refused.
/// </summary>
public sealed class BooruSearchScope : IDisposable
{
    private readonly BooruHostServices _services;
    private BooruJob? _job;
    private readonly object _gate = new();
    private (string Source, string Query)? _search;
    private string? _nextCursor;
    private bool _inFlight;
    private bool _hasPage;

    internal BooruSearchScope(BooruJob job, BooruHostServices services)
    {
        _job = job;
        _services = services;
    }

    internal BooruJob Job => _job ?? throw new ObjectDisposedException(nameof(BooruSearchScope));

    internal bool TryBegin(BooruSearchRequest request)
    {
        lock (_gate)
        {
            var search = (request.SourceId, request.Query);
            // The first cursor can be restored by the host from persisted watcher state.
            if (_inFlight || _search is not null && _search != search
                || _hasPage && request.BeforePostId is not null && request.BeforePostId != _nextCursor)
                return false;
            _search = search;
            _inFlight = true;
            return true;
        }
    }

    internal void End(BooruSearchPage? page)
    {
        lock (_gate)
        {
            if (page is not null)
            {
                _hasPage = true;
                _nextCursor = page.NextBeforePostId;
            }
            _inFlight = false;
        }
    }

    public void Dispose()
    {
        BooruJob? job = Interlocked.Exchange(ref _job, null);
        if (job is not null) _services.CloseJob(job);
    }
}
