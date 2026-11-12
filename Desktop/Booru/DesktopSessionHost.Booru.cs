using System;
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
    private BooruProviderBinding? _booruBinding;
    private BooruWorkbenchServices? _booruServicesOverride;
    private bool _booruClosed;

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
        set => Volatile.Write(ref _booruServicesOverride, value);
    }

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
                Path.GetFullPath(Config.DownloadPath), BooruClockForTests, BooruSchedulerOptionsForTests);
            try
            {
                // Shutdown cancels this token before it waits for the gate, so a start never outlives the session.
                await composition.StartAsync(_startupCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException) _logger.Error(exception, "The Danbooru watchers could not be started.");
                await composition.StopAsync().ConfigureAwait(false);
                composition.ReleaseTransport();
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
            if (!changed)
            {
                await composition.StopAsync().ConfigureAwait(false);
                composition.ReleaseTransport();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.Error(exception, "The Danbooru composition failed; the window stays hidden.");
        }
        finally { _booruGate.Release(); }
        if (changed) BooruChanged?.Invoke();
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
        BooruChanged?.Invoke();
    }

    /// <summary>After the queue stopped no job can use the original-media client any more.</summary>
    private void ReleaseBooruAfterQueue()
    {
        BooruSessionComposition? composition;
        lock (_booruSync) composition = _booru;
        composition?.ReleaseTransport();
    }
}
