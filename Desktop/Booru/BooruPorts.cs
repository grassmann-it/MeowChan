using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

// Ports of the Danbooru UI. The view models depend only on these small interfaces; production
// adapters call the host/queue/scheduler, tests and the smoke supply fakes. A port is the ONLY
// way the UI can cause a provider request, a queue entry or a watcher change: none of the
// models owns a timer that polls, and none retries on its own.

internal enum BooruCallStatus
{
    Ok,
    /// <summary>The provider could not be reached (no network).</summary>
    Offline,
    /// <summary>HTTP 429 / provider throttling; <see cref="BooruCallResult{T}.RetryAt"/> is the earliest retry.</summary>
    RateLimited,
    /// <summary>Another provider pause (host cooldown, 502/503 deferral).</summary>
    Cooldown,
    /// <summary>401/403: the public request was denied.</summary>
    Denied,
    NotFound,
    /// <summary>The host refused the query or id before any request.</summary>
    InvalidInput,
    /// <summary>The provider is not available in this profile (plugin not enabled, no worker).</summary>
    Unavailable,
    Failed
}

/// <summary>Outcome of one provider call. Never carries raw provider or exception text.</summary>
internal sealed record BooruCallResult<T>(BooruCallStatus Status, T? Value = default,
    DateTimeOffset? RetryAt = null, string? Code = null, int? HttpStatus = null)
{
    internal static BooruCallResult<T> Ok(T value) => new(BooruCallStatus.Ok, value);
    internal static BooruCallResult<T> Of(BooruCallStatus status, string? code = null,
        DateTimeOffset? retryAt = null, int? httpStatus = null) => new(status, default, retryAt, code, httpStatus);
}

/// <summary>One bounded provider result page: at most 20 posts, newest first as the provider orders them.</summary>
internal sealed record BooruResultPage(string Query, int Page, ImmutableArray<BooruPostView> Posts, bool IsEnd);

internal interface IBooruSearchPort
{
    string ProviderId { get; }
    string ProviderName { get; }

    /// <summary>One bounded search page for an already canonical query. Page is 1-based, at most 5.</summary>
    Task<BooruCallResult<BooruResultPage>> SearchAsync(string canonicalQuery, int page, CancellationToken cancellationToken);

    /// <summary>One post read for the one-shot flow.</summary>
    Task<BooruCallResult<BooruPostView>> ReadPostAsync(string postId, CancellationToken cancellationToken);
}

internal enum BooruLoadStatus { Queued, AlreadyQueued, AlreadyLoaded, NoOriginal, Restricted, Offline, Failed, RefusalPending }

internal sealed record BooruLoadResult(BooruLoadStatus Status, string? Code = null);

/// <summary>
/// Hand-over to the existing download queue (never a second queue). The port must keep the
/// original variant strict, deduplicate by provider post identity and persist provenance.
/// </summary>
internal interface IBooruQueuePort
{
    Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken);

    /// <summary>
    /// The same hand-over for a search result, with the canonical query that found the post kept as the finding's
    /// provenance. Ports that store no query ignore it; a one-shot post link passes none.
    /// </summary>
    Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, string? query,
        CancellationToken cancellationToken) => LoadOriginalAsync(post, origin, cancellationToken);

    /// <summary>Durably cancels a queued or in-flight original request without cancelling unrelated queue work.</summary>
    Task<bool> CancelOriginalAsync(string postId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Explicitly re-admits the stored original claim after a cancellation or failed admission.</summary>
    Task<BooruLoadResult> RetryOriginalAsync(string postId, CancellationToken cancellationToken) =>
        Task.FromResult(new BooruLoadResult(BooruLoadStatus.Failed, "retry_unavailable"));

    /// <summary>Persists an outstanding refusal without admitting a download.</summary>
    Task<BooruLoadResult> SaveOriginalRefusalAsync(string postId, CancellationToken cancellationToken) =>
        Task.FromResult(new BooruLoadResult(BooruLoadStatus.RefusalPending));

    /// <summary>Local queue/library state by provider post id; unknown ids may be omitted.</summary>
    Task<IReadOnlyDictionary<string, BooruLocalState>> GetLocalStatesAsync(
        IReadOnlyList<string> postIds, CancellationToken cancellationToken);

    /// <summary>Raised when queued work finishes or local states change; may come from any thread.</summary>
    event Action? LocalStatesChanged;
}

/// <summary>
/// Preview bytes through the safe media path. The UI never loads a URL itself: a null result
/// shows "preview unavailable" and no request is made by the view.
/// </summary>
internal interface IBooruPreviewPort
{
    Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken);
}

internal enum BooruWatcherCommandStatus
{
    Done,
    /// <summary>The domain refused the transition; <see cref="BooruWatcherCommandResult.Code"/> is its rejection code.</summary>
    Rejected,
    RateLimited,
    Cooldown,
    Offline,
    Failed
}

internal sealed record BooruWatcherCommandResult(BooruWatcherCommandStatus Status, string? Code = null,
    int NewPosts = 0, int Queued = 0, DateTimeOffset? RetryAt = null)
{
    internal static BooruWatcherCommandResult Success(int newPosts = 0, int queued = 0) =>
        new(BooruWatcherCommandStatus.Done, null, newPosts, queued);
}

/// <summary>One finished watcher check that put new originals into the download queue; shown once as a notification.</summary>
internal sealed record BooruWatcherCheckNotice(string WatcherId, string Query, int Queued);

/// <summary>
/// Explicit watcher commands. "Check now" is one bounded check requested by the user; pause and
/// removal must stop all scheduler requests for that watcher. Removal never deletes local media.
/// </summary>
internal interface IBooruWatcherPort
{
    event Action? Changed;

    /// <summary>
    /// Raised once per finished automatic check that queued at least one new original. Checks the user started with
    /// "Check now" report in the window itself and are not raised. May come from any thread; ports without it never raise.
    /// </summary>
    event Action<BooruWatcherCheckNotice>? CheckQueued { add { } remove { } }

    Task<IReadOnlyList<BooruWatcherView>> ListAsync(CancellationToken cancellationToken);
    /// <summary>Stores a watcher for the canonical query, starting "from now" (no backfill).</summary>
    Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken);
    Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken);
    Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken);
    Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken);
    Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken);

    /// <summary>Clears the Danbooru provider cooldown for this host only; it never starts a check or queues an original.</summary>
    Task<BooruWatcherCommandResult> ResetProviderCooldownAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, "rejected"));

    /// <summary>
    /// Accepts a gap for a watcher whose remaining new posts cannot be reached: the watcher is removed (local media
    /// stay) and a new one for the same query starts from now. Ports without the operation reject it.
    /// </summary>
    Task<BooruWatcherCommandResult> RestartFromNowAsync(string watcherId, CancellationToken cancellationToken) =>
        Task.FromResult(new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, "rejected"));

    /// <summary>Size of the local catalog against its configured limit; null when the port keeps no local catalog.</summary>
    Task<BooruCatalogUsageView?> GetCatalogUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BooruCatalogUsageView?>(null);
}

/// <summary>A port read that failed with a stable code (for example a scheduler fault); the view shows the code's text.</summary>
internal sealed class BooruPortException(string code) : Exception("Booru port failure: " + code)
{
    internal string Code { get; } = code;
}

/// <summary>The port bundle one Danbooru window works with.</summary>
internal sealed class BooruWorkbenchServices
{
    internal BooruWorkbenchServices(IBooruSearchPort search, IBooruQueuePort queue, IBooruWatcherPort watchers,
        IBooruPreviewPort previews, TimeProvider? clock = null, BooruLinkActions? links = null)
    {
        Search = search ?? throw new ArgumentNullException(nameof(search));
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        Watchers = watchers ?? throw new ArgumentNullException(nameof(watchers));
        Previews = previews ?? throw new ArgumentNullException(nameof(previews));
        Clock = clock ?? TimeProvider.System;
        Links = links ?? BooruLinkActions.System;
    }

    internal IBooruSearchPort Search { get; }
    internal IBooruQueuePort Queue { get; }
    internal IBooruWatcherPort Watchers { get; }
    internal IBooruPreviewPort Previews { get; }
    internal TimeProvider Clock { get; }
    internal BooruLinkActions Links { get; }
    internal SourceDescription? Source { get; init; }
}
