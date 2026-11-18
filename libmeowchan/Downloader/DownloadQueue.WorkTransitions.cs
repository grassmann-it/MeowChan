namespace MeowChan.Downloader;

using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Diagnostics;

public partial class DownloadQueue
{
    // Deterministic admission interleaving seam; never invoked under a product lock.
    internal Func<DownloadItem, Task>? BeforeFinalAdmission { get; set; }
    private readonly Dictionary<DownloadItem, int> _transitionItems = new(ReferenceEqualityComparer.Instance);

    private Transition EnterDurable(DownloadItem item, ProfileAdmissionToken? admission = null, bool continuation = false)
    {
        Transition transition = BeginTransition(item, admission, executing: !continuation);
        try
        {
            IWorkStateStore? store = JournalFor(item);
            if (store != null)
            {
                string url = ResourceUrl(item);
                using (PerformanceDiagnostics.Measure("queue.resource.wait", request: item.WorkOperationId, watch: item.Thread.ThreadUri.AbsoluteUri,
                    asset: item.AcquisitionRevisionKey ?? item.MediaIdentifier + ":" + item.Variant, epoch: item.WorkGeneration ?? 0))
                    transition.Fence = WorkStateAccess.For(store).Enter(url);
            }
            return transition;
        }
        catch { transition.Dispose(); throw; }
    }

    /// <summary>
    /// The chan work journal governing <paramref name="item"/>, or null when the queue is volatile or the
    /// item belongs to an <see cref="IExternalMediaWorkOwner"/> whose durability lives elsewhere.
    /// </summary>
    private IWorkStateStore? JournalFor(DownloadItem item)
        => item.Thread is IExternalMediaWorkOwner ? null : _workStore;

    private string ResourceUrl(DownloadItem item)
    {
        if (item.WorkResourceUrl != null) return item.WorkResourceUrl;
        string? url;
        lock (_stateLock) url = _workOwners.TryGetValue(item.Thread, out WorkResource? owner) ? owner.Url : null;
        return url ?? item.Thread.ThreadUri.AbsoluteUri;
    }

    private async Task CancelWaitersAsync(AdmissionWaiter[] waiters)
    {
        foreach (AdmissionWaiter waiter in waiters)
        {
            try { await waiter.Closed.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* The waiter already completed and detached its link. */ }
            catch (Exception exception) { _logger.Error(exception, "Error in media admission cancellation callback"); }
        }
    }

    private Transition BeginTransition(DownloadItem? item = null, ProfileAdmissionToken? admission = null, bool executing = true)
    {
        lock (_stateLock)
        {
            ProfileProducerEpoch.Use? use = executing && admission == null ? EnterProducerLocked() : null;
            Transition transition = new(this, item, admission ?? use?.Admission, use);
            if (_durableTransitions++ == 0) _durableDrained = CreateCompletionSource();
            if (item != null) _transitionItems[item] = _transitionItems.GetValueOrDefault(item) + 1;
            return transition;
        }
    }

    private sealed class Transition(DownloadQueue owner, DownloadItem? item,
        ProfileAdmissionToken? admission, ProfileProducerEpoch.Use? use) : IDisposable
    {
        internal ProfileAdmissionToken? Admission => admission;
        internal IDisposable? Fence;
        internal PerformanceDiagnostics.Measurement Measurement;
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Measurement.Dispose();
            Fence?.Dispose();
            lock (owner._stateLock)
            {
                if (item != null)
                {
                    if (owner._transitionItems[item] == 1) owner._transitionItems.Remove(item);
                    else owner._transitionItems[item]--;
                }
                if (--owner._durableTransitions == 0) owner._durableDrained.TrySetResult();
            }
            use?.Dispose();
        }
    }

    private static MediaWork? FindMedia(IWorkStateStore store, WorkResource owner, DownloadItem item)
        => WorkStateAccess.For(store).FindMedia(new(owner.Url, owner.Generation, item.MediaIdentifier, item.Variant, item.DestinationPath));

    private bool ItemOwnerCurrentLocked(DownloadItem item, WorkResource owner)
        => OwnerStillCurrentLocked(item.Thread, owner)
            && (item.WorkGeneration == null || item.WorkGeneration == owner.Generation && item.WorkResourceUrl == owner.Url);

    internal void RetireWorkOwner(IThread thread)
    {
        // Admission closes immediately, without waiting on I/O or cancelling callbacks under a lock.
        lock (_stateLock) _retiringThreads.GetValue(thread, static _ => new object());
    }

    internal bool IsWorkOwnerRetiring(IThread thread)
    {
        lock (_stateLock) return _retiringThreads.TryGetValue(thread, out _);
    }

    internal void RestoreWorkOwner(IThread thread)
    {
        lock (_stateLock) _retiringThreads.Remove(thread);
    }

    private async ValueTask<IDisposable> AcquirePublicationAsync(DownloadItem item, CancellationToken token, ProfileAdmissionToken? admission)
    {
        Transition transition = BeginTransition(item, admission, executing: false);
        try
        {
            if (JournalFor(item) is IWorkStateStore journal)
            {
                using (PerformanceDiagnostics.Measure("queue.publication.wait", request: item.WorkOperationId, watch: item.Thread.ThreadUri.AbsoluteUri,
                    asset: item.AcquisitionRevisionKey ?? item.MediaIdentifier + ":" + item.Variant, epoch: item.WorkGeneration ?? 0))
                    transition.Fence = await WorkStateAccess.For(journal).EnterAsync(
                        item.WorkResourceUrl ?? item.Thread.ThreadUri.AbsoluteUri, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            bool current;
            lock (_stateLock) current = !_stopping && !_cancelledThreads.TryGetValue(item.Thread, out _);
            if (!current || !CanRunDurableLocked(item, transition.Admission))
                throw new StaleMediaWorkException();
            token.ThrowIfCancellationRequested();
            transition.Measurement = PerformanceDiagnostics.Measure("queue.publication", request: item.WorkOperationId,
                watch: item.Thread.ThreadUri.AbsoluteUri, asset: item.AcquisitionRevisionKey ?? item.MediaIdentifier + ":" + item.Variant,
                epoch: item.WorkGeneration ?? 0);
            PerformanceDiagnostics.Record("queue.publication.authority", "authorized", request: item.WorkOperationId, watch: item.Thread.ThreadUri.AbsoluteUri,
                asset: item.AcquisitionRevisionKey ?? item.MediaIdentifier + ":" + item.Variant, epoch: item.WorkGeneration ?? 0);
            return transition;
        }
        catch { transition.Dispose(); throw; }
    }
}

// Superseded work is not a failed current attempt and must not increment retries or quarantine its successor.
internal sealed class StaleMediaWorkException() : OperationCanceledException("The media acquisition is no longer current work.");
