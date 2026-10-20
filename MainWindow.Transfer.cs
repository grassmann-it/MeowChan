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
    private static readonly Geometry PauseGeometry = Geometry.Parse("M 5,3 L 5,13 M 11,3 L 11,13");
    private static readonly Geometry ResumeGeometry = Geometry.Parse("M 5,3 L 12,8 L 5,13 Z");
    private static readonly Geometry SunGeometry = Geometry.Parse("M 11,8 A 3,3 0 1 1 5,8 A 3,3 0 1 1 11,8 M 8,0 L 8,2 M 8,14 L 8,16 M 0,8 L 2,8 M 14,8 L 16,8 M 2.3,2.3 L 3.7,3.7 M 12.3,12.3 L 13.7,13.7 M 2.3,13.7 L 3.7,12.3 M 12.3,3.7 L 13.7,2.3");
    private static readonly Geometry MoonGeometry = Geometry.Parse("M 13,10 A 6,6 0 1 1 6,2 A 5,5 0 0 0 13,10 Z");

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

    private void UpdateThemeIcon()
    {
        bool dark = _config.Theme == "Dark";
        WorkspaceControl<Path>("ThemeToggleIcon").Data = dark ? SunGeometry : MoonGeometry;
        string label = Texts.T(dark ? "shell.switchToLight" : "shell.switchToDark");
        Button button = WorkspaceControl<Button>("ThemeToggleButton");
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }
}
