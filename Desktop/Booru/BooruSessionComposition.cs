using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// What the plugin runtime supplies for the Danbooru composition: the declared post-search source, the booru host
/// services the plugin worker was started with (one job registry and one bounded metadata transport shared by the
/// worker and the host-side search service), a worker resolver that returns null while the plugin is disabled or
/// not running, and the session's borrowed network transport (redirects off, no credentials).
/// </summary>
internal sealed class BooruProviderBinding
{
    private readonly RequestPolicyState _policy;
    private readonly ILogger _logger;

    internal BooruProviderBinding(SourceDescription source, BooruHostServices services, HttpClient transport,
        RequestPolicyState policy, Func<CancellationToken, Task<IPluginWorkerClient?>> worker, Func<bool> isEnabled,
        ILogger? logger = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        GetWorkerAsync = worker ?? throw new ArgumentNullException(nameof(worker));
        IsEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    internal SourceDescription Source { get; }
    internal BooruHostServices Services { get; }

    /// <summary>Borrowed; never disposed by the composition.</summary>
    internal HttpClient Transport { get; }
    internal Func<CancellationToken, Task<IPluginWorkerClient?>> GetWorkerAsync { get; }
    internal Func<bool> IsEnabled { get; }

    /// <summary>Raised (on any thread) when the plugin was enabled or disabled at runtime.</summary>
    internal event Action? AvailabilityChanged;

    internal void RaiseAvailabilityChanged() => AvailabilityChanged?.Invoke();

    /// <summary>
    /// The existing original-media path of plugin sources (<see cref="PluginOriginalMediaHttp"/>): GET/HEAD only,
    /// declared original-media hosts, validated redirects, shared provider policy. The caller owns the client.
    /// </summary>
    internal HttpClient CreateOriginalMediaClient() => PluginOriginalMediaHttp.CreateClient(Transport, Source, _policy, _logger);
}

/// <summary>
/// The production composition root of the Danbooru feature for one running profile. Built only after the profile
/// is Ready (registry restored, shared queue running) and only while the Danbooru plugin is enabled:
/// <see cref="BooruSearchService"/> over the plugin's booru host services, one <see cref="BooruOriginalQueue"/> over the
/// registry's existing download queue and the profile's <see cref="BooruCatalogStore"/>, the
/// <see cref="BooruWatcherSearchPort"/> and the <see cref="DanbooruWatcherHost"/> in its documented start order, plus
/// the four UI ports of the Danbooru window. <see cref="StopAsync"/> must run before the queue and the registry stop.
/// </summary>
internal sealed class BooruSessionComposition : IAsyncDisposable
{
    private readonly HttpClient _mediaClient;
    private BooruPreviewHttp _previewHttp;
    private readonly BooruOriginalQueuePort _queuePort;
    private readonly BooruSchedulerWatcherPort _watcherPort;
    private readonly BooruProviderBinding _binding;
    private readonly BooruPortAvailabilityGate _availability;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TaskCompletionSource _stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider? _clock;
    private readonly DanbooruWatcherSchedulerOptions? _options;
    private bool _started;
    private bool _watcherStopped;
    private int _enabled;
    private int _stopped;

    private BooruSessionComposition(BooruProviderBinding binding, DownloadQueue queue, BooruCatalogStore catalog,
        string outputRoot, TimeProvider? clock, DanbooruWatcherSchedulerOptions? options)
    {
        _binding = binding;
        _clock = clock;
        _options = options;
        _enabled = binding.IsEnabled() ? 1 : 0;
        _availability = new BooruPortAvailabilityGate(() => binding.IsEnabled()
            && catalog.ProfileLease.Admission.Phase == ProfileAdmissionPhase.Ready);
        Catalog = catalog;
        Search = new BooruSearchService(binding.Services);
        _mediaClient = binding.CreateOriginalMediaClient();
        try
        {
            Originals = new BooruOriginalQueue(queue, catalog, outputRoot, _mediaClient, timeProvider: clock);
            SearchPort = new BooruWatcherSearchPort(Search, binding.Services, binding.GetWorkerAsync, clock);
            WatcherHost = CreateWatcherHost();
            _previewHttp = new BooruPreviewHttp(binding.Transport, binding.Source, timeProvider: clock);
            _queuePort = new BooruOriginalQueuePort(Originals, catalog, binding.Source.SourceId, _availability);
            _watcherPort = new BooruSchedulerWatcherPort(WatcherHost.Scheduler, catalog, binding.Source.SourceId, _availability);
            Services = new BooruWorkbenchServices(
                new BooruHostSearchAdapter(Search, binding.GetWorkerAsync, binding.Source.SourceId, binding.Source.Name),
                _queuePort, _watcherPort, new BooruHostPreviewPort(_previewHttp, binding.Source.SourceId, _availability, () => _previewHttp), clock);
            _binding.AvailabilityChanged += OnAvailabilityChanged;
        }
        catch
        {
            _availability.Close(terminal: true);
            _mediaClient.Dispose();
            throw;
        }
    }

    internal BooruCatalogStore Catalog { get; }
    internal BooruSearchService Search { get; }
    internal BooruOriginalQueue Originals { get; }
    internal BooruWatcherSearchPort SearchPort { get; }
    internal DanbooruWatcherHost WatcherHost { get; private set; }
    internal BooruWorkbenchServices Services { get; }
    internal BooruPreviewHttp Previews => _previewHttp;

    /// <summary>Also closed while a disabled generation is draining; old handles use the same gate.</summary>
    internal bool IsAvailable => _availability.IsAvailable;

    internal static BooruSessionComposition Create(BooruProviderBinding binding, DownloadQueue queue, BooruCatalogStore catalog,
        string outputRoot, TimeProvider? clock = null, DanbooruWatcherSchedulerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        return new BooruSessionComposition(binding, queue, catalog, outputRoot, clock, options);
    }

    /// <summary>
    /// <see cref="DanbooruWatcherHost.StartAsync"/>: load and recover stored watchers, re-admit pending originals from
    /// local data (no provider request), then start the scheduler loop.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || Volatile.Read(ref _stopped) != 0) return;
            _started = true;
            using BooruPortAvailabilityGate.Lease? start = _availability.TryEnter(cancellationToken);
            if (start != null && start.TryStart(() => WatcherHost.StartAsync(start.Token), out Task pending))
                await pending.ConfigureAwait(false);
        }
        catch { _started = false; throw; }
        finally { _lifecycle.Release(); }
    }

    private DanbooruWatcherHost CreateWatcherHost() => new(Catalog, Originals,
        new BooruAvailabilityWatcherSearchPort(SearchPort, _availability), _clock, _options);

    private void OnAvailabilityChanged()
    {
        int enabled = _binding.IsEnabled() ? 1 : 0;
        if (Interlocked.Exchange(ref _enabled, enabled) == enabled) return;
        // Revocation is synchronous even if Start/Stop currently owns the lifecycle lane. Disposing the old
        // preview cache prevents an uncooperative old HTTP completion from populating the next generation.
        _availability.Close();
        _previewHttp.Dispose();
        _ = RefreshAvailabilityAsync();
    }

    private async Task RefreshAvailabilityAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopped) != 0 || _availability.IsStopped || _availability.IsAvailable) return;
            if (!_watcherStopped)
            {
                await WatcherHost.StopAsync().ConfigureAwait(false);
                _watcherStopped = true;
            }
            await _availability.DrainAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _stopped) != 0 || _availability.IsStopped || !_binding.IsEnabled()) return;
            // StopAsync is terminal in B5. Recreate only its lifecycle owner, after a real drain, over the SAME
            // OriginalQueue/catalog/search services. Never reuse pending manual requests from the disabled run.
            WatcherHost = CreateWatcherHost();
            _watcherPort.ReplaceScheduler(WatcherHost.Scheduler);
            _watcherStopped = false;
            _previewHttp = new BooruPreviewHttp(_binding.Transport, _binding.Source, timeProvider: _clock);
            _availability.Open();
            if (_started)
            {
                using BooruPortAvailabilityGate.Lease? start = _availability.TryEnter(CancellationToken.None);
                if (start != null && start.TryStart(() => WatcherHost.StartAsync(start.Token), out Task pending))
                    await pending.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _availability.Close();
            _previewHttp.Dispose();
            Log.Warning(exception, "Danbooru availability transition failed; ports remain unavailable");
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Synchronous terminal cut for the session's shutdown ingress. No lifecycle wait or transport release; StopAsync
    /// still owns physical drain. May be called before the asynchronous engine-stop worker reaches this composition.
    /// </summary>
    internal void ClosePortIngress()
    {
        _availability.Close(terminal: true);
        _previewHttp.Dispose();
    }

    /// <summary>Revokes escaped ports synchronously, then drains the watcher generation. Idempotent.</summary>
    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            await _stopCompletion.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            _binding.AvailabilityChanged -= OnAvailabilityChanged;
            ClosePortIngress();
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                await WatcherHost.StopAsync().ConfigureAwait(false);
                await _availability.DrainAsync().ConfigureAwait(false);
            }
            finally
            {
                _queuePort.Dispose();
                _watcherPort.Dispose();
                _previewHttp.Dispose();
                _lifecycle.Release();
            }
            _stopCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _stopCompletion.TrySetException(exception);
            throw;
        }
    }

    /// <summary>Releases the media client once the shared queue no longer runs jobs that could use it.</summary>
    internal void ReleaseTransport() => _mediaClient.Dispose();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
