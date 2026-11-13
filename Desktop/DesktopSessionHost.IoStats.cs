using System;
using System.IO;
using System.Threading;
using MeowChan.Desktop.Stats;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// Connects the process-wide <see cref="IoStatsHub"/> to this session: the profile's <c>io-stats.json</c>, the download
/// queue's activity and the statistics settings. Statistics are derived data, so none of this can fail the session.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    private readonly IoStatsHub _ioStats;
    private IDisposable? _ioStatsAttachment;

    /// <summary>The hub the header graph, the statistics page and the tray read: the process-wide one unless a test injected its own.</summary>
    internal IoStatsHub IoStats => _ioStats;

    private void StartIoStats()
    {
        try
        {
            _ioStats.Configure(IoStatsOptions.FromConfig(Config));
            _ioStatsAttachment = _ioStats.Attach(new IoStatsStore(_profileLease.ProfileDirectory, _profileLease), ReadIoActivity);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warning(error, "The network and disk statistics could not be connected to the profile.");
        }
    }

    private IoActivity ReadIoActivity()
    {
        (int active, int pending, int waiting) = _downloadQueue.GetCapacitySnapshot();
        return new IoActivity(active, pending, waiting);
    }

    // The registry reports from arbitrary threads and possibly under its own locks; the hub reads the queue's counters, so the
    // refresh runs on the pool, and a burst of changes becomes one refresh.
    private int _ioActivityQueued;

    private void QueueIoActivityRefresh()
    {
        if (_ioStatsAttachment == null || Interlocked.Exchange(ref _ioActivityQueued, 1) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            DesktopSessionHost host = (DesktopSessionHost)state!;
            Volatile.Write(ref host._ioActivityQueued, 0);
            if (host._ioStatsAttachment != null) host._ioStats.NotifyActivityChanged();
        }, this);
    }

    private void ApplyIoStatsOptions() => _ioStats.Configure(IoStatsOptions.FromConfig(Config));

    // Called once the engine has stopped, so the counters are final; writes changed totals and disconnects.
    private void StopIoStats()
    {
        IDisposable? attachment = _ioStatsAttachment;
        _ioStatsAttachment = null;
        attachment?.Dispose();
    }
}
