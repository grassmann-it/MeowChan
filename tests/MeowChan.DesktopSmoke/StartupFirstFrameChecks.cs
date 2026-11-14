using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Diagnostics;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Real default plugin/content/registry startup, an empty authoritative store and one display-only
// cache row. No startup barrier, injected readiness, watch, network response or synthetic UI frame.
internal sealed class StartupFirstFrameSmokeApp : App
{
    internal StartupPreviewNoNetwork Transport { get; } = new();

    protected override MainWindow CreateMainWindow()
    {
        StartupTimingRegistry.Current!.Mark(StartupTimingPoint.HostBegin);
        DesktopSessionHost host;
        try
        {
            host = new(Config.GetConfigDirectory(),
                pluginTransportFactory: () => new HttpClient(Transport, disposeHandler: false));
        }
        finally { StartupTimingRegistry.Current!.Mark(StartupTimingPoint.HostEnd); }
        StartupTimingRegistry.Current!.Mark(StartupTimingPoint.WindowFactoryBegin);
        MainWindow window = new(host);
        StartupTimingRegistry.Current!.Mark(StartupTimingPoint.WindowFactoryEnd);
        StartupFirstFrameChecks.Attach(window);
        return window;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, _) => Transport.Dispose();
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class StartupFirstFrameChecks
{
    internal static StartupFirstFrameMeasurement? Active { get; set; }

    // Explicit window-free contract entry, also useful before collecting any native evidence.
    // This is a harness contract, not an assertion about actual startup or a performance SLO.
    internal static void VerifyContracts()
    {
        const string selector = "MEOWCHAN_STARTUP_FIRST_FRAME_ONLY";
        static KeyValuePair<string, string?> Variable(string name, string value) => new(name, value);
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Startup timing contract: " + message);
        }
        Require(SmokeSelectors.Check([], [Variable(selector, "1")], false, out string scenario) == null
            && scenario == "normal+" + selector, "selector must dispatch the normal focused scene");
        Require(SmokeSelectors.Check(["--no-tray"], [Variable(selector, "1")], false, out _) != null,
            "selector must reject command-line modes");
        Require(SmokeSelectors.Check([], [Variable(selector, "1"), Variable("MEOWCHAN_STARTUP_PREVIEW_ONLY", "1")], false, out _) != null,
            "selector must reject conflicting roots");
        Require(SmokeSelectors.Check([], [Variable(selector, "yes")], false, out _) != null,
            "ignored selector values must be rejected");

        VerifyBootstrapContract();

        StartupTimingTrace trace = new();
        Require(trace.Read(StartupTimingPoint.Opened) == null
            && StartupTimingTrace.Milliseconds(100, null, 1000) == null, "missing is null");
        Require(StartupTimingTrace.Milliseconds(100, 50, 1000) == -50, "intervals remain signed");
        Require(JsonSerializer.Serialize(new { missing = trace.Read(StartupTimingPoint.Opened) }).Contains("\"missing\":null"),
            "JSON preserves an unobserved timestamp as null");
        // Parallel snapshots must not compete with or erase any one-shot writer. The deterministic
        // first value per slot also checks first-wins behavior under repeated writes.
        Parallel.For(0, (int)StartupTimingPoint.Count, i =>
        {
            StartupTimingPoint point = (StartupTimingPoint)i;
            trace.MarkAt(point, 1000 + i);
            for (int j = 0; j < 100; j++)
            {
                _ = trace.Snapshot();
                trace.MarkAt(point, 2000 + i);
            }
        });
        long?[] final = trace.Snapshot();
        for (int i = 0; i < final.Length; i++)
            Require(final[i] == 1000 + i, "snapshot/write contention lost or replaced point " + i);
    }

    // Exercise the real pre-platform constructor and disposal, not just the holder. A static
    // MainWindow access here would run its geometry initializer without a render service.
    private static void VerifyBootstrapContract()
    {
        if (Application.Current != null || AvaloniaLocator.Current.GetService(typeof(IPlatformRenderInterface)) != null)
            throw new InvalidOperationException("Startup timing bootstrap contract requires an uninitialized Avalonia platform.");
        if (StartupTimingRegistry.Current != null || Active != null)
            throw new InvalidOperationException("Startup timing bootstrap contract requires no active measurement.");

        const string reportVariable = "MEOWCHAN_STARTUP_FIRST_FRAME_REPORT";
        string? previousReport = Environment.GetEnvironmentVariable(reportVariable);
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-startup-contract-");
        string path = Path.Combine(directory, "report.json");
        try
        {
            Environment.SetEnvironmentVariable(reportVariable, path);
            using (StartupFirstFrameMeasurement measurement = new(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow))
            {
                StartupTimingTrace trace = StartupTimingRegistry.Current
                    ?? throw new InvalidOperationException("Startup timing bootstrap did not install its trace.");
                if (!ReferenceEquals(Active, measurement))
                    throw new InvalidOperationException("Startup timing bootstrap did not install its measurement.");
                trace.MarkAt(StartupTimingPoint.HostBegin, 1234);
                measurement.Flush();
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement host = report.RootElement.GetProperty("milestones")[0];
                if (report.RootElement.GetProperty("status").GetString() != "observing"
                    || host.GetProperty("timestamp").GetInt64() != 1234 || !File.Exists(path + ".claim"))
                    throw new InvalidOperationException("Startup timing bootstrap report did not preserve its trace.");
            }
            if (StartupTimingRegistry.Current != null || Active != null || File.Exists(path + ".pending"))
                throw new InvalidOperationException("Startup timing bootstrap did not clean up its handoff or pending report.");
            using JsonDocument finalReport = JsonDocument.Parse(File.ReadAllText(path));
            if (finalReport.RootElement.GetProperty("status").GetString() != "incomplete")
                throw new InvalidOperationException("Startup timing bootstrap disposal did not finalize the report.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(reportVariable, previousReport);
            Directory.Delete(directory, recursive: true);
        }
    }

    internal const string KeptProfileVariable = "MEOWCHAN_STARTUP_FIRST_FRAME_PROFILE";
    private const string KeptProfileMarker = ".startup-first-frame-profile";

    /// <summary>
    /// Opt-in warm start: with MEOWCHAN_STARTUP_FIRST_FRAME_PROFILE (absolute path) the measurement uses and keeps that
    /// harness-owned profile, so a second process starts on an initialized profile (plugin cache, plugin state, archive).
    /// Without it (null) every run keeps today's throwaway first-start profile. An existing directory without the
    /// harness marker is refused, never adopted. The report records whether this run was warm.
    /// </summary>
    internal static string? ResolveKeptProfile(bool measuring)
    {
        string? requested = Environment.GetEnvironmentVariable(KeptProfileVariable);
        if (string.IsNullOrEmpty(requested)) return null;
        if (!measuring) throw new InvalidOperationException(KeptProfileVariable + " is only valid for the startup first-frame measurement.");
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("The kept startup profile path must be absolute.");
        string profile = Path.GetFullPath(requested);
        string marker = Path.Combine(profile, KeptProfileMarker);
        bool warm = File.Exists(marker);
        if (!warm && Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
            throw new IOException("The startup measurement refuses an unmarked existing profile: " + profile);
        // The marker follows the initial settings (Prepare): settings refuse a non-empty unknown directory.
        Active!.SetProfileReuse(new(Kept: true, Warm: warm,
            PriorPluginCache: Directory.Exists(Path.Combine(profile, "plugin-cache", "artifacts")),
            PriorPluginState: File.Exists(Path.Combine(profile, "plugin-state", "state.json"))));
        return profile;
    }

    internal static void Prepare(string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "Downloads");
        config.DownloadsPaused = true;
        config.SaveUrlsOnExit = true;
        config.CloseToTray = false;
        config.MinimizeToTray = false;
        config.AutomaticUpdateChecks = false;
        config.FirstRunSetupPending = false;
        config.FirstRunSetupVersion = 1;
        config.WorkspaceMode = "Advanced"; // expose every existing navigation entry
        config.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        new SessionStore(profile, lease).SaveAsync(new SessionSnapshot()).GetAwaiter().GetResult();
        new StartupPreviewStore(profile, lease).Save(new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), 1, 0,
            Array.AsReadOnly(new[] { new StartupPreviewRow(false, "Offline timing cache", "local", "Historical display only", 25, 12) })));
        if (Active!.Reuse.Kept) File.WriteAllText(Path.Combine(profile, KeptProfileMarker), "Disposable startup first-frame measurement profile");
        Active!.SetProfile(profile);
        PerformanceDiagnostics.Record("harness.startup.fixture", "prepared");
    }

    internal static void Attach(MainWindow window) => Active!.Attach(window);

    internal static async Task RunAsync(MainWindow window)
    {
        StartupFirstFrameMeasurement measurement = Active!;
        // No internal latency SLO or 15-second startup poll. The external runner's observation
        // timeout bounds this one process; its last atomic report survives even a blocked ctor.
        await measurement.FirstFrame;
        Console.WriteLine("SMOKE render scaling: actual=" + window.RenderScaling.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + "; requested=" + (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") ?? "unset") + ";");
        await window.StartupTimingRestoreTask;
        if (window.SessionHost.State != DesktopSessionState.Ready)
            throw new InvalidOperationException("Startup did not reach Ready: " + window.SessionHost.StartupError);
        while (window.SessionRowsApplying) await Task.Delay(20);
        measurement.SnapshotNavigation("shell-ready");
        measurement.EndStartupObservation();
        await measurement.DrainRenderObservationAsync();
        // Handler probes are deliberately AFTER the instrumented startup observations.
        // Earlier entries describe offered control gates, not proof of handler/keyboard admission.
        await measurement.ProbeNavigationAsync();
        StartupFirstFrameSmokeApp app = (StartupFirstFrameSmokeApp)Application.Current!;
        measurement.HttpAttempts = app.Transport.Requests;
        if (app.Transport.Requests != 0) throw new InvalidOperationException("Offline startup attempted HTTP.");
        if (window.UrlList.Count != 0 || window.BoardsList.Count != 0)
            throw new InvalidOperationException("A display-only historical cache became authoritative watches.");
    }
}

internal sealed class StartupFirstFrameMeasurement : IDisposable
{
    // MainPageOrder owns workspace order; Settings remains the unnumbered destination.
    private static readonly (string Page, string Button, string Host)[] Pages = MainPageOrder.CommandIds
        .Select(id => (Page: MainPageOrder.ControlStem(id), Button: MainPageOrder.ControlStem(id) + "NavigationButton",
            Host: id == "navigation.downloads" ? "WorkspaceBody" : MainPageOrder.ControlStem(id) + "PageHost"))
        .Append(("Settings", "SettingsNavigationButton", "SettingsPageHost")).ToArray();
    private sealed record PageGate(string Page, bool Enabled, bool EffectivelyEnabled, bool Visible,
        bool ToolsEnabled, bool ToolsVisible, bool OverflowItemEnabled, bool OverflowItemVisible, string? OfferedVia);
    private sealed record NavigationObservation(long Timestamp, string Stage, string HostState, bool CatalogReadable,
        MainWindow.StartupTimingGates Shell, PageGate[] Pages);
    private readonly string _path;
    private readonly long _entryTimestamp;
    private readonly DateTimeOffset _entryUtc;
    private readonly DateTimeOffset _processStartUtc;
    // Auxiliary phase events are best-effort; required milestones are kept separately below.
    private readonly PerformanceDiagnostics.DiagnosticRun _run;
    private readonly StartupTimingTrace _trace = new();
    private readonly System.Threading.Timer _writer;
    private readonly object _writeLock = new();
    private readonly ConcurrentQueue<NavigationObservation> _navigation = new();
    private readonly ConcurrentQueue<object> _probes = new();
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _offeredPages = new(StringComparer.Ordinal);
    private MainWindow? _window;
    private string? _profile, _fixtureHash, _lastNavigation, _writeError, _renderError;
    private string _status = "observing";
    private string? _failure;
    internal sealed record ProfileReuse(bool Kept, bool Warm, bool PriorPluginCache, bool PriorPluginState);
    private ProfileReuse _reuse = new(false, false, false, false);
    private bool _renderPending, _renderDirty;
    private volatile bool _shellRendered, _historicalRendered, _observationEnded;
    private int _disposed;
    private readonly string _harnessSha256, _productSha256;
    internal Task FirstFrame => _firstFrame.Task;
    internal int? HttpAttempts { get; set; }

    internal StartupFirstFrameMeasurement(long entryTimestamp, DateTimeOffset entryUtc)
    {
        _entryTimestamp = entryTimestamp;
        _entryUtc = entryUtc;
        using Process process = Process.GetCurrentProcess();
        _processStartUtc = process.StartTime.ToUniversalTime();
        string path = Environment.GetEnvironmentVariable("MEOWCHAN_STARTUP_FIRST_FRAME_REPORT")
            ?? throw new InvalidOperationException("Set MEOWCHAN_STARTUP_FIRST_FRAME_REPORT to a fresh absolute JSON path per process.");
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Startup report path must be absolute.");
        _path = Path.GetFullPath(path);
        // Reserve separately: the report path itself is never exposed as an empty/partial JSON.
        // A failed or killed run keeps its claim, so a later run cannot overwrite the evidence.
        using (new FileStream(_path + ".claim", FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        if (File.Exists(_path)) throw new IOException("Startup report already exists: " + _path);
        _harnessSha256 = Hash(typeof(StartupFirstFrameMeasurement).Assembly.Location);
        _productSha256 = Hash(typeof(MainWindow).Assembly.Location);
        _run = PerformanceDiagnostics.BeginRun(Guid.NewGuid().ToString("N"),
            Environment.GetEnvironmentVariable("MEOWCHAN_STARTUP_SOURCE_BINDING") ?? "unbound-source-see-binary-hashes",
            "startup-first-frame-v1-empty-authority-one-historical-row", capacity: 32768);
        StartupTimingRegistry.Current = _trace;
        StartupFirstFrameChecks.Active = this;
        PerformanceDiagnostics.Record("harness.startup.entry", "observed", value: entryTimestamp);
        Flush();
        // IO never runs in the constructor/tray/UI timing hooks. Partial reports remain available
        // during a native synchronous stall and on external timeout/kill. Timer touches no UI objects.
        _writer = new(_ => TryFlush(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal ProfileReuse Reuse { get { lock (_writeLock) return _reuse; } }

    internal void SetProfileReuse(ProfileReuse reuse)
    {
        lock (_writeLock) _reuse = reuse;
    }

    internal void SetProfile(string profile)
    {
        string hash = Hash(Path.Combine(profile, "startup-preview.json"));
        lock (_writeLock) { _profile = profile; _fixtureHash = hash; }
    }

    internal void Attach(MainWindow window)
    {
        _window = window;
        window.Opened += OnOpened;
        window.LayoutUpdated += OnLayoutUpdated;
        foreach (var page in Pages)
        {
            Control<Button>(page.Button).PropertyChanged += OnGateChanged;
            if (OverflowItem(page.Page) is MenuItem item) item.PropertyChanged += OnGateChanged;
        }
        Control<Button>("ToolsNavigationButton").PropertyChanged += OnGateChanged;
        SnapshotNavigation("constructor-returned");
    }

    private T Control<T>(string name) where T : Control => _window!.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing startup observation control: " + name);

    private MenuItem? OverflowItem(string page) => (Control<Button>("ToolsNavigationButton").Flyout as MenuFlyout)?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "nav." + page.ToLowerInvariant());

    private void OnOpened(object? sender, EventArgs args)
    {
        SnapshotNavigation("opened");
        QueueRenderObservation();
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        SnapshotNavigation("layout");
        QueueRenderObservation();
    }

    private void OnGateChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == InputElement.IsEnabledProperty || args.Property == Visual.IsVisibleProperty)
            SnapshotNavigation("gate-change");
    }

    internal void SnapshotNavigation(string stage)
    {
        MainWindow window = _window!;
        PageGate[] pages = Pages.Select(page =>
        {
            Button button = Control<Button>(page.Button);
            Button tools = Control<Button>("ToolsNavigationButton");
            MenuItem? item = OverflowItem(page.Page);
            string? via = button.IsEffectivelyEnabled && button.IsEffectivelyVisible ? "direct-button"
                : tools.IsEffectivelyEnabled && tools.IsEffectivelyVisible && item is { IsEnabled: true, IsVisible: true }
                    ? "closed-tools-menu-entry" : null;
            return new PageGate(page.Page, button.IsEnabled, button.IsEffectivelyEnabled, button.IsEffectivelyVisible,
                tools.IsEffectivelyEnabled, tools.IsEffectivelyVisible, item?.IsEnabled == true, item?.IsVisible == true, via);
        }).ToArray();
        NavigationObservation sample = new(Stopwatch.GetTimestamp(), stage, window.SessionHost.State.ToString(),
            window.SessionHost.CatalogReadable, window.ReadStartupTimingGates(), pages);
        string identity = JsonSerializer.Serialize(new { sample.HostState, sample.CatalogReadable, sample.Shell, sample.Pages });
        // Visibility can change at Opened without changing any sampled gate fields. Record
        // first offers before deduplication, otherwise an identical constructor sample hides them.
        foreach (PageGate page in pages)
            if (window.IsVisible && page.OfferedVia != null && _offeredPages.Add(page.Page))
                _trace.Mark(PagePoint(page.Page));
        if (identity == _lastNavigation && stage is not ("opened" or "shell-ready")) return;
        _lastNavigation = identity;
        _navigation.Enqueue(sample);
    }

    private static StartupTimingPoint PagePoint(string page) => page switch
    {
        "Downloads" => StartupTimingPoint.DownloadsOffered, "Archive" => StartupTimingPoint.ArchiveOffered,
        "Media" => StartupTimingPoint.MediaOffered, "Rules" => StartupTimingPoint.RulesOffered,
        "Sources" => StartupTimingPoint.SourcesOffered, "Status" => StartupTimingPoint.DiagnosticsOffered, // the Status page (its Diagnostics segment is the recovery route)
        "Settings" => StartupTimingPoint.SettingsOffered,
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };

    private void QueueRenderObservation()
    {
        if (_observationEnded || _window is not { IsVisible: true } window
            || (_shellRendered && _historicalRendered)) return;
        if (_renderPending) { _renderDirty = true; return; }
        _renderDirty = false;
        Compositor? compositor = ElementComposition.GetElementVisual(window)?.Compositor;
        if (compositor == null) { RenderFailed(new InvalidOperationException("Native window has no composition visual.")); return; }
        _renderPending = true;
        // Obtain the pending batch BEFORE the before-commit callback. Requesting a batch from
        // inside that callback can observe the following commit instead of the initial one.
        var batch = compositor.RequestCompositionBatchCommitAsync();
        compositor.RequestCompositionUpdate(() =>
        {
            try
            {
                // AnimationFrame/Opened/layout alone are NOT render completion. Observe the first
                // composition batch carrying positive visible shell geometry, then await Rendered.
                bool shell = window.IsVisible && window.Bounds.Width > 0 && window.Bounds.Height > 0
                    && Control<Grid>("WorkspaceBody").IsEffectivelyVisible;
                DataGrid grid = Control<DataGrid>("ThreadsDataGrid");
                bool historical = shell && Control<Border>("StartupPreviewPanel").IsEffectivelyVisible
                    && ReferenceEquals(grid.ItemsSource, window.HistoricThreads)
                    && grid.GetVisualDescendants().OfType<DataGridRow>().Any(row =>
                    {
                        Point? at = row.TranslatePoint(default, grid);
                        return row.DataContext is ThreadRow { IsHistorical: true } && row.IsEffectivelyVisible
                            && row.Bounds.Width > 0 && row.Bounds.Height > 0 && at is Point point
                            && new Rect(grid.Bounds.Size).Intersects(new Rect(point, row.Bounds.Size));
                    });
                if (historical) _trace.Mark(StartupTimingPoint.HistoricalRowsObserved);
                _ = ObserveRenderedAsync(batch.Rendered, shell, historical);
            }
            catch (Exception error) { RenderFailed(error); }
        });
    }

    private async Task ObserveRenderedAsync(Task rendered, bool shell, bool historical)
    {
        try
        {
            await rendered.ConfigureAwait(false);
            // Capture on render completion, not at a later, potentially delayed UI continuation.
            // The sink is thread-safe. The candidate flags were captured on the UI thread above.
            if (shell && !_shellRendered)
            {
                _shellRendered = true;
                _trace.Mark(StartupTimingPoint.ShellBatchRendered);
                _firstFrame.TrySetResult();
            }
            if (historical && !_historicalRendered)
            {
                _historicalRendered = true;
                _trace.Mark(StartupTimingPoint.HistoricalBatchRendered);
            }
            Dispatcher.UIThread.Post(() =>
            {
                _renderPending = false;
                if (_renderDirty) QueueRenderObservation();
            }, DispatcherPriority.Background);
        }
        catch (Exception error) { RenderFailed(error); }
    }

    private void RenderFailed(Exception error)
    {
        Volatile.Write(ref _renderError, error.ToString());
        _firstFrame.TrySetException(error);
    }

    internal void EndStartupObservation()
    {
        _observationEnded = true;
        _window!.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        foreach (var page in Pages)
        {
            Control<Button>(page.Button).PropertyChanged -= OnGateChanged;
            if (OverflowItem(page.Page) is MenuItem item) item.PropertyChanged -= OnGateChanged;
        }
        Control<Button>("ToolsNavigationButton").PropertyChanged -= OnGateChanged;
    }

    internal async Task DrainRenderObservationAsync()
    {
        while (_renderPending)
        {
            if (Volatile.Read(ref _renderError) is string error) throw new InvalidOperationException(error);
            await Task.Delay(20);
        }
    }

    internal async Task ProbeNavigationAsync()
    {
        PerformanceDiagnostics.Record("harness.startup.navigation-probes", "begin");
        foreach (var page in Pages)
        {
            while (_window!.ReadStartupTimingGates().CommandBusy) await Task.Delay(20);
            Button button = Control<Button>(page.Button);
            bool offered = button.IsEffectivelyEnabled && button.IsEffectivelyVisible;
            string route = "direct-button";
            if (offered) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (OverflowItem(page.Page) is { IsEnabled: true, IsVisible: true } item
                && Control<Button>("ToolsNavigationButton") is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } tools)
            {
                route = "tools-menu-entry";
                tools.Flyout!.ShowAt(tools);
                while (!item.IsEffectivelyVisible) await Task.Delay(20);
                offered = item.IsEffectivelyEnabled;
                if (offered) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                tools.Flyout.Hide();
            }
            var shell = _window!.ReadStartupTimingGates();
            bool reached = offered && shell.SelectedPage == page.Page && Control<Control>(page.Host).IsEffectivelyVisible;
            _probes.Enqueue(new { timestamp = Stopwatch.GetTimestamp(), page = page.Page, route, offered, reached,
                hostState = _window.SessionHost.State.ToString(), shell,
                controls = Control<Control>(page.Host).GetVisualDescendants().OfType<Control>()
                    .Where(control => control.Name != null && (control is Button or TextBox or ComboBox))
                    .Select(control => new { control.Name, control.IsEnabled, control.IsEffectivelyEnabled, control.IsEffectivelyVisible }).ToArray() });
            if (!reached) throw new InvalidOperationException("Ready navigation handler did not reach " + page.Page);
        }
        Control<Button>("DownloadsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PerformanceDiagnostics.Record("harness.startup.navigation-probes", "end");
    }

    internal void Finish(bool succeeded, string? failure = null)
    {
        lock (_writeLock)
        {
            _failure = failure;
            _status = succeeded ? "completed" : "failed";
            Flush();
        }
        if (succeeded && _writeError != null) throw new IOException("Startup report writer failed: " + _writeError);
    }

    private void TryFlush()
    {
        try { Flush(); }
        catch (Exception error) { _writeError = error.ToString(); }
    }

    internal void Flush()
    {
        lock (_writeLock)
        {
            PerformanceSnapshot diagnostics = _run.Snapshot();
            long?[] points = _trace.Snapshot();
            long? At(StartupTimingPoint point) => points[(int)point];
            double? Separation(StartupTimingPoint from, StartupTimingPoint to) =>
                StartupTimingTrace.Milliseconds(At(from), At(to), Stopwatch.Frequency);
            var milestones = new[]
            {
                ("injected host construction begin", StartupTimingPoint.HostBegin),
                ("injected host construction end", StartupTimingPoint.HostEnd),
                ("window factory begin (includes base/field initialization)", StartupTimingPoint.WindowFactoryBegin),
                ("window factory returned", StartupTimingPoint.WindowFactoryEnd),
                ("MainWindow constructor body begin", StartupTimingPoint.ConstructorBegin),
                ("MainWindow constructor body end", StartupTimingPoint.ConstructorEnd),
                ("SetupTrayIcon begin", StartupTimingPoint.TrayBegin),
                ("SetupTrayIcon end (including exception)", StartupTimingPoint.TrayEnd),
                ("Opened handler entry", StartupTimingPoint.Opened),
                ("observed rendered shell composition batch", StartupTimingPoint.ShellBatchRendered),
                ("historical rows realized/visible in composition update", StartupTimingPoint.HistoricalRowsObserved),
                ("observed rendered historical rows composition batch", StartupTimingPoint.HistoricalBatchRendered),
                ("CatalogReadable observer", StartupTimingPoint.CatalogReadable),
                ("first observed published Ready projection", StartupTimingPoint.ReadyProjection),
                ("shell restore body completed", StartupTimingPoint.ShellRestoreCompleted)
            }.Select(point => new
            {
                name = point.Item1, timestamp = At(point.Item2),
                sinceManagedEntryMs = StartupTimingTrace.Milliseconds(_entryTimestamp, At(point.Item2), Stopwatch.Frequency),
                status = At(point.Item2) != null ? "observed" : "not-observed"
            }).ToArray();
            var report = new
            {
                schema = "cx01-startup-first-frame-v1", status = _status, failure = _failure,
                heartbeatTimestamp = Stopwatch.GetTimestamp(), processId = Environment.ProcessId,
                processStartUtc = _processStartUtc, managedEntryUtc = _entryUtc, managedEntryTimestamp = _entryTimestamp,
                processStartToManagedEntryMs = (_entryUtc - _processStartUtc).TotalMilliseconds,
                clock = "milestones/separations use Stopwatch ticks; OS process-start UTC to managed-entry UTC is a separate wall-clock observation, not an exact monotonic mapping",
                profile = _profile, warm = _reuse.Warm, profileReuse = _reuse, fixtureSha256 = _fixtureHash, harnessSha256 = _harnessSha256, productSha256 = _productSha256,
                httpAttempts = HttpAttempts,
                nativeTray = "real Avalonia SetupTrayIcon, isolated DBus without a supplied StatusNotifier host; exporter availability is observed in the shell gate samples, not a host-desktop tray claim",
                milestones,
                separationsMs = new
                {
                    injectedHostConstruction = Separation(StartupTimingPoint.HostBegin, StartupTimingPoint.HostEnd),
                    injectedHostBeginToWindowConstructorEnd = Separation(StartupTimingPoint.HostBegin, StartupTimingPoint.ConstructorEnd),
                    windowFactory = Separation(StartupTimingPoint.WindowFactoryBegin, StartupTimingPoint.WindowFactoryEnd),
                    constructor = Separation(StartupTimingPoint.ConstructorBegin, StartupTimingPoint.ConstructorEnd),
                    tray = Separation(StartupTimingPoint.TrayBegin, StartupTimingPoint.TrayEnd),
                    constructorEndToOpened = Separation(StartupTimingPoint.ConstructorEnd, StartupTimingPoint.Opened),
                    openedToShellBatch = Separation(StartupTimingPoint.Opened, StartupTimingPoint.ShellBatchRendered),
                    shellBatchToHistoricalBatch = Separation(StartupTimingPoint.ShellBatchRendered, StartupTimingPoint.HistoricalBatchRendered),
                    shellBatchToCatalogReadable = Separation(StartupTimingPoint.ShellBatchRendered, StartupTimingPoint.CatalogReadable),
                    shellBatchToReady = Separation(StartupTimingPoint.ShellBatchRendered, StartupTimingPoint.ReadyProjection),
                    catalogReadableToReady = Separation(StartupTimingPoint.CatalogReadable, StartupTimingPoint.ReadyProjection),
                    shellBatchToRestore = Separation(StartupTimingPoint.ShellBatchRendered, StartupTimingPoint.ShellRestoreCompleted)
                },
                pageFirstOffered = Pages.Select(page => new
                {
                    page = page.Page, timestamp = At(PagePoint(page.Page)),
                    sinceManagedEntryMs = StartupTimingTrace.Milliseconds(_entryTimestamp, At(PagePoint(page.Page)), Stopwatch.Frequency)
                }).ToArray(),
                navigation = _navigation.ToArray(), readyHandlerProbes = _probes.ToArray(), diagnostics,
                writeError = _writeError, renderError = Volatile.Read(ref _renderError),
                limits = "measurement only; no SLO, no cold OS-cache or physical-presentation claim. Rendered is an observed Avalonia batch-completion continuation, not physical presentation or an OS screenshot. Historical rows can retire before a frame: null means unobserved, not zero latency. Startup navigation samples are offered UI gates (including eligible entries in the closed responsive Tools menu); actual managed Click handlers are probed only after startup, not keyboard/AT-SPI/host writer acceptance. The default host is constructed before MainWindow to inject a fail-closed HTTP transport; its separate interval and combined host+window interval are reported, not silently charged to the supplied-session MainWindow constructor. No hold or tray substitution; periodic report IO and composition observation add instrumentation overhead. Essential one-shot timestamps use independent atomic slots, not the lossy diagnostic ring. Ring dropped count applies only to diagnostics. Missing observations remain null and negative separations are retained. The Ready observer may see a stale projection and runs after publication; shell restore is the body completion, not a render barrier. An observing/stale report after external timeout is incomplete, never PASS."
            };
            string pending = _path + ".pending";
            File.WriteAllText(pending, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(pending, _path, overwrite: true);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using (ManualResetEvent drained = new(false))
        {
            if (_writer.Dispose(drained)) drained.WaitOne();
        }
        if (_window != null && Dispatcher.UIThread.CheckAccess()) EndStartupObservation();
        if (_status == "observing") _status = "incomplete";
        try { Flush(); }
        finally
        {
            _run.Dispose();
            StartupTimingRegistry.Current = null;
            StartupFirstFrameChecks.Active = null;
        }
        if (_writeError != null) throw new IOException("Startup report writer failed: " + _writeError);
    }
}
