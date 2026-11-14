using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

/// <summary>The two segments of the Status page, in the order of its segment bar.</summary>
internal enum StatusSegment { Statistics, Diagnostics }

/// <summary>
/// The Status page: a thin container for the statistics and the diagnostics. It shows the title, the hint, one shared status
/// line and the segment bar; the segments themselves are the unchanged <see cref="StatisticsPage"/> and
/// <see cref="DiagnosticsPage"/>, which the window builds, feeds and hands over (<see cref="AttachStatistics"/>,
/// <see cref="AttachDiagnostics"/>). The page owns no timer and holds no claim: which segment shows is the window's choice
/// (<see cref="ShowSegment"/>), and a click on the bar only asks for it (<see cref="SegmentRequested"/>).
/// </summary>
public partial class StatusPage : UserControl
{
    private StatusSegment _segment = StatusSegment.Statistics;
    private bool _selecting;
    private bool _ended;
    private (long Active, long Waiting, int Problems)? _counts;
    private IoRates? _rates;
    private string? _strip;
    // The diagnostics tab shows its problem count as a badge (the chip language of the filter toggles), not as text in the name.
    private readonly TextBlock _diagnosticsLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _diagnosticsBadgeText = new() { Classes = { "chip-label" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _diagnosticsBadge;

    public StatusPage()
    {
        InitializeComponent();
        _diagnosticsBadge = new Border { Classes = { "chip-badge", "chip-compact" }, Child = _diagnosticsBadgeText, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(_diagnosticsBadge, AccessibilityView.Raw);
        DiagnosticsSegmentTab.Header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _diagnosticsLabel, _diagnosticsBadge }
        };
        ShowSegment(_segment);
        ApplyLanguage();
        AttachedToVisualTree += (_, _) => Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
    }

    /// <summary>A click or key on the segment bar asks the window for another segment; the window decides and calls <see cref="ShowSegment"/>.</summary>
    internal event Action<StatusSegment>? SegmentRequested;

    internal StatusSegment Segment => _segment;
    internal TabControl Segments => StatusSegments;
    internal TabItem StatisticsTab => StatisticsSegmentTab;
    internal TabItem DiagnosticsTab => DiagnosticsSegmentTab;
    /// <summary>The hosts of the two segments. Smokes may swap their content for a test page, as they did with the old page hosts.</summary>
    internal ContentControl StatisticsHost => StatisticsPageHost;
    internal ContentControl DiagnosticsHost => DiagnosticsPageHost;
    internal string StripText => StatusStrip.Text ?? string.Empty;

    internal void AttachStatistics(StatisticsPage page) => StatisticsPageHost.Content = page;

    internal void AttachDiagnostics(DiagnosticsPage page) => DiagnosticsPageHost.Content = page;

    /// <summary>Shows <paramref name="segment"/> without raising <see cref="SegmentRequested"/>.</summary>
    internal void ShowSegment(StatusSegment segment)
    {
        _segment = segment;
        _selecting = true;
        try { StatusSegments.SelectedIndex = (int)segment; }
        finally { _selecting = false; }
        StatisticsPageHost.IsVisible = segment == StatusSegment.Statistics;
        DiagnosticsPageHost.IsVisible = segment == StatusSegment.Diagnostics;
    }

    /// <summary>The statistics segment needs a restored session (the hub); before that, as after a failed recovery, only
    /// the diagnostics can be chosen.</summary>
    internal void SetStatisticsAvailable(bool available)
    {
        if (StatisticsSegmentTab.IsEnabled != available) StatisticsSegmentTab.IsEnabled = available;
    }

    /// <summary>The counts of the session projection: active and waiting transfers, current problems (the diagnostics count).</summary>
    internal void ApplyCounts(long active, long waiting, int problems)
    {
        if (_ended) return;
        bool problemsChanged = _counts?.Problems != problems;
        _counts = (active, waiting, problems);
        if (problemsChanged) ApplySegmentHeaders();
        RenderStrip();
    }

    /// <summary>The current rates of the hub, while something holds a claim on it (the statistics segment or the header graph).</summary>
    internal void ApplyRates(IoRates rate)
    {
        if (_ended) return;
        _rates = rate;
        RenderStrip();
    }

    /// <summary>No claim, no fresh rates: the line drops them instead of showing old numbers.</summary>
    internal void ClearRates()
    {
        if (_ended || _rates == null) return;
        _rates = null;
        RenderStrip();
    }

    internal void EndLifetime()
    {
        _ended = true;
        Texts.Changed -= OnLanguageChanged;
    }

    /// <summary>The status line: "Active 3 · Waiting 41 · Problems 2", followed by "↓ 2.4 MiB/s · W 1.9 MiB/s" while rates are known.</summary>
    internal static string ComposeStrip(long active, long waiting, int problems, IoRates? rate)
    {
        string counts = Texts.T("status.strip", active, waiting, problems);
        return rate is { } value
            ? counts + " · " + Texts.T("status.strip.rates", ByteUnits.Rate(value.NetDown), ByteUnits.Rate(value.DiskWrite))
            : counts;
    }

    /// <summary>The name of the diagnostics segment as text: its name, with the number of current problems when there are any
    /// (the tab shows the count as a badge; this is its accessible name).</summary>
    internal static string DiagnosticsHeader(int problems) =>
        problems > 0 ? Texts.T("status.segment.diagnosticsCount", problems) : Texts.T("nav.diagnostics");

    private void RenderStrip()
    {
        string text = _counts is { } counts ? ComposeStrip(counts.Active, counts.Waiting, counts.Problems, _rates) : string.Empty;
        if (string.Equals(text, _strip, StringComparison.Ordinal)) return;
        _strip = text;
        StatusStrip.Text = text;
        StatusStrip.IsVisible = text.Length > 0;
        AutomationProperties.SetName(StatusStrip, text);
    }

    private void ApplySegmentHeaders()
    {
        StatisticsSegmentTab.Header = Texts.T("nav.statistics");
        int problems = _counts?.Problems ?? 0;
        _diagnosticsLabel.Text = Texts.T("nav.diagnostics");
        _diagnosticsBadgeText.Text = problems.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _diagnosticsBadge.IsVisible = problems > 0;
        // The badge alone is not read as a name: the tab's accessible name is the sentence with the count.
        AutomationProperties.SetName(DiagnosticsSegmentTab, DiagnosticsHeader(problems));
    }

    private void ApplyLanguage()
    {
        StatusTitle.Text = Texts.T("nav.status");
        StatusHint.Text = Texts.T("status.hint");
        AutomationProperties.SetName(StatusSegments, Texts.T("status.segments"));
        ApplySegmentHeaders();
        _strip = null;
        RenderStrip();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        ApplyLanguage();
    }

    private void OnSegmentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selecting || _ended || !ReferenceEquals(e.Source, StatusSegments)) return;
        int index = StatusSegments.SelectedIndex;
        if (index is < 0 or > 1) return;
        StatusSegment requested = (StatusSegment)index;
        if (requested == _segment) return;
        // The window may refuse (statistics before a restored session); then the bar returns to the shown segment.
        SegmentRequested?.Invoke(requested);
        if (_segment != requested) ShowSegment(_segment);
    }
}
