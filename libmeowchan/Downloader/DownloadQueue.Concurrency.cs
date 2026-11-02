namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;

public partial class DownloadQueue
{
    // Retained as the compatible default; each queue may opt into a different bounded fence.
    public const int ProtectiveParallelDownloadsCeiling = 32;
    public const int MaximumParallelDownloadsSafetyLimit = 256;

    private int _parallelDownloadsSafetyLimit = ProtectiveParallelDownloadsCeiling;

    /// <summary>The instance's resource safety fence, independent of the requested parallelism.</summary>
    public int ParallelDownloadsSafetyLimit
    {
        get { lock (_stateLock) { return _parallelDownloadsSafetyLimit; } }
    }

    private int EffectiveParallelDownloadsLocked => Math.Min(_maxParallelDownloads, _parallelDownloadsSafetyLimit);

    /// <summary>
    /// Changes the safety fence without cancelling or restarting active transfers.
    /// Reductions drain existing reservations; increases can immediately dispatch queued work.
    /// </summary>
    public void UpdateParallelDownloadsSafetyLimit(int safetyLimit)
    {
        ValidateParallelDownloadsSafetyLimit(safetyLimit);
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _parallelDownloadsSafetyLimit = safetyLimit;
            ready = ReserveWorkersLocked();
        }
        StartWorkers(ready);
        _changes.Signal();
    }

    /// <summary>
    /// Atomically changes requested parallelism and its safety fence before reserving workers.
    /// Applying two separate setters could transiently admit work above the new effective limit.
    /// Existing active transfers still drain normally after a reduction.
    /// </summary>
    public void UpdateConcurrencyLimits(int requested, int safetyLimit)
    {
        if (requested <= 0) throw new ArgumentOutOfRangeException(nameof(requested), "Max parallel downloads must be greater than zero.");
        ValidateParallelDownloadsSafetyLimit(safetyLimit);
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _maxParallelDownloads = requested;
            _parallelDownloadsSafetyLimit = safetyLimit;
            ready = ReserveWorkersLocked();
        }
        StartWorkers(ready);
        _changes.Signal();
    }

    private static void ValidateParallelDownloadsSafetyLimit(int safetyLimit)
    {
        if (safetyLimit is < 1 or > MaximumParallelDownloadsSafetyLimit)
            throw new ArgumentOutOfRangeException(nameof(safetyLimit), "The parallel download safety limit must be between 1 and 256.");
    }

    /// <summary>
    /// Atomic diagnostic snapshot. Configured remains the user's positive persisted value.
    /// Active can temporarily exceed Effective after a reduction: existing transfers drain,
    /// never abort/restart merely to enforce a new limit. DeferredSlots is suppressed requested
    /// concurrency, not a count of files or waiters. StateChanged invalidates this snapshot.
    /// </summary>
    public (int Configured, int Effective, int Active, int DeferredSlots) GetConcurrencySnapshot()
    {
        lock (_stateLock)
            return (_maxParallelDownloads, EffectiveParallelDownloadsLocked, _activeDownloads,
                _maxParallelDownloads - EffectiveParallelDownloadsLocked);
    }
}
