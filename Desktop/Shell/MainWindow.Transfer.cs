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

    /// <summary>The claim this window currently holds on the statistics hub (None while nothing needs samples).</summary>
    internal IoStatsVisibility IoStatsClaim => _ioClaim?.Visibility ?? IoStatsVisibility.None;

    private void StartSpeedSampling()
    {
        _ioStats = _session.IoStats;
        _ioUpdate = new CoalescedUiUpdate(ApplyIoSnapshot);
        _ioClaim = _ioStats.Subscribe(IoStatsVisibility.None);
        _ioStats.Sampled += OnIoStatsSampled;
        PropertyChanged += OnSpeedWindowPropertyChanged;
        ApplyIoSnapshot(force: true);
        UpdateSpeedSamplingState();
    }

    private void StopSpeedSampling()
    {
        PropertyChanged -= OnSpeedWindowPropertyChanged;
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
        IoStatsVisibility want = IoStatsDisplay.Claim(SpeedSamplingWanted, TransferSpeed.IsVisible, pageVisible: false);
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
        if (TransferSpeedGraph.IsVisible) TransferSpeedGraph.SetSeries(snapshot.Series);
        TransferSpeedLabel.Text = "↓ " + ByteUnits.Rate(snapshot.Rate.NetDown);
        TransferDiskLabel.Text = ByteUnits.Rate(IoStatsDisplay.DiskRate(snapshot.Rate));
        // Set the name and tip only when the sentence changes: no live region, so nothing is read out every second.
        string description = IoStatsDisplay.GraphDescription(snapshot.Rate, snapshot.Activity);
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
