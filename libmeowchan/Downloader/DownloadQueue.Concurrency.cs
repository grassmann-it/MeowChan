namespace MeowChan.Downloader;

using System;

public partial class DownloadQueue
{
    // A resource safety fence, not a preference/default. See C10203 HTTP handoff.
    public const int ProtectiveParallelDownloadsCeiling = 32;

    private int EffectiveParallelDownloadsLocked => Math.Min(_maxParallelDownloads, ProtectiveParallelDownloadsCeiling);

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
