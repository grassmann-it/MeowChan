using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Boards.EightKun;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Serilog;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Rules;

namespace MeowChan.Desktop;

internal enum DesktopSessionState { Starting, Ready, RecoveryFailed, Stopping, Stopped }

/// <summary>A passive desktop projection. WatchId identifies this watch lifetime, not just its URL.</summary>
public sealed record DesktopThreadEntry(long WatchId, ThreadSnapshot Snapshot, string HostName, string BoardName);
public sealed record DesktopBoardEntry(long WatchId, BoardSnapshot Snapshot, string HostName, string BoardName);

internal sealed record DesktopAdmissionResult(AdmissionStatus Status, long? WatchId = null);
internal sealed record DesktopSessionView(IReadOnlyList<DesktopThreadEntry> Threads,
    IReadOnlyList<DesktopBoardEntry> Boards, IReadOnlyList<string> Warnings,
    DesktopSessionState State, string? StartupError);

/// <summary>
/// Owns one desktop session and its profile. Observers receive invalidations on arbitrary threads;
/// the presentation layer dispatches a fresh GetView read and never receives live provider objects.
/// StopAsync drains the engine; DisposeAsync releases the profile after native UI cleanup.
/// </summary>
internal sealed partial class DesktopSessionHost : IAsyncDisposable
{
    private sealed record WatchIdentity(long Id, string HostName);

    private readonly object _lifecycle = new object();
    private readonly object _projection = new object();
    private readonly SemaphoreSlim _commands = new SemaphoreSlim(1, 1);
    private readonly ProfileLease _profileLease;
    private readonly Registry _registry;
    private readonly ThreadContentRuntime _content;
    private readonly ILogger _logger;
    private readonly Dictionary<IThread, WatchIdentity> _threads = new Dictionary<IThread, WatchIdentity>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IBoard, WatchIdentity> _boards = new Dictionary<IBoard, WatchIdentity>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, IThread> _threadHandles = new Dictionary<long, IThread>();
    private readonly Dictionary<long, IBoard> _boardHandles = new Dictionary<long, IBoard>();
    private long _nextWatchId;
    private DesktopSessionState _state = DesktopSessionState.Starting;
    private string? _startupError;
    private Task? _startTask;
    private Task? _stopTask;
    private bool _disposed;

    public Config Config { get; }
    internal long ReceivedMediaBytes => _registry.ReceivedMediaBytes;
    internal ExportHistoryStore ExportHistory { get; }
    internal CollectionExportHistoryStore CollectionExportHistory { get; }
    internal IReadOnlyList<ThreadWorkSnapshot> GetThreadWorkSnapshots() => _registry.GetWorkSnapshots();
    public string ProfileDirectory => _profileLease.ProfileDirectory;
    public DesktopSessionState State { get { lock (_lifecycle) { return _state; } } }
    public string? StartupError { get { lock (_lifecycle) { return _startupError; } } }
    public event Action? StateChanged;

    public DesktopSessionHost() : this(global::MeowChan.Config.GetConfigDirectory()) { }

    // Injectable providers and storage keep lifecycle and admission tests independent of the network.
    internal DesktopSessionHost(string profileDirectory, IEnumerable<IImageBoard>? providers = null,
        Func<string, ProfileLease, ISessionStore>? sessionStoreFactory = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
        _profileLease = ProfileLease.Acquire(profileDirectory);
        DownloadQueue? queue = null;
        try
        {
            Config = global::MeowChan.Config.LoadConfig(_profileLease.ProfileDirectory);
            ExportHistory = new ExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            CollectionExportHistory = new CollectionExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            _content = new ThreadContentRuntime(_profileLease.ProfileDirectory, _profileLease);
            queue = new DownloadQueue(Config.MaxParallelDownloads, Config.BandwidthLimiter, logger: _logger);
            queue.SetPaused(Config.DownloadsPaused);
            IImageBoard[] sources = providers?.ToArray() ?? new IImageBoard[]
            {
                new FourChanImageBoard(logger: _logger), new EightKunImageBoard(logger: _logger)
            };
            ISessionStore store = sessionStoreFactory?.Invoke(_profileLease.ProfileDirectory, _profileLease)
                ?? new SessionStore(_profileLease.ProfileDirectory, _profileLease);
            IWorkStateStore workStore = new WorkStateStore(_profileLease.ProfileDirectory, _profileLease);
            _registry = new Registry(sources, queue, Path.GetFullPath(Config.DownloadPath),
                TimeSpan.FromSeconds(Config.RecheckTimer), Config.RecheckTimer, store, Config.SaveUrlsOnExit, _logger, _content, workStore);
            _registry.ConfigureBoardAdmission(new MeowChan.Admission.BoardAdmissionStore(_profileLease.ProfileDirectory, _profileLease));
            _registry.StateChanged += OnRegistryChanged;
        }
        catch
        {
            // The registry has not started any watches before StartAsync.
            queue?.StopAll();
            _profileLease.Dispose();
            throw;
        }
    }

    /// <summary>Runs restoration once. A recovery failure is retained in State/StartupError and keeps commands disabled.</summary>
    public Task StartAsync()
    {
        TaskCompletionSource completion;
        lock (_lifecycle)
        {
            if (_startTask != null) return _startTask;
            if (_state != DesktopSessionState.Starting)
                throw new InvalidOperationException("The desktop session is stopping or stopped.");
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startTask = completion.Task;
        }
        _ = StartCoreAsync(completion);
        return completion.Task;
    }

    private async Task StartCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await _content.InitializeAsync().ConfigureAwait(false);
            await _registry.RestoreAsync().ConfigureAwait(false);
            lock (_lifecycle)
            {
                if (_state == DesktopSessionState.Starting) _state = DesktopSessionState.Ready;
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to restore the desktop session.");
            lock (_lifecycle)
            {
                _startupError = exception.Message;
                if (_state == DesktopSessionState.Starting) _state = DesktopSessionState.RecoveryFailed;
            }
        }
        finally
        {
            SignalChanged();
            completion.TrySetResult();
        }
    }

    public DesktopSessionView GetView()
    {
        DesktopThreadEntry[] threads;
        DesktopBoardEntry[] boards;
        IReadOnlyList<string> warnings;
        lock (_projection)
        {
            // Serialize captures with their handle maps so an older concurrent read cannot resurrect a stale ID.
            RegistryView view = _registry.GetView();
            foreach (IThread thread in _threads.Keys.Where(thread => !view.Threads.ContainsKey(thread)).ToArray())
            {
                _threadHandles.Remove(_threads[thread].Id);
                _threads.Remove(thread);
            }
            foreach (IBoard board in _boards.Keys.Where(board => !view.Boards.ContainsKey(board)).ToArray())
            {
                _boardHandles.Remove(_boards[board].Id);
                _boards.Remove(board);
            }

            List<DesktopThreadEntry> threadEntries = new List<DesktopThreadEntry>(view.Threads.Count);
            foreach (KeyValuePair<IThread, ThreadSnapshot> item in view.Threads)
            {
                if (!_threads.TryGetValue(item.Key, out WatchIdentity? identity))
                {
                    identity = new WatchIdentity(checked(++_nextWatchId), item.Key.Board.ImageBoard.NiceName);
                    _threads.Add(item.Key, identity);
                    _threadHandles.Add(identity.Id, item.Key);
                }
                threadEntries.Add(new DesktopThreadEntry(identity.Id, item.Value, identity.HostName, item.Key.Board.NiceName));
            }
            List<DesktopBoardEntry> boardEntries = new List<DesktopBoardEntry>(view.Boards.Count);
            foreach (KeyValuePair<IBoard, BoardSnapshot> item in view.Boards)
            {
                if (!_boards.TryGetValue(item.Key, out WatchIdentity? identity))
                {
                    identity = new WatchIdentity(checked(++_nextWatchId), item.Key.ImageBoard.NiceName);
                    _boards.Add(item.Key, identity);
                    _boardHandles.Add(identity.Id, item.Key);
                }
                boardEntries.Add(new DesktopBoardEntry(identity.Id, item.Value, identity.HostName, item.Key.NiceName));
            }
            threads = threadEntries.ToArray();
            boards = boardEntries.ToArray();
            warnings = _content.LoadWarning is string warning ? view.Warnings.Append(warning).ToArray() : view.Warnings;
        }
        lock (_lifecycle)
        {
            return new DesktopSessionView(Array.AsReadOnly(threads), Array.AsReadOnly(boards), warnings, _state, _startupError);
        }
    }

    public async Task<DesktopAdmissionResult> AddAsync(string url)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            AdmissionResult result = await _registry.AddAsync(url).ConfigureAwait(false);
            GetView();
            long? watchId = null;
            lock (_projection)
            {
                if (result.Thread != null && _threads.TryGetValue(result.Thread, out WatchIdentity? thread)) watchId = thread.Id;
                if (result.Board != null && _boards.TryGetValue(result.Board, out WatchIdentity? board)) watchId = board.Id;
            }
            return new DesktopAdmissionResult(result.Status, watchId);
        }
        finally { _commands.Release(); }
    }

    internal bool CanRequestCheck(DiagnosticTarget target)
    {
        if (State != DesktopSessionState.Ready || IsPaused) return false;
        lock (_projection)
        {
            return target.Kind == DiagnosticTargetKind.Thread
                ? _threadHandles.TryGetValue(target.WatchId, out IThread? thread) && thread is ICheckRequestSource
                : _boardHandles.TryGetValue(target.WatchId, out IBoard? board) && board is ICheckRequestSource;
        }
    }

    internal async Task<CheckRequestStatus> RequestCheckAsync(DiagnosticTarget target)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IThread? thread;
            IBoard? board;
            lock (_projection)
            {
                _threadHandles.TryGetValue(target.WatchId, out thread);
                _boardHandles.TryGetValue(target.WatchId, out board);
            }
            if (target.Kind == DiagnosticTargetKind.Thread)
                return thread == null ? CheckRequestStatus.Stale : _registry.RequestCheck(thread);
            return board == null ? CheckRequestStatus.Stale : _registry.RequestCheck(board);
        }
        finally { _commands.Release(); }
    }

    internal async Task<int> RetryMissingMediaAsync(long watchId, string? mediaIdentifier = null)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IThread? thread;
            lock (_projection) { _threadHandles.TryGetValue(watchId, out thread); }
            return thread == null ? 0 : _registry.RetryMissingMedia(thread, mediaIdentifier);
        }
        finally { _commands.Release(); }
    }

    internal async Task<int> RetryCompletedMediaAsync(string threadUrl, string? mediaIdentifier = null)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { EnsureReady(); return await _registry.RetryCompletedMediaAsync(threadUrl, mediaIdentifier).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public async Task RemoveThreadAsync(long watchId)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IThread? thread;
            lock (_projection) { _threadHandles.TryGetValue(watchId, out thread); }
            if (thread != null)
            {
                await _registry.RemoveThreadAsync(thread).ConfigureAwait(false);
                lock (_projection)
                {
                    _threadHandles.Remove(watchId);
                    _threads.Remove(thread);
                }
            }
        }
        finally { _commands.Release(); }
    }

    public async Task RemoveBoardAsync(long watchId)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IBoard? board;
            lock (_projection) { _boardHandles.TryGetValue(watchId, out board); }
            if (board != null)
            {
                await _registry.RemoveBoardAsync(board).ConfigureAwait(false);
                lock (_projection)
                {
                    _boardHandles.Remove(watchId);
                    _boards.Remove(board);
                }
            }
        }
        finally { _commands.Release(); }
    }

    public async Task ApplySettingsAsync(Config settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Config candidate = Config.Clone();
        // CopyFrom resolves relative paths using the edit's original loading directory,
        // before both the durable settings write and the session options see the value.
        candidate.CopyFrom(settings);
        candidate.Validate();
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            // Settings drafts must not overwrite the separately controlled live pause state.
            candidate.DownloadsPaused = Config.DownloadsPaused;
            SessionOptions options = new SessionOptions
            {
                DownloadPath = candidate.DownloadPath,
                RecheckInterval = TimeSpan.FromSeconds(candidate.RecheckTimer),
                MaxParallelDownloads = candidate.MaxParallelDownloads,
                BandwidthBytesPerSecond = candidate.BandwidthLimiter
            };
            await _registry.ApplyOptionsAsync(options, candidate.SaveUrlsOnExit,
                () => candidate.SaveConfig(_profileLease.ProfileDirectory)).ConfigureAwait(false);
            Config.CopyFrom(candidate);
        }
        finally { _commands.Release(); }
        SignalChanged();
    }

    public bool IsPaused => _registry.IsPaused;
    public bool IsPauseTransitioning { get; private set; }

    public async Task SetPausedAsync(bool paused)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            Config candidate = Config.Clone();
            candidate.DownloadsPaused = paused;
            candidate.SaveConfig(_profileLease.ProfileDirectory);
            IsPauseTransitioning = true;
            SignalChanged();
            try
            {
                await _registry.SetPausedAsync(paused).ConfigureAwait(false);
                Config.CopyFrom(candidate);
            }
            finally { IsPauseTransitioning = false; SignalChanged(); }
        }
        finally { _commands.Release(); }
        SignalChanged();
    }

    internal IReadOnlyList<ProviderDescriptor> GetProviderDescriptors() => _registry.GetProviderDescriptors();

    internal MediaRuleSet MediaRules => _content.Rules.Current;
    internal EffectiveMediaRule ResolveRules(MediaRuleScope scope) => _content.Rules.Resolve(scope);
    internal Task<ArchiveReader> OpenArchiveReaderAsync(CancellationToken cancellationToken = default) =>
        _content.OpenReaderAsync(cancellationToken);

    internal async Task<MediaRuleSet> SaveRulesAsync(IEnumerable<MediaRule> rules, string expectedRevision)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            MediaRuleSet result = await _content.Rules.SaveAsync(rules, expectedRevision).ConfigureAwait(false);
            SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }

    internal async Task SetCaptureEnabledAsync(ResourceId id, bool enabled)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            BaseThread? active;
            lock (_projection)
                active = _threadHandles.Values.OfType<BaseThread>().FirstOrDefault(thread => thread.SupportsContent
                    && thread.Board.ImageBoard.Name == id.Source && thread.Board.BoardCode == id.Board && thread.ThreadId == id.Thread);
            if (active != null) await active.SetCaptureEnabledAsync(enabled).ConfigureAwait(false);
            else await _content.Catalog.SetCaptureEnabledAsync(id, enabled).ConfigureAwait(false);
            SignalChanged();
        }
        finally { _commands.Release(); }
    }

    private void EnsureReady()
    {
        lock (_lifecycle)
        {
            if (_state != DesktopSessionState.Ready)
                throw new InvalidOperationException("The desktop session is not ready to change watches or settings.");
        }
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        Task restoration;
        lock (_lifecycle)
        {
            if (_stopTask != null) return _stopTask;
            _state = DesktopSessionState.Stopping;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            restoration = _startTask ?? Task.CompletedTask;
        }
        SignalChanged();
        _ = StopCoreAsync(restoration, completion);
        return completion.Task;
    }

    private async Task StopCoreAsync(Task restoration, TaskCompletionSource completion)
    {
        try
        {
            await restoration.ConfigureAwait(false);
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await _registry.StopAsync().ConfigureAwait(false);
                await _registry.DisposeAsync().ConfigureAwait(false);
                await _content.DisposeAsync().ConfigureAwait(false);
                _registry.StateChanged -= OnRegistryChanged;
                lock (_lifecycle) { _state = DesktopSessionState.Stopped; }
            }
            finally { _commands.Release(); }
            SignalChanged();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _profileLease.Dispose();
            StateChanged = null;
        }
    }

    private void OnRegistryChanged() => SignalChanged();

    private void SignalChanged()
    {
        Action? observers = StateChanged;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception exception) { _logger.Error(exception, "Desktop session observer failed."); }
        }
    }
}
