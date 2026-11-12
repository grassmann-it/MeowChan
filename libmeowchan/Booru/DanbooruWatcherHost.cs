using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>
/// Lifecycle owner for Danbooru watchers in a running profile. Composition (B6/root):
/// create after <c>Registry.StartAsync</c> completed (profile admission Ready, shared queue running) and
/// the Danbooru worker is available behind the search port; call <see cref="StartAsync"/>; call
/// <see cref="StopAsync"/> before the Registry/queue stop and before the profile lease is released.
/// <para>
/// Start order (preflight G14): load and recover every stored watcher first (a crash-left Checking
/// watcher returns to its resume target with its cursor), then re-admit pending originals from local data
/// (B4 <see cref="BooruOriginalQueue.RecoverPendingAsync"/>, no provider request), then start the loop.
/// Stop order: cancel the running check and persist its consistent state while the profile still
/// accepts writes; queued downloads stay with the queue's own pause/restart contract.
/// </para>
/// </summary>
internal sealed class DanbooruWatcherHost : IAsyncDisposable
{
    private readonly BooruOriginalQueue _originals;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _started;
    private bool _stopped;

    internal DanbooruWatcherHost(BooruCatalogStore catalog, BooruOriginalQueue originals,
        IDanbooruWatcherSearchPort search, TimeProvider? timeProvider = null,
        DanbooruWatcherSchedulerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _originals = originals ?? throw new ArgumentNullException(nameof(originals));
        Scheduler = new DanbooruWatcherScheduler(catalog, search,
            new DanbooruWatcherQueueAdmission(originals, catalog, timeProvider), timeProvider, options);
    }

    /// <summary>The bindable service surface for the UI (status, check now, pause, resume, remove).</summary>
    internal DanbooruWatcherScheduler Scheduler { get; }

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || _stopped) return;
            // Only mark started once the whole start order succeeded. A cancelled or failed start
            // must stay retryable; otherwise a transient failure permanently disables the watchers.
            Scheduler.Load(cancellationToken);
            await _originals.RecoverPendingAsync(cancellationToken).ConfigureAwait(false);
            await Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally { _lifecycle.Release(); }
    }

    internal async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _stopped = true;
            await Scheduler.StopAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
