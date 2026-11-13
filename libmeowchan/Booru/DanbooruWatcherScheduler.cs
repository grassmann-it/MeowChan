using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>
/// B5 watcher execution. One serial owner for every watcher mutation (no compare-and-set needed in the
/// store, a Removed tombstone is never overwritten) and at most one running check across all watchers,
/// which is the conservative scheduler form of "at most one active metadata send per provider". The
/// host transport behind <see cref="IDanbooruWatcherSearchPort"/> still enforces the 1 s start spacing,
/// the per-job send budget, the 429 cooldown and the redirect/body limits; nothing here sends HTTP.
/// <para>
/// Paging: the first page of a traversal is the newest page of the query; every later page is requested
/// below the traversal cursor (the lowest post id seen so far, Danbooru <c>page=b&lt;id&gt;</c>). Posts that
/// arrive during a traversal therefore never shift a page, and a continuation has no page limit: one check
/// reads at most the watcher's page budget and the next check continues below the stored cursor.
/// </para>
/// <para>
/// Per page the order is: provider page → domain transition (not yet committed) → hand every new post
/// to the shared queue → persist the advanced watcher. A crash in between re-emits the same posts and
/// the B4 admission dedupes them (at-least-once, never a silent loss). Pause/Remove/Shutdown cancel the
/// running check; a result that arrives for a watcher that is no longer the checking instance is
/// discarded and never persisted.
/// </para>
/// </summary>
internal sealed class DanbooruWatcherScheduler : IAsyncDisposable
{
    private static readonly TimeSpan MaximumSingleWait = TimeSpan.FromHours(6);

    private readonly object _sync = new();
    private readonly BooruCatalogStore _catalog;
    private readonly IDanbooruWatcherSearchPort _search;
    private readonly IDanbooruWatcherAdmissionPort _admission;
    private readonly TimeProvider _time;
    private readonly DanbooruWatcherSchedulerOptions _options;
    private readonly Dictionary<string, DanbooruWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DanbooruWatcherCheckSummary> _lastChecks = new(StringComparer.Ordinal);
    private readonly List<ManualRequest> _manual = new();
    private TaskCompletionSource _wake = NewSignal();
    private DateTimeOffset? _backoffUntil;
    private DateTimeOffset? _lastAutomaticStart;
    private RunningCheck? _running;
    private CancellationTokenSource? _lifetime;
    private Task _loop = Task.CompletedTask;
    private bool _loaded;
    private bool _stopping;
    private string? _fault;

    internal DanbooruWatcherScheduler(BooruCatalogStore catalog, IDanbooruWatcherSearchPort search,
        IDanbooruWatcherAdmissionPort admission, TimeProvider? timeProvider = null,
        DanbooruWatcherSchedulerOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new DanbooruWatcherSchedulerOptions();
        if (_options.Interval < DanbooruWatcherSchedule.MinimumInterval)
            throw new ArgumentOutOfRangeException(nameof(options), "The watcher interval is below the provider start spacing.");
        if (_options.CheckStartSpacing < TimeSpan.Zero || _options.MinimumDeferral < TimeSpan.Zero
            || _options.UnavailableRetry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_search.PageSize is < 1 or > DanbooruWatcher.MaximumPageSize)
            throw new ArgumentException("The watcher search port declares invalid page limits.", nameof(search));
    }

    /// <summary>Raised outside locks after any visible change; read <see cref="GetSnapshot"/> afterwards.</summary>
    internal event Action? Changed;

    internal TimeSpan Interval => _options.Interval;

    /// <summary>
    /// Loads every stored watcher, recovers checks interrupted by a crash, restores the provider cooldown
    /// from persisted Cooldown watchers and starts the background loop. A catalog that cannot be read or a
    /// record that cannot be restored is a visible fault: nothing is scheduled and nothing is sent.
    /// </summary>
    internal Task StartAsync(CancellationToken cancellationToken = default)
    {
        Load(cancellationToken);
        lock (_sync)
        {
            if (_lifetime != null || _stopping || _fault != null) return Task.CompletedTask;
            _lifetime = new CancellationTokenSource();
            CancellationToken token = _lifetime.Token;
            _loop = Task.Run(() => RunLoopAsync(token), CancellationToken.None);
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads and recovers stored watchers without starting the loop (startup ordering, tests). Calling it
    /// again is a no-op.
    /// </summary>
    internal void Load(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_loaded || _stopping) return;
            _loaded = true;
            BooruCatalogSnapshot snapshot;
            try { snapshot = _catalog.Read(); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException
                or InvalidOperationException or ObjectDisposedException)
            {
                _fault = "catalog_unreadable";
                return;
            }

            List<DanbooruWatcher> restored = new();
            foreach (BooruWatcherRecord record in snapshot.Watchers)
            {
                if (record.State == nameof(DanbooruWatcherState.Removed))
                {
                    _removed.Add(record.WatcherId);
                    continue;
                }
                try { restored.Add(DanbooruWatcherRecordMapper.FromRecord(record)); }
                catch (InvalidDataException)
                {
                    _fault = "watcher_record_invalid";
                    _watchers.Clear();
                    return;
                }
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (DanbooruWatcher watcher in restored)
            {
                // A check interrupted by a crash returns to its resume target with its cursor intact,
                // before any selection could see a stale Checking watcher (preflight G14).
                DanbooruWatcher recovered = DanbooruWatcher.RecoverAfterRestart(watcher);
                if (!ReferenceEquals(recovered, watcher))
                {
                    if (!TryPersistLocked(recovered, out _))
                    {
                        _fault = "persist_failed";
                        _watchers.Clear();
                        return;
                    }
                }
                _watchers[recovered.WatcherId] = DanbooruWatcherRecordMapper.CompactKnownPostIds(recovered);
                // The transport cooldown lives in memory only; a persisted provider retry time keeps every
                // watcher quiet across a restart (preflight G5).
                if (recovered.State == DanbooruWatcherState.Cooldown && recovered.CooldownUntilUtc is { } until && until > now)
                    _backoffUntil = Max(_backoffUntil, until);
            }
        }
    }

    /// <summary>Cancels a running check (its stored progress stays consistent) and stops the loop.</summary>
    internal async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        Task loop;
        List<ManualRequest> pending;
        lock (_sync)
        {
            _stopping = true;
            lifetime = _lifetime;
            loop = _loop;
            pending = _manual.ToList();
            _manual.Clear();
            _wake.TrySetResult();
        }
        foreach (ManualRequest request in pending)
            request.Completion.TrySetResult(Ended(DanbooruWatcherCheckKind.Cancelled, request.RequestedAtUtc,
                "scheduler_stopped", manual: true));
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lock (_sync)
        {
            if (ReferenceEquals(_lifetime, lifetime))
            {
                _lifetime?.Dispose();
                _lifetime = null;
            }
        }
        RaiseChanged();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ---- Commands (B6 surface) ------------------------------------------------------------------------

    /// <summary>Creates an Active watcher for an explicit Danbooru query. The first check only stores a baseline.</summary>
    internal DanbooruWatcherCommandResult AddWatcher(string query)
    {
        DanbooruWatcherCommandResult result;
        lock (_sync)
        {
            if (Unavailable() is { } code) return new(false, code, null);
            string id = "w-" + Guid.NewGuid().ToString("N");
            if (!DanbooruWatcher.TryCreate(id, query, _time.GetUtcNow(), out DanbooruWatcher? watcher, out _)
                || watcher is null)
                return new(false, "invalid_query", null);
            DanbooruWatcher? existing = _watchers.Values.FirstOrDefault(item => item.CanonicalQuery == watcher.CanonicalQuery);
            if (existing != null) return new(false, "watcher_exists", StatusLocked(existing, _time.GetUtcNow()));
            if (!TryPersistLocked(watcher, out DanbooruWatcher? stored)) return new(false, "persist_failed", null);
            result = new(true, null, StatusLocked(stored!, _time.GetUtcNow()));
            _wake.TrySetResult();
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Requests one bounded manual check, independent of the regular interval. It never runs in parallel
    /// with another check and never bypasses the provider cooldown. Paused watchers may be checked
    /// manually; they stay Paused afterwards.
    /// </summary>
    internal Task<DanbooruWatcherCheckSummary> CheckNowAsync(string watcherId)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Task<DanbooruWatcherCheckSummary> task;
        lock (_sync)
        {
            if (Unavailable() is { } unavailable) return Task.FromResult(Rejected(unavailable, now, true));
            if (_removed.Contains(watcherId)) return Task.FromResult(Rejected("watcher_removed", now, true));
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return Task.FromResult(Rejected("watcher_not_found", now, true));
            if (_running?.WatcherId == watcherId) return Task.FromResult(Rejected("check_already_running", now, true));
            ManualRequest? queued = _manual.FirstOrDefault(item => item.WatcherId == watcherId);
            if (queued != null) return queued.Completion.Task;
            if (Gate(watcher, now, manual: true) is { } rejection) return Task.FromResult(rejection);
            ManualRequest request = new(watcherId, now);
            _manual.Add(request);
            task = request.Completion.Task;
            _wake.TrySetResult();
        }
        RaiseChanged();
        return task;
    }

    internal DanbooruWatcherCommandResult Pause(string watcherId) =>
        Mutate(watcherId, watcher => DanbooruWatcher.Pause(watcher), cancelRunning: true);

    internal DanbooruWatcherCommandResult Resume(string watcherId) =>
        Mutate(watcherId, watcher => DanbooruWatcher.Resume(watcher, _time.GetUtcNow()), cancelRunning: false);

    /// <summary>
    /// Stops observation. Only the watcher record becomes a Removed tombstone; posts, findings, local
    /// media, source tags and private tags are untouched, and queued downloads follow the queue contract.
    /// </summary>
    internal DanbooruWatcherCommandResult Remove(string watcherId) =>
        Mutate(watcherId, watcher => DanbooruWatcher.Remove(watcher), cancelRunning: true);

    /// <summary>
    /// Explicitly accepts a gap: removes the watcher (non-destructively) and creates a new one for the
    /// same query with a fresh baseline from now, so posts between the old progress and now are skipped.
    /// </summary>
    internal DanbooruWatcherCommandResult RestartFromNow(string watcherId)
    {
        string query;
        lock (_sync)
        {
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return new(false, _removed.Contains(watcherId) ? "watcher_removed" : "watcher_not_found", null);
            query = watcher.CanonicalQuery;
        }
        DanbooruWatcherCommandResult removed = Remove(watcherId);
        return removed.Accepted ? AddWatcher(query) : removed;
    }

    internal DanbooruWatcherSchedulerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset? cooldown = ProviderCooldownLocked(now);
            return new DanbooruWatcherSchedulerSnapshot(
                _lifetime != null && !_stopping,
                _options.Interval,
                cooldown,
                _running?.WatcherId,
                _fault,
                _watchers.Values
                    .OrderBy(item => item.CreatedAtUtc)
                    .ThenBy(item => item.WatcherId, StringComparer.Ordinal)
                    .Select(item => StatusLocked(item, now))
                    .ToArray());
        }
    }

    internal DanbooruWatcherStatus? GetStatus(string watcherId)
    {
        lock (_sync)
            return _watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher)
                ? StatusLocked(watcher, _time.GetUtcNow())
                : null;
    }

    // ---- Loop ----------------------------------------------------------------------------------------

    /// <summary>
    /// Runs at most one due check now (manual requests first). Returns null when nothing ran. The
    /// background loop uses the same step; tests may drive it directly without the loop.
    /// </summary>
    internal async Task<DanbooruWatcherCheckSummary?> RunNextAsync(CancellationToken cancellationToken = default)
    {
        (string WatcherId, bool Manual, ManualRequest? Request)? work;
        CancellationTokenSource? lifetime;
        lock (_sync)
        {
            if (_stopping || !_loaded || _fault != null || _running != null) return null;
            work = PickWorkLocked(_time.GetUtcNow(), out _);
            lifetime = _lifetime;
        }
        if (work is not { } item) return null;
        // A direct step must also stop when the scheduler stops, otherwise an out-of-loop check keeps
        // sending after StopAsync (and after the profile lease is gone).
        using CancellationTokenSource linked = lifetime is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        DanbooruWatcherCheckSummary summary = await RunCheckAsync(item.WatcherId, item.Manual, linked.Token)
            .ConfigureAwait(false);
        item.Request?.Completion.TrySetResult(summary);
        RaiseChanged();
        return summary;
    }

    private async Task RunLoopAsync(CancellationToken stop)
    {
        // Give commands issued immediately after StartAsync a scheduling turn to enqueue manual
        // requests before the first automatic selection.  Without this hand-off, the background
        // loop can claim a due watcher before the caller's synchronous CheckNowAsync calls reach
        // _manual, defeating the documented manual-first startup semantics.
        await Task.Yield();
        while (!stop.IsCancellationRequested)
        {
            Task wake;
            DateTimeOffset? wakeAt;
            (string WatcherId, bool Manual, ManualRequest? Request)? work;
            lock (_sync)
            {
                if (_stopping) return;
                if (_wake.Task.IsCompleted) _wake = NewSignal();
                wake = _wake.Task;
                work = PickWorkLocked(_time.GetUtcNow(), out wakeAt);
            }
            try
            {
                if (work is { } item)
                {
                    DanbooruWatcherCheckSummary summary = await RunCheckAsync(item.WatcherId, item.Manual, stop)
                        .ConfigureAwait(false);
                    item.Request?.Completion.TrySetResult(summary);
                    RaiseChanged();
                    continue;
                }
                await WaitAsync(wake, wakeAt, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A defect must never turn into a request loop: keep the state, back off visibly.
                lock (_sync) _backoffUntil = Max(_backoffUntil, _time.GetUtcNow() + _options.UnavailableRetry);
                RaiseChanged();
            }
        }
    }

    private async Task WaitAsync(Task wake, DateTimeOffset? wakeAt, CancellationToken stop)
    {
        TimeSpan delay = wakeAt is { } at ? at - _time.GetUtcNow() : Timeout.InfiniteTimeSpan;
        if (wakeAt is not null && delay <= TimeSpan.Zero) return;
        if (delay > MaximumSingleWait) delay = MaximumSingleWait;
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task delayed = Task.Delay(delay, _time, timer.Token);
        await Task.WhenAny(wake, delayed).ConfigureAwait(false);
        timer.Cancel();
        stop.ThrowIfCancellationRequested();
    }

    private (string WatcherId, bool Manual, ManualRequest? Request)? PickWorkLocked(DateTimeOffset now, out DateTimeOffset? wakeAt)
    {
        wakeAt = null;
        DateTimeOffset? cooldown = ProviderCooldownLocked(now);

        // Manual requests first, FIFO. A cooldown that started after the request was accepted rejects it
        // without a request instead of holding it indefinitely.
        while (_manual.Count > 0)
        {
            ManualRequest request = _manual[0];
            _manual.RemoveAt(0);
            if (!_watchers.TryGetValue(request.WatcherId, out DanbooruWatcher? watcher))
            {
                request.Completion.TrySetResult(Rejected(_removed.Contains(request.WatcherId) ? "watcher_removed" : "watcher_not_found", now, true));
                continue;
            }
            if (Gate(watcher, now, manual: true) is { } rejection)
            {
                request.Completion.TrySetResult(rejection);
                continue;
            }
            return (request.WatcherId, true, request);
        }

        if (cooldown is { } until && until > now)
        {
            wakeAt = until;
            return null;
        }
        if (_lastAutomaticStart is { } last && last + _options.CheckStartSpacing > now)
        {
            wakeAt = last + _options.CheckStartSpacing;
            return null;
        }

        DanbooruWatcher? next = DanbooruWatcherSchedule.SelectNext(_watchers.Values, now, _options.Interval,
            cooldown, null);
        if (next != null) return (next.WatcherId, false, null);

        foreach (DanbooruWatcher watcher in _watchers.Values)
        {
            DateTimeOffset? due = DanbooruWatcherSchedule.NextCheckAtUtc(watcher, _options.Interval, now);
            if (due is { } at && (wakeAt is null || at < wakeAt)) wakeAt = at;
        }
        return null;
    }

    // ---- One check -----------------------------------------------------------------------------------

    private async Task<DanbooruWatcherCheckSummary> RunCheckAsync(string watcherId, bool manual, CancellationToken stop)
    {
        DateTimeOffset started = _time.GetUtcNow();
        CheckCounters counters = new();
        CancellationTokenSource check;
        DanbooruWatcher current;
        lock (_sync)
        {
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return Rejected(_removed.Contains(watcherId) ? "watcher_removed" : "watcher_not_found", started, manual);
            if (Gate(watcher, started, manual) is { } rejection) return Record(watcherId, rejection);
            DanbooruWatcherTransition begin = DanbooruWatcher.BeginCheck(watcher, started, manual);
            if (!begin.Accepted) return Record(watcherId, Rejected(begin.RejectionCode ?? "rejected", started, manual));
            if (!TryPersistLocked(begin.Watcher, out DanbooruWatcher? stored))
                return Record(watcherId, Rejected("persist_failed", started, manual));
            current = stored!;
            check = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _running = new RunningCheck(watcherId, check);
            if (!manual) _lastAutomaticStart = started;
        }
        RaiseChanged();

        try
        {
            // One search session per check: all pages of this check share one host operation scope.
            using LazySearchSession session = new(_search);
            DanbooruWatcherCheckSummary summary = await RunPagesAsync(watcherId, current, manual, started, counters, check,
                session).ConfigureAwait(false);
            return Record(watcherId, summary);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_running?.Cancellation, check)) _running = null;
            }
            check.Dispose();
        }
    }

    /// <summary>Opens the check's search session on the first request, so a check that never sends opens nothing.</summary>
    private sealed class LazySearchSession(IDanbooruWatcherSearchPort port) : IDisposable
    {
        private IDanbooruWatcherSearchSession? _session;

        internal Task<DanbooruWatcherSearchOutcome> SearchAsync(string providerQuery, string? beforePostId, CancellationToken cancellationToken)
        {
            _session ??= port.OpenCheck();
            return _session.SearchAsync(providerQuery, beforePostId, cancellationToken);
        }

        public void Dispose() => _session?.Dispose();
    }

    private async Task<DanbooruWatcherCheckSummary> RunPagesAsync(string watcherId, DanbooruWatcher current,
        bool manual, DateTimeOffset started, CheckCounters counters, CancellationTokenSource check,
        LazySearchSession session)
    {
        bool establishingBaseline = !current.BaselineEstablished;
        while (true)
        {
            // The traversal page ordinal (1 = newest page) binds the domain transition; the request itself
            // names its position by cursor. A later page always has a cursor (the store and the record
            // mapper refuse anything else); without one, requesting the newest page again could declare an
            // unfinished catch-up complete, so stop visibly instead.
            int page = current.Progress.NextPage;
            string? before = current.CursorPostId;
            if (page > 1 && before is null)
            {
                lock (_sync)
                {
                    if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
                    CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "cursor_missing", _time.GetUtcNow()).Watcher);
                }
                return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "cursor_missing", manual);
            }

            DanbooruWatcherSearchOutcome outcome;
            try
            {
                check.Token.ThrowIfCancellationRequested();
                outcome = await session.SearchAsync(current.CanonicalQuery, before, check.Token).ConfigureAwait(false)
                    ?? new DanbooruWatcherSearchFailed("search_result_missing");
            }
            catch (OperationCanceledException) when (check.IsCancellationRequested)
            {
                return CancelRun(watcherId, current, started, counters, manual);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = new DanbooruWatcherSearchFailed("search_port_fault");
            }
            counters.Pages++;

            DanbooruWatcherTransition applied;
            IReadOnlyList<BooruPostClaim> claims = Array.Empty<BooruPostClaim>();
            lock (_sync)
            {
                if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
                DateTimeOffset now = _time.GetUtcNow();
                switch (outcome)
                {
                    case DanbooruWatcherSearchSnapshot snapshot:
                        if (snapshot.Posts is null || snapshot.Posts.Any(post => post is null
                                || post.SourceId != DanbooruWatcher.ProviderId))
                        {
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "foreign_post", now).Watcher);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "foreign_post", manual);
                        }
                        if (!string.Equals(snapshot.BeforePostId, before, StringComparison.Ordinal))
                        {
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "page_binding_mismatch", now).Watcher);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "page_binding_mismatch", manual);
                        }
                        DanbooruSearchPageObservation observation = new(DanbooruWatcher.ProviderId, snapshot.Query,
                            page, snapshot.Posts.Select(post => post.PostId).ToArray(), snapshot.IsEnd);
                        applied = DanbooruWatcher.ApplyPage(current, observation, now);
                        if (!applied.Accepted)
                        {
                            string code = SafeCode(applied.RejectionCode, "page_rejected");
                            CommitWithoutRequestLocked(applied.Watcher);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, code, manual);
                        }
                        claims = snapshot.Posts;
                        break;
                    case DanbooruWatcherSearchDeferred deferred:
                    {
                        DateTimeOffset retryAt = Max(deferred.RetryAtUtc.ToUniversalTime(), now + _options.MinimumDeferral);
                        if (_search.GetProviderCooldownUntilUtc() is { } transport) retryAt = Max(retryAt, transport);
                        _backoffUntil = Max(_backoffUntil, retryAt);
                        string reason = SafeCode(deferred.ReasonCode, "provider_deferred");
                        CommitWithoutRequestLocked(DanbooruWatcher.ApplyDeferred(current, retryAt, reason, now).Watcher);
                        return Summary(DanbooruWatcherCheckKind.Deferred, started, counters, reason, manual, retryAt);
                    }
                    case DanbooruWatcherSearchUnavailable unavailable:
                    {
                        _backoffUntil = Max(_backoffUntil, now + _options.UnavailableRetry);
                        CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher);
                        return Summary(DanbooruWatcherCheckKind.SourceUnavailable, started, counters,
                            SafeCode(unavailable.Code, "source_unavailable"), manual, _backoffUntil);
                    }
                    case DanbooruWatcherSearchFailed failed:
                    {
                        string code = SafeCode(failed.Code, "provider_error");
                        CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, code, now).Watcher);
                        return Summary(DanbooruWatcherCheckKind.Failed, started, counters, code, manual);
                    }
                    default:
                        CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "search_result_unknown", now).Watcher);
                        return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "search_result_unknown", manual);
                }
            }

            // Hand-off before the watcher advances (preflight G4). Oldest new post first.
            foreach (string postId in applied.AddedPostIds.Reverse())
            {
                BooruPostClaim claim = claims.First(item => item.PostId == postId);
                counters.NewPosts++;
                DanbooruWatcherAdmissionResult result;
                try
                {
                    check.Token.ThrowIfCancellationRequested();
                    result = await _admission.AdmitAsync(claim, watcherId, current.CanonicalQuery, check.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (check.IsCancellationRequested)
                {
                    return CancelRun(watcherId, current, started, counters, manual);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The post could not be recorded: do not advance past it. The watcher stops visibly and
                    // the same page is replayed (idempotently) after Resume or a manual check.
                    lock (_sync)
                    {
                        if (IsCurrentLocked(watcherId, current))
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "admission_failed", _time.GetUtcNow()).Watcher);
                    }
                    return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "admission_failed", manual);
                }
                counters.Count(result);
            }

            lock (_sync)
            {
                // Paused/removed while the posts were handed off: discard; the page replays idempotently.
                if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
                if (!TryPersistLocked(applied.Watcher, out DanbooruWatcher? stored))
                {
                    // Never keep running on progress that is not durable.
                    _watchers[watcherId] = DanbooruWatcher.ApplyError(current, "persist_failed", _time.GetUtcNow()).Watcher;
                    return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "persist_failed", manual);
                }
                current = stored!;
            }
            RaiseChanged();

            if (current.State != DanbooruWatcherState.Checking)
            {
                DanbooruWatcherCheckKind kind = establishingBaseline ? DanbooruWatcherCheckKind.BaselineEstablished
                    : current.Progress.BudgetLimited ? DanbooruWatcherCheckKind.PartialBudgetLimited
                    : DanbooruWatcherCheckKind.Completed;
                return Summary(kind, started, counters, null, manual);
            }
            if (check.IsCancellationRequested) return CancelRun(watcherId, current, started, counters, manual);
        }
    }

    private DanbooruWatcherCheckSummary CancelRun(string watcherId, DanbooruWatcher current, DateTimeOffset started,
        CheckCounters counters, bool manual)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
            // Shutdown/cancel: back to the resume target with the cursor unchanged. If the profile already
            // refuses writes, the stored Checking record is recovered identically on the next start.
            CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher);
        }
        return Cancelled(started, counters, manual, "cancelled");
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private DanbooruWatcherCommandResult Mutate(string watcherId, Func<DanbooruWatcher, DanbooruWatcherTransition> transition,
        bool cancelRunning)
    {
        CancellationTokenSource? cancel = null;
        List<ManualRequest> dropped = new();
        DanbooruWatcherCommandResult result;
        lock (_sync)
        {
            if (Unavailable() is { } code) return new(false, code, null);
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return new(false, _removed.Contains(watcherId) ? "watcher_removed" : "watcher_not_found", null);
            DanbooruWatcherTransition applied = transition(watcher);
            if (!applied.Accepted) return new(false, applied.RejectionCode, StatusLocked(watcher, _time.GetUtcNow()));
            if (!TryPersistLocked(applied.Watcher, out DanbooruWatcher? stored))
                return new(false, "persist_failed", StatusLocked(watcher, _time.GetUtcNow()));
            DateTimeOffset now = _time.GetUtcNow();
            if (stored!.State == DanbooruWatcherState.Removed)
            {
                _watchers.Remove(watcherId);
                _removed.Add(watcherId);
                _lastChecks.Remove(watcherId);
            }
            if (cancelRunning)
            {
                if (_running?.WatcherId == watcherId) cancel = _running.Cancellation;
                dropped.AddRange(_manual.Where(item => item.WatcherId == watcherId));
                _manual.RemoveAll(item => item.WatcherId == watcherId);
            }
            result = new(true, null, stored.State == DanbooruWatcherState.Removed ? null : StatusLocked(stored, now));
            _wake.TrySetResult();
        }
        try { cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
        foreach (ManualRequest request in dropped)
            request.Completion.TrySetResult(Ended(DanbooruWatcherCheckKind.Cancelled, request.RequestedAtUtc,
                "superseded", manual: true));
        RaiseChanged();
        return result;
    }

    /// <summary>Gates that must hold before a check may send anything. Null means "may start".</summary>
    private DanbooruWatcherCheckSummary? Gate(DanbooruWatcher watcher, DateTimeOffset now, bool manual)
    {
        if (ProviderCooldownLocked(now) is { } until && until > now)
            return Rejected("provider_cooldown", now, manual, until);
        DanbooruWatcherTransition preview = DanbooruWatcher.BeginCheck(watcher, now, manual);
        if (!preview.Accepted)
            return Rejected(preview.RejectionCode ?? "rejected", now, manual,
                preview.RejectionCode == "cooldown_active" ? watcher.CooldownUntilUtc : null);
        if (!manual && watcher.State == DanbooruWatcherState.Error)
            // Error needs Resume or an explicit manual check (preflight G11).
            return Rejected("watcher_error", now, manual);
        return null;
    }

    private DateTimeOffset? ProviderCooldownLocked(DateTimeOffset now)
    {
        DateTimeOffset? cooldown = _backoffUntil is { } backoff && backoff > now ? backoff : null;
        if (_search.GetProviderCooldownUntilUtc() is { } transport && transport > now) cooldown = Max(cooldown, transport);
        return cooldown;
    }

    private string? Unavailable() => _stopping ? "scheduler_stopped" : !_loaded ? "scheduler_not_started" : _fault;

    private bool IsCurrentLocked(string watcherId, DanbooruWatcher current) =>
        _watchers.TryGetValue(watcherId, out DanbooruWatcher? live) && ReferenceEquals(live, current);

    private void CommitWithoutRequestLocked(DanbooruWatcher watcher)
    {
        // A terminal transition of a check must reach memory even when the profile no longer accepts
        // writes (shutdown); the last durable record then recovers to the same resume target.
        if (!TryPersistLocked(watcher, out _))
            _watchers[watcher.WatcherId] = DanbooruWatcherRecordMapper.CompactKnownPostIds(watcher);
    }

    private bool TryPersistLocked(DanbooruWatcher watcher, out DanbooruWatcher? stored)
    {
        stored = null;
        DanbooruWatcher compact = DanbooruWatcherRecordMapper.CompactKnownPostIds(watcher);
        try
        {
            _catalog.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(compact), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        if (compact.State == DanbooruWatcherState.Removed) _watchers.Remove(compact.WatcherId);
        else _watchers[compact.WatcherId] = compact;
        stored = compact;
        return true;
    }

    private DanbooruWatcherStatus StatusLocked(DanbooruWatcher watcher, DateTimeOffset now)
    {
        DateTimeOffset? providerCooldown = ProviderCooldownLocked(now);
        DateTimeOffset? next = DanbooruWatcherSchedule.NextCheckAtUtc(watcher, _options.Interval, now);
        if (next is { } due && providerCooldown is { } cooling && cooling > due) next = cooling;
        DanbooruWatcherActivity activity;
        if (_running?.WatcherId == watcher.WatcherId || watcher.State == DanbooruWatcherState.Checking)
            activity = DanbooruWatcherActivity.Checking;
        else if (_manual.Any(item => item.WatcherId == watcher.WatcherId))
            activity = DanbooruWatcherActivity.QueuedForCheck;
        else
            activity = watcher.State switch
            {
                DanbooruWatcherState.Paused => DanbooruWatcherActivity.Paused,
                DanbooruWatcherState.Error => DanbooruWatcherActivity.Error,
                DanbooruWatcherState.Cooldown => DanbooruWatcherActivity.Cooldown,
                _ when providerCooldown is not null => DanbooruWatcherActivity.Cooldown,
                _ => DanbooruWatcherActivity.Scheduled
            };
        _lastChecks.TryGetValue(watcher.WatcherId, out DanbooruWatcherCheckSummary? last);
        return new DanbooruWatcherStatus(
            watcher.WatcherId,
            watcher.CanonicalQuery,
            watcher.State,
            watcher.ResumeState,
            activity,
            watcher.CreatedAtUtc,
            watcher.LastCheckedAtUtc,
            next,
            watcher.State == DanbooruWatcherState.Cooldown ? watcher.CooldownUntilUtc : providerCooldown,
            watcher.ErrorCode,
            watcher.BaselineEstablished,
            watcher.CursorPostId is not null,
            watcher.Progress.BudgetLimited,
            watcher.Progress.NextPage,
            last);
    }

    private DanbooruWatcherCheckSummary Record(string watcherId, DanbooruWatcherCheckSummary summary)
    {
        lock (_sync)
        {
            if (_watchers.ContainsKey(watcherId)) _lastChecks[watcherId] = summary;
        }
        return summary;
    }

    private static DanbooruWatcherCheckSummary Rejected(string code, DateTimeOffset at, bool manual, DateTimeOffset? retryAt = null) =>
        new(DanbooruWatcherCheckKind.Rejected, at, at, 0, 0, 0, 0, 0, 0, 0, code, retryAt, manual);

    private DanbooruWatcherCheckSummary Ended(DanbooruWatcherCheckKind kind, DateTimeOffset started, string code, bool manual) =>
        new(kind, started, _time.GetUtcNow(), 0, 0, 0, 0, 0, 0, 0, code, null, manual);

    private DanbooruWatcherCheckSummary Cancelled(DateTimeOffset started, CheckCounters counters, bool manual, string code) =>
        Summary(DanbooruWatcherCheckKind.Cancelled, started, counters, code, manual);

    private DanbooruWatcherCheckSummary Summary(DanbooruWatcherCheckKind kind, DateTimeOffset started, CheckCounters counters,
        string? code, bool manual, DateTimeOffset? retryAt = null) =>
        new(kind, started, _time.GetUtcNow(), counters.Pages, counters.NewPosts, counters.Queued,
            counters.AlreadyQueuedOrLinked, counters.Unavailable, counters.NotAdmitted, counters.KeptCancelled,
            code, retryAt, manual);

    private static string SafeCode(string? code, string fallback)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return fallback;
        foreach (char character in code)
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'))
                return fallback;
        return code;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right) =>
        left is { } value && value >= right ? value : right;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* Observers never change scheduling. */ }
    }

    private sealed record RunningCheck(string WatcherId, CancellationTokenSource Cancellation);

    private sealed class ManualRequest(string watcherId, DateTimeOffset requestedAtUtc)
    {
        internal string WatcherId { get; } = watcherId;
        internal DateTimeOffset RequestedAtUtc { get; } = requestedAtUtc;
        internal TaskCompletionSource<DanbooruWatcherCheckSummary> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CheckCounters
    {
        internal int Pages;
        internal int NewPosts;
        internal int Queued;
        internal int AlreadyQueuedOrLinked;
        internal int Unavailable;
        internal int NotAdmitted;
        internal int KeptCancelled;

        internal void Count(DanbooruWatcherAdmissionResult result)
        {
            switch (result)
            {
                case DanbooruWatcherAdmissionResult.Queued: Queued++; break;
                case DanbooruWatcherAdmissionResult.AlreadyQueued:
                case DanbooruWatcherAdmissionResult.AlreadyLinked: AlreadyQueuedOrLinked++; break;
                case DanbooruWatcherAdmissionResult.Unavailable: Unavailable++; break;
                case DanbooruWatcherAdmissionResult.NotAdmitted: NotAdmitted++; break;
                case DanbooruWatcherAdmissionResult.KeptCancelled: KeptCancelled++; break;
            }
        }
    }
}
