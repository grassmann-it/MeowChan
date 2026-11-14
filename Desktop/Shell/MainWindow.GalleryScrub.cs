using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop;
using Serilog;

namespace MeowChan;

/// <summary>
/// Main-window owner for one profile's video scrub cache. Configuration runs away from the UI thread and
/// serializes behind the previous request so a newer budget cannot be overwritten by a late trim.
/// </summary>
internal sealed class GalleryScrubPreviewHost : IAsyncDisposable
{
    private readonly VideoScrubPreviewCache _cache;
    private readonly Func<VideoScrubPreviewOptions, CancellationToken, Task> _configure;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _latestCancellation;
    private Task _configuration = Task.CompletedTask;
    private long _revision;
    private bool _disposed;

    internal GalleryScrubPreviewHost(string profileDirectory)
    {
        string directory = CacheDirectoryForProfile(profileDirectory);
        _cache = new VideoScrubPreviewCache(directory, VideoScrubPreviewOptions.Default);
        _configure = (options, token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            _cache.Configure(options);
        }, token);
    }

    // Deterministic host seam: production uses the constructor above; tests can make a configure operation
    // wait without constructing a viewer, window or native decoder.
    internal GalleryScrubPreviewHost(VideoScrubPreviewCache cache,
        Func<VideoScrubPreviewOptions, CancellationToken, Task> configure)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    internal VideoScrubPreviewCache Cache => _cache;
    internal string CacheDirectory => _cache.Directory;

    internal static string CacheDirectoryForProfile(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        return Path.Combine(Path.GetFullPath(profileDirectory), "cache", "video-scrub-v1");
    }

    internal static VideoScrubPreviewOptions OptionsFor(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new VideoScrubPreviewOptions(VideoScrubPreviewOptions.ClampCacheLimit(config.VideoScrubCacheLimitMiB));
    }

    internal void Configure(VideoScrubPreviewOptions options)
    {
        Task task;
        try { task = ConfigureAsync(options); }
        catch (ObjectDisposedException) { return; }
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    internal Task ConfigureAsync(VideoScrubPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long revision = ++_revision;
            TryCancel(_latestCancellation);
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _latestCancellation = cancellation;
            Task previous = _configuration;
            Task next = ApplyLatestAsync(previous, revision, options, cancellation);
            _configuration = next;
            return next;
        }
    }

    private async Task ApplyLatestAsync(Task previous, long revision, VideoScrubPreviewOptions options,
        CancellationTokenSource cancellation)
    {
        try
        {
            await previous.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
            }
            await _configure(options, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer budget or host shutdown superseded this optional trim.
        }
        catch (IOException error)
        {
            Log.Warning(error, "Could not configure the video scrub preview cache.");
        }
        catch (UnauthorizedAccessException error)
        {
            Log.Warning(error, "Could not configure the video scrub preview cache.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_latestCancellation, cancellation)) _latestCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        bool first;
        lock (_gate)
        {
            first = !_disposed;
            if (first)
            {
                _disposed = true;
                ++_revision;
                TryCancel(_latestCancellation);
                _lifetime.Cancel();
            }
            pending = _configuration;
        }

        try { await pending.ConfigureAwait(false); }
        finally
        {
            if (first) _lifetime.Dispose();
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation == null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

public partial class MainWindow
{
    private GalleryScrubPreviewHost? _galleryScrubHost;

    private void ConfigureGalleryScrubPreview()
    {
        if (_isClosing) return;
        if (_galleryScrubHost == null)
        {
            if (_gallery == null) return;
            _galleryScrubHost = new GalleryScrubPreviewHost(_session.ProfileDirectory);
        }

        GalleryScrubPreviewHost host = _galleryScrubHost;
        _gallery?.ConfigureScrubPreview(host.Cache);
        host.Configure(GalleryScrubPreviewHost.OptionsFor(_config));
    }

    private Task DisposeGalleryScrubPreviewAsync()
    {
        GalleryScrubPreviewHost? host = _galleryScrubHost;
        _galleryScrubHost = null;
        return host?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }
}
