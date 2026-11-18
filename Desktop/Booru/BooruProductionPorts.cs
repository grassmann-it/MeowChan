using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using Serilog;

namespace MeowChan.Desktop.Booru;

// Production ports of the Danbooru window. They only call the B4 queue, the B3 catalog, the B5 scheduler and the
// bounded preview path and translate results; no queue, storage, scheduling or HTTP policy lives here.

/// <summary>
/// "Load original" over the shared <see cref="BooruOriginalQueue"/>: strict original only (a sample or preview never
/// stands in), one job per provider post id, provenance as a search or one-shot finding. Local states come from the
/// profile's <see cref="BooruCatalogStore"/> plus the queue's in-process outcomes.
/// </summary>
internal sealed class BooruOriginalQueuePort : IBooruQueuePort, IDisposable
{
    private readonly BooruOriginalQueue _originals;
    private readonly BooruCatalogStore _catalog;
    private readonly string _sourceId;
    private readonly BooruPortAvailabilityGate _availability;
    private int _disposed;

    internal BooruOriginalQueuePort(BooruOriginalQueue originals, BooruCatalogStore catalog, string sourceId,
        BooruPortAvailabilityGate? availability = null)
    {
        _originals = originals ?? throw new ArgumentNullException(nameof(originals));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sourceId = sourceId;
        _availability = availability ?? new BooruPortAvailabilityGate();
        _originals.OutcomeChanged += OnOutcomeChanged;
    }

    public event Action? LocalStatesChanged;

    public Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken) =>
        LoadOriginalAsync(post, origin, null, cancellationToken);

    public async Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, string? query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(post.SourceId, _sourceId, StringComparison.Ordinal))
            return new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.BooruSource);
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return Unavailable();
        // Decided from the validated provider claim before anything is stored or queued.
        if (post.OriginalAccess == BooruOriginalAccess.NotOffered) return new BooruLoadResult(BooruLoadStatus.NoOriginal);
        if (post.OriginalAccess == BooruOriginalAccess.Restricted) return new BooruLoadResult(BooruLoadStatus.Restricted);
        BooruPostClaim claim;
        try { claim = BooruClaimMapper.Map(post.Post); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.MalformedPost);
        }
        BooruFindingKind kind = origin == BooruLoadOrigin.OneShot ? BooruFindingKind.OneShot : BooruFindingKind.Search;
        BooruOriginalAdmission? admission;
        try
        {
            // The admission writes the catalog synchronously before its first await: never on the UI thread.
            admission = await Task.Run(async () =>
            {
                if (!operation.TryStart(() => _originals.AdmitAsync(claim, kind, query: Provenance(kind, query),
                    cancellationToken: operation.Token), out Task<BooruOriginalAdmission> pending)) return null;
                return await pending.ConfigureAwait(false);
            }, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (!operation.IsCurrent) { return Unavailable(); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            if (!operation.IsCurrent) return Unavailable();
            Log.Warning(exception, "Danbooru original admission failed for post {PostId}", post.PostId);
            return new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted);
        }
        // An accepted job stays with the queue even if its acknowledgement arrives after revocation. Never cancel
        // the post owner or reinterpret its durable intent here; only discard the stale UI completion.
        if (admission == null || !operation.IsCurrent) return Unavailable();
        BooruLoadResult result = Map(admission);
        // The catalog changed (claim, finding, intent) even when the queue raised nothing yet.
        RaiseLocalStatesChanged();
        return result;
    }

    public async Task<bool> CancelOriginalAsync(string postId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return false;
        try
        {
            Task<bool>? pending = null;
            bool accepted = await Task.Run(() => operation.TryStart(() => _originals.CancelAsync(postId), out pending),
                operation.Token).ConfigureAwait(false);
            if (!accepted || pending is null || !operation.IsCurrent) return false;
            bool cancelled = await pending.ConfigureAwait(false);
            if (!operation.IsCurrent) return false;
            RaiseLocalStatesChanged();
            return cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (!operation.IsCurrent) { return false; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
            or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (operation.IsCurrent) Log.Warning(exception, "Danbooru original cancellation failed for post {PostId}", postId);
            return false;
        }
    }

    public async Task<BooruLoadResult> RetryOriginalAsync(string postId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return Unavailable();
        try
        {
            Task<BooruOriginalAdmission>? pending = null;
            bool accepted = await Task.Run(() => operation.TryStart(
                () => _originals.RetryAsync(postId, operation.Token), out pending), operation.Token).ConfigureAwait(false);
            if (!accepted || pending is null || !operation.IsCurrent) return Unavailable();
            BooruOriginalAdmission admission = await pending.ConfigureAwait(false);
            if (!operation.IsCurrent) return Unavailable();
            BooruLoadResult result = Map(admission);
            RaiseLocalStatesChanged();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (!operation.IsCurrent) { return Unavailable(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
            or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (operation.IsCurrent) Log.Warning(exception, "Danbooru original retry failed for post {PostId}", postId);
            return new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted);
        }
    }

    private static BooruLoadResult Unavailable() => new(BooruLoadStatus.Failed, BooruCodes.SourceUnavailable);

    /// <summary>
    /// The query a search finding keeps: only the canonical provider query the catalog itself accepts. Anything else (a
    /// one-shot, an empty or not canonical text) is dropped rather than failing the load or storing odd text.
    /// </summary>
    internal static string? Provenance(BooruFindingKind kind, string? query) =>
        kind == BooruFindingKind.Search && !string.IsNullOrEmpty(query) && query.Length <= 1024
            && DanbooruTagQuery.TryParse(query, out DanbooruTagQuery? parsed, out _) && parsed is not null
            && string.Equals(parsed.ProviderQuery, query, StringComparison.Ordinal)
            ? query : null;

    internal static BooruLoadResult Map(BooruOriginalAdmission admission) => admission.Status switch
    {
        BooruOriginalAdmissionStatus.Queued => new BooruLoadResult(BooruLoadStatus.Queued),
        BooruOriginalAdmissionStatus.AlreadyQueued => new BooruLoadResult(BooruLoadStatus.AlreadyQueued),
        BooruOriginalAdmissionStatus.AlreadyLinked => new BooruLoadResult(BooruLoadStatus.AlreadyLoaded),
        BooruOriginalAdmissionStatus.Unavailable when admission.SelectionError == BooruOriginalSelectionError.MissingOriginal
            => new BooruLoadResult(BooruLoadStatus.NoOriginal),
        // Withheld URL, missing integrity claims or an unusable original: never substituted by a sample.
        BooruOriginalAdmissionStatus.Unavailable => new BooruLoadResult(BooruLoadStatus.Restricted, admission.SelectionError.ToString()),
        _ => new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted)
    };

    public Task<IReadOnlyDictionary<string, BooruLocalState>> GetLocalStatesAsync(IReadOnlyList<string> postIds,
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyDictionary<string, BooruLocalState>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> wanted = new(postIds, StringComparer.Ordinal);
            IReadOnlyDictionary<string, BooruOriginalDownloadOutcome> outcomes = _originals.Snapshot();
            Dictionary<string, BooruLocalState> states = new(StringComparer.Ordinal);
            foreach (BooruPostRecord record in _catalog.Read().Posts)
            {
                if (record.SourceId != _sourceId || !wanted.Contains(record.PostId)) continue;
                bool queued = record.HasPendingOriginal || outcomes.TryGetValue(record.PostId, out BooruOriginalDownloadOutcome? outcome)
                    && outcome.State == BooruOriginalDownloadState.Queued;
                states[record.PostId] = record.LocalAssetId is not null ? BooruLocalState.Loaded
                    : queued ? BooruLocalState.Queued : BooruLocalState.NotLoaded;
            }
            return states;
        }, cancellationToken);

    private void OnOutcomeChanged(BooruOriginalDownloadOutcome outcome) => RaiseLocalStatesChanged();

    private void RaiseLocalStatesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Action? observers = LocalStatesChanged;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList().Cast<Action>())
        {
            try { observer(); }
            catch (Exception exception) { Log.Error(exception, "Danbooru local-state observer failed"); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _originals.OutcomeChanged -= OnOutcomeChanged;
    }
}

/// <summary>
/// Watcher commands over the B5 <see cref="DanbooruWatcherScheduler"/>. The scheduler owns pacing, cooldown, the single
/// running check and persistence; this port maps its snapshot, command results and check summaries to the window's
/// models. "Check now" on a paused watcher runs one check and the watcher stays paused (scheduler rule).
/// </summary>
internal sealed class BooruSchedulerWatcherPort : IBooruWatcherPort, IDisposable
{
    private volatile DanbooruWatcherScheduler _scheduler;
    private readonly BooruPortAvailabilityGate _availability;
    private readonly BooruCatalogStore? _catalog;
    private readonly string _sourceId;
    private readonly Func<BooruHostHttp.CooldownReset?>? _beginTransportCooldownReset;
    private readonly object _noticeGate = new();
    private readonly Dictionary<string, DateTimeOffset> _reported = new(StringComparer.Ordinal);
    private int _disposed;

    internal BooruSchedulerWatcherPort(DanbooruWatcherScheduler scheduler, BooruCatalogStore? catalog = null,
        string sourceId = DanbooruPostReference.ProviderId, BooruPortAvailabilityGate? availability = null,
        Func<BooruHostHttp.CooldownReset?>? beginTransportCooldownReset = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _availability = availability ?? new BooruPortAvailabilityGate();
        _catalog = catalog;
        _sourceId = sourceId;
        _beginTransportCooldownReset = beginTransportCooldownReset;
        _scheduler.Changed += OnSchedulerChanged;
    }

    public event Action? Changed;

    public event Action<BooruWatcherCheckNotice>? CheckQueued;

    public Task<IReadOnlyList<BooruWatcherView>> ListAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<BooruWatcherView>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DanbooruWatcherSchedulerSnapshot snapshot = _scheduler.GetSnapshot();
            // A load fault (unreadable catalog, invalid record) means nothing is scheduled: show it, not an empty list.
            if (snapshot.FaultCode is { } fault) throw new BooruPortException(fault);
            Dictionary<string, BooruWatcherView> counts = new(StringComparer.Ordinal);
            if (_catalog != null)
            {
                try
                {
                    foreach (BooruWatcherView stored in BooruCatalogReadMapper.Watchers(_catalog.Read(), _sourceId))
                        counts[stored.WatcherId] = stored;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or ObjectDisposedException)
                {
                    // Counts are a convenience; the scheduler snapshot alone still lists every watcher.
                    Log.Warning(exception, "Danbooru watcher counts could not be read");
                }
            }
            return snapshot.Watchers.Select(status => View(status, snapshot.ProviderCooldownUntilUtc,
                counts.GetValueOrDefault(status.WatcherId))).ToArray();
        }, cancellationToken);

    internal static BooruWatcherView View(DanbooruWatcherStatus status, DateTimeOffset? providerCooldown, BooruWatcherView? stored)
    {
        BooruWatcherStatus mapped = status.Activity switch
        {
            DanbooruWatcherActivity.Checking => BooruWatcherStatus.Checking,
            DanbooruWatcherActivity.Paused => BooruWatcherStatus.Paused,
            DanbooruWatcherActivity.Cooldown => BooruWatcherStatus.Cooldown,
            DanbooruWatcherActivity.Error => BooruWatcherStatus.Error,
            _ => BooruWatcherStatus.Active
        };
        DateTimeOffset? cooldown = status.CooldownUntilUtc
            ?? (mapped == BooruWatcherStatus.Cooldown ? providerCooldown : null);
        string? error = status.ErrorCode;
        return new BooruWatcherView(status.WatcherId, status.Query, mapped, status.CreatedAtUtc, status.LastCheckedAtUtc,
            mapped is BooruWatcherStatus.Active ? status.NextCheckAtUtc : null, cooldown, error,
            stored?.HitCount ?? 0, stored?.QueuedCount ?? 0, status.BaselineEstablished, status.BudgetLimited,
            stored?.LatestPostId);
    }

    // Called only with availability closed and the old host physically drained. Escaped UI handles stay valid,
    // but no old scheduler or manual-request queue is revived; the shared OriginalQueue is never replaced.
    internal void ReplaceScheduler(DanbooruWatcherScheduler scheduler)
    {
        _scheduler.Changed -= OnSchedulerChanged;
        _scheduler = scheduler;
        lock (_noticeGate) _reported.Clear();
        if (Volatile.Read(ref _disposed) == 0) _scheduler.Changed += OnSchedulerChanged;
    }

    public Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken) =>
        Mutate(scheduler => scheduler.AddWatcher(canonicalQuery), cancellationToken);

    public async Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken)
    {
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return Unavailable();
        try
        {
            // Queue registration itself must be fenced, not merely the caller's wait. Disable also stops/drains
            // this scheduler in composition, so a cancelled wait cannot leave a queued check for re-enable.
            var registration = await Task.Run(() =>
            {
                bool accepted = operation.TryStart(() => _scheduler.CheckNowAsync(watcherId),
                    out Task<DanbooruWatcherCheckSummary> task);
                return (accepted, task);
            }, operation.Token).ConfigureAwait(false);
            if (!registration.accepted) return Unavailable();
            DanbooruWatcherCheckSummary summary = await registration.task.WaitAsync(operation.Token).ConfigureAwait(false);
            return operation.IsCurrent ? Check(summary) : Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !operation.IsCurrent)
        { return Unavailable(); }
    }

    public Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken) =>
        Mutate(scheduler => scheduler.Pause(watcherId), cancellationToken);

    public Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken) =>
        Mutate(scheduler => scheduler.Resume(watcherId), cancellationToken);

    /// <summary>Removes the observation only; posts, findings and local media stay (scheduler/catalog contract).</summary>
    public Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken) =>
        Mutate(scheduler => scheduler.Remove(watcherId), cancellationToken);

    public Task<BooruWatcherCommandResult> RestartFromNowAsync(string watcherId, CancellationToken cancellationToken) =>
        Mutate(scheduler => scheduler.RestartFromNow(watcherId), cancellationToken);

    public async Task<BooruWatcherCommandResult> ResetProviderCooldownAsync(CancellationToken cancellationToken)
    {
        // Establish the transport barrier before changing durable scheduler state. It remains held
        // until that persistence has succeeded, so no request can enter the gap between the two fences.
        if (_beginTransportCooldownReset is not { } beginTransportReset)
            return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "transport_reset_unavailable");
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return Unavailable();
        try
        {
            BooruWatcherCommandResult result = await Task.Run(() =>
            {
                BooruHostHttp.CooldownReset? transportReset = null;
                bool committed = false;
                try
                {
                    // Do not enter the availability monitor while waiting for an in-flight HTTP send:
                    // that send's lease may need the same monitor to finish and dispose. The transport
                    // barrier is still established before TryStart admits the durable scheduler mutation.
                    if (!operation.IsCurrent) return Unavailable();
                    try { transportReset = beginTransportReset(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "transport_reset_failed"); }
                    if (transportReset is null)
                        return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "transport_reset_unavailable");
                    BooruHostHttp.CooldownReset activeTransportReset = transportReset;

                    if (!operation.TryStart(() =>
                        {
                            DanbooruWatcherCommandResult scheduler = _scheduler.ResetProviderCooldown();
                            if (!scheduler.Accepted) return Command(scheduler);
                            // The host barrier is released only after every durable scheduler reset above
                            // has succeeded. A late 429 from a request admitted before the barrier remains
                            // versioned in the host transaction and cannot be erased here.
                            if (!activeTransportReset.Commit())
                                return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "transport_reset_failed");
                            committed = true;
                            return BooruWatcherCommandResult.Success();
                        }, out BooruWatcherCommandResult value)) return Unavailable();
                    return value;
                }
                finally
                {
                    if (!committed) transportReset?.Dispose();
                }
            }, operation.Token).ConfigureAwait(false);
            return operation.IsCurrent ? result : Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !operation.IsCurrent)
        { return Unavailable(); }
    }

    private async Task<BooruWatcherCommandResult> Mutate(Func<DanbooruWatcherScheduler, DanbooruWatcherCommandResult> command,
        CancellationToken cancellationToken)
    {
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 || operation == null) return Unavailable();
        try
        {
            BooruWatcherCommandResult result = await Task.Run(() =>
                operation.TryStart(() => Command(command(_scheduler)), out BooruWatcherCommandResult value)
                    ? value : Unavailable(), operation.Token).ConfigureAwait(false);
            return operation.IsCurrent ? result : Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !operation.IsCurrent)
        { return Unavailable(); }
    }

    private static BooruWatcherCommandResult Unavailable() =>
        new(BooruWatcherCommandStatus.Failed, BooruCodes.SourceUnavailable);

    /// <summary>Read-only: size, post count and configured limit of the profile's catalog (cached; no parse while unchanged).</summary>
    public Task<BooruCatalogUsageView?> GetCatalogUsageAsync(CancellationToken cancellationToken) => _catalog is not { } catalog
        ? Task.FromResult<BooruCatalogUsageView?>(null)
        : Task.Run<BooruCatalogUsageView?>(() => BooruCatalogUsageView.From(catalog.GetUsage()), cancellationToken);

    internal static BooruWatcherCommandResult Command(DanbooruWatcherCommandResult result) => result.Accepted
        ? BooruWatcherCommandResult.Success()
        : new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, result.Code);

    internal static BooruWatcherCommandResult Check(DanbooruWatcherCheckSummary summary) => summary.Kind switch
    {
        DanbooruWatcherCheckKind.Completed or DanbooruWatcherCheckKind.BaselineEstablished or DanbooruWatcherCheckKind.PartialBudgetLimited
            => BooruWatcherCommandResult.Success(summary.NewPosts, summary.Queued),
        DanbooruWatcherCheckKind.Deferred => new BooruWatcherCommandResult(DeferredStatus(summary.Code), summary.Code,
            summary.NewPosts, summary.Queued, summary.RetryAtUtc),
        DanbooruWatcherCheckKind.Rejected when summary.Code is BooruCodes.CooldownActive or BooruCodes.ProviderCooldown
            && summary.RetryAtUtc is { } retry
            => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Cooldown, summary.Code, RetryAt: retry),
        DanbooruWatcherCheckKind.Rejected => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, summary.Code),
        DanbooruWatcherCheckKind.Failed when summary.Code is "http-network" or "http-timeout"
            => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Offline, summary.Code),
        // A build that does not bind to this SDK is enabled and running, not "disabled or not running": say what it is.
        DanbooruWatcherCheckKind.SourceUnavailable when summary.Code == BooruText.SdkMismatchCode
            => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, BooruCodes.PluginSdkMismatch),
        // The worker's own reason (stopped, worker-exit, …) is diagnostic; the user sees "plugin not available".
        DanbooruWatcherCheckKind.SourceUnavailable => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed,
            BooruCodes.SourceUnavailable),
        _ => new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, summary.Code ?? summary.Kind.ToString())
    };

    private static BooruWatcherCommandStatus DeferredStatus(string? code) => code switch
    {
        BooruCodes.BooruCooldown or BooruCodes.ProviderThrottled => BooruWatcherCommandStatus.RateLimited,
        "http-network" or "http-timeout" => BooruWatcherCommandStatus.Offline,
        _ => BooruWatcherCommandStatus.Cooldown
    };

    private void OnSchedulerChanged()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_availability.IsAvailable) return;
        try { Changed?.Invoke(); }
        catch (Exception exception) { Log.Error(exception, "Danbooru watcher observer failed"); }
        ReportFinishedChecks();
    }

    /// <summary>
    /// The scheduler publishes each watcher's last check summary in its status. A summary not seen before that queued new
    /// originals is reported once (one notice per check). Manual checks are skipped: the window already shows their result,
    /// and a check that queued nothing, failed or was deferred has nothing to announce.
    /// </summary>
    private void ReportFinishedChecks()
    {
        // Summaries are marked as seen even while nobody listens, so a later observer never gets an old check.
        List<BooruWatcherCheckNotice> notices = new();
        try
        {
            lock (_noticeGate)
            {
                DanbooruWatcherSchedulerSnapshot snapshot = _scheduler.GetSnapshot();
                foreach (DanbooruWatcherStatus status in snapshot.Watchers)
                {
                    if (status.LastCheck is not { } check) continue;
                    if (_reported.TryGetValue(status.WatcherId, out DateTimeOffset seen) && check.FinishedAtUtc <= seen) continue;
                    _reported[status.WatcherId] = check.FinishedAtUtc;
                    if (!check.Manual && check.Queued > 0)
                        notices.Add(new BooruWatcherCheckNotice(status.WatcherId, status.Query, check.Queued));
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Danbooru watcher check summaries could not be read");
            return;
        }
        Action<BooruWatcherCheckNotice>? observers = CheckQueued;
        if (observers is null) return;
        foreach (BooruWatcherCheckNotice notice in notices)
        {
            foreach (Action<BooruWatcherCheckNotice> observer in observers.GetInvocationList().Cast<Action<BooruWatcherCheckNotice>>())
            {
                try { observer(notice); }
                catch (Exception exception) { Log.Error(exception, "Danbooru watcher notice observer failed"); }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scheduler.Changed -= OnSchedulerChanged;
    }
}

/// <summary>Preview bytes only through <see cref="BooruPreviewHttp"/> (declared preview hosts, GET, no redirects, bounded).</summary>
internal sealed class BooruHostPreviewPort : IBooruPreviewPort
{
    private readonly Func<BooruPreviewHttp> _http;
    private readonly string _sourceId;
    private readonly BooruPortAvailabilityGate _availability;

    internal BooruHostPreviewPort(BooruPreviewHttp http, string sourceId, BooruPortAvailabilityGate? availability = null,
        Func<BooruPreviewHttp>? currentHttp = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = currentHttp ?? (() => http);
        _sourceId = sourceId;
        _availability = availability ?? new BooruPortAvailabilityGate();
    }

    public async Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (post is null || post.PreviewUrl is null || !string.Equals(post.SourceId, _sourceId, StringComparison.Ordinal))
            return null;
        using BooruPortAvailabilityGate.Lease? operation = _availability.TryEnter(cancellationToken);
        if (operation == null) return null;
        try
        {
            if (!operation.TryStart(() => _http().LoadAsync(post.PreviewUrl, operation.Token), out Task<byte[]?> pending))
                return null;
            byte[]? bytes = await pending.ConfigureAwait(false);
            return operation.IsCurrent ? bytes : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !operation.IsCurrent)
        { return null; }
    }
}

/// <summary>Points the window's code seam at the shared booru message catalog (B8) so codes read as sentences.</summary>
internal static class BooruCodeTexts
{
    internal static string Localized(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Texts.T("booru.failure.unknownNoCode");
        if (BuiltInCatalog.English.ContainsKey("booru.watcher.problem." + code)) return BooruMessages.WatcherProblem(code);
        string text = BooruMessages.Failure(code);
        // An unlisted code keeps its own short form inside the surrounding sentence instead of a nested one.
        return text == Texts.T("booru.failure.unknown", code) ? code : text;
    }
}
