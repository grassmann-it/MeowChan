using System;
using System.Diagnostics;
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

    private void StartSpeedSampling()
    {
        _downloadRate = new DownloadRateHistory(_session.ReceivedMediaBytes);
        _speedTimestamp = Stopwatch.GetTimestamp();
        _speedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnSpeedSample);
        RefreshSpeedLabel();
    }

    private void OnSpeedSample(object? sender, EventArgs e)
    {
        if (_isClosing || _speedTimer == null || _downloadRate == null) return;
        long now = Stopwatch.GetTimestamp();
        _downloadRate.Sample(_session.ReceivedMediaBytes, Stopwatch.GetElapsedTime(_speedTimestamp, now));
        _speedTimestamp = now;
        WorkspaceControl<DownloadSpeedGraph>("TransferSpeedGraph").SetSamples(_downloadRate.Samples);
        RefreshSpeedLabel();
    }

    private void RefreshSpeedLabel()
    {
        double rate = _downloadRate?.BytesPerSecond ?? 0;
        string value = rate >= 1048576 ? (rate / 1048576).ToString("0.0", Texts.Culture) + " MiB/s"
            : rate >= 1024 ? (rate / 1024).ToString("0", Texts.Culture) + " KiB/s"
            : rate.ToString("0", Texts.Culture) + " B/s";
        WorkspaceControl<TextBlock>("TransferSpeedLabel").Text = "↓ " + value;
        string description = Texts.T("shell.transferSpeed", value);
        ToolTip.SetTip(WorkspaceControl<Grid>("TransferSpeed"), description);
        AutomationProperties.SetName(WorkspaceControl<Grid>("TransferSpeed"), description);
    }

    private bool? _lastDarkThemeIcon;

    private void UpdateThemeIcon()
    {
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (_lastDarkThemeIcon != dark)
        {
            _lastDarkThemeIcon = dark;
            WorkspaceControl<Button>("ThemeToggleButton").Content = MeowChan.Desktop.Commands.CommandBindings.Icon("appearance.theme", 16,
                dark ? "choose_light" : "choose_dark");
        }
        string label = Texts.T(dark ? "shell.switchToLight" : "shell.switchToDark");
        Button button = WorkspaceControl<Button>("ThemeToggleButton");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }
}
