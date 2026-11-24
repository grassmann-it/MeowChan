using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Admission;
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
        Func<string, ProfileLease, IWorkStateStore>? workStoreFactory = null, Stats.IoStatsHub? ioStats = null)
    {
        _logger = logger ?? Log.Logger;
        _ioStats = ioStats ?? Stats.IoStatsHub.Shared;
        _profileLease = ProfileLease.Acquire(profileDirectory);
        DownloadQueue? queue = null;
        try
        {
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.host.ctor", "lease");
            _profileStartupAdmission = _profileLease.Admission.EnterStartupWriter();
            Config = global::MeowChan.Config.LoadConfig(_profileLease.ProfileDirectory, _profileLease);
            // Fail closed: nothing may write into a profile whose settings were not read as they are. The startup
            // window explains the files and the way out (SettingsRecoveryRequiredException.LocalizedMessage).
            if (!Config.AllowsOrdinaryWrites) throw SettingsRecoveryRequiredException.For(Config, _profileLease.ProfileDirectory);
            ApplyArchivePersistenceLimits();
            _boardAdmission = OpenBoardAdmissionStore();
            _confirmedConfig = Config.Clone();
            Commands.ShortcutService.Publish(Config.ShortcutBindings);
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.host.ctor", "config-done");
            ExportHistory = new ExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            CollectionExportHistory = new CollectionExportHistoryStore(_profileLease.ProfileDirectory, _profileLease);
            _content = new ThreadContentRuntime(_profileLease.ProfileDirectory, _profileLease);
            queue = new DownloadQueue(Config.MaxParallelDownloads, Config.BandwidthLimiter, logger: _logger);
            queue.UpdateParallelDownloadsSafetyLimit(Config.ParallelDownloadsSafetyLimit);
            _downloadQueue = queue;
            queue.UpdateMinimumFreeSpaceBytes((long)Config.MinimumFreeSpaceGiB * 1024 * 1024 * 1024);
            ApplyPrivateTagLimits();
            ApplyStoreLimits();
            queue.PrivateTagLimits = _privateTagLimits;
            queue.SetPaused(Config.DownloadsPaused);
            if (providers == null)
            {
                Plugins = new PluginSession(_profileLease.ProfileDirectory, _profileLease, Config, queue, _logger,
                    pluginTransportFactory, GetOrCreateSourceMediaIntegrityPolicy, _commands);
                // The read-only plugin probe needs nothing the window or the catalog builds: it overlaps their
                // construction and is awaited in StartCoreAsync where plugin startup ran before. Activation,
                // provenance and adapters keep their place there. Shutdown's startup cancellation also ends it.
                Plugins.BeginProbe(_startupCancellation.Token);
            }
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.host.ctor", "plugin-session-done");
            IImageBoard[] sources = providers?.ToArray() ?? Array.Empty<IImageBoard>();
            ISessionStore store = sessionStoreFactory?.Invoke(_profileLease.ProfileDirectory, _profileLease)
                ?? new SessionStore(_profileLease.ProfileDirectory, _profileLease);
            IWorkStateStore workStore = workStoreFactory?.Invoke(_profileLease.ProfileDirectory, _profileLease)
                ?? new WorkStateStore(_profileLease.ProfileDirectory, _profileLease, _profileStartupAdmission);
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.host.ctor", "stores-done");
            _registry = new Registry(sources, queue, Path.GetFullPath(Config.DownloadPath),
                TimeSpan.FromSeconds(Config.RecheckTimer), Config.RecheckTimer, store, Config.SaveUrlsOnExit, _logger, _content, workStore);
            _registry.PrepareProfileProducers(_profileLease);
            ApplyBooruCatalogLimit();
            _booruProducers = new MeowChan.Booru.BooruProducerLifecycle(_profileLease.Admission);
            _hostFinalWriteParticipant = new HostFinalWriteParticipant(this);
            _registry.RegisterProfileFinalWriteParticipant(_hostFinalWriteParticipant);
            _registry.ConfigureBoardAdmission(_boardAdmission);
            _registry.StateChanged += OnRegistryChanged;
            queue.MediaTagsCarriedOver += OnMediaTagsCarriedOver;
            Updates = updateSessionFactory?.Invoke(_profileLease.ProfileDirectory, Config)
                ?? new UpdateSession(_profileLease.ProfileDirectory, Config);
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.host.ctor", "updates-done");
            StartIoStats();
        }
        catch
        {
            // The registry has not started any watches before StartAsync. Even a
            // cleanup failure must release startup admission and the profile lease.
            try { queue?.StopAll(); }
            finally
            {
                try
                {
                    // An early plugin probe may already import or run describe workers. It ends (workers reaped,
                    // cache writes stopped) before the profile lease is released; its own failure never replaces
                    // the constructor's exception. No continuation here needs the calling (UI) thread.
                    try { _startupCancellation.Cancel(); Plugins?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception error) { _logger.Error(error, "Failed to dispose the plugin session after a failed session construction."); }
                    FinishProfileStartup(succeeded: false);
                    _profileLease.Admission.BeginShutdown();
                    _profileLease.Admission.CompleteShutdown();
                }
                finally { _profileLease.Dispose(); }
            }
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

    // Deterministic startup/stop race seams, all outside product monitors.
    // They observe existing ownership only, never grant authority or complete work.
    internal Func<Task>? BeforeStartupInitialSaveForTests { get; set; }
    internal Action<bool>? AfterStartupFinalWriteCleanupClaimForTests { get; set; }
    internal Action? AfterFinalWriteSealForTests { get; set; }

    /// <summary>
    /// UI decision for native transfers that stopped before their commit; true discards them. Without a
    /// decision (or on false) startup keeps failing with the recovery error, as before.
    /// </summary>
    internal Func<IReadOnlyList<MeowChan.Archive.InterruptedNativeMediaOperation>, CancellationToken, Task<bool>>? InterruptedTransferDecision { get; set; }

    // Still under the startup admission and before the recovery guard: no producer exists yet.
    private async Task ResolveInterruptedTransfersAsync(CancellationToken token)
    {
        if (InterruptedTransferDecision is not { } decide) return;
        IReadOnlyList<MeowChan.Archive.InterruptedNativeMediaOperation> interrupted =
            await _content.Catalog.ReadInterruptedNativeMediaOperationsAsync(token).ConfigureAwait(false);
        if (interrupted.Count == 0 || !await decide(interrupted, token).ConfigureAwait(false)) return;
        int discarded = await _content.Catalog.DiscardInterruptedNativeMediaOperationsAdmittedAsync(_profileStartupAdmission
            ?? throw new InvalidOperationException("No profile startup admission."), token).ConfigureAwait(false);
        _logger.Warning("Discarded {Count} interrupted native transfer operation(s) on user confirmation: {Operations}",
            discarded, string.Join(", ", interrupted.Select(item => (item.Merge ? "merge " : "import ") + item.OperationId.ToString("D"))));
    }

    private async Task StartCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            Config.EnsureOrdinaryWritesAllowed();
            using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.startup.preview", epoch: _epoch))
                await LoadStartupPreviewAsync(_startupCancellation.Token).ConfigureAwait(false);
            // Local catalog preparation needs no plugin: it runs first so the archive is readable while
            // the (much slower) plugin and registry phases continue. Order within it is unchanged.
            using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.startup.content", epoch: _epoch))
                await _content.InitializeAdmittedAsync(_profileStartupAdmission
                    ?? throw new InvalidOperationException("No profile startup admission."), _startupCancellation.Token).ConfigureAwait(false);
            // Startup ownership is still held. An interrupted transfer must not be
            // bypassed by restarting the ordinary producer/queue epoch.
            await ResolveInterruptedTransfersAsync(_startupCancellation.Token).ConfigureAwait(false);
            await _content.Catalog.EnsureNativeMediaRecoveryClearAsync(_startupCancellation.Token).ConfigureAwait(false);
            // Thread marks belong to the readable archive, not plugin/registry readiness. Read only
            // this bounded profile index on the existing startup worker before admitting reader views.
            _startupCancellation.Token.ThrowIfCancellationRequested();
            GetThreadFavoriteSnapshot();
            _startupCancellation.Token.ThrowIfCancellationRequested();
            PublishCatalogReadable();
            if (AfterCatalogReadableForTests is { } readableHold) await readableHold(_startupCancellation.Token).ConfigureAwait(false);
            if (Plugins != null)
            {
                PluginStartupDiagnostics.RecordResources("host.startup.plugins", "begin");
                using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.startup.plugins", epoch: _epoch))
                    await Plugins.StartAsync(_startupCancellation.Token).ConfigureAwait(false);
                PluginStartupDiagnostics.RecordResources("host.startup.plugins", "end");
                await PluginRegistryAdapter.RegisterBeforeRestoreAsync(_registry, Plugins.Sources, _startupCancellation.Token).ConfigureAwait(false);
            }
            Plugins?.AttachContent(_content);
            using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.startup.registry", epoch: _epoch))
                await _registry.RestoreAdmittedAsync(_profileStartupAdmission).ConfigureAwait(false);
            InitializePassiveMetadata();
            lock (_lifecycle)
            {
                if (_state == DesktopSessionState.Starting)
                {
                    // Lifecycle serialization prevents a late Ready after shutdown.
                    FinishProfileStartup(succeeded: true);
                    InstallReadyFinalWriteEpoch();
                    _registry.InstallReadyProfileEpoch();
                }
            }
            // No external/async activation under _lifecycle. Stop synchronously
            // closes Registry ingress and wins before late Ready publication.
            if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Ready)
                await _registry.ActivatePreparedProfileProducersAsync(
                    sealBeforeFailureDrain: _profileLease.Admission.BeginShutdown,
                    notifyBeforeFailureDrain: _hostFinalWriteParticipant.FailTransition).ConfigureAwait(false);
            // Open outside _lifecycle: family observers may inspect/cancel the host. Shutdown marks the family
            // stopped before taking that monitor, so no late generation can survive a concurrent stop.
            if (State == DesktopSessionState.Starting) OpenBooruProducers();
            lock (_lifecycle)
                if (_state == DesktopSessionState.Starting)
                {
                    OpenFinalWriteEpoch();
                    _state = DesktopSessionState.Ready;
                    // Startup drafts now own real uses. Publish their worker-ready
                    // state before shutdown can win; never reject/drop these uses
                    // in the small Ready-to-CompleteStartup continuation gap.
                    CompleteStartupFinalWrites(permittedReady: true);
                }
            CompleteStartupFinalWrites(State == DesktopSessionState.Ready);
            if (State == DesktopSessionState.Ready && Config.Origin == ProfileOrigin.ConfirmedNew)
            {
                if (BeforeStartupInitialSaveForTests is { } beforeSave) await beforeSave().ConfigureAwait(false);
                await SaveViewPreferencesAsync(candidate => candidate.FirstRunSetupPending = true).ConfigureAwait(false);
            }
            if (State == DesktopSessionState.Ready) Updates.Start();
            if (State == DesktopSessionState.Ready) StartMediaTagReconcile();
            if (State == DesktopSessionState.Ready) StartFolderSources();
            // In the background: recovering pending originals may wait for queue admission (paused queue, space gate).
            if (State == DesktopSessionState.Ready) StartBooruInBackground();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to restore the desktop session.");
            // Startup can fail after its first Booru generation opened. Close ingress before sealing the profile,
            // and release the generation below even when Registry rollback itself fails.
            _booruProducers.BeginStop();
            lock (_lifecycle)
            {
                if (_state is DesktopSessionState.Starting or DesktopSessionState.Ready)
                {
                    if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Starting)
                        FinishProfileStartup(succeeded: false);
                    else if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Ready)
                        // CompleteStartup already happened, but activation/installation
                        // failed before Host Ready. Seal before draining the subtree;
                        // FailStartup is deliberately not a valid Ready transition.
                        _profileLease.Admission.BeginShutdown();
                }
            }
            FailFinalWriteTransition(exception); // Notify drafts BEFORE any potentially blocking rollback.
            try
            {
                await _registry.StopAsync().ConfigureAwait(false);
                await DrainFailedStartupFinalWritesAsync().ConfigureAwait(false);
            }
            catch (Exception drainError) { _logger.Error(drainError, "Failed to drain the startup producer subtree."); }
            try { await _booruProducers.StopAsync().ConfigureAwait(false); }
            catch (Exception drainError) { _logger.Error(drainError, "Failed to drain the startup Danbooru producer family."); }
            lock (_lifecycle)
            {
                _startupError = exception.Message;
                if (_state is DesktopSessionState.Starting or DesktopSessionState.Ready)
                {
                    if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Starting)
                        FinishProfileStartup(succeeded: false);
                    _state = DesktopSessionState.RecoveryFailed;
                }
            }
        }
        finally
        {
            // Shutdown may have failed/sealed startup while restoration was in flight.
            // Its token still belongs to that work until it has actually returned.
            lock (_lifecycle) ReleaseProfileStartupAdmission();
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
        => RunInteractiveWithAdmissionAsync(nameof(AddAsync), url.Trim(), admission => AddAdmittedCoreAsync(url, admission));

    private async Task<DesktopAdmissionResult> AddAdmittedCoreAsync(string url, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            AdmissionResult result = await _registry.AddAdmittedAsync(url, admission).ConfigureAwait(false);
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

    internal bool CanRequestCheck(DiagnosticTarget target) => CanRequestCheck(Projection, target);

    internal static bool CanRequestCheck(DesktopSessionProjection frame, DiagnosticTarget target) =>
        frame.View.State == DesktopSessionState.Ready && !frame.IsPaused
        && !frame.IsStale && frame.CheckableTargets.Contains(target);

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
            && HasRetriableMedia(frame.View.Threads.FirstOrDefault(entry => entry.WatchId == watchId)?.Snapshot.Work);
    }

    internal static bool HasRetriableMedia(ThreadWorkSnapshot? work, string? mediaIdentifier = null) =>
        work != null && work.Intent != WorkIntent.Suppressed && work.Media.Any(media =>
            (mediaIdentifier == null || media.MediaIdentifier == mediaIdentifier) && !media.ManualAttempt
            && media.Status is not (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded));

    internal Task<int> RetryMissingMediaAsync(long watchId, string? mediaIdentifier = null)
        => RunInteractiveWithAdmissionAsync(nameof(RetryMissingMediaAsync), watchId.ToString(), admission => RetryMissingMediaAdmittedCoreAsync(watchId, mediaIdentifier, admission));

    private async Task<int> RetryMissingMediaAdmittedCoreAsync(long watchId, string? mediaIdentifier, ProfileAdmissionToken admission)
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
            if (!HasRetriableMedia(work, mediaIdentifier)) return 0;
            // Registry/queue retain lifetime, cooldown, cleanup fences and final manual-attempt authority.
            // A manual retry may record intent while globally paused; it never unpauses transfer.
            return _registry.RetryMissingMediaAdmitted(thread, mediaIdentifier, admission);
        }
        finally { _commands.Release(); }
    }

    internal Task<int> RetryCompletedMediaAsync(string threadUrl, string? mediaIdentifier = null)
        => RunInteractiveWithAdmissionAsync(nameof(RetryCompletedMediaAsync), threadUrl, admission => RetryCompletedMediaAdmittedCoreAsync(threadUrl, mediaIdentifier, admission));

    private async Task<int> RetryCompletedMediaAdmittedCoreAsync(string threadUrl, string? mediaIdentifier, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try { EnsureReady(); return await _registry.RetryCompletedMediaAdmittedAsync(threadUrl, mediaIdentifier, admission).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public Task RemoveThreadAsync(long watchId)
        => RunInteractiveWithAdmissionAsync(nameof(RemoveThreadAsync), watchId.ToString(), admission => RemoveThreadAdmittedCoreAsync(watchId, admission));

    private async Task RemoveThreadAdmittedCoreAsync(long watchId, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IThread? thread;
            lock (_projection) { _threadHandles.TryGetValue(watchId, out thread); }
            if (thread != null)
            {
                await _registry.RemoveThreadAdmittedAsync(thread, admission).ConfigureAwait(false);
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
        => RunInteractiveWithAdmissionAsync(nameof(RemoveBoardAsync), watchId.ToString(), admission => RemoveBoardAdmittedCoreAsync(watchId, admission));

    private async Task RemoveBoardAdmittedCoreAsync(long watchId, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            IBoard? board;
            lock (_projection) { _boardHandles.TryGetValue(watchId, out board); }
            if (board != null)
            {
                await _registry.RemoveBoardAdmittedAsync(board, admission).ConfigureAwait(false);
                lock (_projection)
                {
                    _boardHandles.Remove(watchId);
                    _boards.Remove(board);
                }
            }
        }
        finally { _commands.Release(); }
    }

    // False means the settings were committed and reading-file deletion can be retried.
    public Task<bool> ApplySettingsAsync(Config settings)
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
        frozen.SettingsBaselineGalleryFilmstrip = settings.SettingsBaselineGalleryFilmstrip;
        frozen.SettingsBaselineMediaTagListWidth = settings.SettingsBaselineMediaTagListWidth;
        frozen.SettingsBaselineMediaPanelWidth = settings.SettingsBaselineMediaPanelWidth;
        frozen.GalleryLayoutResetRequested = settings.GalleryLayoutResetRequested;
        frozen.SettingsBaselineProfiles = settings.SettingsBaselineProfiles?.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        frozen.Validate();
        Commands.ShortcutService.ValidateForCommit(frozen.ShortcutBindings);
        var flush = new FinalWriteFlushRegistration(0);
        return RunInteractiveWithAdmissionAsync<bool>(nameof(ApplySettingsAsync), "settings",
            admission => ApplySettingsAdmittedCoreAsync(frozen, admission, flush), finalWriteFlush: flush);
    }

    private async Task<bool> ApplySettingsAdmittedCoreAsync(Config settings, ProfileAdmissionToken admission, FinalWriteFlushRegistration flush)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await FlushCommandPreferencesAsync(flush).ConfigureAwait(false);
        settings.Validate();
        Commands.ShortcutService.ValidateForCommit(settings.ShortcutBindings);
        bool readingFilesCleanedUp;
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
            if (candidate.BoardAdmissionSizeMiB != Config.BoardAdmissionSizeMiB)
                CheckBoardAdmissionSizeLimit(candidate.BoardAdmissionSizeMiB);
            SessionOptions options = new SessionOptions
            {
                DownloadPath = candidate.DownloadPath,
                RecheckInterval = TimeSpan.FromSeconds(candidate.RecheckTimer),
                MaxParallelDownloads = candidate.MaxParallelDownloads,
                ParallelDownloadsSafetyLimit = candidate.ParallelDownloadsSafetyLimit,
                BandwidthBytesPerSecond = candidate.BandwidthLimiter
            };
            await _registry.ApplyOptionsAdmittedAsync(options, candidate.SaveUrlsOnExit,
                () => candidate.SaveConfigAdmitted(_profileLease.ProfileDirectory, admission), admission).ConfigureAwait(false);
            PublishConfig(candidate);
            if (!candidate.RememberReadingPosition) InvalidateReadingPositionWrites();
            readingFilesCleanedUp = candidate.TryCleanUpReadingPositionAfterOptOut();
            Commands.ShortcutService.Publish(Config.ShortcutBindings);
            _downloadQueue.UpdateMinimumFreeSpaceBytes((long)Config.MinimumFreeSpaceGiB * 1024 * 1024 * 1024);
            ApplyBooruCatalogLimit();
            ApplyPrivateTagLimits();
            ApplyStoreLimits();
            ApplyArchivePersistenceLimits();
            ApplyIoStatsOptions();
            ApplyFolderSourceSettings();
            await Updates.ConfigureAsync(Config).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
        SignalChanged();
        return readingFilesCleanedUp;
    }

    internal Task<FirstRunSetupCommitResult> CompleteFirstRunSetupAsync(FirstRunSetupValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var flush = new FinalWriteFlushRegistration(1);
        return RunInteractiveWithAdmissionAsync<FirstRunSetupCommitResult>(nameof(CompleteFirstRunSetupAsync), "first-run",
            admission => CompleteFirstRunSetupAdmittedCoreAsync(values, admission, flush), finalWriteFlush: flush);
    }

    private async Task<FirstRunSetupCommitResult> CompleteFirstRunSetupAdmittedCoreAsync(FirstRunSetupValues values, ProfileAdmissionToken admission, FinalWriteFlushRegistration flush)
    {
        await FlushCommandPreferencesAsync(flush).ConfigureAwait(false);
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
                MaxParallelDownloads = candidate.MaxParallelDownloads,
                ParallelDownloadsSafetyLimit = candidate.ParallelDownloadsSafetyLimit,
                BandwidthBytesPerSecond = candidate.BandwidthLimiter
            };
            await _registry.ApplyOptionsAdmittedAsync(options, candidate.SaveUrlsOnExit, () =>
            {
                candidate.SaveConfigAdmitted(_profileLease.ProfileDirectory, admission);
                persisted = true;
                PublishConfig(candidate);
            }, admission).ConfigureAwait(false);
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
    public Task SetPausedAsync(bool paused) => RunControlWithAdmissionAsync("pause", paused.ToString(), admission => SetPausedCoreAsync(paused, admission));

    private async Task SetPausedCoreAsync(bool paused, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            Config candidate = Config.Clone();
            candidate.DownloadsPaused = paused;
            candidate.SaveConfigAdmitted(_profileLease.ProfileDirectory, admission);
            IsPauseTransitioning = true;
            SignalChanged();
            try
            {
                await _registry.SetPausedAdmittedAsync(paused, admission).ConfigureAwait(false);
                PublishConfig(candidate);
            }
            finally
            {
                // Follow the queue's actual state even if the registry transition failed after changing it.
                try { SyncBooruSessionPause(); }
                finally { IsPauseTransitioning = false; SignalChanged(); }
            }
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

    internal Action<ProfileAdmissionToken>? BeforeMediaRuleAdmittedSaveMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterMediaRuleAdmittedSaveMutationForTests { get; set; }
    internal Task<MediaRuleSet> SaveRulesAsync(IEnumerable<MediaRule> rules, string expectedRevision)
    {
        MediaRuleSet frozen = new(expectedRevision, rules);
        return RunInteractiveWithAdmissionAsync(nameof(SaveRulesAsync), "rules",
            admission => SaveRulesAdmittedCoreAsync(frozen.Rules, expectedRevision, admission));
    }

    private async Task<MediaRuleSet> SaveRulesAdmittedCoreAsync(IEnumerable<MediaRule> rules, string expectedRevision,
        ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            _content.Rules.BeforeAdmittedSaveMutationForTests = BeforeMediaRuleAdmittedSaveMutationForTests;
            _content.Rules.AfterAdmittedSaveMutationForTests = AfterMediaRuleAdmittedSaveMutationForTests;
            MediaRuleSet result = await _content.Rules.SaveAdmittedAsync(rules, expectedRevision, admission).ConfigureAwait(false);
            SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }

    internal Task SetCaptureEnabledAsync(ResourceId id, bool enabled)
        => RunInteractiveWithAdmissionAsync(nameof(SetCaptureEnabledAsync), id.ToString(),
            admission => SetCaptureEnabledAdmittedCoreAsync(id, enabled, admission));

    private async Task SetCaptureEnabledAdmittedCoreAsync(ResourceId id, bool enabled, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            BaseThread? active;
            lock (_projection)
                active = _threadHandles.Values.OfType<BaseThread>().FirstOrDefault(thread => thread.SupportsContent
                    && thread.Board.ImageBoard.Name == id.Source && thread.Board.BoardCode == id.Board && thread.ThreadId == id.Thread);
            if (active != null) await active.SetCaptureEnabledAdmittedAsync(admission, enabled).ConfigureAwait(false);
            else await _content.Catalog.SetCaptureEnabledAdmittedAsync(admission, id, enabled).ConfigureAwait(false);
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
    private TaskCompletionSource? _engineStopCompletion, _stopCompletion;
    private FinalWriteShutdownPlan? _finalWriteShutdownPlan;
    private int _engineStopLaunchClaimed, _stopLaunchClaimed;
    private Task _acceptedDrain = Task.CompletedTask;

    // Every caller settles the same immutable plan outside ALL outer monitors.
    // A published promise/launch claim is deliberately not a settlement claim.
    internal Task BeginShutdown()
    {
        FinalWriteShutdownPlan plan;
        // Terminal cut before profile seal. Already accepted work retains its generation until physical drain.
        // Changed callbacks must never execute under the outer host monitor.
        _booruProducers.BeginStop();
        lock (_lifecycle)
        {
            if (_finalWriteShutdownPlan == null)
            {
                CaptureStartupPreviewCheckpoint();
                plan = BeginFinalWriteShutdown(_startTask ?? Task.CompletedTask);
                _state = DesktopSessionState.Stopping;
                _acceptedDrain = CloseInteractiveAdmission();
                _registry.CloseProducerIngress();
                BeginProfileShutdown();
                _engineStopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _engineStopTask = _engineStopCompletion.Task;
                _finalWriteShutdownPlan = plan;
            }
            plan = _finalWriteShutdownPlan;
        }
        // Revoke escaped Booru handles before scheduling engine shutdown. Cancellation callbacks
        // run outside the outer lifecycle monitor; physical drain still belongs to StopAsync.
        Volatile.Read(ref _booru)?.ClosePortIngress();
        SettleTerminalFinalWriteObservers(plan);
        StartEngineShutdownOnce(plan);
        return _engineStopTask!;
    }
    private void StartEngineShutdownOnce(FinalWriteShutdownPlan plan)
    {
        if (Interlocked.CompareExchange(ref _engineStopLaunchClaimed, 1, 0) != 0) return;
        try
        {
            _ = Task.Run(async () =>
            {
                try { await RunEngineShutdownAsync(plan.Restoration).ConfigureAwait(false); _engineStopCompletion!.TrySetResult(); }
                catch (Exception error) { _engineStopCompletion!.TrySetException(error); }
            });
            PublishLifecycleState(notifyOnWorker: true);
        }
        catch (Exception error) { _engineStopCompletion!.TrySetException(error); }
    }
    private async Task RunEngineShutdownAsync(Task restoration)
    {
        using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.shutdown.engine", epoch: _epoch);
        Task startupCanceled = _startupCancellation.CancelAsync();
        // Danbooru watchers stop first, while the shared queue still runs and the profile still accepts writes.
        Task booruStopped = await StopBooruBeforeQueueAsync().ConfigureAwait(false);
        Task queueStopped = _downloadQueue.StopAllAsync();
        RetireSourceMediaIntegrityPolicies();
        List<Exception> errors = new();
        async Task Drain(Task task)
        { try { await task.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
        await Drain(startupCanceled).ConfigureAwait(false);
        await Drain(restoration).ConfigureAwait(false);
        await Drain(_mediaTagReconcile ?? Task.CompletedTask).ConfigureAwait(false);
        await Drain(StopFolderSourcesAsync()).ConfigureAwait(false);
        Task registryStopped = _registry.StopAsync();
        await Drain(Task.WhenAll(queueStopped, registryStopped)).ConfigureAwait(false);
        // An unpublished start may still own a transport: drain its cleanup before releasing all owners.
        await Drain(booruStopped).ConfigureAwait(false);
        await Drain(_booruProducers.StopAsync()).ConfigureAwait(false);
        ReleaseBooruAfterQueue();
        try { await Updates.DisposeAsync().ConfigureAwait(false); _updatesQuiescent = true; } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Engine shutdown reported failures.", errors);
    }
    public Task StopAsync()
    {
        Task engineStopped = BeginShutdown(); // Never inside StopAsync's reentrant lifecycle lock.
        lock (_lifecycle)
        {
            if (_stopTask == null)
            {
                _stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = _stopCompletion.Task;
            }
        }
        StartStopCoreOnce(engineStopped);
        return _stopTask;
    }
    private void StartStopCoreOnce(Task engineStopped)
    {
        if (Interlocked.CompareExchange(ref _stopLaunchClaimed, 1, 0) != 0) return;
        try
        {
            _ = Task.Run(async () =>
            {
                try { await StopCoreAsync(engineStopped).ConfigureAwait(false); _stopCompletion!.TrySetResult(); }
                catch (Exception error) { _stopCompletion!.TrySetException(error); }
            });
        }
        catch (Exception error) { _stopCompletion!.TrySetException(error); }
    }

    private async Task StopCoreAsync(Task engineStopped)
    {
        using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.shutdown.drain", epoch: _epoch);
        // Command/profile admission is already sealed. Seal the separate final writers now;
        // already accepted work is drained even if the engine is still stopping.
        Task finalWrites = SealFinalWritesAsync();
        List<Exception> errors = new();
        bool quiescent = true;
        async Task Observe(Func<Task> action, bool ownsLifetime = false)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); if (ownsLifetime) quiescent = false; }
        }
        await Observe(() => Task.WhenAll(engineStopped, _acceptedDrain, finalWrites)).ConfigureAwait(false);
        await Observe(() => { StopIoStats(); return Task.CompletedTask; }).ConfigureAwait(false);
        // The optional cache belongs to the existing TerminalOpen authority, before
        // releasing the host family and BEFORE the kernel's final admission drain.
        ProfileProducerEpoch.Use? terminalUse;
        lock (_finalWrites) { terminalUse = _terminalFinalWriteUse; _terminalFinalWriteUse = null; }
        try
        {
            if (terminalUse != null)
            {
                await _commands.WaitAsync().ConfigureAwait(false);
                try { SaveStartupPreviewCheckpoint(terminalUse.Admission!); }
                finally { _commands.Release(); }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally { terminalUse?.Dispose(); }
        await CloseFinalWriteEpochAsync().ConfigureAwait(false);
        await _registry.ReleaseStoppedHostFinalWritesAsync().ConfigureAwait(false);
        // Observer faults never substitute for the real use/engine/command drain.
        await _profileLease.Admission.DrainAsync().ConfigureAwait(false);
        _profileLease.Admission.CompleteShutdown();
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
            _downloadQueue.MediaTagsCarriedOver -= OnMediaTagsCarriedOver;
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

    private void OnRegistryChanged() { QueueIoActivityRefresh(); SignalChanged(); }

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
