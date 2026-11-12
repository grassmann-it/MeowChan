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
    private int _disposed;

    internal BooruOriginalQueuePort(BooruOriginalQueue originals, BooruCatalogStore catalog, string sourceId)
    {
        _originals = originals ?? throw new ArgumentNullException(nameof(originals));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sourceId = sourceId;
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
        BooruOriginalAdmission admission;
        try
        {
            // The admission writes the catalog synchronously before its first await: never on the UI thread.
            admission = await Task.Run(() => _originals.AdmitAsync(claim, kind, query: Provenance(kind, query),
                cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Warning(exception, "Danbooru original admission failed for post {PostId}", post.PostId);
            return new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted);
        }
        BooruLoadResult result = Map(admission);
        // The catalog changed (claim, finding, intent) even when the queue raised nothing yet.
        RaiseLocalStatesChanged();
        return result;
    }

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
    private readonly DanbooruWatcherScheduler _scheduler;
    private readonly BooruCatalogStore? _catalog;
    private readonly string _sourceId;
    private readonly object _noticeGate = new();
    private readonly Dictionary<string, DateTimeOffset> _reported = new(StringComparer.Ordinal);
    private int _disposed;

    internal BooruSchedulerWatcherPort(DanbooruWatcherScheduler scheduler, BooruCatalogStore? catalog = null,
        string sourceId = DanbooruPostReference.ProviderId)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _catalog = catalog;
        _sourceId = sourceId;
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

    public Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken) =>
        Task.Run(() => Command(_scheduler.AddWatcher(canonicalQuery)), cancellationToken);

    public async Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken)
    {
        DanbooruWatcherCheckSummary summary = await Task.Run(() => _scheduler.CheckNowAsync(watcherId), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return Check(summary);
    }

    public Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken) =>
        Task.Run(() => Command(_scheduler.Pause(watcherId)), cancellationToken);

    public Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken) =>
        Task.Run(() => Command(_scheduler.Resume(watcherId)), cancellationToken);

    /// <summary>Removes the observation only; posts, findings and local media stay (scheduler/catalog contract).</summary>
    public Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken) =>
        Task.Run(() => Command(_scheduler.Remove(watcherId)), cancellationToken);

    public Task<BooruWatcherCommandResult> RestartFromNowAsync(string watcherId, CancellationToken cancellationToken) =>
        Task.Run(() => Command(_scheduler.RestartFromNow(watcherId)), cancellationToken);

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
        if (Volatile.Read(ref _disposed) != 0) return;
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
    private readonly BooruPreviewHttp _http;
    private readonly string _sourceId;

    internal BooruHostPreviewPort(BooruPreviewHttp http, string sourceId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _sourceId = sourceId;
    }

    public Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken) =>
        post is null || post.PreviewUrl is null || !string.Equals(post.SourceId, _sourceId, StringComparison.Ordinal)
            ? Task.FromResult<byte[]?>(null)
            : _http.LoadAsync(post.PreviewUrl, cancellationToken);
}

/// <summary>Points the window's code seam at the shared booru message catalog (B8) so codes read as sentences.</summary>
internal static class BooruCodeTexts
{
    internal static string Localized(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Texts.T("booru.failure.unknown", "?");
        if (BuiltInCatalog.English.ContainsKey("booru.watcher.problem." + code)) return BooruMessages.WatcherProblem(code);
        string text = BooruMessages.Failure(code);
        // An unlisted code keeps its own short form inside the surrounding sentence instead of a nested one.
        return text == Texts.T("booru.failure.unknown", code) ? code : text;
    }
}
