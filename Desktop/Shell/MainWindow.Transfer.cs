using System;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

public partial class MainWindow
{
    // The header graph and the statistics page read the process-wide hub. This window holds a claim on it only while
    // it can be seen and something that shows the rates is visible, so a hidden or minimized window runs no timer.
    private IoStatsHub? _ioStats;
    private IoStatsSubscription? _ioClaim;
    private CoalescedUiUpdate? _ioUpdate;
    private string? _graphDescription;
    private IoStatsDisplay.HeaderLayout _headerLayout = IoStatsDisplay.ResolveHeaderLayout(null);
    private bool _headerLayoutApplied;

    /// <summary>The claim this window currently holds on the statistics hub (None while nothing needs samples).</summary>
    internal IoStatsVisibility IoStatsClaim => _ioClaim?.Visibility ?? IoStatsVisibility.None;

    private void StartSpeedSampling()
    {
        _ioStats = _session.IoStats;
        _ioUpdate = new CoalescedUiUpdate(ApplyIoSnapshot);
        _ioClaim = _ioStats.Subscribe(IoStatsVisibility.None);
        _ioStats.Sampled += OnIoStatsSampled;
        PropertyChanged += OnSpeedWindowPropertyChanged;
        ApplyHeaderGraphMode();
        ApplyIoSnapshot(force: true);
        UpdateSpeedSamplingState();
        WorkspaceHeader.LayoutUpdated += OnHeaderLayoutUpdated;
        UpdateHeaderTagline();
    }

    // The tagline lives in the header's star column; below this width it would be a stub, so it disappears.
    private const double HeaderTaglineMinimumWidth = 200;

    // A layout pass is the one place where the star column's width is known; the property only changes when the width really does.
    private void OnHeaderLayoutUpdated(object? sender, EventArgs e) => UpdateHeaderTagline();

    private void UpdateHeaderTagline()
    {
        bool fits = WorkspaceHeader.ColumnDefinitions[1].ActualWidth >= HeaderTaglineMinimumWidth;
        if (WorkspaceSubtitle.IsVisible != fits) WorkspaceSubtitle.IsVisible = fits;
    }

    // Keep space between graphs and text even on the very first layout. Shared grid columns align
    // the headings, direction markers and right-aligned rates across both blocks, independently of their values.
    private const double RowGraphWidth = 128, RowGraphHeight = 16;
    private const double BlockGraphWidth = 120, BlockGraphHeight = 34, BlockGap = 24, TextGap = 12;

    /// <summary>
    /// Sets what the header shows and how from the "Header graph" setting and the window width
    /// (<see cref="IoStatsDisplay.ResolveHeaderLayout"/>): which labelled values, which graphs, and the arrangement. A setting change
    /// or a resize updates it at once; the accessible name follows the values shown. Nothing here can move the window controls:
    /// the header sits in the shrinkable column, the controls in their own.
    /// </summary>
    private void ApplyHeaderGraphMode()
    {
        IoStatsDisplay.HeaderLayout layout = IoStatsDisplay.ResolveHeaderLayout(_config.StatsHeaderGraph, Bounds.Width <= 0 ? double.PositiveInfinity : Bounds.Width);
        if (_headerLayoutApplied && layout == _headerLayout && TransferDiskText.IsVisible == layout.DiskValue && TransferSpeedText.IsVisible == layout.NetValue) return;
        _headerLayout = layout;
        _headerLayoutApplied = true;
        bool blocks = layout.Arrangement == IoStatsDisplay.HeaderArrangement.GraphBlocks;
        bool twoLine = layout.TwoLineLabels;
        bool diskGraphShown = layout.DiskGraph;
        TransferSpeedGraph.IsVisible = layout.NetGraph;
        TransferDiskGraph.IsVisible = diskGraphShown;
        TransferSpeedText.IsVisible = layout.NetValue;
        TransferDiskText.IsVisible = layout.DiskValue;
        double graphHeight = blocks ? BlockGraphHeight : RowGraphHeight;
        TransferSpeedGraph.Width = TransferDiskGraph.Width = blocks ? BlockGraphWidth : RowGraphWidth;
        TransferSpeedGraph.Height = TransferDiskGraph.Height = graphHeight;
        ArrangeHeaderRates(TransferSpeedText, TransferSpeedTitle, TransferDownMark, TransferSpeedLabel,
            TransferUpMark, TransferUploadLabel, twoLine);
        ArrangeHeaderRates(TransferDiskText, TransferDiskTitle, TransferReadMark, TransferDiskReadLabel,
            TransferWriteMark, TransferDiskLabel, twoLine);
        // Wide blocks place the two directions on separate lines; explicit stacked mode keeps
        // one network row and one disk row, with common columns instead of flowing label strings.
        int span = twoLine ? 2 : 1;
        Grid.SetRow(TransferSpeedGraph, 0); Grid.SetColumn(TransferSpeedGraph, 0); Grid.SetRowSpan(TransferSpeedGraph, span);
        Grid.SetRow(TransferSpeedText, 0); Grid.SetColumn(TransferSpeedText, 1); Grid.SetRowSpan(TransferSpeedText, span);
        Grid.SetRow(TransferDiskGraph, twoLine ? 0 : 1); Grid.SetColumn(TransferDiskGraph, twoLine ? 2 : 0); Grid.SetRowSpan(TransferDiskGraph, span);
        Grid.SetRow(TransferDiskText, twoLine ? 0 : 1); Grid.SetColumn(TransferDiskText, twoLine ? 3 : 1); Grid.SetRowSpan(TransferDiskText, span);
        double beside = layout.NetGraph || diskGraphShown ? TextGap : 0;
        TransferSpeedText.Margin = new Thickness(twoLine ? (layout.NetGraph ? TextGap : 0) : beside, 0, 0, 0);
        TransferDiskGraph.Margin = new Thickness(twoLine ? BlockGap : 0, 0, 0, 0);
        TransferDiskText.Margin = new Thickness(twoLine ? (layout.DiskGraph ? TextGap : layout.NetValue ? BlockGap : 0) : beside, 0, 0, 0);
        _graphDescription = null; // the sentence names only the values that are shown
        if (_ioStats != null) ApplyIoSnapshot(force: true);
    }

    internal static void ArrangeHeaderRates(Grid grid, TextBlock title, TextBlock firstMark, TextBlock firstValue,
        TextBlock secondMark, TextBlock secondValue, bool stacked)
    {
        Grid.SetRowSpan(title, stacked ? 2 : 1);
        Grid.SetRow(firstMark, 0); Grid.SetColumn(firstMark, 1);
        Grid.SetRow(firstValue, 0); Grid.SetColumn(firstValue, 2);
        Grid.SetRow(secondMark, stacked ? 1 : 0); Grid.SetColumn(secondMark, stacked ? 1 : 3);
        Grid.SetRow(secondValue, stacked ? 1 : 0); Grid.SetColumn(secondValue, stacked ? 2 : 4);
        // Empty trailing columns must collapse in blocks, not reserve another value width.
        grid.ColumnDefinitions[3].SharedSizeGroup = stacked ? null : "HeaderIoMark";
        grid.ColumnDefinitions[4].SharedSizeGroup = stacked ? null : "HeaderIoRate";
        grid.ColumnDefinitions[4].MinWidth = stacked ? 0 : 64;
        secondMark.Margin = new Thickness(stacked ? 0 : 8, 0, 0, 0);
    }

    private void StopSpeedSampling()
    {
        PropertyChanged -= OnSpeedWindowPropertyChanged;
        WorkspaceHeader.LayoutUpdated -= OnHeaderLayoutUpdated;
        if (_ioStats != null) _ioStats.Sampled -= OnIoStatsSampled;
        _ioClaim?.Dispose();
        _ioClaim = null;
        _ioUpdate?.Dispose();
        _ioUpdate = null;
        _ioStats = null;
    }

    private bool SpeedSamplingWanted => !_isClosing && IsVisible && WindowState != WindowState.Minimized;

    private void OnSpeedWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateSpeedSamplingState();
    }

    /// <summary>Sets the claim from what is visible now. Call it whenever the window, the header graph or the page changes.</summary>
    private void UpdateSpeedSamplingState()
    {
        if (_ioClaim == null || _ioStats == null) return;
        IoStatsVisibility want = IoStatsDisplay.Claim(SpeedSamplingWanted, TransferSpeed.IsVisible, _selectedPage == WorkspacePage.Statistics);
        if (_ioClaim.Visibility == want) return;
        _ioClaim.Visibility = want;
        // A fresh window starts from the hub's cleared history: show it now instead of waiting a whole interval.
        if (want != IoStatsVisibility.None) _ioUpdate?.RequestImmediate();
    }

    // Pool thread: only post. The hub raises this only while something visible changes.
    private void OnIoStatsSampled() => Volatile.Read(ref _ioUpdate)?.Request();

    private void ApplyIoSnapshot() => ApplyIoSnapshot(force: false);

    private void ApplyIoSnapshot(bool force)
    {
        IoStatsHub? hub = _ioStats;
        if (_isClosing || hub == null || (!force && _ioClaim?.Visibility is null or IoStatsVisibility.None)) return;
        IoStatsSnapshot snapshot = hub.Snapshot();
        // Two graph surfaces, each with its own history and scale: network down (solid) and up (dashed), disk write (solid) and read (dashed).
        if (_headerLayout.NetGraph) TransferSpeedGraph.SetSeries(snapshot.Series);
        if (_headerLayout.DiskGraph) TransferDiskGraph.SetSeries(snapshot.Series);
        if (_headerLayout.NetValue)
        {
            TransferSpeedTitle.Text = IoStatsDisplay.NetworkTitle();
            TransferSpeedLabel.Text = ByteUnits.Rate(snapshot.Rate.NetDown);
            TransferUploadLabel.Text = ByteUnits.Rate(snapshot.Rate.NetUp);
        }
        if (_headerLayout.DiskValue)
        {
            TransferDiskTitle.Text = IoStatsDisplay.DiskTitle();
            TransferReadMark.Text = Texts.T("stats.header.readMark");
            TransferWriteMark.Text = Texts.T("stats.header.writeMark");
            TransferDiskReadLabel.Text = ByteUnits.Rate(snapshot.Rate.DiskRead);
            TransferDiskLabel.Text = ByteUnits.Rate(snapshot.Rate.DiskWrite);
        }
        if (_selectedPage == WorkspacePage.Statistics) _statisticsPage?.Apply(snapshot);
        // Set the name and tip only when the sentence changes: no live region, so nothing is read out every second.
        string description = IoStatsDisplay.GraphDescription(snapshot.Rate, snapshot.Activity, _headerLayout.NetValue, _headerLayout.DiskValue);
        if (!string.Equals(description, _graphDescription, StringComparison.Ordinal))
        {
            _graphDescription = description;
            ToolTip.SetTip(TransferSpeed, description);
            AutomationProperties.SetName(TransferSpeed, description);
        }
    }

    private void RefreshSpeedLabel() => ApplyIoSnapshot(force: true);

    private bool? _lastDarkThemeIcon;

    private void UpdateThemeIcon()
    {
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (_lastDarkThemeIcon != dark)
        {
            _lastDarkThemeIcon = dark;
            ThemeToggleButton.Content = MeowChan.Desktop.Commands.CommandBindings.Icon("appearance.theme", 16,
                dark ? "choose_light" : "choose_dark");
        }
        string label = Texts.T(dark ? "shell.switchToLight" : "shell.switchToDark");
        Button button = ThemeToggleButton;
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }
}
