using System;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>
/// The tray's claim on the statistics hub: held only while the window is hidden and the tooltip setting is on, so the hub's slow
/// timer (which itself runs only while downloads are active) exists exactly then. The claim also carries the hub's
/// <see cref="IoStatsHub.Sampled"/> notification, which is raised on a pool thread and must be handed to the UI thread by
/// <c>onSampled</c>. Call <see cref="Update"/> from the UI thread; it does nothing when the answer has not changed.
/// </summary>
internal sealed class TrayStatsClaim : IDisposable
{
    private readonly IoStatsHub _hub;
    private readonly Action _onSampled;
    private IoStatsSubscription? _claim;
    private bool _disposed;

    internal TrayStatsClaim(IoStatsHub hub, Action onSampled)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(onSampled);
        _hub = hub;
        _onSampled = onSampled;
    }

    internal bool Held => _claim != null;

    /// <summary>A visible window drives its own sampling, a closing window has nothing to show, and the setting can switch the tooltip off.</summary>
    internal static bool Wanted(bool closing, bool windowShown, bool tooltipSetting) =>
        !closing && tooltipSetting && !windowShown;

    internal void Update(bool wanted)
    {
        if (_disposed) wanted = false;
        if (wanted == Held) return;
        if (wanted)
        {
            _hub.Sampled += _onSampled;
            _claim = _hub.Subscribe(IoStatsVisibility.Tray);
        }
        else
        {
            _claim?.Dispose();
            _claim = null;
            _hub.Sampled -= _onSampled;
        }
    }

    public void Dispose()
    {
        Update(false);
        _disposed = true;
    }
}
