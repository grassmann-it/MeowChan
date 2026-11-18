using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Stats;
using Serilog;

namespace MeowChan;

// Status page (plan "Diagnose und Statistik", SM1/SM2): one main page with the segments Statistics and Diagnostics. The
// footer button and the menu entries open the segment used last (per profile, Config.StatusPageSegment); deep links (header
// graph, tray, Sources page, the recovery route, the old segment commands) choose their segment. The statistics segment needs
// a restored session; the diagnostics segment is reachable after a failed recovery too.
public partial class MainWindow
{
    private StatusPage? _statusPage;
    private StatusSegment _statusSegment;
    private bool _statusSegmentLoaded;

    /// <summary>The Status page (built after the first frame like the pages it holds).</summary>
    internal StatusPage StatusPage
    {
        get
        {
            EnsureDeferredWorkspacePages();
            return _statusPage ?? throw new InvalidOperationException("The status page is not initialized.");
        }
    }

    /// <summary>The host of the diagnostics segment; smokes swap its content for a test page as they did with the old page host.</summary>
    internal ContentControl DiagnosticsPageHost => StatusPage.DiagnosticsHost;
    /// <summary>The host of the statistics segment.</summary>
    internal ContentControl StatisticsPageHost => StatusPage.StatisticsHost;

    /// <summary>The segment the Status page shows (or will show when it is opened next).</summary>
    internal StatusSegment StatusSegmentSelected => CurrentStatusSegment;
    internal bool DiagnosticsSegmentShown => _selectedPage == WorkspacePage.Status && CurrentStatusSegment == StatusSegment.Diagnostics;
    internal bool StatisticsSegmentShown => _selectedPage == WorkspacePage.Status && CurrentStatusSegment == StatusSegment.Statistics;

    // The saved choice is read once, lazily: the window's config is final by the time a page can be opened.
    private StatusSegment CurrentStatusSegment
    {
        get
        {
            if (!_statusSegmentLoaded)
            {
                _statusSegment = _config.StatusPageSegment == Config.StatusPageSegmentDiagnostics ? StatusSegment.Diagnostics : StatusSegment.Statistics;
                _statusSegmentLoaded = true;
            }
            return _statusSegment;
        }
    }

    /// <summary>Statistics needs the restored session (its hub and the sampling claim); until then only Diagnostics can be shown.</summary>
    private bool StatisticsSegmentAvailable => _restoreCompleted;

    /// <summary>The segment a request ends on: the requested one, or Diagnostics while Statistics is not available yet.</summary>
    private StatusSegment EffectiveStatusSegment(StatusSegment requested) =>
        requested == StatusSegment.Statistics && !StatisticsSegmentAvailable ? StatusSegment.Diagnostics : requested;

    // Built in the deferred step with the diagnostics page; the statistics page is attached when it is built.
    private void InitializeStatusPage()
    {
        _statusPage = new StatusPage();
        _statusPage.SegmentRequested += segment => SelectStatusSegment(segment, focusContent: false);
        _statusPage.ShowSegment(CurrentStatusSegment);
        StatusPageHost.Content = _statusPage;
    }

    /// <summary>
    /// Opens the Status page on <paramref name="segment"/> (null: the segment used last) and, for the diagnostics, with the
    /// source filter preset to <paramref name="sourceId"/> when one is given. False while the window cannot navigate.
    /// </summary>
    internal bool ShowStatus(StatusSegment? segment = null, string? sourceId = null)
    {
        StatusSegment target = EffectiveStatusSegment(segment ?? CurrentStatusSegment);
        if (_selectedPage == WorkspacePage.Status)
        {
            if (WorkspaceCommandBusy) return false;
            SelectStatusSegment(target, focusContent: true);
        }
        else
        {
            StatusSegment previous = CurrentStatusSegment;
            _statusSegment = target;
            if (!NavigateWorkspace(WorkspacePage.Status)) { _statusSegment = previous; return false; }
            // The recovery route falls back to Diagnostics without a restored session: that is no choice to remember.
            if (StatisticsSegmentAvailable) RememberStatusSegment(target);
        }
        if (sourceId != null && target == StatusSegment.Diagnostics) _diagnosticsPage?.FilterSource(sourceId);
        return true;
    }

    /// <summary>Switches the shown Status page to <paramref name="segment"/>; a segment that cannot be shown keeps the current one.
    /// A choice on the segment bar keeps the focus on the bar (arrow keys move between the segments); a deep link focuses the content.</summary>
    private void SelectStatusSegment(StatusSegment segment, bool focusContent)
    {
        if (_isClosing) return;
        StatusSegment target = EffectiveStatusSegment(segment);
        if (_selectedPage != WorkspacePage.Status || WorkspaceCommandBusy)
        {
            _statusPage?.ShowSegment(CurrentStatusSegment);
            return;
        }
        if (target == CurrentStatusSegment)
        {
            _statusPage?.ShowSegment(target);
            if (focusContent) FocusStatusSegment();
            return;
        }
        if (target != StatusSegment.Statistics) _statisticsPage?.Deactivate();
        _statusSegment = target;
        RememberStatusSegment(target);
        ActivateStatusSegment(focusContent);
    }

    // Shows the current segment and does what entering it needs: the claim, the view, the data and (unless the segment bar
    // keeps it) the focus.
    private void ActivateStatusSegment(bool focusContent = true)
    {
        StatusSegment segment = CurrentStatusSegment;
        _statusPage?.ShowSegment(segment);
        _statusPage?.SetStatisticsAvailable(StatisticsSegmentAvailable);
        UpdateSpeedSamplingState(); // the statistics segment claims samples while it is shown, and gives them up when left
        RefreshStatusRates();
        if (segment == StatusSegment.Diagnostics)
        {
            _diagnosticsPage?.ApplyWorkView(ConfiguredWorkView);
            RefreshDiagnosticPage();
        }
        else
        {
            RefreshDiagnosticPage(); // the status line's counts
            _statisticsPage?.ApplyWorkView(ConfiguredWorkView);
            ShowStatisticsPage(focusContent: false);
        }
        if (focusContent) FocusStatusSegment();
    }

    // The content of the shown segment takes the focus: the diagnostics table, or the statistics page's own choice (after
    // layout, so the focus lands on a control that is already visible).
    private void FocusStatusSegment()
    {
        if (DiagnosticsSegmentShown) _diagnosticsPage?.FindControl<DataGrid>("DiagnosticsGrid")?.Focus();
        else if (StatisticsSegmentShown)
            Dispatcher.UIThread.Post(() => { if (!_isClosing && StatisticsSegmentShown) _statisticsPage?.FocusContent(); }, DispatcherPriority.Input);
    }

    // The segment used last is a per-profile view preference: the live copy at once, the write through the preference path.
    private void RememberStatusSegment(StatusSegment segment)
    {
        string value = segment == StatusSegment.Diagnostics ? Config.StatusPageSegmentDiagnostics : Config.StatusPageSegmentStatistics;
        if (_isClosing || _config.StatusPageSegment == value) return;
        _config.StatusPageSegment = value;
        _ = SaveStatusSegmentAsync(value);
    }

    private async System.Threading.Tasks.Task SaveStatusSegmentAsync(string value)
    {
        try { await _session.SaveViewPreferencesAsync(config => config.StatusPageSegment = value); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Log.Warning(error, "The last used Status segment could not be stored; the footer opens the default segment next time."); }
    }

    // The rates of the status line come from the hub only while something claims samples (the statistics segment or the
    // header graph); without a claim the line shows the counts alone instead of old numbers.
    private void RefreshStatusRates()
    {
        if (_statusPage == null) return;
        if (_selectedPage == WorkspacePage.Status && _ioStats != null && _ioClaim?.Visibility is { } claim && claim != IoStatsVisibility.None)
            _statusPage.ApplyRates(_ioStats.Snapshot().Rate);
        else _statusPage.ClearRates();
    }

    // The counts of the status line: the same session snapshot the diagnostics segment shows (active, waiting, problems).
    // The snapshot is handed back so the table takes it instead of building a second one for the same view.
    private DiagnosticsSnapshot? RefreshStatusCounts(DesktopSessionView view)
    {
        if (_statusPage == null) return null;
        DiagnosticsSnapshot snapshot = DiagnosticsSnapshot.From(view, _session.GetTargetWaitStates());
        _statusPage.ApplyCounts(snapshot.Active, snapshot.Queued, snapshot.Entries.Count(entry => entry.IsProblem));
        return snapshot;
    }

    /// <summary>The old page commands open their segment: a binding the user saved for them keeps working (no default gesture).</summary>
    private static StatusSegment? StatusSegmentFor(string commandId) => commandId switch
    {
        "navigation.statistics" => StatusSegment.Statistics,
        "navigation.diagnostics" => StatusSegment.Diagnostics,
        _ => null
    };

    private void OnStatusNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        ShowStatus();
    }

    // The error banner's "Open diagnostics" and the Sources page lead to the diagnostics segment.
    private void OnDiagnosticsNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        ShowStatus(StatusSegment.Diagnostics);
    }

    private void DisposeStatusPage() => _statusPage?.EndLifetime();
}
