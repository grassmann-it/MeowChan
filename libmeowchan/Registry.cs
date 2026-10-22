namespace MeowChan;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Notifications;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using System.Text.Json;
using Serilog;

public enum AdmissionStatus { Added, AlreadyPresent, Invalid, Unsupported }
public sealed record AdmissionResult(AdmissionStatus Status, IThread? Thread = null, IBoard? Board = null);

/// <summary>Owns admission, durable session intent, watch lifetimes and the shared queue. Construction is passive.</summary>
public sealed partial class Registry : IAsyncDisposable
{
    private sealed record ResourceKey(string Source, string Board, string? Thread = null);
    private sealed record Resource(ResourceKey Key, string Url, IThread? Thread, IBoard? Board);
    private sealed class Watch
    {
        public IThread? Thread { get; init; }
        public IBoard? Board { get; init; }
        public required string OutputRoot { get; init; }
        public bool Stopping { get; set; }
        public bool Ready { get; set; }
        public TaskCompletionSource Stopped { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? OwnerRegistration { get; set; }
        public PropertyChangedEventHandler? StateHandler { get; set; }
        public int RemovalRequested;
    }

    private IImageBoard[] _providers;
    private ProviderDescriptor[] _providerDescriptors;
    private readonly ILogger _logger;
    private readonly CoalescedEvent<Action> _stateChanges;
    private readonly Queue<string> _warnings = new Queue<string>();
    private readonly DownloadQueue _queue;
    private readonly ISessionStore? _sessionStore;
    private readonly IWorkStateStore? _workStore;
    private readonly bool _durableWork;
    private ThreadWorkSnapshot[] _workCache = Array.Empty<ThreadWorkSnapshot>();
    private IReadOnlyDictionary<string, ThreadWorkSnapshot> _workCacheByUrl = new ReadOnlyDictionary<string, ThreadWorkSnapshot>(
        new Dictionary<string, ThreadWorkSnapshot>(StringComparer.Ordinal));
    private readonly Dictionary<ResourceKey, WorkResource> _workResources = new();
    private readonly ThreadContentRuntime? _contentRuntime;
    private string _outputDirectory;
    private TimeSpan _threadInterval;
    private TimeSpan _boardInterval;
    private bool _persistSession;
    private bool _restoreRequired;
    private SessionSnapshot _session = new SessionSnapshot();
    private bool _restored;
    private bool _hasMutations;
    private readonly SemaphoreSlim _pauseTransitions = new(1, 1);
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly object _state = new object();
    private readonly Dictionary<ResourceKey, Watch> _watches = new Dictionary<ResourceKey, Watch>();
    private readonly HashSet<ResourceKey> _suppressed = new HashSet<ResourceKey>();
    private readonly HashSet<Task> _backgroundTasks = new HashSet<Task>();
    private readonly object _shutdownLock = new object();
    private bool _stopping;
    private int _completionScanQueued;
    private int _completionScanVersion;
    private Task? _shutdown;

    /// <summary>Asynchronous coalesced invalidation. Subscribe first, then read GetSnapshot; intermediate transitions may coalesce.</summary>
    public event Action? StateChanged
    {
        add => _stateChanges.Add(value);
        remove => _stateChanges.Remove(value);
    }

    public DownloadSessionSnapshot GetSnapshot()
    {
        RegistryView view = GetView();
        return new DownloadSessionSnapshot(Array.AsReadOnly(view.Threads.Values.ToArray()),
            Array.AsReadOnly(view.Boards.Values.ToArray()), view.IsStopping, view.Warnings) { IsPaused = view.IsPaused };
    }

    /// <summary>Captures exact resource handles with their copied state, for in-process views and removal commands.</summary>
    public RegistryView GetView()
    {
        IReadOnlyDictionary<string, ThreadWorkSnapshot> work = Volatile.Read(ref _workCacheByUrl);
        Watch[] watches;
        bool stopping;
        string[] warnings;
        lock (_state)
        {
            watches = _watches.Values.Where(watch => watch.Ready && !watch.Stopping).ToArray();
            stopping = _stopping;
            warnings = _warnings.ToArray();
        }
        IThread[] threads = watches.Where(watch => watch.Thread != null).Select(watch => watch.Thread!).ToArray();
        IReadOnlyDictionary<IThread, DownloadCounts> counts = _queue.GetThreadCounts(threads);
        Dictionary<IThread, ThreadSnapshot> threadSnapshots = new Dictionary<IThread, ThreadSnapshot>(ReferenceEqualityComparer.Instance);
        Dictionary<IBoard, BoardSnapshot> boardSnapshots = new Dictionary<IBoard, BoardSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (IThread thread in threads)
        {
            ThreadSnapshot snapshot = thread.GetSnapshot(counts[thread]);
            threadSnapshots.Add(thread, work.TryGetValue(snapshot.Url, out ThreadWorkSnapshot? state)
                ? snapshot with { Work = state } : snapshot);
        }
        foreach (Watch watch in watches)
        {
            if (watch.Board == null) continue;
            string directory = StorageLayout.GetBoardDirectory(watch.OutputRoot, watch.Board.ImageBoard.Name, watch.Board.BoardCode);
            boardSnapshots.Add(watch.Board, watch.Board.GetSnapshot(directory));
        }
        return new RegistryView(new ReadOnlyDictionary<IThread, ThreadSnapshot>(threadSnapshots),
            new ReadOnlyDictionary<IBoard, BoardSnapshot>(boardSnapshots), stopping, Array.AsReadOnly(warnings)) { IsPaused = _queue.IsPaused };
    }

    public IReadOnlyList<ThreadWorkSnapshot> GetWorkSnapshots()
    {
        return Array.AsReadOnly(Volatile.Read(ref _workCache));
    }

    private void RefreshWorkCache()
    {
        if (_workStore == null) return;
        ThreadWorkSnapshot[] current = _workStore.LoadResources().Where(resource => resource.IsThread)
            .Select(resource => new ThreadWorkSnapshot(resource.Url, resource.Intent, resource.Source,
                resource.EndObservedAt, resource.CapturePending,
                Array.AsReadOnly(_workStore.LoadMedia(resource.Url, resource.Generation).ToArray()))).ToArray();
        IReadOnlyDictionary<string, ThreadWorkSnapshot> indexed = new ReadOnlyDictionary<string, ThreadWorkSnapshot>(
            current.ToDictionary(item => item.Url, StringComparer.Ordinal));
        Volatile.Write(ref _workCacheByUrl, indexed);
        Volatile.Write(ref _workCache, current);
    }

    /// <summary>Passive registry descriptions; does not resolve URLs, make requests or create watches.</summary>
    public IReadOnlyList<ProviderDescriptor> GetProviderDescriptors() => Array.AsReadOnly(_providerDescriptors
        .Select(descriptor => descriptor with { CaptureAvailable = _contentRuntime?.IsInitialized == true
            && descriptor.Capabilities?.HasFlag(ProviderCapabilities.PostCapture) == true }).ToArray());

    public IReadOnlyList<IThread> Threads
    {
        get { lock (_state) { return _watches.Values.Where(w => w.Thread != null && w.Ready && !w.Stopping).Select(w => w.Thread!).ToArray(); } }
    }
    public IReadOnlyList<IBoard> Boards
    {
        get { lock (_state) { return _watches.Values.Where(w => w.Board != null && w.Ready && !w.Stopping).Select(w => w.Board!).ToArray(); } }
    }

    public Registry(IEnumerable<IImageBoard> providers, DownloadQueue queue, string outputDirectory,
        TimeSpan threadInterval, int boardIntervalSeconds, ISessionStore? sessionStore = null, bool persistSession = true, ILogger? logger = null,
        ThreadContentRuntime? contentRuntime = null, IWorkStateStore? workStore = null)
    {
        _contentRuntime = contentRuntime;
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (threadInterval.TotalMilliseconds < 1 || threadInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(threadInterval));
        if (boardIntervalSeconds <= 0 || TimeSpan.FromSeconds(boardIntervalSeconds).TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(boardIntervalSeconds));
        _providers = providers.ToArray();
        if (_providers.Any(p => p == null) || _providers.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != _providers.Length)
            throw new ArgumentException("Providers must have distinct, non-null source identities.", nameof(providers));
        _providerDescriptors = _providers.Select(provider =>
        {
            IProviderDescriptorSource? description = provider as IProviderDescriptorSource;
            return new ProviderDescriptor(provider.Name, provider.NiceName, provider.BaseUri, description?.Capabilities,
                Array.AsReadOnly(description?.SupportedHosts.ToArray() ?? Array.Empty<string>()), description?.AdapterVersion, false);
        }).ToArray();
        string resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        _logger = logger ?? Serilog.Core.Logger.None;
        _stateChanges = new CoalescedEvent<Action>(handler => handler(),
            exception => _logger.Error(exception, "Session observer failed"));
        _queue = queue;
        _queue.StateChanged += SignalChanged;
        _queue.PersistenceFailed += Warn;
        _outputDirectory = resolvedOutputDirectory;
        _threadInterval = threadInterval;
        _boardInterval = TimeSpan.FromSeconds(boardIntervalSeconds);
        _sessionStore = sessionStore;
        _durableWork = workStore != null;
        _workStore = workStore ?? new MemoryWorkStateStore();
        _queue.ConfigureWorkState(_workStore);
        _persistSession = persistSession;
        _restoreRequired = sessionStore != null && persistSession;
    }

    public bool IsPaused => _queue.IsPaused;
    /// <summary>Cumulative received media bytes for the current session, including failed attempts.</summary>
    public long ReceivedMediaBytes => _queue.ReceivedMediaBytes;
    public void SetPaused(bool paused) => SetPausedAsync(paused).GetAwaiter().GetResult();

    public async Task SetPausedAsync(bool paused)
    {
        await _pauseTransitions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (paused)
            {
                Task transfers = _queue.SetPausedAsync(true);
                Watch[] active;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_stopping, this);
                    lock (_state) active = _watches.Values.Where(watch => !watch.Stopping).ToArray();
                }
                finally { _gate.Release(); }
                // Provider callbacks may need _gate; never hold it while draining monitors.
                await Task.WhenAll(active.Select(watch => watch.Thread != null
                    ? watch.Thread.StopMonitoringAsync() : watch.Board!.StopMonitoringAsync()).Append(transfers)).ConfigureAwait(false);
            }
            else
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_stopping, this);
                    Watch[] active;
                    lock (_state) active = _watches.Values.Where(watch => watch.Ready && !watch.Stopping).ToArray();
                    foreach (Watch watch in active)
                    {
                        if (watch.Thread != null) watch.Thread.StartMonitoring(_queue, watch.OutputRoot, _threadInterval);
                        else watch.Board!.StartMonitoring((int)_boardInterval.TotalSeconds);
                    }
                    await _queue.SetPausedAsync(false).ConfigureAwait(false);
                }
                finally { _gate.Release(); }
            }
            SignalChanged();
        }
        finally { _pauseTransitions.Release(); }
    }

    public string GetDownloadDirectory(IBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        lock (_state)
        {
            if (!_watches.TryGetValue(Key(board), out Watch? watch) || !ReferenceEquals(watch.Board, board))
                throw new InvalidOperationException("The board is not registered with this session.");
            return StorageLayout.GetBoardDirectory(watch.OutputRoot, board.ImageBoard.Name, board.BoardCode);
        }
    }

    public async Task<AdmissionResult> AddAsync(string url)
    {
        while (true)
        {
            Task? oldLifetime = null;
            AdmissionResult? result = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                EnsureRestoredForPersistence();
                AdmissionStatus status = Resolve(url, out Resource? resource);
                if (resource == null) return new AdmissionResult(status);
                Watch? current;
                lock (_state) { _watches.TryGetValue(resource.Key, out current); }
                if (current != null)
                {
                    if (current.Stopping) oldLifetime = current.Stopped.Task;
                    else
                    {
                        if (current.Thread != null)
                        {
                            SessionSnapshot next = CopySnapshot(_session);
                            next.DiscoveredThreadUrls = next.DiscoveredThreadUrls.Where(value => value != resource.Url).ToArray();
                            if (next.DiscoveredThreadUrls.Length != _session.DiscoveredThreadUrls.Length)
                            {
                                await SaveSessionAsync(next).ConfigureAwait(false);
                                _session = next;
                            }
                            _queue.SetThreadPriority(current.Thread, DownloadPriority.Manual);
                        }
                        result = new AdmissionResult(AdmissionStatus.AlreadyPresent, current.Thread, current.Board);
                    }
                }
                else
                {
                    string root = _workResources.TryGetValue(resource.Key, out WorkResource? previousWork)
                        ? previousWork.OutputRoot : _workStore?.LoadResources().FirstOrDefault(item => item.Url == resource.Url)?.OutputRoot
                            ?? _outputDirectory;
                    result = await AdmitAsync(resource, root, persist: true).ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
            if (oldLifetime != null) { await oldLifetime.ConfigureAwait(false); continue; }
            if (result!.Status == AdmissionStatus.Added) SignalChanged();
            return result;
        }
    }

    /// <summary>Explicitly restores before user admissions. No partial restore snapshot is saved.</summary>
    public async Task RestoreAsync()
    {
        List<AdmissionResult> added = new List<AdmissionResult>();
        List<string> warnings = new List<string>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_restored) return;
            if (_hasMutations) throw new InvalidOperationException("Restore the session before adding or removing watches.");
            SessionLoadResult loaded = _sessionStore == null || !_persistSession
                ? new SessionLoadResult(new SessionSnapshot(), null, false)
                : await _sessionStore.LoadAsync().ConfigureAwait(false);
            if (loaded.Warning != null) warnings.Add(loaded.Warning);
            Dictionary<ResourceKey, Resource> resources = new Dictionary<ResourceKey, Resource>();
            Dictionary<string, string> canonicalUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> threads = NormalizeRestoredUrls(loaded.Snapshot.ThreadUrls, true, resources, canonicalUrls, warnings);
            HashSet<string> boards = NormalizeRestoredUrls(loaded.Snapshot.BoardUrls, false, resources, canonicalUrls, warnings);
            IReadOnlyList<WorkResource> durable = _workStore?.LoadResources() ?? Array.Empty<WorkResource>();
            Dictionary<string, WorkResource> byUrl = durable.ToDictionary(item => item.Url, StringComparer.Ordinal);
            foreach (WorkResource saved in durable)
            {
                if (Resolve(saved.Url, out Resource? resolved) == AdmissionStatus.Added && resolved != null)
                    _workResources[resolved.Key] = saved;
            }
            if (_workStore != null)
            {
                // The journal is authoritative; the JSON document is an idempotent projection.
                foreach (WorkResource saved in durable)
                {
                    if (saved.Intent is not (WorkIntent.Watching or WorkIntent.Repairing) || !_persistSession) continue;
                    AdmissionStatus status = Resolve(saved.Url, out Resource? resource);
                    if (resource == null) { warnings.Add($"Could not restore durable watch '{saved.Url}' ({status})."); continue; }
                    resources[resource.Key] = resource;
                    if (saved.IsThread) threads.Add(resource.Url); else boards.Add(resource.Url);
                }
                foreach (Resource resource in resources.Values.ToArray())
                {
                    if (byUrl.TryGetValue(resource.Url, out WorkResource? saved)
                        && saved.Intent is not (WorkIntent.Watching or WorkIntent.Repairing))
                    {
                        resources.Remove(resource.Key);
                        threads.Remove(resource.Url);
                        boards.Remove(resource.Url);
                    }
                }
                if (!_persistSession) { resources.Clear(); threads.Clear(); boards.Clear(); }
            }
            HashSet<string> suppressedUrls = new HashSet<string>(StringComparer.Ordinal);
            _suppressed.Clear();
            foreach (string url in loaded.Snapshot.SuppressedThreadUrls)
            {
                if (_workStore != null && byUrl.TryGetValue(url, out WorkResource? intent) && intent.Intent != WorkIntent.Suppressed)
                    continue;
                try
                {
                    AdmissionStatus status = Resolve(url, out Resource? resource);
                    if (resource?.Thread != null)
                    {
                        _suppressed.Add(resource.Key);
                        suppressedUrls.Add(resource.Url);
                        canonicalUrls[url] = resource.Url;
                    }
                    else { suppressedUrls.Add(url); warnings.Add($"Could not restore suppressed thread '{url}' ({status}); its saved entry was retained."); }
                }
                catch (Exception exception) { suppressedUrls.Add(url); warnings.Add($"Could not resolve suppressed thread '{url}': {exception.Message}"); }
            }
            if (_workStore != null)
            {
                foreach (WorkResource saved in durable.Where(item => item.IsThread && item.Intent == WorkIntent.Suppressed))
                    if (Resolve(saved.Url, out Resource? resolved) == AdmissionStatus.Added && resolved != null)
                    { _suppressed.Add(resolved.Key); suppressedUrls.Add(saved.Url); }
            }
            Dictionary<string, string> roots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in loaded.Snapshot.DownloadDirectories)
            {
                string canonical = canonicalUrls.TryGetValue(entry.Key, out string? mapped) ? mapped : entry.Key;
                roots[canonical] = entry.Value;
            }
            // Install every assignment before any provider starts and produces discoveries.
            foreach (Resource resource in resources.Values.Where(r => r.Board != null))
                roots.TryAdd(resource.Url, _outputDirectory);
            foreach (Resource resource in resources.Values.Where(r => r.Thread != null))
            {
                string boardUrl = resource.Thread!.Board.BoardUri.AbsoluteUri;
                string inherited = roots.TryGetValue(boardUrl, out string? boardRoot) ? boardRoot : _outputDirectory;
                roots.TryAdd(resource.Url, inherited);
            }
            foreach (Resource resource in resources.Values)
            {
                if (_workStore == null) break;
                if (!byUrl.TryGetValue(resource.Url, out WorkResource? saved))
                {
                    saved = _workStore.CommitIntent(new WorkResource { Url = resource.Url,
                        OutputRoot = roots[resource.Url], Generation = 1, Intent = WorkIntent.Watching,
                        IsThread = resource.Thread != null,
                        Discovered = loaded.Snapshot.DiscoveredThreadUrls.Contains(resource.Url, StringComparer.Ordinal) });
                }
                _workResources[resource.Key] = saved;
                roots[resource.Url] = saved.OutputRoot;
            }
            HashSet<string> discovered = loaded.Snapshot.DiscoveredThreadUrls
                .Select(url => canonicalUrls.TryGetValue(url, out string? canonical) ? canonical : url)
                .Where(threads.Contains).ToHashSet(StringComparer.Ordinal);
            SessionSnapshot normalized = new SessionSnapshot
            {
                ThreadUrls = threads.ToArray(), BoardUrls = boards.ToArray(), DiscoveredThreadUrls = discovered.ToArray(),
                SuppressedThreadUrls = suppressedUrls.ToArray(), DownloadDirectories = roots
            };
            bool missingAssignments = resources.Values.Any(resource =>
                !loaded.Snapshot.DownloadDirectories.TryGetValue(resource.Url, out string? savedRoot) || savedRoot != roots[resource.Url]);
            bool projectionChanged = missingAssignments
                || !threads.SetEquals(loaded.Snapshot.ThreadUrls)
                || !boards.SetEquals(loaded.Snapshot.BoardUrls)
                || !suppressedUrls.SetEquals(loaded.Snapshot.SuppressedThreadUrls)
                || !discovered.SetEquals(loaded.Snapshot.DiscoveredThreadUrls);
            if (_persistSession && !loaded.Recovered && projectionChanged)
            {
                // Migrate the entire valid legacy snapshot before activation, preserving
                // unsupported entries and suppressions. Recovery documents remain untouched.
                try { await SaveSessionAsync(normalized).ConfigureAwait(false); }
                catch (Exception exception) when (_durableWork)
                { warnings.Add($"Session projection could not be saved: {exception.Message}"); }
            }
            _session = normalized;
            RefreshWorkCache();
            _restored = true;
            _restoreRequired = false;
            foreach (Resource resource in resources.Values)
            {
                if (resource.Thread != null && _suppressed.Contains(resource.Key)) continue;
                try { added.Add(await AdmitAsync(resource, roots[resource.Url], persist: false, discovered: discovered.Contains(resource.Url)).ConfigureAwait(false)); }
                catch (Exception exception) { warnings.Add($"Could not start restored watch '{resource.Url}': {exception.Message}. Its saved entry was retained."); }
            }
        }
        finally { _gate.Release(); }
        foreach (string warning in warnings) Warn(warning);
        if (added.Count > 0 || warnings.Count > 0) SignalChanged();
    }

    public async Task ApplyOptionsAsync(SessionOptions options, bool persistSession, Action? commitSettings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        string nextRoot = Path.GetFullPath(options.DownloadPath);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (persistSession && _restoreRequired && !_restored)
                throw new InvalidOperationException("Call RestoreAsync before applying persistent session options.");
            SessionSnapshot? previousDurable = null;
            if (persistSession && _sessionStore != null)
            {
                if (commitSettings != null) previousDurable = (await _sessionStore.LoadAsync().ConfigureAwait(false)).Snapshot;
                await _sessionStore.SaveAsync(CopySnapshot(_session)).ConfigureAwait(false);
            }
            try { commitSettings?.Invoke(); }
            catch (Exception commitFailure)
            {
                if (previousDurable != null)
                {
                    try { await _sessionStore!.SaveAsync(previousDurable).ConfigureAwait(false); }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException("Settings could not be committed and session rollback also failed.", commitFailure, rollbackFailure);
                    }
                }
                throw;
            }
            _queue.UpdateMaxParallelDownloads(options.MaxParallelDownloads);
            _queue.UpdateMaxBandwidth(options.BandwidthBytesPerSecond);
            Watch[] active;
            lock (_state) { active = _watches.Values.Where(w => !w.Stopping).ToArray(); }
            foreach (Watch watch in active)
            {
                if (watch.Thread != null) watch.Thread.UpdateMonitoringInterval(options.RecheckInterval);
                else watch.Board!.UpdateMonitoringInterval(options.RecheckInterval);
            }
            _outputDirectory = nextRoot;
            _threadInterval = options.RecheckInterval;
            _boardInterval = options.RecheckInterval;
            _persistSession = persistSession;
            if (!persistSession) _restoreRequired = false;
        }
        finally { _gate.Release(); }
    }

    // Caller holds the gate. Durable intent precedes activation and public success.
    private async Task<AdmissionResult> AdmitAsync(Resource resource, string outputRoot, bool persist, bool discovered = false)
    {
        SessionSnapshot previous = _session;
        bool previouslySuppressed = _suppressed.Contains(resource.Key);
        WorkResource? durable = null;
        if (persist)
        {
            SessionSnapshot next = AdmissionSnapshot(resource, outputRoot, discovered);
            // Without a durable work authority, the session commit remains the admission barrier.
            if (!_durableWork) await SaveSessionAsync(next).ConfigureAwait(false);
            if (_workStore != null)
            {
                _workResources.TryGetValue(resource.Key, out WorkResource? old);
                old ??= _workStore.LoadResources().FirstOrDefault(item => item.Url == resource.Url);
                durable = _workStore.CommitIntent(new WorkResource { Url = resource.Url, OutputRoot = outputRoot,
                    Generation = (old?.Generation ?? 0) + 1, Intent = WorkIntent.Watching,
                    IsThread = resource.Thread != null, Discovered = discovered });
                _workResources[resource.Key] = durable;
            }
            if (_durableWork)
            {
                try { await SaveSessionAsync(next).ConfigureAwait(false); }
                catch (Exception exception) { Warn($"Session projection could not be saved: {exception.Message}"); }
            }
            _session = next;
            _hasMutations = true;
            if (resource.Thread != null) _suppressed.Remove(resource.Key);
        }
        Watch watch = new Watch { Thread = resource.Thread, Board = resource.Board, OutputRoot = outputRoot };
        lock (_state) { _watches.Add(resource.Key, watch); }
        try
        {
            if (resource.Thread != null)
            {
                if (_workStore != null)
                {
                    durable ??= _workResources[resource.Key];
                    if (resource.Thread is BaseThread durableThread)
                    {
                        durableThread.SetTimeProvider(_queue.Clock);
                        durableThread.RestoreDurableState(durable);
                        durableThread.SetLifecycleSinks((state, observed, finalSnapshot) => CommitSourceEndAsync(resource.Key, resource.Thread, state, observed, finalSnapshot),
                            () => CommitCaptureSettledAsync(resource.Key, resource.Thread));
                    }
                    _queue.BindWork(resource.Thread, durable);
                }
                if (_contentRuntime != null && resource.Thread is BaseThread { SupportsContent: true } contentThread)
                    await _contentRuntime.BindAsync(contentThread, outputRoot, restored: !persist).ConfigureAwait(false);
                watch.OwnerRegistration = resource.Thread.RegisterRemovalSink(removed =>
                {
                    if (Interlocked.Exchange(ref watch.RemovalRequested, 1) == 0)
                        Track(async () =>
                        {
                            try { await RemoveThreadAsync(removed).ConfigureAwait(false); }
                            finally { Volatile.Write(ref watch.RemovalRequested, 0); }
                        });
                });
                watch.StateHandler = (_, _) => SignalChanged();
                resource.Thread.PropertyChanged += watch.StateHandler;
                _queue.SetThreadPriority(resource.Thread, discovered ? DownloadPriority.BoardDiscovery : DownloadPriority.Manual);
                resource.Thread.StartMonitoring(_queue, watch.OutputRoot, _threadInterval);
            }
            else
            {
                watch.OwnerRegistration = resource.Board!.RegisterDiscoverySink((thread, token) =>
                    new ValueTask(AdmitDiscoveryAsync(watch, thread, token)));
                watch.StateHandler = (_, _) => SignalChanged();
                resource.Board.PropertyChanged += watch.StateHandler;
                if (resource.Board is BaseBoard baseBoard)
                {
                    baseBoard.AttachSessionQueue(_queue);
                    baseBoard.WatchGeneration = (durable ?? _workResources.GetValueOrDefault(resource.Key))?.Generation ?? 0;
                }
                resource.Board.StartMonitoring((int)_boardInterval.TotalSeconds);
                resource.Board.UpdateMonitoringInterval(_boardInterval);
            }
            lock (_state) { watch.Ready = true; }
            return new AdmissionResult(AdmissionStatus.Added, resource.Thread, resource.Board);
        }
        catch (Exception setupFailure)
        {
            List<Exception> failures = new List<Exception> { setupFailure };
            DetachObservers(watch);
            try
            {
                // A rejected second owner must not stop the first session's resource.
                if (watch.OwnerRegistration != null) await StopWatchAsync(watch).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            finally
            {
                watch.OwnerRegistration?.Dispose();
                lock (_state) { _watches.Remove(resource.Key); }
                watch.Stopped.TrySetResult();
            }
            if (persist && !_durableWork)
            {
                try
                {
                    await SaveSessionAsync(previous).ConfigureAwait(false);
                    _session = previous;
                    if (previouslySuppressed) _suppressed.Add(resource.Key);
                }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            }
            if (failures.Count > 1) throw new AggregateException("Watch setup failed and cleanup or session rollback also failed.", failures);
            throw;
        }
    }

    private async Task AdmitDiscoveryAsync(Watch parent, IThread thread, CancellationToken cancellationToken)
    {
        AdmissionResult? result = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping || parent.Stopping) return;
            ResourceKey boardKey = Key(parent.Board!);
            ResourceKey threadKey = Key(thread);
            lock (_state)
            {
                if (!_watches.TryGetValue(boardKey, out Watch? current) || !ReferenceEquals(current, parent) || _watches.ContainsKey(threadKey)) return;
            }
            if (!AllowsBoardDiscovery(parent.Board!, thread)) return;
            if (_suppressed.Contains(threadKey)) return;
            if (_workResources.TryGetValue(threadKey, out WorkResource? prior)
                && prior.Intent is WorkIntent.Completed or WorkIntent.Repairing or WorkIntent.Suppressed) return;
            Resource resource = new Resource(threadKey, thread.ThreadUri.AbsoluteUri, thread, null);
            result = await AdmitAsync(resource, parent.OutputRoot, persist: true, discovered: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Warn($"Session change failed: {exception.Message}");
            throw;
        }
        finally { _gate.Release(); }
        if (result != null) SignalChanged();
    }

    private async Task CommitSourceEndAsync(ResourceKey key, IThread thread, ThreadSourceState state, DateTimeOffset observed,
        CapturedThreadSnapshot? finalSnapshot)
    {
        if (_workStore == null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_watches.TryGetValue(key, out Watch? watch) || !ReferenceEquals(watch.Thread, thread) || watch.Stopping
                || !_workResources.TryGetValue(key, out WorkResource? current)) return;
            WorkResource? committed = _workStore.CommitEnd(current.Url, current.Generation,
                state == ThreadSourceState.Archived ? ThreadSourceStateValue.Archived : ThreadSourceStateValue.NotFound,
                observed, finalSnapshot == null ? null : JsonSerializer.Serialize(finalSnapshot));
            if (committed == null) return;
            _workResources[key] = committed;
            _queue.UpdateWorkOwner(thread, committed);
        }
        finally { _gate.Release(); }
        SignalChanged();
    }

    private async Task CommitCaptureSettledAsync(ResourceKey key, IThread thread)
    {
        if (_workStore == null) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_watches.TryGetValue(key, out Watch? watch) || !ReferenceEquals(watch.Thread, thread) || watch.Stopping
                || !_workResources.TryGetValue(key, out WorkResource? current) || !current.CapturePending) return;
            WorkResource? committed = _workStore.CommitState(current with { CapturePending = false });
            if (committed != null) { _workResources[key] = committed; _queue.UpdateWorkOwner(thread, committed); }
        }
        finally { _gate.Release(); }
        SignalChanged();
    }

    public CheckRequestStatus RequestCheck(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return RequestCheck(Key(thread), thread, null);
    }

    /// <summary>One explicit outer media attempt; it never starts a new source watch or end window.</summary>
    public int RetryMissingMedia(IThread thread, string? mediaIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        lock (_state)
        {
            if (_stopping || !_watches.TryGetValue(Key(thread), out Watch? watch) || !ReferenceEquals(watch.Thread, thread)
                || watch.Stopping || !watch.Ready) return 0;
        }
        return _queue.RequestMediaRetry(thread, mediaIdentifier);
    }

    /// <summary>Repairs a completed thread from saved retrieval metadata without polling its endpoint.</summary>
    public async Task<int> RetryCompletedMediaAsync(string threadUrl, string? mediaIdentifier = null)
    {
        if (_workStore == null) return 0;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (Resolve(threadUrl, out Resource? resource) != AdmissionStatus.Added || resource?.Thread == null) return 0;
            if (!_workResources.TryGetValue(resource.Key, out WorkResource? state))
            {
                state = _workStore.LoadResources().FirstOrDefault(item => item.Url == resource.Url);
                if (state == null) return 0;
                _workResources[resource.Key] = state;
            }
            if (state.Intent != WorkIntent.Completed) return 0;
            WorkResource? repairing = _workStore.CommitState(state with { Intent = WorkIntent.Repairing });
            if (repairing == null) return 0;
            _workResources[resource.Key] = repairing;
            await AdmitAsync(resource, repairing.OutputRoot, persist: false).ConfigureAwait(false);
            return _queue.RequestMediaRetry(resource.Thread, mediaIdentifier);
        }
        finally { _gate.Release(); }
    }

    public CheckRequestStatus RequestCheck(IBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return RequestCheck(Key(board), null, board);
    }

    private CheckRequestStatus RequestCheck(ResourceKey key, IThread? thread, IBoard? board)
    {
        lock (_state)
        {
            if (_stopping) return CheckRequestStatus.Stopping;
            if (!_watches.TryGetValue(key, out Watch? watch) || !watch.Ready || watch.Stopping
                || !ReferenceEquals(watch.Thread, thread) || !ReferenceEquals(watch.Board, board)) return CheckRequestStatus.Stale;
            if (_queue.IsPaused) return CheckRequestStatus.Paused;
            // Optional contract only signals a lifetime; no provider work runs under this lock.
            ICheckRequestSource? checks = (object?)thread as ICheckRequestSource ?? board as ICheckRequestSource;
            return checks?.RequestCheck() ?? CheckRequestStatus.Unsupported;
        }
    }

    public Task RemoveThreadAsync(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return RemoveAsync(Key(thread), thread, null);
    }
    public Task RemoveBoardAsync(IBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return RemoveAsync(Key(board), null, board);
    }

    private async Task RemoveAsync(ResourceKey key, IThread? thread, IBoard? board, bool shutdown = false)
    {
        Watch? watch;
        Task? alreadyStopping = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping && !shutdown, this);
            lock (_state)
            {
                if (!_watches.TryGetValue(key, out watch) || !ReferenceEquals(watch.Thread, thread) || !ReferenceEquals(watch.Board, board)) return;
                if (watch.Stopping) alreadyStopping = watch.Stopped.Task;
            }
            if (alreadyStopping == null)
            {
                if (!shutdown)
                {
                    string url = thread?.ThreadUri.AbsoluteUri ?? board!.BoardUri.AbsoluteUri;
                    SessionSnapshot next = RemovalSnapshot(url, thread != null);
                    if (!_durableWork) await SaveSessionAsync(next).ConfigureAwait(false);
                    if (_workStore != null && _workResources.TryGetValue(key, out WorkResource? previous))
                    {
                        WorkResource suppressed = _workStore.CommitIntent(previous with
                        { Generation = previous.Generation + 1, Intent = WorkIntent.Suppressed });
                        _workResources[key] = suppressed;
                    }
                    if (_durableWork)
                    {
                        try { await SaveSessionAsync(next).ConfigureAwait(false); }
                        catch (Exception exception) { Warn($"Session projection could not be saved: {exception.Message}"); }
                    }
                    _session = next;
                    _hasMutations = true;
                    if (thread != null) _suppressed.Add(key);
                }
                lock (_state) { watch.Stopping = true; }
                DetachObservers(watch);
            }
        }
        finally { _gate.Release(); }
        if (alreadyStopping != null) { await alreadyStopping.ConfigureAwait(false); return; }
        try { await StopWatchAsync(watch!).ConfigureAwait(false); }
        finally
        {
            watch!.OwnerRegistration?.Dispose();
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_state) { _watches.Remove(key); }
                watch!.Stopped.TrySetResult();
            }
            finally { _gate.Release(); }
            SignalChanged();
        }
    }

    private Task StopWatchAsync(Watch watch)
    {
        if (watch.Thread == null) return watch.Board!.StopMonitoringAsync();
        Task cancelled = _queue.CancelDownloadsForThreadAsync(watch.Thread);
        Task stopped;
        try { stopped = watch.Thread.StopMonitoringAsync(); }
        catch (Exception exception) { stopped = Task.FromException(exception); }
        return Task.WhenAll(cancelled, stopped);
    }

    private SessionSnapshot AdmissionSnapshot(Resource resource, string root, bool discovered)
    {
        SessionSnapshot next = CopySnapshot(_session);
        if (resource.Thread != null)
        {
            next.ThreadUrls = next.ThreadUrls.Append(resource.Url).Distinct(StringComparer.Ordinal).ToArray();
            if (discovered) next.DiscoveredThreadUrls = next.DiscoveredThreadUrls.Append(resource.Url).Distinct(StringComparer.Ordinal).ToArray();
            next.SuppressedThreadUrls = next.SuppressedThreadUrls.Where(url => url != resource.Url).ToArray();
        }
        else next.BoardUrls = next.BoardUrls.Append(resource.Url).Distinct(StringComparer.Ordinal).ToArray();
        next.DownloadDirectories[resource.Url] = root;
        return next;
    }

    private SessionSnapshot RemovalSnapshot(string url, bool thread)
    {
        SessionSnapshot next = CopySnapshot(_session);
        if (thread)
        {
            next.ThreadUrls = next.ThreadUrls.Where(value => value != url).ToArray();
            next.DiscoveredThreadUrls = next.DiscoveredThreadUrls.Where(value => value != url).ToArray();
            next.SuppressedThreadUrls = next.SuppressedThreadUrls.Append(url).Distinct(StringComparer.Ordinal).ToArray();
        }
        else next.BoardUrls = next.BoardUrls.Where(value => value != url).ToArray();
        next.DownloadDirectories.Remove(url);
        return next;
    }

    private Task SaveSessionAsync(SessionSnapshot snapshot)
        => _persistSession && _sessionStore != null ? _sessionStore.SaveAsync(CopySnapshot(snapshot)) : Task.CompletedTask;

    private void EnsureRestoredForPersistence()
    {
        if (_persistSession && _restoreRequired && !_restored)
            throw new InvalidOperationException("Call RestoreAsync before adding persistent session watches.");
    }

    private static SessionSnapshot CopySnapshot(SessionSnapshot snapshot) => new SessionSnapshot
    {
        SchemaVersion = snapshot.SchemaVersion,
        ThreadUrls = snapshot.ThreadUrls.ToArray(), BoardUrls = snapshot.BoardUrls.ToArray(),
        DiscoveredThreadUrls = snapshot.DiscoveredThreadUrls.ToArray(),
        SuppressedThreadUrls = snapshot.SuppressedThreadUrls.ToArray(),
        DownloadDirectories = new Dictionary<string, string>(snapshot.DownloadDirectories, StringComparer.Ordinal)
    };

    private HashSet<string> NormalizeRestoredUrls(IEnumerable<string> urls, bool threads,
        Dictionary<ResourceKey, Resource> resources, Dictionary<string, string> canonicalUrls, List<string> warnings)
    {
        HashSet<string> normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (string url in urls)
        {
            try
            {
                AdmissionStatus status = Resolve(url, out Resource? resource);
                if (resource != null && (resource.Thread != null) == threads)
                {
                    normalized.Add(resource.Url);
                    canonicalUrls[url] = resource.Url;
                    resources.TryAdd(resource.Key, resource);
                }
                else { normalized.Add(url); warnings.Add($"Could not restore watch '{url}' ({status}); its saved entry was retained."); }
            }
            catch (Exception exception) { normalized.Add(url); warnings.Add($"Could not resolve watch '{url}': {exception.Message}. Its saved entry was retained."); }
        }
        return normalized;
    }

    private AdmissionStatus Resolve(string url, out Resource? resource)
    {
        resource = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo))
            return AdmissionStatus.Invalid;
        IImageBoard? provider = _providers.FirstOrDefault(p => p.CanHandle(uri));
        if (provider == null) return AdmissionStatus.Unsupported;
        if (provider.IsThreadUri(uri))
        {
            IThread thread = provider.GetThread(uri);
            resource = new Resource(Key(thread), thread.ThreadUri.AbsoluteUri, thread, null);
        }
        else if (provider.IsBoardUri(uri))
        {
            IBoard board = provider.GetBoard(uri);
            resource = new Resource(Key(board), board.BoardUri.AbsoluteUri, null, board);
        }
        else return AdmissionStatus.Invalid;
        return AdmissionStatus.Added;
    }

    private static void DetachObservers(Watch watch)
    {
        if (watch.StateHandler != null)
        {
            if (watch.Thread != null) watch.Thread.PropertyChanged -= watch.StateHandler;
            if (watch.Board != null) watch.Board.PropertyChanged -= watch.StateHandler;
        }
    }

    public Task StopAsync()
    {
        lock (_shutdownLock) { return _shutdown ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        Watch[] watches;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_state) { _stopping = true; watches = _watches.Values.ToArray(); }
        }
        finally { _gate.Release(); }
        Task queueStopped = _queue.StopAllAsync();
        Task[] removals = watches.Select(w => w.Thread != null
            ? RemoveAsync(Key(w.Thread), w.Thread, null, shutdown: true)
            : RemoveAsync(Key(w.Board!), null, w.Board, shutdown: true)).ToArray();
        await Task.WhenAll(removals.Append(queueStopped)).ConfigureAwait(false);
        Task[] background;
        lock (_state) { background = _backgroundTasks.ToArray(); }
        await Task.WhenAll(background).ConfigureAwait(false);
    }

    private void Track(Func<Task> operation)
    {
        Task task = Task.Run(async () =>
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Error(exception, "Session callback failed"); Warn($"Session change failed: {exception.Message}"); }
        });
        lock (_state) { _backgroundTasks.Add(task); }
        _ = task.ContinueWith(completed => { lock (_state) { _backgroundTasks.Remove(completed); } },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void SignalChanged()
    {
        if (_workStore == null || _stopping)
        {
            _stateChanges.Signal();
            return;
        }
        Interlocked.Increment(ref _completionScanVersion);
        if (Interlocked.Exchange(ref _completionScanQueued, 1) == 0)
            Track(async () =>
            {
                int scanned = 0;
                try
                {
                    do
                    {
                        scanned = Volatile.Read(ref _completionScanVersion);
                        await TryCompleteAsync().ConfigureAwait(false);
                    } while (!_stopping && scanned != Volatile.Read(ref _completionScanVersion));
                }
                catch (Exception exception)
                {
                    // A failed durable completion is blocked work, not a self-triggering retry loop.
                    scanned = Volatile.Read(ref _completionScanVersion);
                    Warn($"Completion could not be committed: {exception.Message}", scheduleCompletion: false);
                }
                finally
                {
                    Volatile.Write(ref _completionScanQueued, 0);
                    // A completion arriving during the scan must not lose its only wakeup.
                    if (!_stopping && scanned != Volatile.Read(ref _completionScanVersion)) SignalChanged();
                }
            });
    }

    private async Task TryCompleteAsync()
    {
        List<(ResourceKey Key, Watch Watch)> completed = new();
        bool refreshed = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping || _workStore == null) return;
            RefreshWorkCache();
            refreshed = true;
            foreach (KeyValuePair<ResourceKey, WorkResource> entry in _workResources.ToArray())
            {
                WorkResource state = entry.Value;
                if (state.Intent is not (WorkIntent.Watching or WorkIntent.Repairing) || state.Source == ThreadSourceStateValue.Active || state.CapturePending) continue;
                if (!_watches.TryGetValue(entry.Key, out Watch? watch) || watch.Thread == null || watch.Stopping) continue;
                DownloadCounts counts = _queue.GetThreadCounts(new[] { watch.Thread })[watch.Thread];
                if (counts.Active != 0 || counts.Queued != 0) continue;
                IReadOnlyList<MediaWork> media = _workStore.LoadMedia(state.Url, state.Generation);
                DateTimeOffset now = _queue.UtcNow;
                if (state.Intent == WorkIntent.Repairing)
                {
                    if (media.Any(item => item.ManualAttempt)) continue;
                }
                else
                {
                    if (media.Any(item => item.Status == MediaWorkStatus.NeedsIntervention ||
                        item.Status != MediaWorkStatus.Succeeded && item.HttpStatus != 404)) continue;
                    if (media.Any(item => item.Status != MediaWorkStatus.Succeeded && item.Status != MediaWorkStatus.Expired)) continue;
                    if (media.Any(item => item.Status != MediaWorkStatus.Succeeded &&
                        (state.EndObservedAt == null || now < state.EndObservedAt.Value.AddHours(12)))) continue;
                }
                WorkResource? committed = _workStore.CommitState(state with { Intent = WorkIntent.Completed });
                if (committed == null) continue;
                _workResources[entry.Key] = committed;
                SessionSnapshot next = RemovalSnapshot(state.Url, true);
                next.SuppressedThreadUrls = next.SuppressedThreadUrls.Where(url => url != state.Url).ToArray();
                try { await SaveSessionAsync(next).ConfigureAwait(false); }
                catch (Exception exception) { Warn($"Session projection could not be saved: {exception.Message}"); }
                _session = next;
                completed.Add((entry.Key, watch));
            }
            if (completed.Count > 0) RefreshWorkCache();
        }
        finally
        {
            _gate.Release();
            // Publish the refreshed projection without scheduling another completion scan.
            // Event-only consumers must receive an invalidation after durable media results become visible.
            if (refreshed) _stateChanges.Signal();
        }
        foreach ((ResourceKey key, Watch watch) in completed)
            await RemoveAsync(key, watch.Thread, null, shutdown: true).ConfigureAwait(false);
    }

    private void Warn(string warning) => Warn(warning, scheduleCompletion: true);

    private void Warn(string warning, bool scheduleCompletion)
    {
        lock (_state)
        {
            if (_warnings.Count == 32) _warnings.Dequeue();
            _warnings.Enqueue(warning);
        }
        _logger.Warning("{SessionWarning}", warning);
        if (scheduleCompletion) SignalChanged(); else _stateChanges.Signal();
    }
    private static ResourceKey Key(IThread thread) => new ResourceKey(thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);
    private static ResourceKey Key(IBoard board) => new ResourceKey(board.ImageBoard.Name, board.BoardCode);
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _queue.StateChanged -= SignalChanged;
            _queue.PersistenceFailed -= Warn;
            _stateChanges.Dispose();
        }
    }
}
