using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop;

namespace MeowChan;

// Fixed, one-shot slots are independent of the best-effort performance ring and its snapshot
// lock. No IO, callbacks or waiting on the UI or host startup threads. Zero means unobserved.
internal enum StartupTimingPoint
{
    HostBegin, HostEnd, WindowFactoryBegin, WindowFactoryEnd, ConstructorBegin, ConstructorEnd,
    TrayBegin, TrayEnd, Opened, ShellBatchRendered, HistoricalRowsObserved, HistoricalBatchRendered,
    CatalogReadable, ReadyProjection, ShellRestoreCompleted,
    DownloadsOffered, ArchiveOffered, MediaOffered, RulesOffered, SourcesOffered,
    DiagnosticsOffered, PluginsOffered, SettingsOffered, Count
}

internal sealed class StartupTimingTrace
{
    private readonly long[] _timestamps = new long[(int)StartupTimingPoint.Count];

    internal void Mark(StartupTimingPoint point) => MarkAt(point, Stopwatch.GetTimestamp());
    internal void MarkAt(StartupTimingPoint point, long timestamp) =>
        Interlocked.CompareExchange(ref _timestamps[(int)point], timestamp, 0);
    internal long? Read(StartupTimingPoint point)
    {
        long timestamp = Interlocked.Read(ref _timestamps[(int)point]);
        return timestamp == 0 ? null : timestamp;
    }
    internal long?[] Snapshot()
    {
        long?[] snapshot = new long?[(int)StartupTimingPoint.Count];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = Read((StartupTimingPoint)i);
        return snapshot;
    }
    internal static double? Milliseconds(long? from, long? to, long frequency) =>
        from is long start && to is long end ? (end - start) * 1000.0 / frequency : null;
}

public partial class MainWindow
{
    // Set only by the opt-in isolated harness before constructing the host/window.
    private static StartupTimingTrace? _startupTiming;
    internal static StartupTimingTrace? StartupTiming
    {
        get => Volatile.Read(ref _startupTiming);
        set => Volatile.Write(ref _startupTiming, value);
    }
    private bool _startupTimingAttached;
    private int _startupTimingReady;

    private static void RecordStartupTiming(StartupTimingPoint point) => StartupTiming?.Mark(point);

    private void AttachStartupTiming()
    {
        if (StartupTiming == null) return;
        _startupTimingAttached = true;
        _session.CatalogReadableChanged += RecordStartupCatalogReadable;
        _session.ProjectionPublished += RecordStartupReady;
        Closed += OnStartupTimingClosed;
    }

    private void RecordStartupCatalogReadable() => RecordStartupTiming(StartupTimingPoint.CatalogReadable);

    private void RecordStartupReady()
    {
        // Observer time of the first published Ready projection, not the host's private state
        // assignment or the later propagation of shell commands. A projection may be stale.
        if (_session.Projection.View.State == DesktopSessionState.Ready
            && Interlocked.Exchange(ref _startupTimingReady, 1) == 0)
            RecordStartupTiming(StartupTimingPoint.ReadyProjection);
    }

    private void OnStartupTimingClosed(object? sender, EventArgs args) => DetachStartupTiming();

    private void DetachStartupTiming()
    {
        if (!_startupTimingAttached) return;
        _startupTimingAttached = false;
        _session.CatalogReadableChanged -= RecordStartupCatalogReadable;
        _session.ProjectionPublished -= RecordStartupReady;
        Closed -= OnStartupTimingClosed;
    }

    internal Task StartupTimingRestoreTask => _restoreTask;
    internal StartupTimingGates ReadStartupTimingGates() => new(_selectedPage.ToString(), _restoreCompleted,
        WorkspaceCommandBusy, WorkspaceEditorBusy, UrlInput.IsEnabled, AddUrlButton.IsEnabled,
        _settingsEditor?.IsAvailable == true, _settingsEditor?.IsEditable == true,
        _rulesPage?.IsAvailable == true, _rulesPage?.IsEditable == true,
        _trayIcon != null, _trayIcon?.NativeMenuExporter != null);

    internal sealed record StartupTimingGates(string SelectedPage, bool RestoreCompleted, bool CommandBusy,
        bool EditorBusy, bool UrlInputEnabled, bool AddUrlEnabled, bool SettingsAvailable,
        bool SettingsEditable, bool RulesAvailable, bool RulesEditable, bool TrayIconAlive, bool TrayMenuExporterAvailable);
}
