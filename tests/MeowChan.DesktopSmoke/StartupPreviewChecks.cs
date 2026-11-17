using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal sealed class StartupPreviewSmokeApp : App, IDisposable
{
    internal StartupPreviewBarrier Barrier { get; } = new();
    internal StartupPreviewBarrier ReadableBarrier { get; } = new();
    internal StartupPreviewObservedStore Store { get; private set; } = null!;
    internal StartupPreviewNoNetwork Transport { get; } = new();
    private readonly HttpClient _client;

    public StartupPreviewSmokeApp() => _client = new HttpClient(Transport);
    public void Dispose() => _client.Dispose();

    internal DesktopSessionHost CreateHost(string profile, StartupPreviewBarrier? barrier,
        bool failRestore, out StartupPreviewObservedStore observed)
    {
        StartupPreviewObservedStore? store = null;
        DesktopSessionHost host = new(profile, new IImageBoard[] { new FourChanImageBoard(_client) },
            (directory, lease) => store = new(new SessionStore(directory, lease), failRestore));
        observed = store!;
        if (barrier != null) host.AfterStartupPreviewForTests = barrier.HoldAsync;
        return host;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Exit += (_, _) => _client.Dispose();
        base.OnFrameworkInitializationCompleted();
    }

    protected override MainWindow CreateMainWindow()
    {
        DesktopSessionHost host = CreateHost(Config.GetConfigDirectory(), Barrier, false, out var store);
        Store = store;
        host.AfterCatalogReadableForTests = ReadableBarrier.HoldAsync; // main window only: UX103 early archive
        return new MainWindow(host);
    }
}

// Only the startup dependency boundary is held/faulted. SessionStore, host publications,
// RestoreSessionAsync, RefreshSession, RowPlan, XAML and the native windows remain real.
internal sealed class StartupPreviewBarrier
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CancellationDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HoldCancellationDrain { get; init; }
    internal async Task HoldAsync(CancellationToken token)
    {
        Entered.TrySetResult();
        try { await Release.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Cancelled.TrySetResult();
            // Acknowledge cancellation, then hold only its completion so the native shell can
            // deterministically observe Stopping. Finally blocks always release this test gate.
            if (HoldCancellationDrain) await CancellationDrain.Task;
            throw;
        }
    }
}

internal sealed class StartupPreviewObservedStore(SessionStore inner, bool failRestore) : ISessionStore
{
    private int _loads;
    internal int Loads => Volatile.Read(ref _loads);
    internal const string Failure = "Synthetic startup-preview authoritative restore failure";
    public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _loads);
        return failRestore ? Task.FromException<SessionLoadResult>(new IOException(Failure)) : inner.LoadAsync(cancellationToken);
    }
    public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        => inner.SaveAsync(snapshot, cancellationToken);
}

internal sealed class StartupPreviewNoNetwork : HttpMessageHandler
{
    private int _requests;
    internal int Requests => Volatile.Read(ref _requests);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        throw new InvalidOperationException("Paused startup-preview fixture attempted HTTP: " + request.RequestUri);
    }
}

internal static class StartupPreviewChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string CachedTitle = "OLD TITLE — display only, never a restored watch";
    private static readonly string[] ChildCases = ["failure", "stop", "foreign", "malformed", "future", "oversize"];
    private static string CaseProfile(string profile, string name) => Path.Combine(profile, "startup-preview-cases", name);
    private static string CachePath(string profile) => Path.Combine(profile, "startup-preview.json");
    private static string ThreadUrl(int number) => "https://boards.4chan.org/b/thread/" + number;

    internal static void Prepare(string profile)
    {
        // Called only by Program's existing MEOWCHAN_STARTUP_PREVIEW_ONLY branch on its disposable profile.
        Seed(profile);
        EarlyArchiveChecks.Seed(profile);
        foreach (string name in ChildCases) Seed(CaseProfile(profile, name));
        File.WriteAllText(CachePath(CaseProfile(profile, "malformed")), "{bad");
        string future = CachePath(CaseProfile(profile, "future"));
        JsonObject document = JsonNode.Parse(File.ReadAllText(future))!.AsObject();
        document["schemaVersion"] = 99; // otherwise valid, not an incomplete-document proxy for SP-01
        File.WriteAllText(future, document.ToJsonString());
        File.WriteAllBytes(CachePath(CaseProfile(profile, "oversize")), new byte[StartupPreviewStore.MaximumBytes + 1]);
    }

    private static void Seed(string profile)
    {
        Directory.CreateDirectory(profile);
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "Downloads");
        config.SaveUrlsOnExit = true;
        config.DownloadsPaused = true;
        config.CloseToTray = false;
        config.MinimizeToTray = false;
        config.FirstRunSetupPending = false;
        config.FirstRunSetupVersion = 1;
        config.AutomaticUpdateChecks = false;
        config.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        new SessionStore(profile, lease).SaveAsync(new SessionSnapshot()).GetAwaiter().GetResult();
        new StartupPreviewStore(profile, lease).Save(new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), 1, 0,
            Array.AsReadOnly(new[] { new StartupPreviewRow(false, "Synthetic cached source", "b", CachedTitle, 25, 12) })));
    }

    internal static async Task RunAsync(MainWindow window)
    {
        Dispatcher.UIThread.VerifyAccess();
        StartupPreviewSmokeApp app = (StartupPreviewSmokeApp)Application.Current!;
        await CheckHeldAsync(window, app.Barrier, app.Store, "pending");
        app.Barrier.Release.TrySetResult();
        await EarlyArchiveChecks.RunHeldAsync(window, app.ReadableBarrier, () => app.Store.Loads);
        await AwaitReadyAsync(window);
        Require(app.Store.Loads == 1, "Authoritative SessionStore was not loaded exactly once.");
        Require(window.UrlList.Count == 0 && window.BoardsList.Count == 0, "Authoritative empty restore adopted cached rows.");
        AssertRetired(window);
        Program.CaptureWindow(window, "smoke-startup-authoritative-ready.png");

        // Watch visibility throughout later real captures, not just the final settled frame.
        Border panel = Control<Border>(window, "StartupPreviewPanel");
        int resurrections = 0;
        void ObserveVisibility(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && panel.IsVisible) resurrections++;
        }
        panel.PropertyChanged += ObserveVisibility;
        try
        {
            await CheckLiveRefreshAsync(window);
            await CheckForeignRowsAsync(window, app);
            await CheckTerminalAsync(window, app, failRestore: true);
            await CheckTerminalAsync(window, app, failRestore: false);
            foreach (string name in new[] { "malformed", "future", "oversize" })
                await CheckInvalidCacheAsync(window, app, name);
            await AwaitAppliedAsync(window);
            Require(resurrections == 0, "Historical preview became visible during a later Ready publication.");
            AssertRetired(window);
            Require(app.Transport.Requests == 0, "A preview, foreign row, or paused real watch attempted network activity.");
        }
        finally { panel.PropertyChanged -= ObserveVisibility; }
        Console.WriteLine("SMOKE startup preview PASS: held real shell rendered inert text/counts; dispatcher responsive; commands rejected; real empty Ready and multi-slice live refresh without resurrection; foreign watch IDs inert; RecoveryFailed/Closing/Stopped and SP-01 cache fallback checked. Synthetic/private desktop only; no 2-minute latency or other-platform claim.");
    }

    private static async Task CheckHeldAsync(MainWindow window, StartupPreviewBarrier barrier,
        StartupPreviewObservedStore store, string capture)
    {
        await barrier.Entered.Task.WaitAsync(Deadline);
        await Program.WaitUntilAsync(() => Control<Border>(window, "StartupPreviewPanel").IsEffectivelyVisible);
        Require(window.IsVisible && window.SessionHost.State == DesktopSessionState.Starting, "Preview accidentally marked session Ready or has no native shell.");
        Require(store.Loads == 0 && !window.SessionHost.StartAsync().IsCompleted, "Authoritative restore passed the held preview boundary.");
        AssertCommandControlsClosed(window);
        AssertEmptyAuthority(window);
        Require(Control<TextBlock>(window, "StartupPreviewTitle").Text == Texts.T("shell.startupPreview"), "Unverified label missing.");
        byte[] originalCache = File.ReadAllBytes(CachePath(window.SessionHost.ProfileDirectory));
        // The cached session now fills the NORMAL Downloads table (same columns/layout), marked as last known.
        DataGrid grid = Control<DataGrid>(window, "ThreadsDataGrid");
        Require(ReferenceEquals(grid.ItemsSource, window.HistoricThreads) && Control<Grid>(window, "WorkspaceBody").IsVisible,
            "The cached session is not shown in the normal Downloads table.");
        ThreadRow cached = Require1(window.HistoricThreads);
        Require(cached.IsHistorical && cached.CompletedMedia == 12 && cached.KnownMedia == 25 && cached.Title == CachedTitle
            && cached.WatchId < 0 && cached.Status == Texts.T("shell.lastKnownStatus"), "Cached display data changed or claims live state.");
        await Program.WaitUntilAsync(() => grid.Bounds.Height > 0 && grid.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.IsEffectivelyVisible && text.Text == CachedTitle));
        Require(grid.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == Texts.T("shell.lastKnownStatus")),
            "Last-known marking not rendered by the real DataTemplate.");
        // The counts the former preview list rendered through Inlines ("12 / 25") are three TextBlocks in the table's progress cell.
        Require(grid.GetVisualDescendants().OfType<StackPanel>().Any(cell => cell.IsEffectivelyVisible
                && cell.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(new[] { "12", "/", "25" })),
            "Cached counts not rendered by the real DataTemplate.");
        Require(!grid.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Name != null && button.Name.Contains("Retry")),
            "Display-only rows acquired an action control.");
        grid.SelectedIndex = 0; // selecting a display row must never select/create an authoritative watch
        await Program.WaitUntilAsync(() => grid.SelectedItem != null);
        Require(window.SelectedThreadsForTests().Length == 0, "A historical row became a command target.");
        await RejectCommandsAsync(window.SessionHost);
        for (int pulse = 0; pulse < 3; pulse++) await DispatcherPulseAsync();
        Require(!barrier.Release.Task.IsCompleted && !barrier.Cancelled.Task.IsCompleted && store.Loads == 0,
            "Fixture released/cancelled or loaded authority before responsiveness assertion.");
        AssertEmptyAuthority(window);
        AssertCommandControlsClosed(window);
        Require(originalCache.SequenceEqual(File.ReadAllBytes(CachePath(window.SessionHost.ProfileDirectory))),
            "Held startup/selection/rejected commands rewrote the preview cache.");
        Require(Control<Border>(window, "StartupPreviewPanel").IsEffectivelyVisible, "Held preview disappeared.");
        Program.CaptureWindow(window, "smoke-startup-cached-" + capture + ".png");
        Console.WriteLine("SMOKE startup held " + capture + ": native template/counts visible, three background dispatcher pulses, store loads=0, command rejection and empty authority.");
    }

    private static async Task CheckLiveRefreshAsync(MainWindow window)
    {
        // Real paused Registry watches, not fabricated projections/rows or synthetic command handles.
        // 129 existing entries guarantee several 64-operation plan slices for the next real watch.
        for (int index = 0; index < 129; index++)
            Require((await window.SessionHost.AddAsync(ThreadUrl(700000 + index)).WaitAsync(Deadline)).WatchId.HasValue,
                "Could not admit an actual paused fixture watch.");
        await AwaitAppliedAsync(window);
        Require(window.UrlList.Count == 129 && window.BoardsList.Count == 0 && window.SessionHost.IsPaused,
            "Real paused multi-slice fixture did not settle.");
        TaskCompletionSource boundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int observations = 0;
        void DuringRealRowSlice(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action != NotifyCollectionChangedAction.Add || observations != 0) return;
            observations++;
            try
            {
                Require(window.SessionHost.State == DesktopSessionState.Ready && window.SessionRowsApplying,
                    "Second refresh missed the actual Ready row-application boundary.");
                var plan = Read<IncrementalRowReconciliation<long, DesktopThreadEntry, ThreadRow>>(window, "_threadReconciliation");
                Require(plan.Slices >= 2, "Regression did not cross multiple real row slices.");
                long version = Read<DesktopSessionProjection>(window, "_applyingSessionProjection").Version;
                // Synchronous second presentation invalidation INSIDE a genuine plan-owned collection
                // notification. No polling race, BeginSessionRows call, or lifecycle/visibility writes.
                Refresh(window);
                AssertRetired(window);
                Console.WriteLine($"SMOKE startup SP-02 boundary: Ready version={version}, prior slices={plan.Slices}, actual rows={window.UrlList.Count}; second RefreshSession did not resurrect preview.");
                boundary.TrySetResult();
            }
            catch (Exception error) { boundary.TrySetException(error); }
        }
        window.UrlList.CollectionChanged += DuringRealRowSlice;
        try
        {
            await window.SessionHost.AddAsync(ThreadUrl(800000)).WaitAsync(Deadline);
            await boundary.Task.WaitAsync(Deadline);
            await AwaitAppliedAsync(window);
            Require(observations == 1 && window.UrlList.Count == 130, "Real live refresh did not complete exactly one observed add.");
            Require(window.UrlList.All(row => row.Snapshot.Title != CachedTitle), "Cache text entered the authoritative live rows.");
            AssertRetired(window);
            Program.CaptureWindow(window, "smoke-startup-live-ready-no-resurrection.png");
        }
        finally { window.UrlList.CollectionChanged -= DuringRealRowSlice; }
    }

    private static async Task CheckForeignRowsAsync(MainWindow window, StartupPreviewSmokeApp app)
    {
        await using DesktopSessionHost foreign = app.CreateHost(CaseProfile(window.SessionHost.ProfileDirectory, "foreign"), null, false, out _);
        await foreign.StartAsync().WaitAsync(Deadline);
        Require(foreign.State == DesktopSessionState.Ready, "Foreign fixture did not really restore.");
        long thread = (await foreign.AddAsync(ThreadUrl(900000)).WaitAsync(Deadline)).WatchId!.Value;
        long board = (await foreign.AddAsync("https://boards.4chan.org/g/").WaitAsync(Deadline)).WatchId!.Value;
        long[] own = window.SessionHost.Projection.View.Threads.Select(row => row.WatchId).ToArray();
        Require(!own.Contains(thread) && foreign.Projection.Epoch != window.SessionHost.Projection.Epoch, "Foreign fixture is not another host/watch lifetime.");
        Require(!window.SessionHost.CanRequestCheck(new(DiagnosticTargetKind.Thread, thread))
            && !window.SessionHost.CanRetryMissingMedia(thread), "Foreign row acquired a command predicate.");
        Require(await window.SessionHost.RequestCheckAsync(new(DiagnosticTargetKind.Thread, thread)).WaitAsync(Deadline) == CheckRequestStatus.Stale
            && await window.SessionHost.RequestCheckAsync(new(DiagnosticTargetKind.Board, board)).WaitAsync(Deadline) == CheckRequestStatus.Stale,
            "Foreign row scheduled a real check.");
        Require(!await window.SessionHost.SetUserThreadPriorityAsync(thread, true).WaitAsync(Deadline), "Foreign row acquired priority authority.");
        Require(await window.SessionHost.RetryMissingMediaAsync(thread).WaitAsync(Deadline) == 0, "Foreign row scheduled media retries.");
        await window.SessionHost.RemoveThreadAsync(thread).WaitAsync(Deadline);
        await window.SessionHost.RemoveBoardAsync(board).WaitAsync(Deadline);
        await AwaitAppliedAsync(window);
        Require(own.SequenceEqual(window.SessionHost.Projection.View.Threads.Select(row => row.WatchId))
            && window.SessionHost.Projection.HighPriorityWatches.Count == 0 && window.BoardsList.Count == 0
            && window.SessionHost.IsPaused && foreign.IsPaused,
            "Foreign IDs modified this shell's watches/priority.");
        Require(foreign.Projection.View.Threads.Single().WatchId == thread && foreign.Projection.View.Boards.Single().WatchId == board,
            "Cross-host command changed the foreign watches.");
        Console.WriteLine("SMOKE startup foreign rows: actual other-host thread/board IDs have no local action authority; both hosts unchanged, still paused.");
    }

    private static async Task CheckTerminalAsync(MainWindow owner, StartupPreviewSmokeApp app, bool failRestore)
    {
        string name = failRestore ? "failure" : "stop";
        string profile = CaseProfile(owner.SessionHost.ProfileDirectory, name);
        byte[] before = File.ReadAllBytes(CachePath(profile));
        StartupPreviewBarrier barrier = new() { HoldCancellationDrain = !failRestore };
        DesktopSessionHost host = app.CreateHost(profile, barrier, failRestore, out var store);
        await using MainWindow window = new(host);
        window.Show(owner);
        try
        {
            await CheckHeldAsync(window, barrier, store, name + "-pending");
            if (failRestore)
            {
                barrier.Release.TrySetResult();
                await RestoreTask(window).WaitAsync(Deadline);
                await AwaitAppliedAsync(window);
                Require(host.State == DesktopSessionState.RecoveryFailed && store.Loads == 1
                    && host.StartupError?.Contains(StartupPreviewObservedStore.Failure, StringComparison.Ordinal) == true,
                    "Injected authoritative store failure did not reach real RecoveryFailed.");
                Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible, "RecoveryFailed retained the historical preview.");
                AssertCommandControlsClosed(window);
                AssertEmptyAuthority(window);
                await RejectCommandsAsync(host);
                Program.CaptureWindow(window, "smoke-startup-recovery-failed.png");
            }
            // Actual shell close owns cancellation, Stopping, StopAsync, subscription/lease disposal.
            // Do NOT dispose the host behind a visible window or force _allowClose/_isClosing.
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible, "Closing did not synchronously hide preview.");
            if (!failRestore)
            {
                await barrier.Cancelled.Task.WaitAsync(Deadline);
                Require(host.State == DesktopSessionState.Stopping && !RestoreTask(window).IsCompleted && store.Loads == 0,
                    "Stop did not retain Stopping while the cancelled startup drained.");
                AssertCommandControlsClosed(window);
                await RejectCommandsAsync(host);
                await DispatcherPulseAsync();
                Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible, "Stopping resurrected the held preview.");
                Program.CaptureWindow(window, "smoke-startup-stopping.png");
                barrier.CancellationDrain.TrySetResult();
            }
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Stopped && host.IsProfileReleased
                && !Read<bool>(window, "_shutdownRunning"));
            // Failed/overtaken startup intentionally refuses final preference writes. The existing
            // shutdown contract can leave a safely disposed shell for error acknowledgement.
            // Acknowledge only AFTER real Stop/disposal, never force its close/lifecycle flags.
            if (window.IsVisible)
            {
                Require(Read<bool>(window, "_allowClose"), "Terminal shell retained ownership or cannot acknowledge shutdown.");
                Console.WriteLine("SMOKE startup " + name + ": acknowledging disposed shutdown-error shell via a second real Close (no preference-success claim).");
                window.Close();
            }
            await closed.Task.WaitAsync(Deadline);
            await RestoreTask(window).WaitAsync(Deadline);
            Require(host.State == DesktopSessionState.Stopped && host.IsProfileReleased, "Shell close did not stop/release the host.");
            if (!failRestore)
            {
                Require(barrier.Cancelled.Task.IsCompletedSuccessfully && !barrier.Release.Task.IsCompleted && store.Loads == 0,
                    "Stop did not cancel at the held preview boundary before real restore.");
                barrier.Release.TrySetResult(); // late fixture completion must not activate anything
                await DispatcherPulseAsync();
                Refresh(window); // disposed/closing presentation must stay inert
                Require(host.State == DesktopSessionState.Stopped && store.Loads == 0
                    && !Control<Border>(window, "StartupPreviewPanel").IsVisible, "Late release resurrected stopped preview/session.");
            }
            await RejectCommandsAsync(host);
            AssertEmptyAuthority(window);
            Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible
                && window.HistoricThreads.Count == 0, "Stopped shell retained preview presentation.");
            Require(before.SequenceEqual(File.ReadAllBytes(CachePath(profile))), "Failed/stopped startup overwrote the last good display cache.");
            using ProfileLease nextOwner = ProfileLease.Acquire(profile);
            Console.WriteLine("SMOKE startup " + name + ": preview hidden, real shell closed/Stopped, commands rejected, cache byte-identical, lease reacquired.");
        }
        finally
        {
            barrier.CancellationDrain.TrySetResult();
            barrier.Release.TrySetResult();
            // An unsuccessful journey stays a failure; request only the normal shell shutdown.
            if (window.IsVisible) window.Close();
        }
    }

    private static async Task CheckInvalidCacheAsync(MainWindow owner, StartupPreviewSmokeApp app, string name)
    {
        string profile = CaseProfile(owner.SessionHost.ProfileDirectory, name);
        byte[] before = File.ReadAllBytes(CachePath(profile));
        StartupPreviewBarrier barrier = new();
        DesktopSessionHost host = app.CreateHost(profile, barrier, false, out var store);
        await using MainWindow window = new(host);
        window.Show(owner);
        try
        {
            await barrier.Entered.Task.WaitAsync(Deadline);
            Require(host.State == DesktopSessionState.Starting && host.StartupPreview == null && store.Loads == 0,
                "SP-01 rejected display cache prevented reaching the pre-restore hook or published invalid text: " + name);
            await DispatcherPulseAsync();
            Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible, "Invalid cache became visible: " + name);
            barrier.Release.TrySetResult();
            await AwaitReadyAsync(window);
            Require(host.StartupPreview == null && store.Loads == 1, "SP-01 invalid cache prevented normal SessionStore restore: " + name);
            AssertEmptyAuthority(window);
            Require(before.SequenceEqual(File.ReadAllBytes(CachePath(profile))), "Startup rewrote invalid display cache: " + name);
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(Deadline);
            Require(host.State == DesktopSessionState.Stopped && host.IsProfileReleased, "Invalid-cache shell did not release ownership.");
            Console.WriteLine("SMOKE startup SP-01 " + name + ": no preview; real store loads=1, Ready/empty/gates open, normal shell shutdown.");
        }
        finally
        {
            barrier.Release.TrySetResult();
            if (window.IsVisible) window.Close();
        }
    }

    private static async Task AwaitReadyAsync(MainWindow window)
    {
        await RestoreTask(window).WaitAsync(Deadline); // host completion alone does not finish shell RestoreSessionAsync
        Require(window.SessionHost.State == DesktopSessionState.Ready, "Real startup did not reach Ready: " + window.SessionHost.StartupError);
        await AwaitAppliedAsync(window);
        await Program.WaitUntilAsync(() => Control<Button>(window, "AddUrlButton").IsEnabled
            && Control<Button>(window, "PauseButton").IsEnabled && !Control<Border>(window, "StartupPreviewPanel").IsVisible);
        AssertRetired(window);
    }

    private static async Task AwaitAppliedAsync(MainWindow window)
    {
        await Program.WaitUntilAsync(() =>
        {
            DesktopSessionProjection current = window.SessionHost.Projection;
            var applied = ReadNullable<DesktopSessionProjection>(window, "_appliedSessionProjection");
            return !window.SessionRowsApplying && !Read<bool>(window, "_rowSlicePosted")
                && applied != null && applied.Epoch == current.Epoch && applied.Version == current.Version;
        });
    }

    private static async Task RejectCommandsAsync(DesktopSessionHost host)
    {
        Require(!host.CanRequestCheck(new(DiagnosticTargetKind.Thread, -1)) && !host.CanRetryMissingMedia(-1), "Non-Ready display enabled row predicates.");
        await RejectedAsync(() => host.AddAsync(ThreadUrl(999999)), "Add");
        await RejectedAsync(() => host.SetPausedAsync(false), "Resume");
        await RejectedAsync(() => host.RequestCheckAsync(new(DiagnosticTargetKind.Thread, -1)), "Check");
        await RejectedAsync(() => host.SetUserThreadPriorityAsync(-1, true), "Priority");
        await RejectedAsync(() => host.RemoveThreadAsync(-1), "Remove thread");
        await RejectedAsync(() => host.RemoveBoardAsync(-1), "Remove board");
        await RejectedAsync(() => host.RetryMissingMediaAsync(-1), "Retry media");
        Require(host.AcceptedCommandCount == 0, "Rejected commands retained command admission.");
    }

    private static async Task RejectedAsync(Func<Task> command, string name)
    {
        try { await command().WaitAsync(Deadline); }
        catch (InvalidOperationException error) when (error is not DesktopSessionBusyException) { return; }
        throw new InvalidOperationException(name + " was not rejected by the real non-Ready host gate.");
    }

    private static void AssertCommandControlsClosed(MainWindow window)
        => Require(!Control<Button>(window, "AddUrlButton").IsEnabled && !Control<Button>(window, "PauseButton").IsEnabled
            && !Control<TextBox>(window, "UrlInput").IsEnabled, "Non-Ready preview enabled actions/input.");

    private static void AssertEmptyAuthority(MainWindow window)
        => Require(window.UrlList.Count == 0 && window.BoardsList.Count == 0
            && window.SessionHost.Projection.View.Threads.Count == 0 && window.SessionHost.Projection.View.Boards.Count == 0
            && window.SessionHost.Projection.CheckableTargets.Count == 0,
            "Cached display created real watch rows or check authority.");

    private static void AssertRetired(MainWindow window)
    {
        Require(!Control<Border>(window, "StartupPreviewPanel").IsVisible
            && window.HistoricThreads.Count == 0 && ReferenceEquals(Control<DataGrid>(window, "ThreadsDataGrid").ItemsSource, window.VisibleThreads)
            && Control<Grid>(window, "WorkspaceBody").IsVisible, "Completed startup preview reappeared during a later Ready refresh.");
    }

    private static async Task DispatcherPulseAsync()
    {
        TaskCompletionSource pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => pulse.TrySetResult(), DispatcherPriority.Background);
        await pulse.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static T Control<T>(MainWindow window, string name) where T : Control
        => window.FindControl<T>(name) ?? throw new InvalidOperationException("Missing real shell control: " + name);
    private static Task RestoreTask(MainWindow window) => Read<Task>(window, "_restoreTask");
    private static T Read<T>(object owner, string field) => (T)(owner.GetType().GetField(field, Private)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing/noninitialized lifecycle observation: " + field));
    private static T? ReadNullable<T>(object owner, string field) where T : class
        => (T?) (owner.GetType().GetField(field, Private) ?? throw new InvalidOperationException("Missing lifecycle observation: " + field)).GetValue(owner);
    private static void Refresh(MainWindow window) => (typeof(MainWindow).GetMethod("RefreshSession", Private)
        ?? throw new InvalidOperationException("Missing real shell refresh boundary.")).Invoke(window, null);
    private static ThreadRow Require1(System.Collections.Generic.IReadOnlyList<ThreadRow> rows)
    {
        Require(rows.Count == 1, "Expected exactly one cached row, found " + rows.Count);
        return rows[0];
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
