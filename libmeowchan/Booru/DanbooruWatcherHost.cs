using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using Serilog;

namespace MeowChan.Booru;

/// <summary>
/// Lifecycle owner for Danbooru watchers in a running profile. Composition (B6/root):
/// create after <c>Registry.StartAsync</c> completed (profile admission Ready, shared queue running) and
/// the Danbooru worker is available behind the search port; call <see cref="StartAsync"/>; call
/// <see cref="StopAsync"/> before the Registry/queue stop and before the profile lease is released.
/// <para>
/// Start order (preflight G14): load and recover every stored watcher first (a crash-left Checking
/// watcher returns to its resume target with its cursor), then begin re-admitting pending originals from
/// local data (B4 <see cref="BooruOriginalQueue.RecoverPendingAsync"/>, no provider request), then start
/// the loop. The re-admission runs as an observed background task with its own cancellation: the shared
/// queue may hold an admission back (paused producers, free-space gate), and the host start must not wait
/// for that. A pending original the loop finds again meanwhile is deduplicated by the queue adapter (one
/// owner per post). A failed recovery is logged and leaves every request pending for the next start.
/// Stop order: cancel the recovery and the running check, persist the check's consistent state while the
/// profile still accepts writes, and wait for both; queued downloads stay with the queue's own
/// pause/restart contract.
/// </para>
/// </summary>
internal sealed class DanbooruWatcherHost : IAsyncDisposable
{
    private readonly BooruOriginalQueue _originals;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _recoveryCancellation;
    private Task<IReadOnlyList<BooruOriginalAdmission>> _recovery = Task.FromResult<IReadOnlyList<BooruOriginalAdmission>>([]);
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

    /// <summary>
    /// The background re-admission of pending originals begun by the last successful start. A stop cancels it;
    /// it then yields the admissions already handed to the queue. An expected failure (unreadable catalog,
    /// profile closing) is logged and yields an empty list; only a programming error faults it.
    /// </summary>
    internal Task<IReadOnlyList<BooruOriginalAdmission>> Recovery => Volatile.Read(ref _recovery);

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || _stopped) return;
            // Only mark started once the whole start order succeeded. A cancelled or failed start
            // must stay retryable; otherwise a transient failure permanently disables the watchers.
            Scheduler.Load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource recovery = new();
            Task<IReadOnlyList<BooruOriginalAdmission>> recovering = Task.Run(() => RecoverAsync(recovery.Token), CancellationToken.None);
            try { await Scheduler.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                recovery.Cancel();
                await recovering.ConfigureAwait(false);
                recovery.Dispose();
                throw;
            }
            _recoveryCancellation = recovery;
            Volatile.Write(ref _recovery, recovering);
            _started = true;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<IReadOnlyList<BooruOriginalAdmission>> RecoverAsync(CancellationToken cancellationToken)
    {
        try { return await _originals.RecoverPendingAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return []; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ObjectDisposedException)
        {
            // Nothing was lost: every request that was not re-admitted is still pending in the catalog. An unreadable
            // catalog is already visible as the scheduler's load fault.
            Log.Warning(exception, "Danbooru pending originals could not be re-admitted; they stay pending for the next start");
            return [];
        }
    }

    internal async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _stopped = true;
            CancellationTokenSource? recovery = _recoveryCancellation;
            _recoveryCancellation = null;
            try { recovery?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { await Scheduler.StopAsync().ConfigureAwait(false); }
            finally
            {
                // A recovery admission waiting at the queue observes the cancellation; one already handed over
                // keeps its own queue job. Only a programming error faults RecoverAsync.
                try { await Recovery.ConfigureAwait(false); }
                finally { recovery?.Dispose(); }
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
