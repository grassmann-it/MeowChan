using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

/// <summary>
/// Network and disk statistics in the tray, text only (tooltip and two menu lines). The logic is <see cref="TrayStatsPresenter"/>;
/// this file connects it to the window. Nothing here owns a timer.
/// </summary>
public partial class MainWindow
{
    private TrayStatsPresenter? _trayStats;

    // The statistics page belongs to the statistics work package; its merge adds
    // "partial void ShowStatisticsFromTray() => ShowStatistics();". Until then a click only brings the window back.
    partial void ShowStatisticsFromTray();

    private bool TrayWindowShown => IsVisible && WindowState != WindowState.Minimized;

    private void AddTrayStatsItems(NativeMenu menu)
    {
        _trayStats = new TrayStatsPresenter(_session.IoStats, menu, new TrayStatsHost(
            TooltipEnabled: () => _config.StatsTrayTooltip,
            MenuEnabled: () => _config.StatsTrayMenu,
            WindowShown: () => TrayWindowShown,
            Closing: () => _isClosing,
            Downloads: TrayDownloadState,
            SetTooltip: tip => { if (_trayIcon != null && _trayIcon.ToolTipText != tip) _trayIcon.ToolTipText = tip; },
            OpenStatistics: () => { RestoreWindow(); ShowStatisticsFromTray(); },
            Post: action => Dispatcher.UIThread.Post(action)));
    }

    private void DisposeTrayStats()
    {
        _trayStats?.Dispose();
        _trayStats = null;
    }

    private (bool Paused, int Active) TrayDownloadState()
    {
        bool paused = _session.Projection.IsPaused;
        return (paused, paused ? 0 : UrlList.Sum(row => row.Active));
    }
}
