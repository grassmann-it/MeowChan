using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

// Danbooru composition lifecycle of one desktop session.
//
// Start: only once the session is Ready (registry restored, profile admission Ready, shared queue running) and only
// while the Danbooru plugin is delivered and enabled. A plugin enabled later in the running session composes then.
// Stop: first step of the engine shutdown, BEFORE the shared queue and the registry stop and before the profile is
// released, so a running watcher check is cancelled and persisted while the profile still accepts writes.
internal sealed partial class DesktopSessionHost
{
    private static readonly TimeSpan BooruStopWait = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _booruGate = new(1, 1);
    private readonly object _booruSync = new();
    private BooruSessionComposition? _booru;
    private readonly List<BooruSessionComposition> _booruTransportOwners = new();
    private BooruProviderBinding? _booruBinding;
    private BooruWorkbenchServices? _booruServicesOverride;
    private bool _booruClosed;

    // One family for the whole profile session; F4 plugin disable stops a host, never this reversible family.
    private BooruProducerLifecycle _booruProducers = null!;
    internal BooruProducerLifecycle BooruProducers => _booruProducers;
    internal Func<CancellationToken, Task>? BeforeBooruStartForTests { get; set; }

    private void OpenBooruProducers()
    {
        lock (_lifecycle)
            if (_state is not (DesktopSessionState.Starting or DesktopSessionState.Ready))
                throw new InvalidOperationException("Session stopping before Danbooru producer activation.");
        // Changed observers may reenter the host. Stop in the gap wins because Open refuses a stopped family.
        _booruProducers.Open();
    }

    private void SyncBooruSessionPause()
    {
        BooruSessionComposition[] owners;
        lock (_booruSync) owners = _booruTransportOwners.ToArray();
        // A composition may already recover/start before it is published as _booru. Include those owners too;
        // each one reads the current queue state itself so an old snapshot cannot overwrite a later pause.
        foreach (BooruSessionComposition owner in owners) owner.SyncSessionPause();
    }

    /// <summary>
    /// The ports behind the Danbooru window: the production composition while the Danbooru plugin is enabled, null
    /// otherwise (the menu entry then stays hidden). The UI never creates a queue, a store or a scheduler of its own.
    /// Assigning a value replaces the composed ports (fakes in smokes); assigning null returns to the composition.
    /// </summary>
    internal BooruWorkbenchServices? BooruServices
    {
        get
        {
            BooruWorkbenchServices? overridden = Volatile.Read(ref _booruServicesOverride);
            if (overridden != null) return overridden;
            BooruSessionComposition? composed = Volatile.Read(ref _booru);
            return composed is { IsAvailable: true } ? composed.Services : null;
        }
        set
        {
            Volatile.Write(ref _booruServicesOverride, value);
            SyncBooruNoticeObservation();
        }
    }

    /// <summary>
    /// Raised (on any thread) once per finished automatic watcher check that queued new originals; the main window shows it
    /// as a notification. Follows whichever ports <see cref="BooruServices"/> currently names.
    /// </summary>
    internal event Action<BooruWatcherCheckNotice>? BooruWatcherQueued;

    private IBooruWatcherPort? _observedWatchers;

    private void SyncBooruNoticeObservation()
    {
        lock (_booruSync)
        {
            IBooruWatcherPort? current = BooruServices?.Watchers;
            if (ReferenceEquals(current, _observedWatchers)) return;
            if (_observedWatchers != null) _observedWatchers.CheckQueued -= OnBooruCheckQueued;
            _observedWatchers = current;
            if (current != null) current.CheckQueued += OnBooruCheckQueued;
        }
    }

    private void OnBooruCheckQueued(BooruWatcherCheckNotice notice) => BooruWatcherQueued?.Invoke(notice);

    /// <summary>The running composition (tests, diagnostics); null when Danbooru is not composed.</summary>
    internal BooruSessionComposition? BooruComposition => Volatile.Read(ref _booru);

    /// <summary>Test seam: supplies the plugin binding instead of the plugin session (loopback stubs, in-process plugin).</summary>
    internal Func<BooruProviderBinding?>? BooruBindingForTests { get; set; }
    internal TimeProvider? BooruClockForTests { get; set; }
    internal DanbooruWatcherSchedulerOptions? BooruSchedulerOptionsForTests { get; set; }

    /// <summary>Test observer: runs right after the watcher host stopped during shutdown with "is the shared queue stopping".</summary>
    internal Action<bool>? AfterBooruStoppedForTests { get; set; }

    /// <summary>Raised after the composition was created, started or stopped.</summary>
    internal event Action? BooruChanged;

    private Task _booruStart = Task.CompletedTask;

    private void StartBooruInBackground()
    {
        lock (_booruSync)
            if (!_booruClosed) _booruStart = Task.Run(StartBooruAfterReadyAsync);
    }

    private BooruProviderBinding? ResolveBooruBinding() => BooruBindingForTests is { } factory ? factory() : Plugins?.Booru;

    // Ready continuation of StartCoreAsync. Never throws: Danbooru is optional and must not fail the session.
    private async Task StartBooruAfterReadyAsync()
    {
        BooruProviderBinding? binding;
        try { binding = ResolveBooruBinding(); }
        catch (Exception exception)
        {
            _logger.Error(exception, "The Danbooru binding could not be resolved.");
            return;
        }
        if (binding == null) return;
        lock (_booruSync)
        {
            if (_booruClosed) return;
            _booruBinding = binding;
        }
        binding.AvailabilityChanged += OnBooruAvailabilityChanged;
        await EnsureBooruComposedAsync(binding).ConfigureAwait(false);
    }

    private void OnBooruAvailabilityChanged()
    {
        BooruProviderBinding? binding;
        lock (_booruSync) binding = _booruClosed ? null : _booruBinding;
        if (binding == null) return;
        _ = Task.Run(() => EnsureBooruComposedAsync(binding));
        BooruChanged?.Invoke();
    }

    private async Task EnsureBooruComposedAsync(BooruProviderBinding binding)
    {
        try
        {
            await _booruGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return; }
        bool changed = false;
        try
        {
            lock (_booruSync)
                if (_booruClosed || _booru != null || State != DesktopSessionState.Ready || !binding.IsEnabled()) return;
            BooruCatalogStore? catalog = _registry.BooruCatalog;
            if (catalog == null)
            {
                _logger.Warning("Danbooru is enabled, but the profile has no booru catalog; the window stays hidden.");
                return;
            }
            BooruSessionComposition composition = BooruSessionComposition.Create(binding, _downloadQueue, catalog,
                Path.GetFullPath(Config.DownloadPath), _booruProducers, BooruClockForTests, BooruSchedulerOptionsForTests);
            // Recovery may hand originals to the shared queue before StartAsync completes. The session owns
            // their transport even if this composition never publishes; release only after both drains.
            lock (_booruSync) _booruTransportOwners.Add(composition);
            try
            {
                // Shutdown cancels this token before it waits for the gate, so a start never outlives the session.
                if (BeforeBooruStartForTests is { } beforeStart)
                    await beforeStart(_startupCancellation.Token).ConfigureAwait(false);
                await composition.StartAsync(_startupCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException) _logger.Error(exception, "The Danbooru watchers could not be started.");
                await composition.StopAsync().ConfigureAwait(false);
                return;
            }
            lock (_booruSync)
            {
                if (!_booruClosed)
                {
                    _booru = composition;
                    changed = true;
                }
            }
            // Re-read after publication as well; SetPaused includes starting/unpublished owners in its snapshot.
            if (changed) SyncBooruSessionPause();
            if (!changed)
            {
                await composition.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.Error(exception, "The Danbooru composition failed; the window stays hidden.");
        }
        finally { _booruGate.Release(); }
        if (changed)
        {
            SyncBooruNoticeObservation();
            BooruChanged?.Invoke();
        }
    }

    /// <summary>
    /// First shutdown step: closes composition and stops the watcher host (running check cancelled and persisted).
    /// Waits at most <see cref="BooruStopWait"/> so a stuck check can never hold back the queue stop; the returned
    /// task still completes only when the host has really stopped.
    /// </summary>
    private async Task<Task> StopBooruBeforeQueueAsync()
    {
        BooruProviderBinding? binding;
        lock (_booruSync)
        {
            _booruClosed = true;
            binding = _booruBinding;
        }
        if (binding != null) binding.AvailabilityChanged -= OnBooruAvailabilityChanged;
        Task start;
        lock (_booruSync) start = _booruStart;
        Task stopped = Task.WhenAll(start, StopComposedBooruAsync());
        try { await stopped.WaitAsync(BooruStopWait).ConfigureAwait(false); }
        catch (TimeoutException) { _logger.Warning("The Danbooru watchers did not stop within {Seconds} s; the queue stops anyway.", BooruStopWait.TotalSeconds); }
        catch (Exception) { /* observed by the caller through the returned task */ }
        AfterBooruStoppedForTests?.Invoke(_downloadQueue.IsStopping);
        return stopped;
    }

    private async Task StopComposedBooruAsync()
    {
        // A composition that is still starting holds the gate; its start observes the cancelled startup token.
        await _booruGate.WaitAsync().ConfigureAwait(false);
        BooruSessionComposition? composition;
        try
        {
            lock (_booruSync)
            {
                composition = _booru;
            }
        }
        finally { _booruGate.Release(); }
        if (composition == null) return;
        await composition.StopAsync().ConfigureAwait(false);
        SyncBooruNoticeObservation();
        BooruChanged?.Invoke();
    }

    /// <summary>After queue and Booru start/stop drain, release even transports that never published. Idempotent.</summary>
    private void ReleaseBooruAfterQueue()
    {
        BooruSessionComposition[] owners;
        lock (_booruSync)
        {
            owners = _booruTransportOwners.ToArray();
            _booruTransportOwners.Clear();
        }
        foreach (BooruSessionComposition composition in owners) composition.ReleaseTransport();
    }
}
