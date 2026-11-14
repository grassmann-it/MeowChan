using System;
using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeowChan;

public partial class MainWindow
{
    private void InitializeStatisticsPage()
    {
        // The header gets a recent-window scale, not a peak from the entire retained page history.
        TransferSpeedGraph.SampleIntervalMs = () => _session.IoStats.Options.SampleIntervalMs;
        TransferDiskGraph.SampleIntervalMs = () => _session.IoStats.Options.SampleIntervalMs;
        _statisticsPage = new StatisticsPage();
        _statisticsPage.Attach(_session.IoStats, SourceDisplayName);
        _statusPage?.AttachStatistics(_statisticsPage); // the statistics segment of the Status page
    }

    // The display name of a source id for the table; null for ids no provider announces (they show as written).
    private string? SourceDisplayName(string sourceId) =>
        _session.GetProviderDescriptors().FirstOrDefault(provider => provider.SourceId == sourceId)?.DisplayName;

    /// <summary>The statistics segment of the Status page. False while the window cannot navigate (no restored session, busy editors).</summary>
    internal bool ShowStatistics() => ShowStatus(StatusSegment.Statistics);

    private void ShowStatisticsPage(bool focusContent = true)
    {
        if (_statisticsPage == null) return;
        _statisticsPage.Apply(_session.IoStats.Snapshot());
        // After layout, so the focus lands on a control that is already visible.
        if (focusContent)
            Dispatcher.UIThread.Post(() => { if (!_isClosing && StatisticsSegmentShown) _statisticsPage?.FocusContent(); }, DispatcherPriority.Input);
    }

    // The header graph leads to the statistics segment of the Status page.
    private void OnTransferSpeedClick(object? sender, RoutedEventArgs e) => ShowStatistics();
}
