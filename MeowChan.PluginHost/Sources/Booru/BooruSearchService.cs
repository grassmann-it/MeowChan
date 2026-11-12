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
    public const int SearchLimit = BooruHostHttp.MaximumSearchLimit;

    private readonly BooruHostServices _services;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;

    public BooruSearchService(BooruHostServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sources = services.Sources;
    }

    public async Task<BooruSearchResult> SearchAsync(IPluginWorkerClient worker, string sourceId,
        string userQuery, int page, CancellationToken cancellationToken = default)
    {
        if (!TryGetSource(sourceId, out SourceDescription? source, out FailureDetail? sourceFailure))
            return new BooruSearchFailure(sourceId, sourceFailure!);
        if (page is < 1 or > BooruHostHttp.MaximumSearchPage)
            return new BooruSearchFailure(sourceId, new FailureDetail("invalid_page", "The booru page must be between 1 and 5."));
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

        BooruSearchRequest request = new(sourceId, providerQuery, page, SearchLimit);
        IReadOnlyList<ValidationIssue> requestIssues = SdkValidation.ValidateBooruSearchRequest(request, source!);
        if (requestIssues.Count > 0)
            return new BooruSearchFailure(sourceId, PublicFailure(requestIssues[0]));
        if (_services.TryGetCooldown(sourceId, out DateTimeOffset retryAt))
            return new BooruSearchDeferred(sourceId, retryAt, "booru-cooldown");

        ArgumentNullException.ThrowIfNull(worker);
        BooruJob job = _services.OpenJob();
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
        finally
        {
            _services.CloseJob(job);
        }
    }

    public async Task<BooruPostResult> ReadPostAsync(IPluginWorkerClient worker, SourcePostId post,
        CancellationToken cancellationToken = default)
    {
        if (post is null)
            return new BooruPostFailure(new SourcePostId(string.Empty, string.Empty), new FailureDetail("invalid_post", InvalidPostIdMessage));
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

    private static bool IsValidPostId(SourcePostId post, SourceDescription source)
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
