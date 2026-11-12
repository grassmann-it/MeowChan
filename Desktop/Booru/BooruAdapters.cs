using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

// Thin adapters from APIs that already exist on the branch to the UI ports. They only CALL those APIs
// and translate their results; no queue, storage or watcher logic lives here.

/// <summary>Translates host/worker results into the UI's call results. Never forwards provider message text.</summary>
internal static class BooruSdkMapper
{
    internal const string ThrottledReason = "provider-throttled";

    internal static BooruCallResult<BooruResultPage> Map(BooruSearchResult result) => result switch
    {
        BooruSearchSnapshot snapshot => BooruCallResult<BooruResultPage>.Ok(new BooruResultPage(
            snapshot.Page.Query, snapshot.Page.Page,
            snapshot.Page.Posts.Select(BooruPostView.From).ToImmutableArray(), snapshot.Page.IsEnd)),
        BooruSearchDeferred deferred => Deferred<BooruResultPage>(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
        BooruSearchFailure failure => Failure<BooruResultPage>(failure.Failure),
        _ => BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Failed, "unknown_result")
    };

    internal static BooruCallResult<BooruPostView> Map(BooruPostResult result) => result switch
    {
        BooruPostSnapshot snapshot => BooruCallResult<BooruPostView>.Ok(BooruPostView.From(snapshot.Post)),
        BooruPostNotFound notFound => BooruCallResult<BooruPostView>.Of(BooruCallStatus.NotFound, "not-found", httpStatus: notFound.HttpStatusCode),
        BooruPostDeferred deferred => Deferred<BooruPostView>(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
        BooruPostFailure failure => Failure<BooruPostView>(failure.Failure),
        _ => BooruCallResult<BooruPostView>.Of(BooruCallStatus.Failed, "unknown_result")
    };

    private static BooruCallResult<T> Deferred<T>(DateTimeOffset retryAt, string reason, int? http) =>
        BooruCallResult<T>.Of(http == 429 || reason == ThrottledReason ? BooruCallStatus.RateLimited : BooruCallStatus.Cooldown,
            reason, retryAt, http);

    private static BooruCallResult<T> Failure<T>(FailureDetail failure)
    {
        BooruCallStatus status = failure.HttpStatusCode is 401 or 403 || failure.Code == "access-denied" ? BooruCallStatus.Denied
            : failure.HttpStatusCode == 404 || failure.Code == "not-found" ? BooruCallStatus.NotFound
            : failure.Code is "http-network" or "http-timeout" ? BooruCallStatus.Offline
            : failure.Code is "capability" ? BooruCallStatus.Unavailable
            : failure.Code is BooruSearchService.InvalidQueryCode or BooruSearchService.InvalidPostIdCode or "invalid_page"
                or "invalid-request" or "post-id" ? BooruCallStatus.InvalidInput
            : BooruCallStatus.Failed;
        return BooruCallResult<T>.Of(status, failure.Code, failure.RetryAt, failure.HttpStatusCode);
    }
}

/// <summary>
/// Search port over the host's validated <see cref="BooruSearchService"/>. The worker is resolved per call
/// so a disabled or restarted plugin shows as "unavailable" instead of a stale client.
/// </summary>
internal sealed class BooruHostSearchAdapter : IBooruSearchPort
{
    private readonly BooruSearchService _service;
    private readonly Func<IPluginWorkerClient?> _worker;

    internal BooruHostSearchAdapter(BooruSearchService service, Func<IPluginWorkerClient?> worker,
        string providerId = DanbooruPostReference.ProviderId, string providerName = "Danbooru")
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        ProviderId = providerId;
        ProviderName = providerName;
    }

    public string ProviderId { get; }
    public string ProviderName { get; }

    public async Task<BooruCallResult<BooruResultPage>> SearchAsync(string canonicalQuery, int page, CancellationToken cancellationToken)
    {
        IPluginWorkerClient? worker = _worker();
        if (worker is null) return BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Unavailable, "no-worker");
        return BooruSdkMapper.Map(await _service.SearchAsync(worker, ProviderId, canonicalQuery, page, cancellationToken).ConfigureAwait(false));
    }

    public async Task<BooruCallResult<BooruPostView>> ReadPostAsync(string postId, CancellationToken cancellationToken)
    {
        IPluginWorkerClient? worker = _worker();
        if (worker is null) return BooruCallResult<BooruPostView>.Of(BooruCallStatus.Unavailable, "no-worker");
        return BooruSdkMapper.Map(await _service.ReadPostAsync(worker, new SourcePostId(ProviderId, postId), cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Read-only projection of the additive booru catalog. It supplies the "already in your library" state and
/// the stored watchers; the counts are lower bounds from the stored findings, because the catalog does not
/// know the live queue. Commands (create, check, pause, resume, remove) belong to the scheduler, not here.
/// </summary>
internal static class BooruCatalogReadMapper
{
    internal static IReadOnlyDictionary<string, BooruLocalState> LocalStates(BooruCatalogSnapshot snapshot,
        string sourceId, IReadOnlyList<string> postIds)
    {
        HashSet<string> wanted = new(postIds, StringComparer.Ordinal);
        Dictionary<string, BooruLocalState> states = new(StringComparer.Ordinal);
        foreach (BooruPostRecord post in snapshot.Posts)
            if (post.SourceId == sourceId && wanted.Contains(post.PostId) && post.LocalAssetId is not null)
                states[post.PostId] = BooruLocalState.Loaded;
        return states;
    }

    internal static ImmutableArray<BooruWatcherView> Watchers(BooruCatalogSnapshot snapshot, string sourceId)
    {
        Dictionary<string, (int Hits, int Loaded)> hits = new(StringComparer.Ordinal);
        foreach (BooruPostRecord post in snapshot.Posts.Where(post => post.SourceId == sourceId))
            foreach (string watcherId in post.Findings.Where(finding => finding.Kind == BooruFindingKind.Watcher
                && finding.WatcherId is not null).Select(finding => finding.WatcherId!).Distinct(StringComparer.Ordinal))
            {
                (int found, int loaded) = hits.GetValueOrDefault(watcherId);
                hits[watcherId] = (found + 1, loaded + (post.LocalAssetId is null ? 0 : 1));
            }
        return snapshot.Watchers
            .Where(record => record.SourceId == sourceId)
            .Select(record =>
            {
                (int found, int loaded) = hits.GetValueOrDefault(record.WatcherId);
                return new BooruWatcherView(record.WatcherId, record.Query, Status(record.State), record.CreatedAtUtc,
                    record.LastCheckedAtUtc, null, record.CooldownUntilUtc, record.ErrorCode, found, loaded,
                    record.BaselineEstablished);
            })
            .Where(view => view.Status != BooruWatcherStatus.Removed)
            .ToImmutableArray();
    }

    internal static BooruWatcherStatus Status(string? state) =>
        Enum.TryParse(state, ignoreCase: true, out DanbooruWatcherState parsed) ? FromDomain(parsed) : BooruWatcherStatus.Error;

    internal static BooruWatcherStatus FromDomain(DanbooruWatcherState state) => state switch
    {
        DanbooruWatcherState.Active => BooruWatcherStatus.Active,
        DanbooruWatcherState.Paused => BooruWatcherStatus.Paused,
        DanbooruWatcherState.Checking => BooruWatcherStatus.Checking,
        DanbooruWatcherState.Cooldown => BooruWatcherStatus.Cooldown,
        DanbooruWatcherState.Error => BooruWatcherStatus.Error,
        _ => BooruWatcherStatus.Removed
    };

    /// <summary>Projection of the domain aggregate for the scheduler adapter (next check and counts come from it).</summary>
    internal static BooruWatcherView View(DanbooruWatcher watcher, DateTimeOffset? nextCheckAt, int hitCount, int queuedCount) =>
        new(watcher.WatcherId, watcher.CanonicalQuery, FromDomain(watcher.State), watcher.CreatedAtUtc,
            watcher.LastCheckedAtUtc, nextCheckAt, watcher.CooldownUntilUtc, watcher.ErrorCode, hitCount, queuedCount,
            watcher.BaselineEstablished, watcher.Progress.BudgetLimited);
}

/// <summary>Local-state lookups over a catalog read; the read itself is the store's blocking call.</summary>
internal sealed class BooruCatalogLocalStateReader
{
    private readonly Func<BooruCatalogSnapshot?> _read;
    private readonly string _sourceId;

    internal BooruCatalogLocalStateReader(Func<BooruCatalogSnapshot?> read, string sourceId = DanbooruPostReference.ProviderId)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _sourceId = sourceId;
    }

    internal Task<IReadOnlyDictionary<string, BooruLocalState>> GetAsync(IReadOnlyList<string> postIds, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyDictionary<string, BooruLocalState>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot? snapshot = _read();
            return snapshot is null ? new Dictionary<string, BooruLocalState>() : BooruCatalogReadMapper.LocalStates(snapshot, _sourceId, postIds);
        }, cancellationToken);
}
