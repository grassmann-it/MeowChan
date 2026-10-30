using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Serilog;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Rules;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Sources;

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
    private readonly DownloadQueue _downloadQueue;
    private readonly CancellationTokenSource _startupCancellation = new CancellationTokenSource();
    internal PluginSession? Plugins { get; }
    internal UpdateSession Updates { get; }
    private readonly Dictionary<IThread, WatchIdentity> _threads = new Dictionary<IThread, WatchIdentity>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IBoard, WatchIdentity> _boards = new Dictionary<IBoard, WatchIdentity>(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, IThread> _threadHandles = new Dictionary<long, IThread>();
    private readonly Dictionary<long, IBoard> _boardHandles = new Dictionary<long, IBoard>();
    private static long s_nextWatchId;
    private volatile DesktopSessionState _state = DesktopSessionState.Starting;
    private string? _startupError;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private volatile bool _disposed;

    public Config Config { get; }
    internal long ReceivedMediaBytes => _registry.ReceivedMediaBytes;
    internal ExportHistoryStore ExportHistory { get; }
    internal CollectionExportHistoryStore CollectionExportHistory { get; }
    internal IReadOnlyList<ThreadWorkSnapshot> GetThreadWorkSnapshots() => _registry.GetWorkSnapshots();
    public string ProfileDirectory => _profileLease.ProfileDirectory;
    public DesktopSessionState State => _state;
    public string? StartupError => Volatile.Read(ref _startupError);
    public event Action? StateChanged;

    public DesktopSessionHost() : this(global::MeowChan.Config.GetConfigDirectory()) { }

    // Injectable providers and storage keep lifecycle and admission tests independent of the network.
    internal DesktopSessionHost(string profileDirectory, IEnumerable<IImageBoard>? providers = null,
        Func<string, ProfileLease, ISessionStore>? sessionStoreFactory = null, ILogger? logger = null,
        Func<string, Config, UpdateSession>? updateSessionFactory = null, Func<System.Net.Http.HttpClient>? pluginTransportFactory = null,
        Func<string, ProfileLease, IWorkStateStore>? workStoreFactory = null)
    {
        _logger = logger ?? Log.Logger;
        _profileLease = ProfileLease.Acquire(profileDirectory);
        DownloadQueue? queue = null;
        try
        {
            Config = global::MeowChan.Config.LoadConfig(_profileLease.ProfileDirectory, _profileLease);
            Config.EnsureOrdinaryWritesAllowed();
            _confirmedConfig = Config.Clone();
            Commands.ShortcutService.Publish(Config.ShortcutBindings);
            ExportHistory = new ExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            CollectionExportHistory = new CollectionExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            _content = new ThreadContentRuntime(_profileLease.ProfileDirectory, _profileLease);
            queue = new DownloadQueue(Config.MaxParallelDownloads, Config.BandwidthLimiter, logger: _logger);
            _downloadQueue = queue;
            queue.UpdateMinimumFreeSpaceBytes((long)Config.MinimumFreeSpaceGiB * 1024 * 1024 * 1024);
            queue.SetPaused(Config.DownloadsPaused);
            if (providers == null) Plugins = new PluginSession(_profileLease.ProfileDirectory, _profileLease, Config, queue, _logger,
                pluginTransportFactory, GetOrCreateSourceMediaIntegrityPolicy);
            IImageBoard[] sources = providers?.ToArray() ?? Array.Empty<IImageBoard>();
            ISessionStore store = sessionStoreFactory?.Invoke(_profileLease.ProfileDirectory, _profileLease)
                ?? new SessionStore(_profileLease.ProfileDirectory, _profileLease);
            IWorkStateStore workStore = workStoreFactory?.Invoke(_profileLease.ProfileDirectory, _profileLease)
                ?? new WorkStateStore(_profileLease.ProfileDirectory, _profileLease);
            _registry = new Registry(sources, queue, Path.GetFullPath(Config.DownloadPath),
                TimeSpan.FromSeconds(Config.RecheckTimer), Config.RecheckTimer, store, Config.SaveUrlsOnExit, _logger, _content, workStore);
            _registry.ConfigureBoardAdmission(new MeowChan.Admission.BoardAdmissionStore(_profileLease.ProfileDirectory, _profileLease));
            _registry.StateChanged += OnRegistryChanged;
            Updates = updateSessionFactory?.Invoke(_profileLease.ProfileDirectory, Config)
                ?? new UpdateSession(_profileLease.ProfileDirectory, Config);
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
        _ = Task.Run(() => StartCoreAsync(completion));
        return completion.Task;
    }

    private async Task StartCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            Config.EnsureOrdinaryWritesAllowed();
            if (Plugins != null)
            {
                await Plugins.StartAsync(_startupCancellation.Token).ConfigureAwait(false);
                await PluginRegistryAdapter.RegisterBeforeRestoreAsync(_registry, Plugins.Sources, _startupCancellation.Token).ConfigureAwait(false);
            }
            await _content.InitializeAsync(_startupCancellation.Token).ConfigureAwait(false);
            Plugins?.AttachContent(_content);
            await _registry.RestoreAsync().ConfigureAwait(false);
            InitializePassiveMetadata();
            lock (_lifecycle)
            {
                if (_state == DesktopSessionState.Starting) _state = DesktopSessionState.Ready;
            }
            CompleteStartupFinalWrites(State == DesktopSessionState.Ready);
            if (State == DesktopSessionState.Ready && Config.Origin == ProfileOrigin.ConfirmedNew)
                await SaveViewPreferencesAsync(candidate => candidate.FirstRunSetupPending = true).ConfigureAwait(false);
            if (State == DesktopSessionState.Ready) Updates.Start();
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
            CompleteStartupFinalWrites(State == DesktopSessionState.Ready);
            SignalChanged();
            // Publish the final startup state before callers observe completion; the capture runs off the caller's thread.
            try { await CaptureProjectionAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Error(exception, "Startup projection capture failed."); }
            completion.TrySetResult();
        }
    }

    /// <summary>The latest complete passive view. Forced refreshes also use the bounded capture coordinator.</summary>
    public DesktopSessionView GetView() => Projection.View;

    // Caller holds _viewCapture. The registry is read before _projection is taken, so handle-map lookups
    // made from the UI (CanRequestCheck, command resolution) never wait for engine reads.
    private DesktopSessionView GetViewCore()
    {
        DesktopThreadEntry[] threads;
        DesktopBoardEntry[] boards;
        IReadOnlyList<string> warnings;
        // _viewCapture serializes captures with their handle maps so an older concurrent read cannot resurrect a stale ID.
        RegistryView view = _registry.GetView();
        lock (_projection)
        {
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
                    identity = new WatchIdentity(Interlocked.Increment(ref s_nextWatchId), item.Key.Board.ImageBoard.NiceName);
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
                    identity = new WatchIdentity(Interlocked.Increment(ref s_nextWatchId), item.Key.ImageBoard.NiceName);
                    _boards.Add(item.Key, identity);
                    _boardHandles.Add(identity.Id, item.Key);
                }
                boardEntries.Add(new DesktopBoardEntry(identity.Id, item.Value, identity.HostName, item.Key.NiceName));
            }
            threads = threadEntries.ToArray();
            boards = boardEntries.ToArray();
            warnings = _content.LoadWarning is string warning ? view.Warnings.Append(warning).ToArray() : view.Warnings;
            if (Plugins != null) warnings = warnings.Concat(Plugins.Warnings).ToArray();
        }
        lock (_lifecycle)
        {
            return new DesktopSessionView(Array.AsReadOnly(threads), Array.AsReadOnly(boards), warnings, _state, _startupError);
        }
    }

    public Task<DesktopAdmissionResult> AddAsync(string url)
        => RunInteractiveAsync(nameof(AddAsync), url.Trim(), () => AddAdmittedCoreAsync(url));

    private async Task<DesktopAdmissionResult> AddAdmittedCoreAsync(string url)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            AdmissionResult result = await _registry.AddAsync(url).ConfigureAwait(false);
            await CaptureProjectionAsync().ConfigureAwait(false);
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
        DesktopSessionProjection frame = Projection;
        return frame.View.State == DesktopSessionState.Ready && !frame.IsPaused
            && !frame.IsStale && frame.CheckableTargets.Contains(target);
    }

    internal Task<CheckRequestStatus> RequestCheckAsync(DiagnosticTarget target)
        => RunInteractiveAsync(nameof(RequestCheckAsync), target.ToString(), () => RequestCheckAdmittedCoreAsync(target));

    private async Task<CheckRequestStatus> RequestCheckAdmittedCoreAsync(DiagnosticTarget target)
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
            if (Projection.IsStale) return CheckRequestStatus.Stale;
            if (target.Kind == DiagnosticTargetKind.Thread)
                return thread == null || thread.Board.ImageBoard is PluginSourceImageBoard { Enabled: false }
                    ? CheckRequestStatus.Stale : _registry.RequestCheck(thread);
            return board == null || board.ImageBoard is PluginSourceImageBoard { Enabled: false }
                ? CheckRequestStatus.Stale : _registry.RequestCheck(board);
        }
        finally { _commands.Release(); }
    }

    internal bool CanRetryMissingMedia(long watchId)
    {
        DesktopSessionProjection frame = Projection;
        return frame.View.State == DesktopSessionState.Ready && !frame.IsStale
            && frame.View.Threads.FirstOrDefault(entry => entry.WatchId == watchId)?.Snapshot.Work
                is ThreadWorkSnapshot work && WorkStatusFormatter.Format(work).CanRetry;
    }

    internal Task<int> RetryMissingMediaAsync(long watchId, string? mediaIdentifier = null)
        => RunInteractiveAsync(nameof(RetryMissingMediaAsync), watchId.ToString(), () => RetryMissingMediaAdmittedCoreAsync(watchId, mediaIdentifier));

    private async Task<int> RetryMissingMediaAdmittedCoreAsync(long watchId, string? mediaIdentifier = null)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IThread? thread;
            lock (_projection) { _threadHandles.TryGetValue(watchId, out thread); }
            if (thread == null || Projection.IsStale || thread.Board.ImageBoard is PluginSourceImageBoard { Enabled: false }) return 0;
            // Recheck live work after command admission, never trust a visible row predicate.
            ThreadWorkSnapshot? work = _registry.GetWorkSnapshots().FirstOrDefault(item => item.Url == thread.Url);
            if (work == null || work.Intent == WorkIntent.Suppressed || !work.Media.Any(media =>
                (mediaIdentifier == null || media.MediaIdentifier == mediaIdentifier) && !media.ManualAttempt
                && media.Status is not (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded))) return 0;
            // Registry/queue retain lifetime, cooldown, cleanup fences and final manual-attempt authority.
            // A manual retry may record intent while globally paused; it never unpauses transfer.
            return _registry.RetryMissingMedia(thread, mediaIdentifier);
        }
        finally { _commands.Release(); }
    }

    internal Task<int> RetryCompletedMediaAsync(string threadUrl, string? mediaIdentifier = null)
        => RunInteractiveAsync(nameof(RetryCompletedMediaAsync), threadUrl, () => RetryCompletedMediaAdmittedCoreAsync(threadUrl, mediaIdentifier));

    private async Task<int> RetryCompletedMediaAdmittedCoreAsync(string threadUrl, string? mediaIdentifier = null)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { EnsureReady(); return await _registry.RetryCompletedMediaAsync(threadUrl, mediaIdentifier).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public Task RemoveThreadAsync(long watchId)
        => RunInteractiveAsync(nameof(RemoveThreadAsync), watchId.ToString(), () => RemoveThreadAdmittedCoreAsync(watchId));

    private async Task RemoveThreadAdmittedCoreAsync(long watchId)
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

    public Task RemoveBoardAsync(long watchId)
        => RunInteractiveAsync(nameof(RemoveBoardAsync), watchId.ToString(), () => RemoveBoardAdmittedCoreAsync(watchId));

    private async Task RemoveBoardAdmittedCoreAsync(long watchId)
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

    public Task ApplySettingsAsync(Config settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Config frozen = settings.Clone();
        // Clone starts a new editing baseline. Admission freezes an existing draft instead:
        // retain its original merge baseline, independently of later caller edits.
        frozen.SettingsBaselineCompactDensity = settings.SettingsBaselineCompactDensity;
        frozen.SettingsBaselineReadingFontSize = settings.SettingsBaselineReadingFontSize;
        frozen.SettingsBaselineWorkspaceMode = settings.SettingsBaselineWorkspaceMode;
        frozen.SettingsBaselineVideoVolume = settings.SettingsBaselineVideoVolume;
        frozen.SettingsBaselineGalleryPlayback = settings.SettingsBaselineGalleryPlayback;
        frozen.SettingsBaselineProfiles = settings.SettingsBaselineProfiles?.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        frozen.Validate();
        Commands.ShortcutService.ValidateForCommit(frozen.ShortcutBindings);
        return RunInteractiveAsync(nameof(ApplySettingsAsync), "settings", () => ApplySettingsAdmittedCoreAsync(frozen),
            frozen.RememberReadingPosition ? null : InvalidateReadingPositionWrites);
    }

    private async Task ApplySettingsAdmittedCoreAsync(Config settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await FlushPreferencesAsync().ConfigureAwait(false);
        settings.Validate();
        Commands.ShortcutService.ValidateForCommit(settings.ShortcutBindings);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            // Merge the edited fields into the latest live configuration. Layout,
            // density, gallery context and pause are direct preferences and may have
            // changed while a settings draft was open.
            Config candidate = Config.Clone();
            candidate.CopySettingsFieldsFrom(settings);
            candidate.Validate();
            if (!candidate.RememberReadingPosition) DeleteReadingPositionFile();
            SessionOptions options = new SessionOptions
            {
                DownloadPath = candidate.DownloadPath,
                RecheckInterval = TimeSpan.FromSeconds(candidate.RecheckTimer),
                MaxParallelDownloads = candidate.MaxParallelDownloads,
                BandwidthBytesPerSecond = candidate.BandwidthLimiter
            };
            await _registry.ApplyOptionsAsync(options, candidate.SaveUrlsOnExit,
                () => candidate.SaveConfig(_profileLease.ProfileDirectory)).ConfigureAwait(false);
            PublishConfig(candidate);
            Commands.ShortcutService.Publish(Config.ShortcutBindings);
            _downloadQueue.UpdateMinimumFreeSpaceBytes((long)Config.MinimumFreeSpaceGiB * 1024 * 1024 * 1024);
            await Updates.ConfigureAsync(Config).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
        SignalChanged();
    }

    internal Task<FirstRunSetupCommitResult> CompleteFirstRunSetupAsync(FirstRunSetupValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return RunInteractiveAsync<FirstRunSetupCommitResult>(nameof(CompleteFirstRunSetupAsync), "first-run",
            () => CompleteFirstRunSetupAdmittedCoreAsync(values));
    }

    private async Task<FirstRunSetupCommitResult> CompleteFirstRunSetupAdmittedCoreAsync(FirstRunSetupValues values)
    {
        await FlushPreferencesAsync().ConfigureAwait(false);
        await _commands.WaitAsync().ConfigureAwait(false);
        bool persisted = false;
        try
        {
            EnsureReady();
            if (!FirstRunSetupCoordinator.CanOffer(Config, State) || !Config.FirstRunSetupPending)
                throw new InvalidOperationException("First-run setup is no longer pending for this profile.");
            FirstRunSetupCoordinator.ValidateValues(values, Config.DownloadPathBaseDirectory);
            Config candidate = Config.Clone();
            candidate.DownloadPath = values.DownloadPath;
            candidate.BandwidthLimiter = values.BandwidthBytesPerSecond;
            candidate.MaxParallelDownloads = values.ParallelDownloads;
            candidate.FirstRunSetupPending = false;
            candidate.FirstRunSetupVersion = 1;
            candidate.Validate();
            SessionOptions options = new()
            {
                DownloadPath = candidate.DownloadPath, RecheckInterval = TimeSpan.FromSeconds(candidate.RecheckTimer),
                MaxParallelDownloads = candidate.MaxParallelDownloads, BandwidthBytesPerSecond = candidate.BandwidthLimiter
            };
            await _registry.ApplyOptionsAsync(options, candidate.SaveUrlsOnExit, () =>
            {
                candidate.SaveConfig(_profileLease.ProfileDirectory);
                persisted = true;
                PublishConfig(candidate);
            }).ConfigureAwait(false);
            SignalChanged();
            return new(true);
        }
        catch (Exception error) when (persisted)
        {
            _logger.Warning(error, "First-run settings were durably saved, but activation reported a failure.");
            SignalChanged();
            return new(true, error.Message);
        }
        finally { _commands.Release(); }
    }

    public bool IsPaused => Projection.IsPaused;
    public bool IsPauseTransitioning { get; private set; }

    /// <summary>
    /// Runs on the thread pool from the first instruction: even when the command gate is free, the synchronous
    /// configuration write must never execute on the dispatcher that accepted the click.
    /// </summary>
    public Task SetPausedAsync(bool paused) => RunControlAsync("pause", paused.ToString(), () => SetPausedCoreAsync(paused));

    private async Task SetPausedCoreAsync(bool paused)
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
                PublishConfig(candidate);
            }
            finally { IsPauseTransitioning = false; SignalChanged(); }
        }
        finally { _commands.Release(); }
        SignalChanged();
        // The caller resumes with the confirmed pause state already published.
        await CaptureProjectionAsync().ConfigureAwait(false);
    }

    internal IReadOnlyList<ProviderDescriptor> GetProviderDescriptors() => Projection.Providers;
    internal IReadOnlyList<DownloadTargetWaitState> GetTargetWaitStates() => Projection.TargetWaitStates;
    internal Task CheckTargetsAsync(CancellationToken cancellationToken = default)
        => RunControlAsync("check-targets", "all", () => _downloadQueue.CheckTargetsAsync(cancellationToken));

    internal MediaRuleSet MediaRules => _content.Rules.Current;
    internal EffectiveMediaRule ResolveRules(MediaRuleScope scope) => _content.Rules.Resolve(scope);
    internal Task<ArchiveReader> OpenArchiveReaderAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _content.OpenReaderAsync(cancellationToken), cancellationToken);

    internal Task<MediaRuleSet> SaveRulesAsync(IEnumerable<MediaRule> rules, string expectedRevision)
    {
        MediaRuleSet frozen = new(expectedRevision, rules);
        return RunInteractiveAsync(nameof(SaveRulesAsync), "rules", () => SaveRulesAdmittedCoreAsync(frozen.Rules, expectedRevision));
    }

    private async Task<MediaRuleSet> SaveRulesAdmittedCoreAsync(IEnumerable<MediaRule> rules, string expectedRevision)
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

    internal Task SetCaptureEnabledAsync(ResourceId id, bool enabled)
        => RunInteractiveAsync(nameof(SetCaptureEnabledAsync), id.ToString(), () => SetCaptureEnabledAdmittedCoreAsync(id, enabled));

    private async Task SetCaptureEnabledAdmittedCoreAsync(ResourceId id, bool enabled)
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
            Config.EnsureOrdinaryWritesAllowed();
            if (_state != DesktopSessionState.Ready)
                throw new InvalidOperationException("The desktop session is not ready to change watches or settings.");
        }
    }

    private Task? _engineStopTask;
    private Task _acceptedDrain = Task.CompletedTask;

    /// <summary>
    /// Phase one: synchronously close admission, then request engine cancellation on a worker, independently
    /// of gallery/native cleanup and of the profile command gate. The returned task is the actual engine drain.
    /// Final view preferences and conscious reading-position writes remain accepted until StopAsync seals them.
    /// </summary>
    internal Task BeginShutdown()
    {
        lock (_lifecycle)
        {
            if (_engineStopTask != null) return _engineStopTask;
            CompleteStartupFinalWrites(_state == DesktopSessionState.Ready);
            _state = DesktopSessionState.Stopping;
            _acceptedDrain = CloseInteractiveAdmission();
            Task restoration = _startTask ?? Task.CompletedTask;
            _engineStopTask = Task.Run(async () =>
            {
                using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.shutdown.engine", epoch: _epoch);
                // CancelAsync does not execute arbitrary cancellation callbacks on the dispatcher.
                Task startupCanceled = _startupCancellation.CancelAsync();
                // Queue admission/cancellation must not wait for restoration, profile writes or native cleanup.
                Task queueStopped = _downloadQueue.StopAllAsync();
                RetireSourceMediaIntegrityPolicies();
                List<Exception> errors = new();
                async Task Drain(Task task)
                { try { await task.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
                await Drain(startupCanceled).ConfigureAwait(false);
                await Drain(restoration).ConfigureAwait(false);
                Task registryStopped = _registry.StopAsync();
                await Drain(Task.WhenAll(queueStopped, registryStopped)).ConfigureAwait(false);
                try { await Updates.DisposeAsync().ConfigureAwait(false); _updatesQuiescent = true; } catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) throw new AggregateException("Engine shutdown reported failures.", errors);
            });
            PublishLifecycleState(notifyOnWorker: true);
            return _engineStopTask;
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stopTask != null) return _stopTask;
            Task engineStopped = BeginShutdown();
            _stopTask = Task.Run(() => StopCoreAsync(engineStopped));
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(Task engineStopped)
    {
        using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.shutdown.drain", epoch: _epoch);
        // Seal only the final writers. Already accepted writes are drained even if the engine is still stopping.
        Task finalWrites = SealFinalWritesAsync();
        List<Exception> errors = new();
        bool quiescent = true;
        async Task Observe(Func<Task> action, bool ownsLifetime = false)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); if (ownsLifetime) quiescent = false; }
        }
        await Observe(() => Task.WhenAll(engineStopped, _acceptedDrain, finalWrites)).ConfigureAwait(false);
        quiescent &= _updatesQuiescent;
        await Observe(CloseProjectionAsync, ownsLifetime: true).ConfigureAwait(false);
        await Observe(() => _lifecycleNotification).ConfigureAwait(false);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            await Observe(() => _registry.DisposeAsync().AsTask(), ownsLifetime: true).ConfigureAwait(false);
            await Observe(() => _content.DisposeAsync().AsTask(), ownsLifetime: true).ConfigureAwait(false);
            if (Plugins != null) await Observe(() => Plugins.DisposeAsync().AsTask(), ownsLifetime: true).ConfigureAwait(false);
            _registry.StateChanged -= OnRegistryChanged;
            _ownersQuiescent = quiescent;
            lock (_lifecycle) { if (quiescent) _state = DesktopSessionState.Stopped; }
        }
        finally { _commands.Release(); }
        PublishLifecycleState();
        lock (_lifecycle) _projectionClosed = true;
        if (errors.Count != 0) throw new AggregateException("Session shutdown reported failures.", errors);
    }

    private bool _ownersQuiescent;
    private volatile bool _updatesQuiescent;
    private bool _externalOwnershipBlocked;
    internal bool IsProfileReleased { get { lock (_lifecycle) return _disposed; } }
    internal void RetainProfileForExternalCleanupFailure()
    { lock (_lifecycle) _externalOwnershipBlocked = true; }

    private void EnsureFinalWriteAllowed()
    {
        lock (_lifecycle)
        {
            Config.EnsureOrdinaryWritesAllowed();
            if (_finalWriteStartupState != 1 || (_state != DesktopSessionState.Ready && _state != DesktopSessionState.Stopping))
                throw new InvalidOperationException("The desktop session is not ready to save preferences.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
        lock (_lifecycle)
        {
            if (!_disposed && _ownersQuiescent && !_externalOwnershipBlocked)
            {
            _disposed = true;
            _projectionClosed = true;
            ProjectionPublished = null;
            _startupCancellation.Dispose();
            lock (_ratingsSync)
            {
                _ratingsStore = null; _ratingEntries = null; _ratingValues = null; _ratingLoadError = null;
            }
            _profileLease.Dispose();
            StateChanged = null;
            }
        }
        }
    }

    private void OnRegistryChanged() => SignalChanged();

    private void SignalChanged() => RequestProjection();

    // Invalidations now follow complete publication, so background/test observers also read the passive GetView.
    private void NotifyStateObservers()
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
