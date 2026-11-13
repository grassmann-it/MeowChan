using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private DispatcherTimer? _speedTimer;
    private DownloadRateHistory? _downloadRate;
    private long _speedTimestamp;

    // A silent, hidden or minimized window must not wake the UI thread every second.
    // The sampling window is 60 samples; once all of them are zero nothing visible changes.
    private const int SpeedGraphSamples = 60;
    private int _speedIdleTicks;

    private void StartSpeedSampling()
    {
        _downloadRate = new DownloadRateHistory(_session.ReceivedMediaBytes);
        _speedTimestamp = Stopwatch.GetTimestamp();
        _speedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnSpeedSample);
        PropertyChanged += OnSpeedWindowPropertyChanged;
        RefreshSpeedLabel();
        UpdateSpeedSamplingState();
    }

    private void StopSpeedSampling()
    {
        PropertyChanged -= OnSpeedWindowPropertyChanged;
        _speedTimer?.Stop();
        if (_speedTimer != null) _speedTimer.Tick -= OnSpeedSample;
        _speedTimer = null;
        _downloadRate = null;
    }

    private bool SpeedSamplingWanted => !_isClosing && IsVisible && WindowState != WindowState.Minimized;

    private void OnSpeedWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateSpeedSamplingState();
    }

    private void UpdateSpeedSamplingState()
    {
        if (_speedTimer == null || _downloadRate == null) return;
        if (SpeedSamplingWanted)
        {
            if (_speedTimer.IsEnabled) return;
            // Bytes received while hidden are not a rate: start a fresh window instead of one huge spike.
            _downloadRate = new DownloadRateHistory(_session.ReceivedMediaBytes);
            _speedTimestamp = Stopwatch.GetTimestamp();
            _speedIdleTicks = 0;
            TransferSpeedGraph.SetSamples(_downloadRate.Samples);
            RefreshSpeedLabel();
            _speedTimer.Start();
        }
        else _speedTimer.Stop();
    }

    private void OnSpeedSample(object? sender, EventArgs e)
    {
        if (_isClosing || _speedTimer == null || _downloadRate == null) return;
        long now = Stopwatch.GetTimestamp();
        _downloadRate.Sample(_session.ReceivedMediaBytes, Stopwatch.GetElapsedTime(_speedTimestamp, now));
        _speedTimestamp = now;
        _speedIdleTicks = _downloadRate.BytesPerSecond > 0 ? 0 : Math.Min(_speedIdleTicks + 1, SpeedGraphSamples + 1);
        if (_speedIdleTicks > SpeedGraphSamples) return;
        TransferSpeedGraph.SetSamples(_downloadRate.Samples);
        RefreshSpeedLabel();
    }

    private void RefreshSpeedLabel()
    {
        double rate = _downloadRate?.BytesPerSecond ?? 0;
        string value = rate >= 1048576 ? (rate / 1048576).ToString("0.0", Texts.Culture) + " MiB/s"
            : rate >= 1024 ? (rate / 1024).ToString("0", Texts.Culture) + " KiB/s"
            : rate.ToString("0", Texts.Culture) + " B/s";
        TransferSpeedLabel.Text = "↓ " + value;
        string description = Texts.T("shell.transferSpeed", value);
        ToolTip.SetTip(TransferSpeed, description);
        AutomationProperties.SetName(TransferSpeed, description);
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
