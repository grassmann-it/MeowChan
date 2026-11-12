using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Http;
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
    private readonly BooruPreviewHttp _previewHttp;
    private readonly BooruOriginalQueuePort _queuePort;
    private readonly BooruSchedulerWatcherPort _watcherPort;
    private readonly BooruProviderBinding _binding;
    private int _stopped;

    private BooruSessionComposition(BooruProviderBinding binding, DownloadQueue queue, BooruCatalogStore catalog,
        string outputRoot, TimeProvider? clock, DanbooruWatcherSchedulerOptions? options)
    {
        _binding = binding;
        Catalog = catalog;
        Search = new BooruSearchService(binding.Services);
        _mediaClient = binding.CreateOriginalMediaClient();
        try
        {
            Originals = new BooruOriginalQueue(queue, catalog, outputRoot, _mediaClient, timeProvider: clock);
            SearchPort = new BooruWatcherSearchPort(Search, binding.Services, binding.GetWorkerAsync, clock);
            WatcherHost = new DanbooruWatcherHost(catalog, Originals, SearchPort, clock, options);
            _previewHttp = new BooruPreviewHttp(binding.Transport, binding.Source, timeProvider: clock);
            _queuePort = new BooruOriginalQueuePort(Originals, catalog, binding.Source.SourceId);
            _watcherPort = new BooruSchedulerWatcherPort(WatcherHost.Scheduler, catalog, binding.Source.SourceId);
            Services = new BooruWorkbenchServices(
                new BooruHostSearchAdapter(Search, binding.GetWorkerAsync, binding.Source.SourceId, binding.Source.Name),
                _queuePort, _watcherPort, new BooruHostPreviewPort(_previewHttp, binding.Source.SourceId), clock);
        }
        catch
        {
            _mediaClient.Dispose();
            throw;
        }
    }

    internal BooruCatalogStore Catalog { get; }
    internal BooruSearchService Search { get; }
    internal BooruOriginalQueue Originals { get; }
    internal BooruWatcherSearchPort SearchPort { get; }
    internal DanbooruWatcherHost WatcherHost { get; }
    internal BooruWorkbenchServices Services { get; }
    internal BooruPreviewHttp Previews => _previewHttp;

    /// <summary>True while the plugin is enabled; a disabled plugin hides the window entry.</summary>
    internal bool IsAvailable => Volatile.Read(ref _stopped) == 0 && _binding.IsEnabled();

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
    internal Task StartAsync(CancellationToken cancellationToken) => WatcherHost.StartAsync(cancellationToken);

    /// <summary>Cancels a running check and stops the loop while the profile still accepts writes. Idempotent.</summary>
    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { await WatcherHost.StopAsync().ConfigureAwait(false); }
        finally
        {
            _queuePort.Dispose();
            _watcherPort.Dispose();
            _previewHttp.Dispose();
        }
    }

    /// <summary>Releases the media client once the shared queue no longer runs jobs that could use it.</summary>
    internal void ReleaseTransport() => _mediaClient.Dispose();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
