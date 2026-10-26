using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private DispatcherTimer? _windowPreferenceTimer;
    private bool _windowPreferencesReady;
    private readonly Dictionary<string, Dictionary<string, double>> _pendingColumnWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _pendingColumnOrder = new(StringComparer.Ordinal);
    private bool _applyingColumnProfile;
    private readonly Dictionary<string, DataGridLength> _defaultColumnWidths = new(StringComparer.Ordinal);
    private Task _windowPreferenceSave = Task.CompletedTask;
    private Task _windowPreferenceWrite = Task.CompletedTask;

    private void InitializeWindowPreferences()
    {
        foreach (string name in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
        {
            DataGrid grid = WorkspaceControl<DataGrid>(name);
            grid.ColumnDisplayIndexChanged += (_, _) => OnDownloadColumnOrderChanged(name);
            foreach (DataGridColumn column in grid.Columns)
            {
                string key = name + "." + column.SortMemberPath;
                _defaultColumnWidths[key] = column.Width;
                if (_config.ColumnWidths.TryGetValue(key, out double width) && double.IsFinite(width) && width >= 32)
                    column.Width = new DataGridLength(width);
                column.PropertyChanged += OnDownloadColumnWidthChanged;
            }
        }
        PropertyChanged += OnWindowPreferenceChanged;
    }

    private void RestoreWindowPreferences()
    {
        Width = _config.WindowWidth;
        Height = _config.WindowHeight;
        if (_config.WindowPositionX is double x && _config.WindowPositionY is double y)
            Position = new PixelPoint((int)x, (int)y);
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen != null)
        {
            var desired = new Size(Width, Height);
            var fit = WindowStartupGeometry.Fit(desired, desired, new Size(MinWidth, MinHeight),
                screen.WorkingArea, screen.Scaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
            MinWidth = fit.Minimum.Width; MinHeight = fit.Minimum.Height;
            Width = fit.Client.Width; Height = fit.Client.Height; Position = fit.Position;
        }
        RememberNormalGeometry();
        // Wait for native frame measurement before restoring maximization.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) return;
            FitStartupWindowToWorkingArea();
            RememberNormalGeometry();
            if ((_startupWindowClient != null || _startupFrameClient != null) && _config.WindowMaximized) WindowState = WindowState.Maximized;
            _windowPreferencesReady = true;
            CompleteStartupWindowFit();
        }, DispatcherPriority.Loaded);
    }

    private void OnDownloadColumnWidthChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_windowPreferencesReady || _applyingColumnProfile || sender is not DataGridColumn column) return;
        if (e.Property != DataGridColumn.WidthProperty || !column.Width.IsAbsolute) return;
        string mode = _config.WorkspaceMode;
        foreach (string name in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
            if (WorkspaceControl<DataGrid>(name).Columns.Contains(column))
            {
                    if (!_pendingColumnWidths.TryGetValue(mode, out Dictionary<string, double>? pending))
                        _pendingColumnWidths[mode] = pending = new(StringComparer.Ordinal);
                    pending[name + "." + column.SortMemberPath] = column.Width.Value;
                _config.GetLayoutProfile(mode).ColumnWidths[name + "." + column.SortMemberPath] = column.Width.Value;
            }
        QueueWindowPreferences();
    }

    private void OnDownloadColumnOrderChanged(string name)
    {
        if (!_windowPreferencesReady || _applyingColumnProfile) return;
        string mode = _config.WorkspaceMode;
        if (!_pendingColumnOrder.TryGetValue(mode, out Dictionary<string, int>? pending))
            _pendingColumnOrder[mode] = pending = new(StringComparer.Ordinal);
        foreach (DataGridColumn column in WorkspaceControl<DataGrid>(name).Columns)
        {
            pending[name + "." + column.SortMemberPath] = column.DisplayIndex;
            _config.GetLayoutProfile(mode).ColumnOrder[name + "." + column.SortMemberPath] = column.DisplayIndex;
        }
        QueueWindowPreferences();
    }

    private void OnWindowPreferenceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty) QueueWindowPreferences();
    }

    private void QueueWindowPreferences()
    {
        if (!_windowPreferencesReady || _isClosing || _fullscreenOrigin != null || _restoringFullscreen
            || WindowState == WindowState.FullScreen) return;
        _windowPreferenceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { _ = FlushWindowPreferencesAsync(); });
        _windowPreferenceTimer.Stop(); _windowPreferenceTimer.Start();
    }

    private Task FlushWindowPreferencesAsync()
    {
        _windowPreferenceTimer?.Stop();
        if (!_windowPreferencesReady) return _windowPreferenceSave;
        Size client = _fullscreenOrigin?.Client ?? _lastNormalClient;
        PixelPoint position = _fullscreenOrigin?.Position ?? _lastNormalPosition;
        bool maximized = (_fullscreenOrigin?.State ?? WindowState) == WindowState.Maximized;
        var columns = _pendingColumnWidths.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, double>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var order = _pendingColumnOrder.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, int>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        _pendingColumnWidths.Clear();
        _pendingColumnOrder.Clear();
        Task write;
        try
        {
            // Submit immediately: the host synchronously freezes/merges every independent delta.
            // Never await the previous shell save before entering its bounded writer.
            write = _session.SaveViewPreferencesAsync(current =>
            {
                current.WindowWidth = client.Width; current.WindowHeight = client.Height;
                current.WindowPositionX = position.X; current.WindowPositionY = position.Y;
                current.WindowMaximized = maximized;
                foreach (var mode in columns)
                    foreach (var pair in mode.Value) current.GetLayoutProfile(mode.Key).ColumnWidths[pair.Key] = pair.Value;
                foreach (var mode in order)
                    foreach (var pair in mode.Value) current.GetLayoutProfile(mode.Key).ColumnOrder[pair.Key] = pair.Value;
            });
        }
        catch (Exception error)
        {
            // A synchronous rejection never entered the host's retained draft. Keep these deltas
            // locally as well, so a later corrected preference can retry every independent field.
            foreach (var mode in columns)
            {
                if (!_pendingColumnWidths.TryGetValue(mode.Key, out var pending))
                    _pendingColumnWidths[mode.Key] = pending = new(StringComparer.Ordinal);
                foreach (var pair in mode.Value) pending.TryAdd(pair.Key, pair.Value);
            }
            foreach (var mode in order)
            {
                if (!_pendingColumnOrder.TryGetValue(mode.Key, out var pending))
                    _pendingColumnOrder[mode.Key] = pending = new(StringComparer.Ordinal);
                foreach (var pair in mode.Value) pending.TryAdd(pair.Key, pair.Value);
            }
            write = Task.FromException(error);
        }
        // Pending host callers share a completion. One observer per completion also bounds shell
        // continuations; timer ticks themselves must not add an async waiter for every flush.
        if (!ReferenceEquals(write, _windowPreferenceWrite))
        {
            _windowPreferenceWrite = write;
            _windowPreferenceSave = ObserveWindowPreferenceWriteAsync(write);
        }
        return _windowPreferenceSave;
    }

    private async Task ObserveWindowPreferenceWriteAsync(Task write)
    {
        try { await write; }
        catch (Exception error)
        {
            // Host retains the draft. Shutdown's final host flush retries it and propagates a
            // persistent failure; automatic observations report it without unobserved timer faults.
            Log.Warning(error, "Could not save window preferences; retaining the draft for retry.");
            if (!_isClosing) ShowNotification(Texts.T("notification.error"),
                Texts.T("preferences.failed", error.Message), NotificationType.Error, context: "window", operation: "preferences.window");
        }
    }

    private void DisposeWindowPreferences()
    {
        _windowPreferenceTimer?.Stop();
        PropertyChanged -= OnWindowPreferenceChanged;
        foreach (string name in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
            foreach (DataGridColumn column in WorkspaceControl<DataGrid>(name).Columns)
                column.PropertyChanged -= OnDownloadColumnWidthChanged;
    }
}
