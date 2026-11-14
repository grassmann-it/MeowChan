using System;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
    private bool _headerIcons;

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
        RefreshSpeedLabel();
        UpdateSpeedSamplingState();
        StartHeaderTagline();
    }

    // Keep space between graphs and text even on the very first layout. Shared grid columns align
    // the direction words and right-aligned rates across both blocks, independently of their values.
    private const double RowGraphWidth = 128, RowGraphHeight = 14;
    private const double BlockGraphWidth = 120, BlockGraphHeight = 30, BlockGap = 24, TextGap = 8;

    /// <summary>
    /// Sets what the header shows and how from the "Header graph" setting and the window width
    /// (<see cref="IoStatsDisplay.ResolveHeaderLayout"/>): which labelled values, which graphs, and the arrangement. A setting change
    /// or a resize updates it at once; the accessible name follows the values shown. Nothing here can move the window controls:
    /// the header sits in the shrinkable column, the controls in their own.
    /// </summary>
    private void ApplyHeaderGraphMode()
    {
        IoStatsDisplay.HeaderLayout layout = IoStatsDisplay.ResolveHeaderLayout(_config.StatsHeaderGraph, Bounds.Width <= 0 ? double.PositiveInfinity : Bounds.Width);
        if (_headerLayoutApplied && _headerIcons == _config.StatsHeaderIcons && layout == _headerLayout && TransferDiskText.IsVisible == layout.DiskValue && TransferSpeedText.IsVisible == layout.NetValue) return;
        _headerLayout = layout;
        _headerLayoutApplied = true;
        _headerIcons = _config.StatsHeaderIcons;
        SetHeaderMarkerMode(TransferDownMark, TransferDownMarker, _headerIcons);
        SetHeaderMarkerMode(TransferUpMark, TransferUpMarker, _headerIcons);
        SetHeaderMarkerMode(TransferReadMark, TransferReadMarker, _headerIcons);
        SetHeaderMarkerMode(TransferWriteMark, TransferWriteMarker, _headerIcons);
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
        ArrangeHeaderRates(TransferSpeedText, TransferDownCell, TransferSpeedLabel,
            TransferUpCell, TransferUploadLabel, twoLine);
        ArrangeHeaderRates(TransferDiskText, TransferReadCell, TransferDiskReadLabel,
            TransferWriteCell, TransferDiskLabel, twoLine);
        // Solid series first, dashed second: Down over Up / Read over Write in blocks,
        // Down above Read / Up above Write in compact rows. Remove unused tracks so
        // their spacing cannot survive a layout switch or enlarge a single block.
        TransferLayoutGrid.RowDefinitions = new RowDefinitions(twoLine ? "Auto" : "Auto,Auto");
        Grid.SetRow(TransferSpeedGraph, 0); Grid.SetColumn(TransferSpeedGraph, 0);
        Grid.SetRow(TransferSpeedText, 0); Grid.SetColumn(TransferSpeedText, 1);
        Grid.SetRow(TransferDiskGraph, twoLine ? 0 : 1); Grid.SetColumn(TransferDiskGraph, twoLine ? 2 : 0);
        Grid.SetRow(TransferDiskText, twoLine ? 0 : 1); Grid.SetColumn(TransferDiskText, twoLine ? 3 : 1);
        TransferSpeedText.Margin = new Thickness(layout.NetGraph ? TextGap : 0, 0, 0, 0);
        TransferDiskGraph.Margin = new Thickness(twoLine && layout.NetValue ? BlockGap : 0, 0, 0, 0);
        TransferDiskText.Margin = new Thickness(layout.DiskGraph ? TextGap : twoLine && layout.NetValue ? BlockGap : 0, 0, 0, 0);
        _graphDescription = null; // the sentence names only the values that are shown
        if (_ioStats != null) ApplyIoSnapshot(force: true);
    }

    internal static void ArrangeHeaderRates(Grid grid, Control firstWord, TextBlock firstValue,
        Control secondWord, TextBlock secondValue, bool stacked)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "Auto,Auto" : "Auto,Auto,Auto,Auto");
        grid.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        grid.ColumnSpacing = 4;
        grid.RowSpacing = 2;
        grid.ColumnDefinitions[0].SharedSizeGroup = "HeaderIoLabel";
        grid.ColumnDefinitions[1].SharedSizeGroup = "HeaderIoRate";
        grid.ColumnDefinitions[1].MinWidth = 56;
        if (!stacked)
        {
            grid.ColumnDefinitions[2].SharedSizeGroup = "HeaderIoLabel2";
            grid.ColumnDefinitions[3].SharedSizeGroup = "HeaderIoRate";
            grid.ColumnDefinitions[3].MinWidth = 56;
        }
        Grid.SetRow(firstWord, 0); Grid.SetColumn(firstWord, 0);
        Grid.SetRow(firstValue, 0); Grid.SetColumn(firstValue, 1);
        Grid.SetRow(secondWord, stacked ? 1 : 0); Grid.SetColumn(secondWord, stacked ? 0 : 2);
        Grid.SetRow(secondValue, stacked ? 1 : 0); Grid.SetColumn(secondValue, stacked ? 1 : 3);
        // Four from ColumnSpacing + eight from this margin = twelve between pairs;
        // the direction-to-value gap stays compact in both arrangements.
        secondWord.Margin = new Thickness(stacked ? 0 : 8, 0, 0, 0);
    }

    /// <summary>Only presentation changes: the button retains its name, action and keyboard target.</summary>
    internal static void SetHeaderMarkerMode(TextBlock word, Control marker, bool icons)
    {
        word.IsVisible = !icons;
        marker.IsVisible = icons;
    }

    private void StopSpeedSampling()
    {
        PropertyChanged -= OnSpeedWindowPropertyChanged;
        StopHeaderTagline();
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
        // The Status page claims only while its statistics segment is shown; the diagnostics segment needs no samples.
        IoStatsVisibility want = IoStatsDisplay.Claim(SpeedSamplingWanted, TransferSpeed.IsVisible, StatisticsSegmentShown);
        if (_ioClaim.Visibility == want) return;
        _ioClaim.Visibility = want;
        if (want == IoStatsVisibility.None) _statusPage?.ClearRates(); // no fresh rates for the status line without a claim
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
        // Two graph surfaces, each with its own history and scale: network down (solid) and up (dashed), disk read (solid) and write (dashed).
        if (_headerLayout.NetGraph) TransferSpeedGraph.SetSeries(snapshot.Series);
        if (_headerLayout.DiskGraph) TransferDiskGraph.SetSeries(snapshot.Series);
        if (_headerLayout.NetValue)
        {
            TransferSpeedLabel.Text = ByteUnits.Rate(snapshot.Rate.NetDown);
            TransferUploadLabel.Text = ByteUnits.Rate(snapshot.Rate.NetUp);
        }
        if (_headerLayout.DiskValue)
        {
            TransferDiskReadLabel.Text = ByteUnits.Rate(snapshot.Rate.DiskRead);
            TransferDiskLabel.Text = ByteUnits.Rate(snapshot.Rate.DiskWrite);
        }
        if (StatisticsSegmentShown) _statisticsPage?.Apply(snapshot);
        if (_selectedPage == WorkspacePage.Status) _statusPage?.ApplyRates(snapshot.Rate);
        // Set the name and tip only when the sentence changes: no live region, so nothing is read out every second.
        string description = IoStatsDisplay.GraphDescription(snapshot.Rate, snapshot.Activity, _headerLayout.NetValue, _headerLayout.DiskValue);
        if (!string.Equals(description, _graphDescription, StringComparison.Ordinal))
        {
            _graphDescription = description;
            ToolTip.SetTip(TransferSpeed, description);
            // Local tips identify each symbol without requiring colour vision or guessing an arrow's meaning.
            ToolTip.SetTip(TransferDownCell, Texts.T("stats.headerIcons.downTip", ByteUnits.Rate(snapshot.Rate.NetDown)));
            ToolTip.SetTip(TransferUpCell, Texts.T("stats.headerIcons.upTip", ByteUnits.Rate(snapshot.Rate.NetUp)));
            ToolTip.SetTip(TransferReadCell, Texts.T("stats.headerIcons.readTip", ByteUnits.Rate(snapshot.Rate.DiskRead)));
            ToolTip.SetTip(TransferWriteCell, Texts.T("stats.headerIcons.writeTip", ByteUnits.Rate(snapshot.Rate.DiskWrite)));
            AutomationProperties.SetName(TransferSpeed, description);
        }
    }

    private void RefreshSpeedLabel()
    {
        // Refresh words even for a hidden metric or a hub that has not started yet:
        // a language change must not wait for the next sampled rate.
        TransferDownMark.Text = Texts.T("stats.header.down");
        TransferUpMark.Text = Texts.T("stats.header.up");
        TransferWriteMark.Text = Texts.T("stats.header.write");
        TransferReadMark.Text = Texts.T("stats.header.read");
        ApplyIoSnapshot(force: true);
    }

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
