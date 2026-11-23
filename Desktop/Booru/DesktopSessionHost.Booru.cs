using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

// Danbooru composition lifecycle of one desktop session.
//
// A published composition owns the original-media client for the whole session. Availability changes replace only
// the watcher generation; they do not replace the composition or its queue owner. The shared DownloadQueue has no
// owner-scoped drain that can prove an old client is no longer referenced, so a transport is retired only by the
// terminal queue-drain path below.
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
    private readonly Dictionary<string, BooruSessionComposition> _booruCompositions = new(StringComparer.Ordinal);
    // Every composition that may have reached StartAsync is retained until the shared queue has physically drained.
    // This is deliberately broader than the UI availability gate: a recovery or an accepted original may still hold
    // the composition's client after its watcher host was stopped. There is no owner-scoped DownloadQueue drain in
    // this layer, so removing an entry on Disable/Replace would be an unsafe, unproved early retirement.
    private readonly List<BooruSessionComposition> _booruTransportOwners = new();
    private BooruProviderBinding[] _booruBindings = [];
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
        get => ActiveBooruServices.FirstOrDefault();
        set
        {
            Volatile.Write(ref _booruServicesOverride, value);
            SyncBooruNoticeObservation();
        }
    }

    internal IReadOnlyList<BooruWorkbenchServices> ActiveBooruServices
    {
        get
        {
            if (Volatile.Read(ref _booruServicesOverride) is { } overridden) return [overridden];
            lock (_booruSync)
                return _booruCompositions.Values.Where(composition => composition.IsAvailable)
                    .Select(composition => composition.Services)
                    .OrderBy(services => services.Search.ProviderId != DanbooruPostReference.ProviderId)
                    .ThenBy(services => services.Search.ProviderId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Raised (on any thread) once per finished automatic watcher check that queued new originals; the main window shows it
    /// as a notification. Follows whichever ports <see cref="BooruServices"/> currently names.
    /// </summary>
    internal event Action<BooruWatcherCheckNotice>? BooruWatcherQueued;

    private readonly HashSet<IBooruWatcherPort> _observedWatchers = new();

    private void SyncBooruNoticeObservation()
    {
        lock (_booruSync)
        {
            HashSet<IBooruWatcherPort> current = ActiveBooruServices.Select(services => services.Watchers).ToHashSet();
            foreach (IBooruWatcherPort port in _observedWatchers.Except(current).ToArray())
            {
                port.CheckQueued -= OnBooruCheckQueued;
                _observedWatchers.Remove(port);
            }
            foreach (IBooruWatcherPort port in current)
                if (_observedWatchers.Add(port)) port.CheckQueued += OnBooruCheckQueued;
        }
    }

    private void OnBooruCheckQueued(BooruWatcherCheckNotice notice) => BooruWatcherQueued?.Invoke(notice);

    /// <summary>The running composition (tests, diagnostics); null when Danbooru is not composed.</summary>
    internal BooruSessionComposition? BooruComposition
    {
        get { lock (_booruSync) return _booruCompositions.GetValueOrDefault(DanbooruPostReference.ProviderId); }
    }

    private void CloseBooruPortIngress()
    {
        BooruSessionComposition[] owners;
        lock (_booruSync) owners = _booruTransportOwners.ToArray();
        foreach (BooruSessionComposition owner in owners) owner.ClosePortIngress();
    }

    /// <summary>Test seam: supplies the plugin binding instead of the plugin session (loopback stubs, in-process plugin).</summary>
    internal Func<BooruProviderBinding?>? BooruBindingForTests { get; set; }
    internal BooruProviderBinding[]? BooruBindingsForTests { get; set; }
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

    private BooruProviderBinding[] ResolveBooruBindings() => BooruBindingForTests is { } factory
        ? (factory() is { } binding ? [binding] : [])
        : (BooruBindingsForTests ?? Plugins?.BooruSources.Values.ToArray() ?? [])
            .Where(binding => binding.SupportsWorkbench).ToArray();

    // Ready continuation of StartCoreAsync. Never throws: Danbooru is optional and must not fail the session.
    private async Task StartBooruAfterReadyAsync()
    {
        BooruProviderBinding[] bindings;
        try { bindings = ResolveBooruBindings(); }
        catch (Exception exception)
        {
            _logger.Error(exception, "The Danbooru binding could not be resolved.");
            return;
        }
        lock (_booruSync)
        {
            if (_booruClosed) return;
            _booruBindings = bindings;
            foreach (BooruProviderBinding binding in bindings) binding.AvailabilityChanged += OnBooruAvailabilityChanged;
        }
        foreach (BooruProviderBinding binding in bindings)
            await EnsureBooruComposedAsync(binding).ConfigureAwait(false);
    }

    private void OnBooruAvailabilityChanged()
    {
        BooruProviderBinding[] bindings;
        lock (_booruSync)
        {
            if (_booruClosed) return;
            bindings = _booruBindings;
        }
        _ = Task.Run(async () =>
        {
            foreach (BooruProviderBinding binding in bindings)
                await EnsureBooruComposedAsync(binding).ConfigureAwait(false);
        });
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
                if (_booruClosed || _booruCompositions.ContainsKey(binding.Source.SourceId) || State != DesktopSessionState.Ready || !binding.IsEnabled()) return;
            BooruCatalogStore? catalog = _registry.BooruCatalog;
            if (catalog == null)
            {
                _logger.Warning("Danbooru is enabled, but the profile has no booru catalog; the window stays hidden.");
                return;
            }
            BooruSessionComposition composition = BooruSessionComposition.Create(binding, _downloadQueue, catalog,
                Path.GetFullPath(Config.DownloadPath), _booruProducers, BooruClockForTests, BooruSchedulerOptionsForTests);
            // Recovery may hand originals to the shared queue before StartAsync completes. The session owns
            // their transport even if this composition never publishes. Stop/availability drain only producer
            // ingress; the only proven transport retirement point remains after the shared queue has stopped.
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
                    _booruCompositions.Add(binding.Source.SourceId, composition);
                    composition.AvailabilityChanged += NotifyBooruChanged;
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
        if (changed) NotifyBooruChanged();
    }

    private void NotifyBooruChanged()
    {
        SyncBooruNoticeObservation();
        BooruChanged?.Invoke();
    }

    /// <summary>
    /// First shutdown step: closes composition and stops the watcher host (running check cancelled and persisted).
    /// Waits at most <see cref="BooruStopWait"/> so a stuck check can never hold back the queue stop; the returned
    /// task still completes only when the host has really stopped.
    /// </summary>
    private async Task<Task> StopBooruBeforeQueueAsync()
    {
        lock (_booruSync)
        {
            _booruClosed = true;
            foreach (BooruProviderBinding binding in _booruBindings) binding.AvailabilityChanged -= OnBooruAvailabilityChanged;
        }
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
        BooruSessionComposition[] compositions;
        try
        {
            lock (_booruSync) compositions = _booruCompositions.Values.ToArray();
        }
        finally { _booruGate.Release(); }
        foreach (BooruSessionComposition composition in compositions)
            composition.AvailabilityChanged -= NotifyBooruChanged;
        await Task.WhenAll(compositions.Select(composition => composition.StopAsync())).ConfigureAwait(false);
        NotifyBooruChanged();
    }

    /// <summary>
    /// The sole transport-retirement point: the shared queue has stopped, so no accepted job can call the
    /// composition's provider client again. Booru/availability drains alone are intentionally insufficient.
    /// Idempotent for both published and unpublished owners.
    /// </summary>
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
