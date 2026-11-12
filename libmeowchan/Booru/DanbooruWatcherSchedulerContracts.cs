using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Booru;

// B5 ports and read models. The scheduler is core code: it knows neither the plugin SDK nor HTTP.
// The host supplies the search port (bounded B1 transport + Danbooru worker) and the admission port
// (B4 shared download queue). Pacing, the single active metadata send, retries and the provider
// cooldown stay in the host transport; the scheduler only decides *which* watcher checks *when*.

/// <summary>One provider page for a watcher check. Posts are already validated core claims, newest first.</summary>
internal abstract record DanbooruWatcherSearchOutcome;

/// <summary>
/// A provider page. <paramref name="Query"/> is what the provider page claims; <paramref name="BeforePostId"/>
/// is the cursor the page answers (null for the newest page of the query).
/// </summary>
internal sealed record DanbooruWatcherSearchSnapshot(string Query, string? BeforePostId, IReadOnlyList<BooruPostClaim> Posts,
    bool IsEnd) : DanbooruWatcherSearchOutcome;

/// <summary>The provider or the host transport asked to wait (429 cooldown, 502/503 after the host retry).</summary>
internal sealed record DanbooruWatcherSearchDeferred(DateTimeOffset RetryAtUtc, string ReasonCode,
    int? HttpStatusCode = null) : DanbooruWatcherSearchOutcome;

/// <summary>A non-retryable provider/transport/validation failure (3xx, 401/403/404/410, oversize, malformed).</summary>
internal sealed record DanbooruWatcherSearchFailed(string Code, int? HttpStatusCode = null) : DanbooruWatcherSearchOutcome;

/// <summary>
/// The search path itself is not available (plugin disabled, worker not running). This is not a
/// provider answer: the check is cancelled without changing the watcher and retried later.
/// </summary>
internal sealed record DanbooruWatcherSearchUnavailable(string Code) : DanbooruWatcherSearchOutcome;

internal interface IDanbooruWatcherSearchPort
{
    /// <summary>The fixed number of posts the transport requests per page.</summary>
    int PageSize { get; }

    /// <summary>The provider-wide cooldown currently known to the transport, if any. Never sends.</summary>
    DateTimeOffset? GetProviderCooldownUntilUtc();

    /// <summary>
    /// Opens the operation scope of exactly one watcher check. Every page of that check is requested through
    /// the returned session, so the host can bound the whole check (request count, lifetime) as one operation
    /// instead of once per page. The caller disposes the session when the check ends, whatever the outcome.
    /// </summary>
    IDanbooruWatcherSearchSession OpenCheck();
}

/// <summary>One watcher check's search operation. Pages of one check share one host job lifetime.</summary>
internal interface IDanbooruWatcherSearchSession : IDisposable
{
    /// <summary>
    /// Exactly one bounded provider page: the newest posts of the query when <paramref name="beforePostId"/> is
    /// null, otherwise the posts with a lower id than <paramref name="beforePostId"/>, newest first (Danbooru
    /// <c>page=b&lt;id&gt;</c>). A cursor names its position, so posts that arrive meanwhile never shift it, and
    /// there is no page limit for a continuation. Caller cancellation must propagate as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<DanbooruWatcherSearchOutcome> SearchAsync(string providerQuery, string? beforePostId, CancellationToken cancellationToken);
}

/// <summary>What the shared queue did with one newly found post.</summary>
internal enum DanbooruWatcherAdmissionResult
{
    Queued,
    AlreadyQueued,
    AlreadyLinked,
    /// <summary>No acceptable original (missing, restricted, no integrity claims); visible per post.</summary>
    Unavailable,
    /// <summary>The queue refused the job; visible per post, retried only on explicit request.</summary>
    NotAdmitted,
    /// <summary>The user cancelled this post's original before; the finding is kept, nothing is re-queued.</summary>
    KeptCancelled
}

internal interface IDanbooruWatcherAdmissionPort
{
    /// <summary>
    /// Persists the claim and the watcher finding, then admits the original to the shared queue. Must be
    /// idempotent per (post, watcher): a replay after a crash never creates a second job or finding.
    /// </summary>
    Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
        CancellationToken cancellationToken);
}

/// <summary>Scheduler options. The defaults are the product defaults; tests override them explicitly.</summary>
internal sealed record DanbooruWatcherSchedulerOptions
{
    /// <summary>
    /// Regular check interval for an Active watcher. Deliberately conservative and independent of the
    /// provider's 1 s start spacing: one check is at most five page requests, so the default keeps a
    /// watcher at no more than five metadata requests per hour.
    /// </summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    /// <summary>Minimum gap between two automatic check starts (any watchers), e.g. after app start.</summary>
    internal static readonly TimeSpan DefaultCheckStartSpacing = TimeSpan.FromSeconds(5);

    /// <summary>Floor for a provider-requested retry time, so a short Retry-After never causes a burst.</summary>
    internal static readonly TimeSpan DefaultMinimumDeferral = TimeSpan.FromSeconds(60);

    /// <summary>How long the scheduler waits before retrying after the search path was unavailable.</summary>
    internal static readonly TimeSpan DefaultUnavailableRetry = TimeSpan.FromMinutes(5);

    internal TimeSpan Interval { get; init; } = DefaultInterval;
    internal TimeSpan CheckStartSpacing { get; init; } = DefaultCheckStartSpacing;
    internal TimeSpan MinimumDeferral { get; init; } = DefaultMinimumDeferral;
    internal TimeSpan UnavailableRetry { get; init; } = DefaultUnavailableRetry;
}

/// <summary>What a UI should show for a watcher. Derived from the persisted domain state plus runtime facts.</summary>
internal enum DanbooruWatcherActivity
{
    /// <summary>Active and waiting for <see cref="DanbooruWatcherStatus.NextCheckAtUtc"/>.</summary>
    Scheduled,
    /// <summary>A manual "check now" is waiting for the running check of another watcher.</summary>
    QueuedForCheck,
    Checking,
    Paused,
    /// <summary>Waiting for a provider retry time (429/503) or the provider-wide cooldown.</summary>
    Cooldown,
    /// <summary>Stopped by a non-retryable failure; needs Resume or a manual check.</summary>
    Error
}

/// <summary>Result kind of one check.</summary>
internal enum DanbooruWatcherCheckKind
{
    /// <summary>The traversal reached known posts or the provider end; everything new was handed to the queue.</summary>
    Completed,
    /// <summary>The first check stored the current newest post as the baseline; nothing was downloaded.</summary>
    BaselineEstablished,
    /// <summary>
    /// The check budget was spent before reaching known posts; the cursor is stored and the next check
    /// continues below it.
    /// </summary>
    PartialBudgetLimited,
    Deferred,
    Failed,
    /// <summary>The check stopped because of pause, removal or shutdown; stored progress is unchanged.</summary>
    Cancelled,
    /// <summary>No request was sent (paused, removed, already running, cooldown...).</summary>
    Rejected,
    /// <summary>The search path was unavailable; no provider answer, watcher unchanged.</summary>
    SourceUnavailable
}

/// <summary>Runtime summary of one check. Not persisted; per-post outcomes stay in the queue/catalog.</summary>
internal sealed record DanbooruWatcherCheckSummary(
    DanbooruWatcherCheckKind Kind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    int PagesRead,
    int NewPosts,
    int Queued,
    int AlreadyQueuedOrLinked,
    int Unavailable,
    int NotAdmitted,
    int KeptCancelled,
    string? Code = null,
    DateTimeOffset? RetryAtUtc = null,
    bool Manual = false);

/// <summary>Bindable read model for one watcher (B6).</summary>
internal sealed record DanbooruWatcherStatus(
    string WatcherId,
    string Query,
    DanbooruWatcherState State,
    DanbooruWatcherState ResumeState,
    DanbooruWatcherActivity Activity,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? NextCheckAtUtc,
    DateTimeOffset? CooldownUntilUtc,
    string? ErrorCode,
    bool BaselineEstablished,
    bool HasPartialProgress,
    bool BudgetLimited,
    int NextPage,
    DanbooruWatcherCheckSummary? LastCheck);

/// <summary>Bindable read model for the whole scheduler (B6).</summary>
internal sealed record DanbooruWatcherSchedulerSnapshot(
    bool Running,
    TimeSpan Interval,
    DateTimeOffset? ProviderCooldownUntilUtc,
    string? CheckingWatcherId,
    string? FaultCode,
    IReadOnlyList<DanbooruWatcherStatus> Watchers);

/// <summary>Result of a UI command. <see cref="Code"/> is a stable rejection/diagnostic code.</summary>
internal sealed record DanbooruWatcherCommandResult(bool Accepted, string? Code, DanbooruWatcherStatus? Watcher);
