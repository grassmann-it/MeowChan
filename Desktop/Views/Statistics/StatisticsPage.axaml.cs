using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

/// <summary>
/// The statistics page: the current rates, one history graph for network and disk, and a table per source. It only shows
/// what the window hands it through <see cref="Apply"/>; it owns no timer and holds no claim on the hub, so a page nobody
/// looks at costs nothing.
/// </summary>
public partial class StatisticsPage : UserControl
{
    private readonly ObservableCollection<StatisticsRow> _rows = [];
    private IoStatsHub? _hub;
    private Func<string, string?>? _sourceName;
    private IoStatsSnapshot? _last;
    private string? _graphName;
    private bool _resetting;
    private bool _ended;

    public StatisticsPage()
    {
        InitializeComponent();
        StatisticsGrid.ItemsSource = _rows;
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
        Texts.Changed -= OnLanguageChanged;
    }

    internal bool IsResetConfirmationOpen => ResetConfirmPanel.IsVisible;
    internal Button ResetButton => ResetStatisticsButton;
    internal Button ConfirmButton => ConfirmResetButton;
    internal Button CancelButton => CancelResetButton;
    internal DataGrid RowsGrid => StatisticsGrid;
    internal Border LoadNotice => LoadNoticePanel;
    internal Border PersistError => PersistErrorPanel;
    internal MeowChan.Desktop.Controls.IoGraph Graph => StatisticsGraph;

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
        NowTitle.Text = Texts.T("stats.now");
        NetCaption.Text = Texts.T("stats.metric.net");
        DiskWriteCaption.Text = Texts.T("stats.metric.diskWrite");
        DiskReadCaption.Text = Texts.T("stats.metric.diskRead");
        RequestsCaption.Text = Texts.T("stats.metric.requests");
        LegendNet.Text = Texts.T("stats.legend.net");
        LegendDisk.Text = Texts.T("stats.legend.disk");
        SourcesTitle.Text = Texts.T("nav.sources");
        SourcesEmpty.Text = Texts.T("stats.sources.empty");
        StatisticsFootnote.Text = Texts.T("stats.footnote");
        ResetStatisticsButton.Content = Texts.T("stats.reset");
        ResetQuestion.Text = Texts.T("stats.reset.question");
        ConfirmResetButton.Content = Texts.T("stats.reset.confirm");
        CancelResetButton.Content = Texts.T("stats.reset.cancel");
        AutomationProperties.SetName(StatisticsGrid, Texts.T("nav.sources"));
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

        StatisticsGraph.SetSeries(snapshot.Series);
        int seconds = IoStatsDisplay.HistorySeconds(snapshot.Series.Capacity, _hub?.Options.SampleIntervalMs ?? Config.StatsSampleIntervalMsDefault);
        string peak = ByteUnits.Rate(StatisticsGraph.Peak);
        HistoryTitle.Text = Texts.T("stats.history", seconds);
        PeakText.Text = Texts.T("stats.peak", peak);
        string graphName = Texts.T("stats.graph.page.name", seconds, peak);
        if (!string.Equals(graphName, _graphName, StringComparison.Ordinal))
        {
            _graphName = graphName;
            AutomationProperties.SetName(StatisticsGraph, graphName);
        }

        ApplyRows(IoStatsRows.Build(snapshot, _sourceName));
        SourcesEmpty.IsVisible = snapshot.Sources.Count == 0;
        ShowNotice(LoadNoticePanel, LoadNoticeText, _hub?.LoadNotice is { Length: > 0 } notice ? Texts.T("stats.notice", notice) : null);
        ShowNotice(PersistErrorPanel, PersistErrorText, _hub?.LastPersistError is { Length: > 0 } error ? Texts.T("stats.persistError", error) : null);
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
            for (int i = 0; i < texts.Count; i++) _rows[i].Update(texts[i]);
            return;
        }
        _rows.Clear();
        foreach (IoStatsRowText text in texts) _rows.Add(new StatisticsRow(text));
    }

    /// <summary>The rows as shown, for tests.</summary>
    internal System.Collections.Generic.IReadOnlyList<StatisticsRow> Rows => _rows;

    /// <summary>Leaving the page withdraws an open question: it would still be there on the way back.</summary>
    internal void Deactivate() => CloseConfirmation(focusReset: false);

    internal void FocusContent()
    {
        if (ResetConfirmPanel.IsVisible) CancelResetButton.Focus();
        else StatisticsGrid.Focus();
    }

    private void OnResetClick(object? sender, RoutedEventArgs e) => OpenConfirmation();

    private void OpenConfirmation()
    {
        if (_ended || _hub == null || _resetting) return;
        ResetConfirmPanel.IsVisible = true;
        ResetStatisticsButton.IsEnabled = false;
        StatisticsStatus.IsVisible = false;
        // The destructive answer is never the default: focus starts on Cancel.
        CancelResetButton.Focus();
    }

    private void CloseConfirmation(bool focusReset)
    {
        ResetConfirmPanel.IsVisible = false;
        if (!_resetting) ResetStatisticsButton.IsEnabled = true;
        if (focusReset) ResetStatisticsButton.Focus();
    }

    private void OnCancelResetClick(object? sender, RoutedEventArgs e) => CloseConfirmation(focusReset: true);

    private void OnResetConfirmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ResetConfirmPanel.IsVisible) return;
        e.Handled = true;
        CloseConfirmation(focusReset: true);
    }

    private async void OnConfirmResetClick(object? sender, RoutedEventArgs e)
    {
        IoStatsHub? hub = _hub;
        if (_ended || hub == null || _resetting) return;
        _resetting = true;
        ConfirmResetButton.IsEnabled = false;
        try
        {
            // Deleting the file is I/O: off the UI thread. The hub raises Sampled afterwards, which refreshes the page.
            await Task.Run(hub.Reset);
            if (_ended) return;
            StatisticsStatus.Text = Texts.T("stats.reset.done");
            StatisticsStatus.IsVisible = true;
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
