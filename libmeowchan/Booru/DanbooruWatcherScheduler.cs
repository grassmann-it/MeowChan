using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

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
/// <para>
/// Profile transitions (F2): with a <see cref="BooruProducerLifecycle"/> every check, watcher command and restart
/// recovery write runs as a counted <see cref="BooruProducerUse"/> of the open generation and writes with that
/// generation's admission. Nothing starts while the family is not open. A cut cancels the running check; it then
/// writes only its terminal transition (resume target with the last durable cursor, or a provider deferral that must
/// survive a restart) and never page progress of the cut generation. Queued manual requests are answered with
/// <c>profile_suspended</c>. A new generation wakes the loop.
/// </para>
/// <para>
/// Global session pause (F3): a transient gate, never persisted. While it is set no automatic check starts; a running
/// automatic check finishes the page in flight (its posts are admitted and its progress persisted) and stops before
/// the next page. An explicit manual check (Check now) still runs, with the same provider-cooldown and watcher rules
/// as without the pause (product decision 14.11.2026); only a profile transition blocks it. Individual pause, cursor,
/// high-water mark and cooldown stay as they are; clearing the gate wakes the loop.
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
    private readonly BooruProducerLifecycle? _lifecycle;
    private readonly Dictionary<string, DanbooruWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DanbooruWatcherCheckSummary> _lastChecks = new(StringComparer.Ordinal);
    private readonly List<ManualRequest> _manual = new();
    private TaskCompletionSource _wake = NewSignal();
    private DateTimeOffset? _backoffUntil;
    // Quiet time for automatic starts after a local write failure (disk full, lease lost, rights). Kept apart
    // from the provider backoff on purpose (CX2-R1): it is not a provider pause, it never blocks a manual check
    // (which then fails honestly with persist_failed), and the next successful write ends it.
    private DateTimeOffset? _persistBackoffUntil;
    private DateTimeOffset? _lastAutomaticStart;
    private RunningCheck? _running;
    // The watcher picked for the next check, held from the pick until the check has finished. The pick and
    // the registration of _running are separate critical sections; without this claim a manual request (or
    // a direct step) in between sees neither a running check nor a queued one and starts a second check.
    private string? _claimed;
    private CancellationTokenSource? _lifetime;
    private Task _loop = Task.CompletedTask;
    private bool _loaded;
    private bool _stopping;
    private bool _sessionPaused;
    private string? _fault;

    internal const string SessionPausedCode = "session_paused";
    internal const string ProfileSuspendedCode = "profile_suspended";

    internal DanbooruWatcherScheduler(BooruCatalogStore catalog, IDanbooruWatcherSearchPort search,
        IDanbooruWatcherAdmissionPort admission, TimeProvider? timeProvider = null,
        DanbooruWatcherSchedulerOptions? options = null, BooruProducerLifecycle? lifecycle = null)
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
        _lifecycle = lifecycle;
        if (_lifecycle != null) _lifecycle.Changed += OnLifecycleChanged;
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
    /// again is a no-op. With a lifecycle the restart-recovery writes run as <paramref name="use"/> (or a use
    /// entered here); a family that is not open refuses the load, which stays retryable.
    /// </summary>
    internal void Load(CancellationToken cancellationToken = default, BooruProducerUse? use = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BooruProducerUse? owned = null;
        try
        {
        lock (_sync)
        {
            if (_loaded || _stopping) return;
            if (_lifecycle != null && use is null)
                use = owned = _lifecycle.TryEnter()
                    ?? throw new InvalidOperationException("The Danbooru producer family is not open; the watchers cannot load now.");
            ProfileAdmissionToken? admission = use?.Admission;
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
                    if (!TryPersistLocked(recovered, out _, admission))
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
        finally { owned?.Dispose(); }
    }

    /// <summary>Cancels a running check (its stored progress stays consistent) and stops the loop.</summary>
    internal async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        Task loop;
        RunningCheck? running;
        List<ManualRequest> pending;
        if (_lifecycle != null) _lifecycle.Changed -= OnLifecycleChanged;
        lock (_sync)
        {
            _stopping = true;
            lifetime = _lifetime;
            loop = _loop;
            running = _running;
            pending = _manual.ToList();
            _manual.Clear();
            _wake.TrySetResult();
        }
        foreach (ManualRequest request in pending)
            request.Completion.TrySetResult(Ended(DanbooruWatcherCheckKind.Cancelled, request.RequestedAtUtc,
                "scheduler_stopped", manual: true));
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        // A direct RunNextAsync may have started before the loop (or without one), so its token need not
        // be linked to this lifetime. Cancel and drain that check itself before the profile is released:
        // its cancellation recovery and session disposal must have finished first. No check can register
        // after _stopping was set (RunCheckAsync re-checks it under the same lock).
        try { running?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        try { await loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        if (running != null) await running.Completion.Task.ConfigureAwait(false);
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
            using BooruProducerUse? use = _lifecycle?.TryEnter();
            if (_lifecycle != null && use is null) return new(false, ProfileSuspendedCode, null);
            string id = "w-" + Guid.NewGuid().ToString("N");
            if (!DanbooruWatcher.TryCreate(id, query, _time.GetUtcNow(), out DanbooruWatcher? watcher, out _)
                || watcher is null)
                return new(false, "invalid_query", null);
            DanbooruWatcher? existing = _watchers.Values.FirstOrDefault(item => item.CanonicalQuery == watcher.CanonicalQuery);
            if (existing != null) return new(false, "watcher_exists", StatusLocked(existing, _time.GetUtcNow()));
            if (!TryPersistLocked(watcher, out DanbooruWatcher? stored, use?.Admission)) return new(false, "persist_failed", null);
            result = new(true, null, StatusLocked(stored!, _time.GetUtcNow()));
            _wake.TrySetResult();
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Requests one bounded manual check, independent of the regular interval. It never runs in parallel
    /// with another check and never bypasses the provider cooldown. Paused watchers may be checked
    /// manually; they stay Paused afterwards. The global session pause does not block it (it only stops automatic
    /// checks); a profile exclusive transition does (<c>profile_suspended</c>, nothing is sent).
    /// </summary>
    internal Task<DanbooruWatcherCheckSummary> CheckNowAsync(string watcherId)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Task<DanbooruWatcherCheckSummary> task;
        lock (_sync)
        {
            if (Unavailable() is { } unavailable) return Task.FromResult(Rejected(unavailable, now, true));
            if (_removed.Contains(watcherId)) return Task.FromResult(Rejected("watcher_removed", now, true));
            if (HoldCodeLocked(manual: true) is { } hold) return Task.FromResult(Rejected(hold, now, true));
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return Task.FromResult(Rejected("watcher_not_found", now, true));
            if (_running?.WatcherId == watcherId || _claimed == watcherId)
                return Task.FromResult(Rejected("check_already_running", now, true));
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

    /// <summary>True while the transient global session pause gate is set.</summary>
    internal bool SessionPaused { get { lock (_sync) return _sessionPaused; } }

    /// <summary>
    /// Sets the transient global session pause gate (F3). Set it from the confirmed session pause before the first
    /// start and after every confirmed change. Pausing lets a running automatic check stop after its page in flight;
    /// queued and running manual checks are not affected. Resuming wakes the loop. Nothing is persisted: individual
    /// pause, cursor, high-water mark and cooldown are untouched. The clock is not read here, so a composition may
    /// set the gate before any clock-dependent work starts.
    /// </summary>
    internal void SetSessionPaused(bool paused)
    {
        lock (_sync)
        {
            if (_sessionPaused == paused) return;
            _sessionPaused = paused;
            _wake.TrySetResult();
        }
        RaiseChanged();
    }

    private void OnLifecycleChanged()
    {
        List<ManualRequest> dropped = new();
        lock (_sync)
        {
            if (_lifecycle is { IsOpen: false })
            {
                // A queued request must not wait for an exclusive operation (or for ever after a recovery fence).
                dropped.AddRange(_manual);
                _manual.Clear();
            }
            _wake.TrySetResult();
        }
        DateTimeOffset now = dropped.Count == 0 ? default : _time.GetUtcNow();
        foreach (ManualRequest request in dropped)
            request.Completion.TrySetResult(Rejected(ProfileSuspendedCode, now, true));
        RaiseChanged();
    }

    /// <summary>
    /// Transient reasons why no check may start now; never persisted. Null means "no hold". A profile transition holds
    /// every check; the global session pause holds automatic checks only.
    /// </summary>
    private string? HoldCodeLocked(bool manual) =>
        _lifecycle is { IsOpen: false } ? ProfileSuspendedCode : _sessionPaused && !manual ? SessionPausedCode : null;

    private static bool IsHoldCode(string? code) => code is SessionPausedCode or ProfileSuspendedCode;

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
            if (_stopping || !_loaded || _fault != null || _running != null || _claimed != null) return null;
            work = PickWorkLocked(_time.GetUtcNow(), out _);
            if (work is { } picked) _claimed = picked.WatcherId;
            lifetime = _lifetime;
        }
        if (work is not { } item) return null;
        DanbooruWatcherCheckSummary? summary = null;
        try
        {
            // A direct step must also stop when the scheduler stops, otherwise an out-of-loop check keeps
            // sending after StopAsync (and after the profile lease is gone).
            using CancellationTokenSource linked = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            try
            {
                summary = await RunCheckAsync(item.WatcherId, item.Manual, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                CompleteRequest(item.Request, summary, linked.Token);
            }
        }
        finally
        {
            ReleaseClaim(item.WatcherId);
        }
        RaiseChanged();
        return summary;
    }

    /// <summary>Frees the single check slot and wakes the loop, which does not pick while a slot is taken.</summary>
    private void ReleaseClaim(string watcherId)
    {
        lock (_sync)
        {
            if (_claimed == watcherId) _claimed = null;
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// A dequeued manual request is answered exactly once, also when the check itself throws; otherwise the
    /// caller of <see cref="CheckNowAsync"/> (the UI button) waits for ever (CX-R1).
    /// </summary>
    private void CompleteRequest(ManualRequest? request, DanbooruWatcherCheckSummary? summary, CancellationToken stop)
    {
        if (request is null) return;
        request.Completion.TrySetResult(summary ?? (stop.IsCancellationRequested
            ? Ended(DanbooruWatcherCheckKind.Cancelled, request.RequestedAtUtc, "scheduler_stopped", manual: true)
            : Ended(DanbooruWatcherCheckKind.Failed, request.RequestedAtUtc, "check_faulted", manual: true)));
    }

    private async Task RunLoopAsync(CancellationToken stop)
    {
        // Manual-first is an ordering guarantee only for requests queued before StartAsync (CheckNowAsync
        // works after Load); a request issued after StartAsync races the first pick by design. When the
        // loop wins, the claim answers the request with check_already_running instead of a second check.
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
                if (_running != null || _claimed != null)
                {
                    // A direct step holds the single check slot. Picking now would either start a second
                    // check or, rejected by RunCheckAsync, spin on a due watcher; its ReleaseClaim wakes us.
                    work = null;
                    wakeAt = null;
                }
                else
                {
                    work = PickWorkLocked(_time.GetUtcNow(), out wakeAt);
                    if (work is { } picked) _claimed = picked.WatcherId;
                }
            }
            try
            {
                if (work is { } item)
                {
                    DanbooruWatcherCheckSummary? summary = null;
                    try
                    {
                        summary = await RunCheckAsync(item.WatcherId, item.Manual, stop).ConfigureAwait(false);
                    }
                    finally
                    {
                        CompleteRequest(item.Request, summary, stop);
                        ReleaseClaim(item.WatcherId);
                    }
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
        if (HoldCodeLocked(manual: true) is { } hold)
        {
            // Profile transition: nothing starts; the lifecycle change wakes the loop.
            foreach (ManualRequest request in _manual) request.Completion.TrySetResult(Rejected(hold, now, true));
            _manual.Clear();
            return null;
        }
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

        // Global session pause: manual requests above still run; nothing starts automatically until it is cleared,
        // which wakes the loop.
        if (_sessionPaused) return null;

        if (cooldown is { } until && until > now)
        {
            wakeAt = _persistBackoffUntil is { } local && local > until ? local : until;
            return null;
        }
        if (_persistBackoffUntil is { } quiet && quiet > now)
        {
            wakeAt = quiet;
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
        RunningCheck running;
        DanbooruWatcher current;
        BooruProducerUse? use = null;
        lock (_sync)
        {
            // Re-checked here because the pick (RunNextAsync, loop) and this registration are separate
            // critical sections (CX-R10). StopAsync may have run in between: nothing may start after it.
            // The claim keeps two picks apart for all known callers; the _running check keeps "at most one
            // running check" true for any future caller as well.
            if (_stopping) return Rejected("scheduler_stopped", started, manual);
            if (_running != null) return Rejected("check_already_running", started, manual);
            if (!_watchers.TryGetValue(watcherId, out DanbooruWatcher? watcher))
                return Rejected(_removed.Contains(watcherId) ? "watcher_removed" : "watcher_not_found", started, manual);
            if (Gate(watcher, started, manual) is { } rejection)
                // A transient hold of an automatic pick is not a check result of this watcher.
                return !manual && IsHoldCode(rejection.Code) ? rejection : Record(watcherId, rejection);
            DanbooruWatcherTransition begin = DanbooruWatcher.BeginCheck(watcher, started, manual);
            if (!begin.Accepted) return Record(watcherId, Rejected(begin.RejectionCode ?? "rejected", started, manual));
            // The single counted ownership of this check in the open generation; entered under the same lock as the
            // registration, so a cut either precedes it (nothing starts) or drains it.
            use = _lifecycle?.TryEnter();
            if (_lifecycle != null && use is null)
            {
                DanbooruWatcherCheckSummary suspended = Rejected(ProfileSuspendedCode, started, manual);
                return manual ? Record(watcherId, suspended) : suspended;
            }
            if (!TryPersistLocked(begin.Watcher, out DanbooruWatcher? stored, use?.Admission))
            {
                use?.Dispose();
                // An automatic start that cannot be made durable must not be selected again on the
                // next loop turn.  The watcher remains due, so without this backoff a full catalog or a
                // transient disk/lease failure spins and serializes the same state over and over without
                // ever reaching the provider.  It is a local backoff, not the provider's (CX2-R1).
                if (!manual)
                    _persistBackoffUntil = Max(_persistBackoffUntil, _time.GetUtcNow() + _options.UnavailableRetry);
                return Record(watcherId, Rejected("persist_failed", started, manual));
            }
            current = stored!;
            check = use is null
                ? CancellationTokenSource.CreateLinkedTokenSource(stop)
                : CancellationTokenSource.CreateLinkedTokenSource(stop, use.Cut);
            _running = running = new RunningCheck(watcherId, check, use);
            if (!manual) _lastAutomaticStart = started;
        }
        RaiseChanged();

        try
        {
            // One search session per check: all pages of this check share one host operation scope.
            using LazySearchSession session = new(_search);
            DanbooruWatcherCheckSummary summary = await RunPagesAsync(watcherId, current, manual, started, counters, running,
                session).ConfigureAwait(false);
            return Record(watcherId, summary);
        }
        catch (Exception exception)
        {
            // No exception may leave the watcher in the persisted Checking state: SelectNext skips every
            // watcher while one is Checking and BeginCheck refuses manual checks for it, so a single escaped
            // exception would stall all watchers until the next restart (CX-R1). The known trigger is an
            // OperationCanceledException from a token that is not this check's (plugin worker lifetime);
            // RunPagesAsync maps that one per call, this is the last line for everything else.
            return Record(watcherId, FaultRun(watcherId, running, started, counters, manual,
                cancelled: exception is OperationCanceledException && check.IsCancellationRequested));
        }
        finally
        {
            lock (_sync)
            {
                check.Dispose();
                running.Completion.TrySetResult();
                if (ReferenceEquals(_running, running)) _running = null;
            }
            // Last: the generation's drain waits for this, after the terminal write and the scope disposal.
            use?.Dispose();
        }
    }

    /// <summary>
    /// Terminal transition for a check that ended in an exception instead of a result. Only the watcher
    /// instance of this very check is touched; a Pause/Remove that already moved it on wins.
    /// </summary>
    private DanbooruWatcherCheckSummary FaultRun(string watcherId, RunningCheck running, DateTimeOffset started,
        CheckCounters counters, bool manual, bool cancelled)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_running, running) && _watchers.TryGetValue(watcherId, out DanbooruWatcher? live)
                && live.State == DanbooruWatcherState.Checking)
            {
                CommitWithoutRequestLocked(cancelled
                    ? DanbooruWatcher.CancelCheck(live).Watcher
                    : DanbooruWatcher.ApplyError(live, "check_faulted", _time.GetUtcNow()).Watcher, running);
            }
        }
        return cancelled
            ? Cancelled(started, counters, manual, CancelCode(running))
            : Summary(DanbooruWatcherCheckKind.Failed, started, counters, "check_faulted", manual);
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
        bool manual, DateTimeOffset started, CheckCounters counters, RunningCheck running,
        LazySearchSession session)
    {
        CancellationTokenSource check = running.Cancellation;
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
                    CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "cursor_missing", _time.GetUtcNow()).Watcher, running);
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
                return CancelRun(watcherId, current, started, counters, manual, running);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by a token that is not this check's: the plugin worker lifetime ended (stop,
                // eviction, plugin disabled) while the page was in flight. That is neither a provider answer
                // nor a defect, so the search path counts as unavailable: the watcher returns to its resume
                // target and the scheduler backs off instead of stalling in Checking (CX-R1).
                outcome = new DanbooruWatcherSearchUnavailable("source_unavailable");
            }
            catch (Exception)
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
                if (running.Use is { IsCurrent: false })
                {
                    // The generation was cut while the page was in flight. A provider deferral is a fact about the
                    // provider that must survive a restart; it is the check's terminal transition and written with the
                    // admission counted before the cut. Any other answer is discarded: the watcher returns to its
                    // resume target with the last durable cursor and the page replays (idempotently) later.
                    if (outcome is DanbooruWatcherSearchDeferred late)
                        return ApplyDeferredLocked(current, late, now, started, counters, manual, running);
                    CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher, running);
                    return Cancelled(started, counters, manual, ProfileSuspendedCode);
                }
                switch (outcome)
                {
                    case DanbooruWatcherSearchSnapshot snapshot:
                        if (snapshot.Posts is null || snapshot.Posts.Any(post => post is null
                                || post.SourceId != DanbooruWatcher.ProviderId))
                        {
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "foreign_post", now).Watcher, running);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "foreign_post", manual);
                        }
                        if (!string.Equals(snapshot.BeforePostId, before, StringComparison.Ordinal))
                        {
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "page_binding_mismatch", now).Watcher, running);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "page_binding_mismatch", manual);
                        }
                        DanbooruSearchPageObservation observation = new(DanbooruWatcher.ProviderId, snapshot.Query,
                            page, snapshot.Posts.Select(post => post.PostId).ToArray(), snapshot.IsEnd);
                        applied = DanbooruWatcher.ApplyPage(current, observation, now);
                        if (!applied.Accepted)
                        {
                            string code = SafeCode(applied.RejectionCode, "page_rejected");
                            CommitWithoutRequestLocked(applied.Watcher, running);
                            return Summary(DanbooruWatcherCheckKind.Failed, started, counters, code, manual);
                        }
                        claims = snapshot.Posts;
                        break;
                    case DanbooruWatcherSearchDeferred deferred:
                        return ApplyDeferredLocked(current, deferred, now, started, counters, manual, running);
                    case DanbooruWatcherSearchUnavailable unavailable:
                    {
                        _backoffUntil = Max(_backoffUntil, now + _options.UnavailableRetry);
                        CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher, running);
                        return Summary(DanbooruWatcherCheckKind.SourceUnavailable, started, counters,
                            SafeCode(unavailable.Code, "source_unavailable"), manual, _backoffUntil);
                    }
                    case DanbooruWatcherSearchFailed failed:
                    {
                        string code = SafeCode(failed.Code, "provider_error");
                        CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, code, now).Watcher, running);
                        return Summary(DanbooruWatcherCheckKind.Failed, started, counters, code, manual);
                    }
                    default:
                        CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "search_result_unknown", now).Watcher, running);
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
                    return CancelRun(watcherId, current, started, counters, manual, running);
                }
                catch (Exception) when (check.IsCancellationRequested || running.Use is { IsUncut: false })
                {
                    // The admission failed because the check was cancelled (pause, remove, stop, cut) or because the
                    // profile kernel refuses fresh writers for an announced exclusive transition. Neither is a local
                    // storage failure: no Error state, no local backoff; the page replays after the transition.
                    return CancelRun(watcherId, current, started, counters, manual, running);
                }
                catch (BooruCatalogPersistenceException)
                {
                    // The catalog write failed at the admission boundary. Do not advance past the post: the
                    // stored high-water mark/cursor stays at the last durable page. Automatic checks get the
                    // same local quiet period as other persistence failures; manual Check Now remains allowed.
                    lock (_sync)
                    {
                        if (!manual)
                            _persistBackoffUntil = Max(_persistBackoffUntil, _time.GetUtcNow() + _options.UnavailableRetry);
                        if (IsCurrentLocked(watcherId, current))
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "persist_failed", _time.GetUtcNow()).Watcher, running);
                    }
                    return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "persist_failed", manual);
                }
                catch (Exception)
                {
                    // The post could not be recorded (also when the queue's own lifetime cancelled the
                    // admission): do not advance past it. The watcher stops visibly and the same page is
                    // replayed (idempotently) after Resume or a manual check.
                    lock (_sync)
                    {
                        if (IsCurrentLocked(watcherId, current))
                            CommitWithoutRequestLocked(DanbooruWatcher.ApplyError(current, "admission_failed", _time.GetUtcNow()).Watcher, running);
                    }
                    return Summary(DanbooruWatcherCheckKind.Failed, started, counters, "admission_failed", manual);
                }
                counters.Count(result);
            }

            lock (_sync)
            {
                // Paused/removed while the posts were handed off: discard; the page replays idempotently.
                if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
                // Cut while the posts were handed off: the cut generation writes no new progress (F2).
                if (running.Use is { IsCurrent: false })
                {
                    CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher, running);
                    return Cancelled(started, counters, manual, ProfileSuspendedCode);
                }
                if (!TryPersistLocked(applied.Watcher, out DanbooruWatcher? stored, running.Use?.Admission))
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
            if (check.IsCancellationRequested) return CancelRun(watcherId, current, started, counters, manual, running);
            // The page in flight is durable; stop before the next one for a session pause (F3, automatic checks only)
            // or an announced profile transition (F2). The stored cursor continues the traversal after Resume.
            bool sessionPaused;
            lock (_sync) sessionPaused = _sessionPaused && !manual;
            if (sessionPaused || running.Use is { IsUncut: false })
                return CancelRun(watcherId, current, started, counters, manual, running,
                    sessionPaused && running.Use is not { IsUncut: false } ? SessionPausedCode : null);
        }
    }

    private DanbooruWatcherCheckSummary ApplyDeferredLocked(DanbooruWatcher current, DanbooruWatcherSearchDeferred deferred,
        DateTimeOffset now, DateTimeOffset started, CheckCounters counters, bool manual, RunningCheck running)
    {
        DateTimeOffset retryAt = Max(deferred.RetryAtUtc.ToUniversalTime(), now + _options.MinimumDeferral);
        if (_search.GetProviderCooldownUntilUtc() is { } transport) retryAt = Max(retryAt, transport);
        _backoffUntil = Max(_backoffUntil, retryAt);
        string reason = SafeCode(deferred.ReasonCode, "provider_deferred");
        CommitWithoutRequestLocked(DanbooruWatcher.ApplyDeferred(current, retryAt, reason, now).Watcher, running);
        return Summary(DanbooruWatcherCheckKind.Deferred, started, counters, reason, manual, retryAt);
    }

    private DanbooruWatcherCheckSummary CancelRun(string watcherId, DanbooruWatcher current, DateTimeOffset started,
        CheckCounters counters, bool manual, RunningCheck running, string? code = null)
    {
        lock (_sync)
        {
            if (!IsCurrentLocked(watcherId, current)) return Cancelled(started, counters, manual, "superseded");
            // Shutdown/cancel/cut: back to the resume target with the cursor unchanged. With a lifecycle this is
            // written with the generation admission counted before the cut or stop. Without one, a profile that
            // already refuses writes recovers the stored Checking record identically on the next start.
            CommitWithoutRequestLocked(DanbooruWatcher.CancelCheck(current).Watcher, running);
        }
        return Cancelled(started, counters, manual, code ?? CancelCode(running));
    }

    /// <summary>The code of a cancelled check: a profile transition is reported as such.</summary>
    private string CancelCode(RunningCheck running) =>
        running.Use is { IsUncut: false } && _lifecycle is { Phase: not BooruProducerPhase.Stopped } && !_stopping
            ? ProfileSuspendedCode : "cancelled";

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
            // A command is a counted use of the open generation too; during a profile transition it is refused
            // honestly instead of failing in the store.
            using BooruProducerUse? use = _lifecycle?.TryEnter();
            if (_lifecycle != null && use is null)
                return new(false, ProfileSuspendedCode, StatusLocked(watcher, _time.GetUtcNow()));
            if (!TryPersistLocked(applied.Watcher, out DanbooruWatcher? stored, use?.Admission))
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
        if (HoldCodeLocked(manual) is { } hold) return Rejected(hold, now, manual);
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

    private void CommitWithoutRequestLocked(DanbooruWatcher watcher, RunningCheck running)
    {
        // A terminal transition of a check must reach memory even when the profile no longer accepts
        // writes (shutdown without a lifecycle); the last durable record then recovers to the same resume target.
        if (!TryPersistLocked(watcher, out _, running.Use?.Admission))
            _watchers[watcher.WatcherId] = DanbooruWatcherRecordMapper.CompactKnownPostIds(watcher);
    }

    /// <summary>
    /// Persists with the counted generation admission of the caller's use, or (without a lifecycle) with the store's
    /// own per-call writer admission.
    /// </summary>
    private bool TryPersistLocked(DanbooruWatcher watcher, out DanbooruWatcher? stored, ProfileAdmissionToken? admission)
    {
        stored = null;
        DanbooruWatcher compact = DanbooruWatcherRecordMapper.CompactKnownPostIds(watcher);
        try
        {
            BooruWatcherRecord record = DanbooruWatcherRecordMapper.ToRecord(compact);
            if (admission is null) _catalog.UpsertWatcher(record, CancellationToken.None);
            else _catalog.UpsertWatcherAdmitted(record, admission, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        if (compact.State == DanbooruWatcherState.Removed) _watchers.Remove(compact.WatcherId);
        else _watchers[compact.WatcherId] = compact;
        stored = compact;
        // The store accepts writes again (e.g. a manual check after the user freed disk space).
        _persistBackoffUntil = null;
        return true;
    }

    private DanbooruWatcherStatus StatusLocked(DanbooruWatcher watcher, DateTimeOffset now)
    {
        DateTimeOffset? providerCooldown = ProviderCooldownLocked(now);
        DateTimeOffset? next = DanbooruWatcherSchedule.NextCheckAtUtc(watcher, _options.Interval, now);
        if (next is { } due && providerCooldown is { } cooling && cooling > due) next = cooling;
        // A local write backoff only moves the next automatic check; it is not shown as a provider cooldown.
        if (next is { } planned && _persistBackoffUntil is { } quiet && quiet > now && quiet > planned) next = quiet;
        DanbooruWatcherActivity activity;
        if (_running?.WatcherId == watcher.WatcherId || watcher.State == DanbooruWatcherState.Checking)
            activity = DanbooruWatcherActivity.Checking;
        else if (_manual.Any(item => item.WatcherId == watcher.WatcherId))
            activity = DanbooruWatcherActivity.QueuedForCheck;
        else
            activity = watcher.State switch
            {
                DanbooruWatcherState.Paused => DanbooruWatcherActivity.Paused,
                // Paused during (or by) a provider wait: the user paused it, the retry time stays in
                // CooldownUntilUtc and keeps the provider wait across restarts (CX2-R2).
                DanbooruWatcherState.Cooldown when watcher.ResumeState == DanbooruWatcherState.Paused
                    => DanbooruWatcherActivity.Paused,
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

    private sealed record RunningCheck(string WatcherId, CancellationTokenSource Cancellation, BooruProducerUse? Use)
    {
        /// <summary>Set once the check has fully ended (terminal state committed, session disposed).</summary>
        internal TaskCompletionSource Completion { get; } = NewSignal();
    }

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
