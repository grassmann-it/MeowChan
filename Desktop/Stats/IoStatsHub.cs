using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MeowChan.Desktop.Stats;

/// <summary>One source's row of the statistics: totals for this session, today and the retention window, plus its current rate.</summary>
internal sealed record IoSourceStats(string Key, IoCounts Session, IoCounts Today, IoCounts Retained, IoRates Rate);

/// <summary>A consistent copy of everything the statistics page and the header graph show. Safe to keep and read on any thread.</summary>
internal sealed class IoStatsSnapshot
{
    internal DateOnly Today { get; init; }
    internal DateTimeOffset SessionStart { get; init; }
    internal int RetentionDays { get; init; }
    internal IoRates Rate { get; init; }
    internal IoActivity Activity { get; init; }
    internal IoSeries Series { get; init; } = IoSeries.Empty;
    /// <summary>Largest session download first; sources beyond the source limit are summed into <see cref="IoStatsKeys.Other"/>.</summary>
    internal IReadOnlyList<IoSourceStats> Sources { get; init; } = [];
    internal IoSourceStats Total { get; init; } = new(string.Empty, default, default, default, default);
}

/// <summary>The cheap read for the tray: no ring copy, no per-source rows.</summary>
internal readonly record struct IoStatsPeek(IoRates Rate, IoCounts Session, IoCounts Today, IoActivity Activity);

/// <summary>Holds a visibility claim on the hub. The hub's timers exist only while a claim needs them.</summary>
internal sealed class IoStatsSubscription : IDisposable
{
    private readonly IoStatsHub _hub;

    internal IoStatsSubscription(IoStatsHub hub, IoStatsVisibility visibility) { _hub = hub; Current = visibility; }

    /// <summary>Guarded by the hub's lock; read it through <see cref="Visibility"/>.</summary>
    internal IoStatsVisibility Current;
    internal bool Disposed;

    internal IoStatsVisibility Visibility
    {
        get => _hub.VisibilityOf(this);
        set => _hub.SetVisibility(this, value);
    }

    public void Dispose() => _hub.Unsubscribe(this);
}

/// <summary>
/// Samples the transport and media counters into a ring buffer for the graphs and into per-source session and day totals.
/// <para>
/// CPU rules (the owner wants nothing running in the tray): while a window claim exists one timer runs at the sample
/// interval; while only a tray claim exists, the tray tooltip setting is on and downloads are active one timer runs at the
/// background interval and only collects; hidden and idle means no sampling timer at all. A second timer for writing the
/// totals exists only while totals are changing or downloads are active. <see cref="ActiveTimerCount"/> is that number.
/// </para>
/// <para>
/// Threading: timers run on the thread pool and <see cref="Sampled"/> is raised on that thread, outside every lock; handlers
/// must be quick and post to the UI thread themselves. All reads copy under one lock.
/// </para>
/// </summary>
internal sealed class IoStatsHub : IDisposable
{
    /// <summary>The process-wide hub of the desktop; the session host attaches the profile's store and the queue probe.</summary>
    internal static IoStatsHub Shared { get; } = new(new TransferMeterSource());

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly IIoCounterSource _source;
    private readonly TimeProvider _time;
    private readonly IoRing _ring;
    private readonly List<IoCounterReading> _readings = [];
    private readonly Dictionary<string, IoCounts> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IoCounts> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IoCounts> _session = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IoCounts> _pending = new(StringComparer.Ordinal);
    private readonly IoDays _days = new();
    private readonly List<IoStatsSubscription> _subscriptions = [];
    private readonly DateTimeOffset _sessionStart;
    private IoStatsOptions _options;
    private Dictionary<string, IoRates> _rates = new(StringComparer.Ordinal);
    private IoRates _totalRate, _previousTotalRate;
    private long _lastSampleTimestamp;
    private int _zeroRun;
    private DateOnly _today;
    private bool _dirty, _disposed, _activeLast, _loaded;
    private ITimer? _sampleTimer, _persistTimer;
    private IoStatsVisibility _mode;
    private int _sampleIntervalMs, _sampleGeneration, _persistGeneration;
    private TimeSpan _persistPeriod;
    private IoStatsStore? _store;
    private Func<IoActivity>? _activity;
    private object? _attachment;
    private string? _loadNotice, _lastPersistError;

    internal IoStatsHub(IIoCounterSource source, TimeProvider? time = null, IoStatsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = time ?? TimeProvider.System;
        _options = (options ?? IoStatsOptions.Default).Normalized();
        _ring = new IoRing(_options.HistorySamples);
        _sessionStart = _time.GetLocalNow();
        _today = DateOnly.FromDateTime(_sessionStart.DateTime);
        _lastSampleTimestamp = _time.GetTimestamp();
    }

    /// <summary>Raised after a sample (and after <see cref="Reset"/>) unless nothing visible changed. See the threading note.</summary>
    internal event Action? Sampled;

    internal IoStatsOptions Options { get { lock (_gate) return _options; } }

    /// <summary>Number of timers that currently exist (sampling plus writing). Zero while hidden and idle.</summary>
    internal int ActiveTimerCount { get { lock (_gate) return (_sampleTimer != null ? 1 : 0) + (_persistTimer != null ? 1 : 0); } }

    /// <summary>The strongest claim that currently drives the sampling timer: <see cref="IoStatsVisibility.None"/> when no sampling timer runs.</summary>
    internal IoStatsVisibility SamplingMode { get { lock (_gate) return _sampleTimer == null ? IoStatsVisibility.None : _mode; } }

    internal int SamplingIntervalMs { get { lock (_gate) return _sampleTimer == null ? 0 : _sampleIntervalMs; } }

    /// <summary>A sentence for the session messages when the saved statistics were damaged, restored or unreadable; null otherwise.</summary>
    internal string? LoadNotice { get { lock (_gate) return _loadNotice; } }

    /// <summary>The last write error of io-stats.json, null after a successful write. A failed write is retried at the next write time.</summary>
    internal string? LastPersistError { get { lock (_gate) return _lastPersistError; } }

    // --- host wiring ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Connects the profile's store and the download-queue probe, and loads the saved days when persisting is on.
    /// Disposing the result writes changed totals and disconnects, but only if it is still the current attachment.
    /// </summary>
    internal IDisposable Attach(IoStatsStore? store, Func<IoActivity>? activity)
    {
        object attachment = new();
        lock (_writeGate)
        {
            lock (_gate)
            {
                _store = store;
                _activity = activity;
                _attachment = attachment;
                _loaded = false;
            }
            LoadSavedLocked();
        }
        NotifyActivityChanged();
        return new Detacher(this, attachment);
    }

    private sealed class Detacher(IoStatsHub hub, object attachment) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) hub.Detach(attachment); }
    }

    private void Detach(object attachment)
    {
        lock (_writeGate)
        {
            lock (_gate) if (!ReferenceEquals(_attachment, attachment)) return;
            Flush();
            lock (_gate)
            {
                // The days belong to the profile just left (written by the flush above); the next attachment loads its own.
                _store = null; _activity = null; _attachment = null; _loaded = false; _activeLast = false;
                _days.Clear(); _dirty = false;
                ApplyTimersLocked();
            }
        }
    }

    private void LoadSavedLocked()
    {
        IoStatsStore? store;
        IoStatsOptions options;
        DateOnly today;
        lock (_gate)
        {
            if (_loaded || _store == null || !_options.Persist) return;
            store = _store;
            options = _options;
            today = _today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        }
        IoStatsLoadResult result = store.Load(today, options.RetentionDays, options.MaxSources);
        lock (_gate)
        {
            foreach ((DateOnly day, Dictionary<string, IoCounts> sources) in result.Days.Days) _days.Merge(day, sources, _options.MaxSources);
            _loaded = result.Status != IoStatsLoadStatus.Unreadable; // an unreadable file is read again when settings change
            _loadNotice = result.Notice;
            if (result.Status is IoStatsLoadStatus.RestoredFromBackup or IoStatsLoadStatus.Damaged) _dirty = true;
            ApplyTimersLocked();
        }
    }

    /// <summary>Applies new settings at once: timers, history length, retention and source limit follow without a restart.</summary>
    internal void Configure(IoStatsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Normalized();
        bool loadNow;
        lock (_gate)
        {
            if (_disposed || options == _options) return;
            IoStatsOptions previous = _options;
            _options = options;
            _ring.Resize(options.HistorySamples);
            CollectLocked();
            if (options.RetentionDays < previous.RetentionDays) _dirty |= _days.Prune(_today.AddDays(-(options.RetentionDays - 1)));
            if (options.MaxSources < previous.MaxSources) _dirty |= _days.Renormalize(options.MaxSources);
            loadNow = options.Persist && !_loaded && _store != null;
            if (!loadNow) ApplyTimersLocked();
        }
        if (loadNow)
        {
            lock (_writeGate) LoadSavedLocked();
        }
    }

    /// <summary>Re-reads the download queue's activity. The host calls it when the queue changes; it is a no-op unless "any download active" flipped.</summary>
    internal void NotifyActivityChanged()
    {
        Func<IoActivity>? probe;
        lock (_gate) probe = _activity;
        bool active = probe?.Invoke().Active > 0;
        lock (_gate)
        {
            if (_disposed) return;
            if (active == _activeLast) return;
            _activeLast = active;
            CollectLocked(); // fold what happened while no timer ran, so a hidden idle window never loses the tail
            ApplyTimersLocked();
        }
    }

    // --- subscriptions ------------------------------------------------------------------------------------------------

    /// <summary>Claims a visibility. Dispose the result when the claim ends; set <see cref="IoStatsSubscription.Visibility"/> when it changes.</summary>
    internal IoStatsSubscription Subscribe(IoStatsVisibility visibility)
    {
        IoStatsSubscription subscription = new(this, visibility);
        lock (_gate)
        {
            if (_disposed) return subscription;
            _subscriptions.Add(subscription);
            ApplyTimersLocked();
        }
        return subscription;
    }

    internal IoStatsVisibility VisibilityOf(IoStatsSubscription subscription) { lock (_gate) return subscription.Current; }

    internal void SetVisibility(IoStatsSubscription subscription, IoStatsVisibility value)
    {
        lock (_gate)
        {
            if (subscription.Disposed || subscription.Current == value) return;
            subscription.Current = value;
            ApplyTimersLocked();
        }
    }

    internal void Unsubscribe(IoStatsSubscription subscription)
    {
        lock (_gate)
        {
            subscription.Disposed = true;
            _subscriptions.Remove(subscription);
            ApplyTimersLocked();
        }
    }

    // --- reads ----------------------------------------------------------------------------------------------------------

    /// <summary>The last values without starting any timer: counts are folded in now, rates are those of the last sample (zero when stale).</summary>
    internal IoStatsPeek Peek()
    {
        IoActivity activity = ReadActivity();
        lock (_gate)
        {
            CollectLocked();
            ApplyTimersLocked();
            return new IoStatsPeek(StaleLocked() ? IoRates.Zero : _totalRate, Sum(_session), TodayLocked(), activity);
        }
    }

    /// <summary>Everything for the statistics page and the graphs. Folds the counters in first; rates and series are those of the last sample.</summary>
    internal IoStatsSnapshot Snapshot()
    {
        IoActivity activity = ReadActivity();
        lock (_gate)
        {
            CollectLocked();
            ApplyTimersLocked();
            DateOnly oldest = _today.AddDays(-(_options.RetentionDays - 1));
            bool stale = StaleLocked();
            Dictionary<string, IoCounts> today = _days.TryGet(_today, out Dictionary<string, IoCounts>? bucket)
                ? new Dictionary<string, IoCounts>(bucket, StringComparer.Ordinal) : new Dictionary<string, IoCounts>(StringComparer.Ordinal);
            Dictionary<string, IoCounts> retained = _days.Sum(oldest, _today.AddDays(2));
            Dictionary<string, IoRates> rates = stale ? new Dictionary<string, IoRates>(StringComparer.Ordinal) : _rates;
            return BuildSnapshotLocked(activity, today, retained, rates, stale ? IoRates.Zero : _totalRate);
        }
    }

    /// <summary>Deletes the saved copies, then clears the counts and ring. A failed delete keeps the in-memory statistics.</summary>
    internal bool Reset()
    {
        IoStatsStore? store;
        lock (_writeGate)
        {
            lock (_gate) store = _store;
            if (store?.Delete() == false) return false;
            lock (_gate)
            {
                CollectLocked();
                _session.Clear(); _pending.Clear(); _days.Clear(); _rates = new(StringComparer.Ordinal);
                _totalRate = _previousTotalRate = IoRates.Zero;
                _ring.Clear(); _zeroRun = 0; _dirty = false; _lastPersistError = null; _loadNotice = null;
                _lastSampleTimestamp = _time.GetTimestamp();
                ApplyTimersLocked();
            }
        }
        Sampled?.Invoke();
        return true;
    }

    /// <summary>Writes changed totals now (at exit, on detach). A failed write is kept for the next try and never throws.</summary>
    internal void Flush() => PersistNow();

    // --- sampling -------------------------------------------------------------------------------------------------------

    private IoActivity ReadActivity()
    {
        Func<IoActivity>? probe;
        lock (_gate) probe = _activity;
        return probe?.Invoke() ?? default;
    }

    private bool StaleLocked()
    {
        if (_sampleTimer == null) return true;
        return _time.GetElapsedTime(_lastSampleTimestamp, _time.GetTimestamp()) > TimeSpan.FromMilliseconds(3.0 * _sampleIntervalMs);
    }

    private IoCounts TodayLocked() => _days.TryGet(_today, out Dictionary<string, IoCounts>? bucket) ? Sum(bucket) : IoCounts.Zero;

    private static IoCounts Sum(Dictionary<string, IoCounts> sources)
    {
        IoCounts total = default;
        foreach (IoCounts counts in sources.Values) total = total.Plus(counts);
        return total;
    }

    private void OnSampleTick(int generation)
    {
        // The probe is read outside the lock: the queue's own lock must never nest under the hub's.
        IoActivity activity = ReadActivity();
        bool raise;
        lock (_gate)
        {
            if (_disposed || generation != _sampleGeneration || _sampleTimer == null) return;
            _activeLast = activity.Active > 0;
            CollectLocked();
            raise = SampleLocked(intoRing: _mode == IoStatsVisibility.Window);
            ApplyTimersLocked();
        }
        if (raise) Sampled?.Invoke();
    }

    private void OnPersistTick(int generation)
    {
        IoActivity activity = ReadActivity();
        lock (_gate)
        {
            if (_disposed || generation != _persistGeneration) return;
            _activeLast = activity.Active > 0;
        }
        PersistNow();
        lock (_gate) { if (generation == _persistGeneration) ApplyTimersLocked(); }
    }

    /// <summary>Turns the counts gathered since the last sample into rates. Returns whether subscribers have something new to draw.</summary>
    private bool SampleLocked(bool intoRing)
    {
        long now = _time.GetTimestamp();
        TimeSpan elapsed = _time.GetElapsedTime(_lastSampleTimestamp, now);
        if (elapsed <= TimeSpan.Zero) return false;
        double seconds = elapsed.TotalSeconds;
        Dictionary<string, IoRates> rates = new(_pending.Count, StringComparer.Ordinal);
        IoCounts total = default;
        foreach ((string key, IoCounts counts) in _pending)
        {
            rates[key] = new IoRates(NetDown: counts.NetDown / seconds, NetUp: counts.NetUp / seconds,
                DiskWrite: counts.DiskWritten / seconds, DiskRead: counts.DiskRead / seconds, Requests: counts.Requests / seconds);
            total = total.Plus(counts);
        }
        _rates = rates;
        _previousTotalRate = _totalRate;
        _totalRate = new IoRates(NetDown: total.NetDown / seconds, NetUp: total.NetUp / seconds,
            DiskWrite: total.DiskWritten / seconds, DiskRead: total.DiskRead / seconds, Requests: total.Requests / seconds);
        _pending.Clear();
        _lastSampleTimestamp = now;
        bool zero = _totalRate == IoRates.Zero;
        if (!intoRing) return !(zero && _previousTotalRate == IoRates.Zero);
        _ring.Add(new IoSample(NetDown: (float)_totalRate.NetDown, NetUp: (float)_totalRate.NetUp,
            DiskWrite: (float)_totalRate.DiskWrite, DiskRead: (float)_totalRate.DiskRead, Requests: (float)_totalRate.Requests));
        _zeroRun = zero ? Math.Min(_zeroRun + 1, int.MaxValue - 1) : 0;
        // Once the whole buffer is zero, redrawing it shows nothing new.
        return _zeroRun <= _ring.Capacity;
    }

    /// <summary>Reads the counters and folds the change since the last read into the session and day totals.</summary>
    private void CollectLocked()
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        if (today != _today)
        {
            // Bytes that arrived before midnight but are read after it land on the new day: the granularity is one sample.
            _today = today;
            _dirty |= _days.Prune(today.AddDays(-(_options.RetentionDays - 1)));
        }
        _readings.Clear();
        _current.Clear();
        _source.Read(_readings);
        foreach (IoCounterReading reading in _readings)
        {
            string key = IoStatsKeys.Normalize(reading.Key);
            IoCounts counts = new IoCounts(NetDown: reading.NetDown, NetUp: reading.NetUp, DiskWritten: reading.DiskWritten,
                DiskRead: reading.DiskRead, Requests: reading.Requests, Errors: reading.Errors);
            _current[key] = _current.TryGetValue(key, out IoCounts existing) ? existing.Plus(counts) : counts;
        }
        int maxSources = _options.MaxSources;
        foreach ((string key, IoCounts now) in _current)
        {
            _last.TryGetValue(key, out IoCounts before);
            _last[key] = now;
            // A counter that went backwards (a restarted source) contributes nothing for that field.
            IoCounts delta = new IoCounts(NetDown: Math.Max(0, now.NetDown - before.NetDown), NetUp: Math.Max(0, now.NetUp - before.NetUp),
                DiskWritten: Math.Max(0, now.DiskWritten - before.DiskWritten), DiskRead: Math.Max(0, now.DiskRead - before.DiskRead),
                Requests: Math.Max(0, now.Requests - before.Requests), Errors: Math.Max(0, now.Errors - before.Errors));
            if (delta.IsZero) continue;
            IoDays.AddTo(_session, key, delta, maxSources);
            IoDays.AddTo(_pending, key, delta, maxSources);
            _days.Add(_today, key, delta, maxSources);
            _dirty = true;
        }
    }

    // --- timers ---------------------------------------------------------------------------------------------------------

    private IoStatsVisibility WantedModeLocked()
    {
        if (_disposed) return IoStatsVisibility.None;
        bool tray = false;
        foreach (IoStatsSubscription subscription in _subscriptions)
        {
            IoStatsVisibility visibility = subscription.Current;
            if (visibility == IoStatsVisibility.Window) return IoStatsVisibility.Window;
            tray |= visibility == IoStatsVisibility.Tray;
        }
        return tray && _options.TrayTooltip && _activeLast ? IoStatsVisibility.Tray : IoStatsVisibility.None;
    }

    private bool PersistWantedLocked() => !_disposed && _options.Persist && _store != null && (_dirty || _activeLast);

    private void ApplyTimersLocked()
    {
        IoStatsVisibility mode = WantedModeLocked();
        if (mode == IoStatsVisibility.None)
        {
            if (_sampleTimer != null) { _sampleTimer.Dispose(); _sampleTimer = null; _sampleGeneration++; }
        }
        else
        {
            int interval = mode == IoStatsVisibility.Window ? _options.SampleIntervalMs : _options.BackgroundIntervalMs;
            if (_sampleTimer == null || mode != _mode || interval != _sampleIntervalMs) StartSamplingLocked(mode, interval);
        }

        if (!PersistWantedLocked())
        {
            if (_persistTimer != null) { _persistTimer.Dispose(); _persistTimer = null; _persistGeneration++; }
        }
        else
        {
            TimeSpan period = TimeSpan.FromMinutes(_options.PersistIntervalMinutes);
            if (_persistTimer == null || period != _persistPeriod)
            {
                _persistTimer?.Dispose();
                int generation = ++_persistGeneration;
                _persistPeriod = period;
                _persistTimer = _time.CreateTimer(_ => OnPersistTick(generation), null, period, period);
            }
        }
    }

    private void StartSamplingLocked(IoStatsVisibility mode, int intervalMs)
    {
        bool windowChanged = mode == IoStatsVisibility.Window && (_sampleTimer == null || _mode != IoStatsVisibility.Window || _sampleIntervalMs != intervalMs);
        _sampleTimer?.Dispose();
        // Bytes that arrived while no timer ran are not a rate: fold them into the totals, then measure from now.
        CollectLocked();
        _pending.Clear();
        _lastSampleTimestamp = _time.GetTimestamp();
        // The ring holds equally spaced samples of the window cadence; a new cadence or a gap starts a clean graph.
        if (windowChanged) { _ring.Clear(); _zeroRun = 0; }
        if (mode != IoStatsVisibility.Window) { _totalRate = _previousTotalRate = IoRates.Zero; _rates = new(StringComparer.Ordinal); }
        _mode = mode;
        _sampleIntervalMs = intervalMs;
        int generation = ++_sampleGeneration;
        TimeSpan period = TimeSpan.FromMilliseconds(intervalMs);
        _sampleTimer = _time.CreateTimer(_ => OnSampleTick(generation), null, period, period);
    }

    // --- persistence ----------------------------------------------------------------------------------------------------

    private bool PersistNow()
    {
        lock (_writeGate)
        {
            // A file that could not be read (locked) must not be overwritten by this session alone: try reading it again first.
            LoadSavedLocked();
            IoStatsStore store;
            string json;
            lock (_gate)
            {
                CollectLocked();
                if (_store == null || !_options.Persist || !_loaded || !_dirty) return false;
                store = _store;
                json = IoStatsStore.Serialize(_days, _today, _options.RetentionDays);
                _dirty = false;
            }
            bool saved = store.TrySave(json, out string? error);
            lock (_gate)
            {
                if (!saved) _dirty = true;
                _lastPersistError = saved ? null : error;
            }
            return saved;
        }
    }

    // --- snapshot rows --------------------------------------------------------------------------------------------------

    private IoStatsSnapshot BuildSnapshotLocked(IoActivity activity, Dictionary<string, IoCounts> today,
        Dictionary<string, IoCounts> retained, Dictionary<string, IoRates> rates, IoRates totalRate)
    {
        HashSet<string> keys = new(_session.Keys, StringComparer.Ordinal);
        keys.UnionWith(today.Keys); keys.UnionWith(retained.Keys); keys.UnionWith(rates.Keys);
        List<IoSourceStats> rows = new(keys.Count);
        foreach (string key in keys)
        {
            _session.TryGetValue(key, out IoCounts session);
            today.TryGetValue(key, out IoCounts day);
            retained.TryGetValue(key, out IoCounts window);
            rates.TryGetValue(key, out IoRates rate);
            rows.Add(new IoSourceStats(key, session, day, window, rate));
        }
        rows.Sort(static (a, b) => a.Key == IoStatsKeys.Other ? (b.Key == IoStatsKeys.Other ? 0 : 1) : b.Key == IoStatsKeys.Other ? -1
            : b.Session.NetDown != a.Session.NetDown ? b.Session.NetDown.CompareTo(a.Session.NetDown)
            : b.Retained.NetDown != a.Retained.NetDown ? b.Retained.NetDown.CompareTo(a.Retained.NetDown)
            : string.CompareOrdinal(a.Key, b.Key));
        int limit = _options.MaxSources;
        if (rows.Count > limit)
        {
            IoSourceStats tail = rows.FirstOrDefault(row => row.Key == IoStatsKeys.Other) ?? new IoSourceStats(IoStatsKeys.Other, default, default, default, default);
            rows.RemoveAll(row => row.Key == IoStatsKeys.Other);
            List<IoSourceStats> kept = rows.Take(limit - 1).ToList();
            foreach (IoSourceStats row in rows.Skip(limit - 1)) tail = Combine(tail.Key, tail, row);
            kept.Add(tail);
            rows = kept;
        }
        IoSourceStats total = new(string.Empty, default, default, default, totalRate);
        foreach (IoSourceStats row in rows) total = Combine(string.Empty, total, row) with { Rate = totalRate };
        return new IoStatsSnapshot
        {
            Today = _today, SessionStart = _sessionStart, RetentionDays = _options.RetentionDays, Rate = totalRate, Activity = activity,
            Series = _ring.ToSeries(), Sources = rows, Total = total
        };
    }

    private static IoSourceStats Combine(string key, IoSourceStats a, IoSourceStats b) => new(key, a.Session.Plus(b.Session), a.Today.Plus(b.Today),
        a.Retained.Plus(b.Retained), new IoRates(NetDown: a.Rate.NetDown + b.Rate.NetDown, NetUp: a.Rate.NetUp + b.Rate.NetUp,
            DiskWrite: a.Rate.DiskWrite + b.Rate.DiskWrite, DiskRead: a.Rate.DiskRead + b.Rate.DiskRead,
            Requests: a.Rate.Requests + b.Rate.Requests));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sampleTimer?.Dispose(); _sampleTimer = null; _sampleGeneration++;
            _persistTimer?.Dispose(); _persistTimer = null; _persistGeneration++;
        }
        PersistNow(); // a disposed hub still writes its last changes once; the store keeps working until it is detached
    }
}
