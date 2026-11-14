using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

/// <summary>
/// The statistics page: the current rates, one history graph each for network, disk writing and disk reading, and a table per source. It only shows
/// what the window hands it through <see cref="Apply"/>; it owns no sampling timer and holds no claim on the hub, so a page nobody
/// looks at costs nothing.
/// </summary>
public partial class StatisticsPage : UserControl
{
    private readonly ObservableCollection<StatisticsRow> _rows = [];
    private IoStatsHub? _hub;
    private Func<string, string?>? _sourceName;
    private IoStatsSnapshot? _last;
    private string? _netGraphName, _writeGraphName, _readGraphName;
    private bool _resetting;
    private bool _ended;
    private IDisposable? _statusHide;
    private bool _restoreResetFocus;
    private long _resetFocusGeneration;

    public StatisticsPage()
    {
        InitializeComponent();
        StatisticsGrid.ItemsSource = _rows;
        StatisticsCompactList.ItemsSource = _rows;
        // The total row is set off by a rule above it (style stats-total-row in the markup).
        StatisticsGrid.LoadingRow += (_, e) => e.Row.Classes.Set("stats-total-row", e.Row.DataContext is StatisticsRow { IsTotal: true });
        ApplyLanguage();
        AttachedToVisualTree += (_, _) => Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
    }

    /// <summary>Connects the hub the Reset button clears and the lookup that turns a source id into its display name.</summary>
    internal void Attach(IoStatsHub hub, Func<string, string?>? sourceName)
    {
        _hub = hub;
        _sourceName = sourceName;
    }

    internal void EndLifetime()
    {
        _ended = true;
        _statusHide?.Dispose();
        ResetConfirmation.Hide();
        Texts.Changed -= OnLanguageChanged;
    }

    private Flyout ResetConfirmation => (Flyout)FlyoutBase.GetAttachedFlyout(ResetStatisticsButton)!;

    internal bool IsResetConfirmationOpen => ResetConfirmation.IsOpen;
    internal Button ResetButton => ResetStatisticsButton;
    /// <summary>Whether the user can reach Reset right now: on the bar, or behind the bar's More button.</summary>
    internal bool ResetIsReachable => ResetStatisticsButton.IsEffectivelyVisible
        || StatisticsActions.OverflowHost?.Children.Contains(ResetStatisticsButton) == true && StatisticsActions.More is { IsEffectivelyVisible: true };
    internal Button ConfirmButton => ConfirmResetButton;
    internal Button CancelButton => CancelResetButton;
    internal DataGrid RowsGrid => StatisticsGrid;
    internal Border LoadNotice => LoadNoticePanel;
    internal Border PersistError => PersistErrorPanel;
    internal MeowChan.Desktop.Controls.IoGraph Graph => NetGraph;
    internal MeowChan.Desktop.Controls.IoGraph WriteGraph => DiskWriteGraph;
    internal MeowChan.Desktop.Controls.IoGraph ReadGraph => DiskReadGraph;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        ApplyLanguage();
        if (_last != null) Apply(_last);
    }

    private void ApplyLanguage()
    {
        StatisticsTitle.Text = Texts.T("nav.statistics");
        StatisticsSubtitle.Text = Texts.T("stats.subtitle");
        HistoryEnd.Text = Texts.T("stats.now");
        HistoryEmpty.Text = Texts.T("stats.sources.empty");
        if (ResetStatusPanel.IsVisible) StatisticsStatus.Text = Texts.T("stats.reset.done");
        NetCaption.Text = Texts.T("stats.metric.net");
        DiskWriteCaption.Text = Texts.T("stats.metric.diskWrite");
        DiskReadCaption.Text = Texts.T("stats.metric.diskRead");
        RequestsCaption.Text = Texts.T("stats.metric.requests");
        HistoryNote.Text = Texts.T("stats.history.note");
        NetTitle.Text = Texts.T("stats.metric.net");
        DiskWriteTitle.Text = Texts.T("stats.metric.diskWrite");
        DiskReadTitle.Text = Texts.T("stats.metric.diskRead");
        SourcesTitle.Text = Texts.T("nav.sources");
        SourcesEmpty.Text = Texts.T("stats.sources.empty");
        StatisticsHelpButton.HelpTextOverride = Texts.T("stats.footnote");
        ResetStatisticsButton.Content = Texts.T("stats.reset");
        StatisticsActions.RefreshOverflowLabel();
        ResetQuestion.Text = Texts.T("stats.reset.question");
        ConfirmResetButton.Content = Texts.T("stats.reset.confirm");
        CancelResetButton.Content = Texts.T("stats.reset.cancel");
        AutomationProperties.SetName(StatisticsGrid, Texts.T("nav.sources"));
        CompactSourceHeader.Text = Texts.T("stats.col.source");
        CompactRateHeader.Text = Texts.T("stats.col.rate");
        CompactSessionHeader.Text = Texts.T("stats.col.session");
        CompactErrorsHeader.Text = Texts.T("stats.col.errors");
        SetHeader(StatisticsGrid.Columns[0], "stats.col.source", left: true);
        SetHeader(StatisticsGrid.Columns[1], "stats.col.rate");
        SetHeader(StatisticsGrid.Columns[2], "stats.col.session");
        SetHeader(StatisticsGrid.Columns[3], "stats.col.today");
        SetHeader(StatisticsGrid.Columns[4], "stats.col.retained", _last?.RetentionDays ?? Config.StatsRetentionDaysDefault);
        SetHeader(StatisticsGrid.Columns[5], "stats.col.diskWritten");
        SetHeader(StatisticsGrid.Columns[6], "stats.col.diskRead");
        SetHeader(StatisticsGrid.Columns[7], "stats.col.requests");
        SetHeader(StatisticsGrid.Columns[8], "stats.col.errors");
    }

    private static void SetHeader(DataGridColumn column, string key, object? argument = null, bool left = false)
    {
        string text = argument == null ? Texts.T(key) : Texts.T(key, argument);
        column.Header = new TextBlock
        {
            Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextAlignment = left ? Avalonia.Media.TextAlignment.Left : Avalonia.Media.TextAlignment.Right,
            HorizontalAlignment = left ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    /// <summary>Shows a snapshot of the hub. Only changed texts and rows are touched.</summary>
    internal void Apply(IoStatsSnapshot snapshot)
    {
        if (_ended) return;
        bool retentionChanged = _last == null || _last.RetentionDays != snapshot.RetentionDays;
        _last = snapshot;
        if (retentionChanged) SetHeader(StatisticsGrid.Columns[4], "stats.col.retained", snapshot.RetentionDays);

        NetValue.Text = ByteUnits.Rate(snapshot.Rate.NetDown);
        DiskWriteValue.Text = ByteUnits.Rate(snapshot.Rate.DiskWrite);
        DiskReadValue.Text = ByteUnits.Rate(snapshot.Rate.DiskRead);
        RequestsValue.Text = Texts.T("stats.requestsRate", IoStatsDisplay.RequestRate(snapshot.Rate.Requests));

        ActivityText.Text = Texts.T("stats.activity", snapshot.Activity.Active, snapshot.Activity.Pending + snapshot.Activity.WaitingAdmission);
        int interval = _hub?.Options.SampleIntervalMs ?? Config.StatsSampleIntervalMsDefault;
        int seconds = IoStatsDisplay.HistorySeconds(snapshot.Series.Capacity, interval);
        HistoryTitle.Text = Texts.T("stats.history", seconds);
        HistoryStart.Text = "−" + seconds + " s";
        HistoryEnd.Text = Texts.T("stats.now");
        ApplyGraph(NetGraph, NetScale, "stats.metric.net", snapshot.Series, seconds, ref _netGraphName);
        ApplyGraph(DiskWriteGraph, DiskWriteScale, "stats.metric.diskWrite", snapshot.Series, seconds, ref _writeGraphName);
        ApplyGraph(DiskReadGraph, DiskReadScale, "stats.metric.diskRead", snapshot.Series, seconds, ref _readGraphName);
        bool hasTraffic = NetGraph.HasTraffic;
        // Retain the width that was actually arranged before hiding the peak. A fixed DIP floor
        // only works for one label/font: longer translations and larger values would still make
        // the Auto column collapse on reset and reflow HistoryNote. Before the first layout there
        // is no visible column to preserve; later zero snapshots must not erase the retained width.
        if (!hasTraffic && PeakText.IsVisible)
            PeakSlot.MinWidth = Math.Max(PeakSlot.MinWidth, PeakSlot.Bounds.Width);
        PeakText.Text = hasTraffic ? Texts.T("stats.peak", ByteUnits.Rate(NetGraph.TrafficPeak)) : string.Empty;
        PeakText.IsVisible = hasTraffic;
        ScaleTop.IsVisible = hasTraffic;
        ScaleHalf.IsVisible = hasTraffic;
        ScaleTop.Text = ByteUnits.Rate(NetGraph.Peak);
        ScaleHalf.Text = ByteUnits.Rate(NetGraph.Peak / 2);
        HistoryEmpty.IsVisible = !hasTraffic;

        ApplyRows(IoStatsRows.Build(snapshot, _sourceName));
        SourcesEmpty.IsVisible = snapshot.Sources.Count == 0;
        ShowNotice(LoadNoticePanel, LoadNoticeText, _hub?.LoadNotice is { Length: > 0 } notice ? Texts.T("stats.notice", notice) : null);
        ShowNotice(PersistErrorPanel, PersistErrorText, _hub?.LastPersistError is { Length: > 0 } error ? Texts.T("stats.persistError", error) : null);
    }

    // Each source has its own axis; the scale label and accessible name only change when the source scale changes.
    private static void ApplyGraph(MeowChan.Desktop.Controls.IoGraph graph, TextBlock scaleText, string metricKey, IoSeries series, int seconds, ref string? name)
    {
        graph.SetSeries(series);
        string scale = ByteUnits.Rate(graph.Peak);
        string text = Texts.T("stats.scale", scale);
        if (!string.Equals(scaleText.Text, text, StringComparison.Ordinal)) scaleText.Text = text;
        string graphName = Texts.T("stats.graph.page.name", Texts.T(metricKey), seconds, scale);
        if (string.Equals(graphName, name, StringComparison.Ordinal)) return;
        name = graphName;
        AutomationProperties.SetName(graph, graphName);
    }

    private static void ShowNotice(Border panel, TextBlock text, string? message)
    {
        panel.IsVisible = message != null;
        if (message != null && !string.Equals(text.Text, message, StringComparison.Ordinal)) text.Text = message;
    }

    // Same keys in the same order: update in place. Anything else: rebuild once.
    private void ApplyRows(System.Collections.Generic.IReadOnlyList<IoStatsRowText> texts)
    {
        bool sameShape = _rows.Count == texts.Count;
        for (int i = 0; sameShape && i < texts.Count; i++) sameShape = _rows[i].Key == texts[i].Key && _rows[i].IsTotal == texts[i].IsTotal;
        if (sameShape)
        {
            for (int i = 0; i < texts.Count; i++) _rows[i].Update(texts[i], _last?.RetentionDays ?? Config.StatsRetentionDaysDefault);
            return;
        }
        _rows.Clear();
        foreach (IoStatsRowText text in texts) _rows.Add(new StatisticsRow(text, _last?.RetentionDays ?? Config.StatsRetentionDaysDefault));
    }

    // Use the actual table viewport, not the outer window (which may include navigation chrome).
    private void OnSourcesSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 900;
        StatisticsGrid.IsVisible = !compact;
        CompactSources.IsVisible = compact;
    }

    /// <summary>The rows as shown, for tests.</summary>
    internal System.Collections.Generic.IReadOnlyList<StatisticsRow> Rows => _rows;

    /// <summary>Leaving the page withdraws an open question: it would still be there on the way back.</summary>
    internal void Deactivate()
    {
        CloseConfirmation(focusReset: false);
        ResetStatusPanel.IsVisible = false;
        _statusHide?.Dispose();
    }

    internal void FocusContent()
    {
        if (ResetConfirmation.IsOpen) CancelResetButton.Focus();
        else if (StatisticsGrid.IsVisible) StatisticsGrid.Focus();
        else FocusReset();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e) => OpenConfirmation();

    private void OpenConfirmation()
    {
        if (_ended || _hub == null || _resetting) return;
        ResetStatusPanel.IsVisible = false;
        _statusHide?.Dispose();
        _restoreResetFocus = true;
        ++_resetFocusGeneration;
        // Where Reset sits under More, the closed popup cannot anchor the question: the bar does.
        ResetConfirmation.ShowAt(ResetStatisticsButton.IsEffectivelyVisible ? ResetStatisticsButton : StatisticsActions);
    }

    private void CloseConfirmation(bool focusReset)
    {
        _restoreResetFocus = focusReset;
        ResetConfirmation.Hide();
        if (!_resetting) ResetStatisticsButton.IsEnabled = true;
        if (focusReset) FocusReset();
    }

    // Where Reset sits under More, the closed popup cannot take focus: the More button of the same bar does (R11).
    private void FocusReset()
    {
        if (ResetStatisticsButton.IsEffectivelyVisible) ResetStatisticsButton.Focus();
        else StatisticsActions.More?.Focus();
    }

    private void OnResetConfirmationOpened(object? sender, EventArgs e)
    {
        // A native popup can apply its own initial focus after Opened. Make the safe answer the
        // settled focus target too, but only once for this opening (not an unbounded focus loop).
        CancelResetButton.Focus();
        long generation = _resetFocusGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _resetFocusGeneration && !_ended && ResetConfirmation.IsOpen)
                CancelResetButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnResetConfirmationClosed(object? sender, EventArgs e)
    {
        ++_resetFocusGeneration;
        if (!_ended && IsVisible && !_resetting && _restoreResetFocus) FocusReset();
    }

    private void OnCancelResetClick(object? sender, RoutedEventArgs e) => CloseConfirmation(focusReset: true);

    private void OnResetConfirmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ResetConfirmation.IsOpen) return;
        e.Handled = true;
        CloseConfirmation(focusReset: true);
    }

    private async void OnConfirmResetClick(object? sender, RoutedEventArgs e)
    {
        IoStatsHub? hub = _hub;
        if (_ended || hub == null || _resetting || !ResetConfirmation.IsOpen) return;
        _resetting = true;
        ConfirmResetButton.IsEnabled = false;
        ResetStatisticsButton.IsEnabled = false;
        try
        {
            // Deleting the file is I/O: off the UI thread. The hub raises Sampled afterwards, which refreshes the page.
            await Task.Run(hub.Reset);
            if (_ended) return;
            StatisticsStatus.Text = Texts.T("stats.reset.done");
            ResetStatusPanel.IsVisible = true;
            _statusHide?.Dispose();
            _statusHide = DispatcherTimer.RunOnce(() => ResetStatusPanel.IsVisible = false, TimeSpan.FromSeconds(5));
        }
        finally
        {
            _resetting = false;
            ConfirmResetButton.IsEnabled = true;
            if (!_ended)
            {
                CloseConfirmation(focusReset: true);
                Apply(hub.Snapshot());
            }
        }
    }
}
