using System;
using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeowChan;

public partial class MainWindow
{
    private void InitializeStatisticsPage()
    {
        _statisticsPage = new StatisticsPage();
        _statisticsPage.Attach(_session.IoStats, SourceDisplayName);
        StatisticsPageHost.Content = _statisticsPage;
    }

    // The display name of a source id for the table; null for ids no provider announces (they show as written).
    private string? SourceDisplayName(string sourceId) =>
        _session.GetProviderDescriptors().FirstOrDefault(provider => provider.SourceId == sourceId)?.DisplayName;

    /// <summary>The statistics page of Werkzeuge. False while the window cannot navigate (no restored session, busy editors).</summary>
    internal bool ShowStatistics() => NavigateWorkspace(WorkspacePage.Statistics);

    private void ShowStatisticsPage()
    {
        if (_statisticsPage == null) return;
        _statisticsPage.Apply(_session.IoStats.Snapshot());
        // After layout, so the focus lands on a control that is already visible.
        Dispatcher.UIThread.Post(() => { if (!_isClosing && _selectedPage == WorkspacePage.Statistics) _statisticsPage?.FocusContent(); }, DispatcherPriority.Input);
    }

    private void OnStatisticsNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        ShowStatistics();
    }

    private void OnTransferSpeedClick(object? sender, RoutedEventArgs e) => ShowStatistics();
}
