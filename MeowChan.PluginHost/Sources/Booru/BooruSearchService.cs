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
    public const int SearchLimit = BooruHostHttp.MaximumSearchLimit;

    private readonly BooruHostServices _services;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;

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
        if (page is < 1 or > BooruHostHttp.MaximumSearchPage)
            return new BooruSearchFailure(sourceId, new FailureDetail("invalid_page", "The booru page must be between 1 and 5."));
        if (beforePostId is not null && (source!.PostOrder != PostOrderKind.DecimalPostId
                || !IsValidPostId(new BooruPostId(sourceId, beforePostId), source)))
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidCursorCode, InvalidCursorMessage));
        if (userQuery is null)
            return new BooruSearchFailure(sourceId, new FailureDetail(InvalidQueryCode, InvalidQueryMessage));

        string providerQuery = userQuery;
        if (string.Equals(sourceId, "danbooru", StringComparison.Ordinal))
        {
            if (!DanbooruTagQuery.TryParse(userQuery, out DanbooruTagQuery? parsed, out _)
                || parsed is null)
                return new BooruSearchFailure(sourceId, new FailureDetail(InvalidQueryCode, InvalidQueryMessage));
            providerQuery = parsed.ProviderQuery;
        }

        BooruSearchRequest request = new(sourceId, providerQuery, page, SearchLimit, beforePostId);
        IReadOnlyList<ValidationIssue> requestIssues = SdkValidation.ValidateBooruSearchRequest(request, source!);
        if (requestIssues.Count > 0)
            return new BooruSearchFailure(sourceId, PublicFailure(requestIssues[0]));
        if (_services.TryGetCooldown(sourceId, out DateTimeOffset retryAt))
            return new BooruSearchDeferred(sourceId, retryAt, "booru-cooldown");

        ArgumentNullException.ThrowIfNull(worker);
        BooruJob job = scope.Job; // throws if the scope was disposed
        try
        {
            BooruSearchResult result = await worker.SearchBooruPostsAsync(request, job.Operation, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ValidationIssue> resultIssues = SdkValidation.ValidateBooruSearchResult(request, result, source!);
            return resultIssues.Count == 0 ? result : new BooruSearchFailure(sourceId, PublicFailure(resultIssues[0]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginWorkerException exception)
        {
            return new BooruSearchFailure(sourceId, WorkerFailure(exception));
        }
        catch (NotSupportedException exception)
        {
            return new BooruSearchFailure(sourceId, new FailureDetail("capability", exception.Message));
        }
    }

    public async Task<BooruPostResult> ReadPostAsync(IPluginWorkerClient worker, BooruPostId post,
        CancellationToken cancellationToken = default)
    {
        if (post is null)
            return new BooruPostFailure(new BooruPostId(string.Empty, string.Empty), new FailureDetail("invalid_post", InvalidPostIdMessage));
        if (!TryGetSource(post.SourceId, out SourceDescription? source, out FailureDetail? sourceFailure))
            return new BooruPostFailure(post, sourceFailure!);
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
        try
        {
            BooruPostResult result = await worker.ReadBooruPostAsync(request, job.Operation, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ValidationIssue> resultIssues = SdkValidation.ValidateBooruPostResult(request, result, source!);
            return resultIssues.Count == 0 ? result : new BooruPostFailure(post, PublicFailure(resultIssues[0]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PluginWorkerException exception)
        {
            return new BooruPostFailure(post, WorkerFailure(exception));
        }
        catch (NotSupportedException exception)
        {
            return new BooruPostFailure(post, new FailureDetail("capability", exception.Message));
        }
        finally
        {
            _services.CloseJob(job);
        }
    }

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

    private static bool IsValidPostId(BooruPostId post, SourceDescription source)
    {
        if (string.Equals(source.SourceId, "danbooru", StringComparison.Ordinal))
            return post.PostId.Length is > 0 and <= 18 && post.PostId[0] != '0' && post.PostId.All(char.IsAsciiDigit);
        return post.PostId.Length is > 0 and <= 256 && post.PostId.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static FailureDetail PublicFailure(ValidationIssue issue) =>
        new(issue.Code, issue.Message);

    private static FailureDetail WorkerFailure(PluginWorkerException exception) =>
        new(exception.Code, "The booru worker did not return a usable result.", exception.FailureInfo.HttpStatusCode is not null
            ? (int)exception.FailureInfo.HttpStatusCode : null, exception.FailureInfo.RetryAt);
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

    internal BooruSearchScope(BooruJob job, BooruHostServices services)
    {
        _job = job;
        _services = services;
    }

    internal BooruJob Job => _job ?? throw new ObjectDisposedException(nameof(BooruSearchScope));

    public void Dispose()
    {
        BooruJob? job = Interlocked.Exchange(ref _job, null);
        if (job is not null) _services.CloseJob(job);
    }
}
