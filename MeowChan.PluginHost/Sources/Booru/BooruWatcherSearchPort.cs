using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>
/// B5 search port: one bounded Danbooru page per call through the existing host façade
/// (<see cref="BooruSearchService"/> → worker → <see cref="BooruHostHttp"/>). It adds no HTTP, pacing,
/// retry or cooldown logic of its own; it only classifies results for the core watcher scheduler.
/// <list type="bullet">
/// <item>snapshot → validated core claims (<see cref="BooruClaimMapper"/>), newest first as delivered;</item>
/// <item>deferred (429 cooldown, 502/503 after the host retry) → provider retry time;</item>
/// <item>transient transport failures (network, timeout, job deadline) → retry time without a provider value;</item>
/// <item>worker/plugin not available → "unavailable" (no watcher state change);</item>
/// <item>everything else → non-retryable failure (3xx, 401/403, 404/410, oversize, malformed, refused).</item>
/// </list>
/// </summary>
public sealed class BooruWatcherSearchPort : IDanbooruWatcherSearchPort
{
    private const string SourceId = DanbooruPostReference.ProviderId;

    private static readonly HashSet<string> UnavailableCodes = new(StringComparer.Ordinal)
    {
        "capability", "stopped", "worker-exit", "restart-limit", "unresponsive", "start", "capacity",
        "memory-limit", "temporary-limit", "temporary-budget", "source_unavailable", "intervention-required",
        "timeout", "worker_unavailable"
    };

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "http-network", "http-timeout", "booru-budget"
    };

    private readonly BooruSearchService _search;
    private readonly BooruHostServices _services;
    private readonly Func<CancellationToken, Task<IPluginWorkerClient?>> _worker;
    private readonly TimeProvider _time;

    /// <param name="search">The host façade bound to <paramref name="services"/>.</param>
    /// <param name="services">The booru host services whose transport owns the provider cooldown.</param>
    /// <param name="worker">Returns the running Danbooru worker, or null while it is not available.</param>
    /// <param name="timeProvider">Clock for transient retry times (defaults to the system clock).</param>
    public BooruWatcherSearchPort(BooruSearchService search, BooruHostServices services,
        Func<CancellationToken, Task<IPluginWorkerClient?>> worker, TimeProvider? timeProvider = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _time = timeProvider ?? TimeProvider.System;
    }

    int IDanbooruWatcherSearchPort.MaximumPage => BooruHostHttp.MaximumSearchPage;

    int IDanbooruWatcherSearchPort.PageSize => BooruSearchService.SearchLimit;

    DateTimeOffset? IDanbooruWatcherSearchPort.GetProviderCooldownUntilUtc() =>
        _services.TryGetCooldown(SourceId, out DateTimeOffset retryAt) ? retryAt : null;

    async Task<DanbooruWatcherSearchOutcome> IDanbooruWatcherSearchPort.SearchAsync(string providerQuery, int page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPluginWorkerClient? client;
        try
        {
            client = await _worker(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DanbooruWatcherSearchUnavailable("worker_unavailable");
        }
        if (client is null) return new DanbooruWatcherSearchUnavailable("worker_unavailable");

        BooruSearchResult result = await _search.SearchAsync(client, SourceId, providerQuery, page, cancellationToken)
            .ConfigureAwait(false);
        switch (result)
        {
            case BooruSearchSnapshot snapshot:
            {
                List<BooruPostClaim> claims = new(snapshot.Page.Posts.Count);
                try
                {
                    foreach (BooruPost post in snapshot.Page.Posts) claims.Add(BooruClaimMapper.Map(post));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException
                    or FormatException or OverflowException)
                {
                    return new DanbooruWatcherSearchFailed("malformed_post");
                }
                return new DanbooruWatcherSearchSnapshot(snapshot.Page.Query, snapshot.Page.Page, claims, snapshot.Page.IsEnd);
            }
            case BooruSearchDeferred deferred:
                return new DanbooruWatcherSearchDeferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
            case BooruSearchFailure failure when UnavailableCodes.Contains(failure.Failure.Code):
                return new DanbooruWatcherSearchUnavailable(failure.Failure.Code);
            case BooruSearchFailure failure when TransientCodes.Contains(failure.Failure.Code):
                // The host already retried once; wait at least the scheduler's deferral floor.
                return new DanbooruWatcherSearchDeferred(failure.Failure.RetryAt ?? _time.GetUtcNow(), failure.Failure.Code,
                    failure.Failure.HttpStatusCode);
            case BooruSearchFailure failure:
                return new DanbooruWatcherSearchFailed(failure.Failure.Code, failure.Failure.HttpStatusCode);
            default:
                return new DanbooruWatcherSearchFailed("search_result_unknown");
        }
    }
}
