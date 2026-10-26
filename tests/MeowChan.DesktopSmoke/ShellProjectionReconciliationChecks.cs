using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Actual MainWindow source/search transaction, driven at its existing private dispatcher boundary.
/// Only this fixture's host is changed: no live providers, process-global product hooks or new timers.
/// </summary>
internal static class ShellProjectionReconciliationChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, FailureInfo> NoFailures =
        new ReadOnlyDictionary<string, FailureInfo>(new Dictionary<string, FailureInfo>());

    internal static async Task RunAsync(MainWindow owner)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("shell-projection-reconciliation-");
        string language = Texts.Language;
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        DesktopSessionProjection? original = null;
        bool captureWasClosed = false, restored = false;
        Task? close = null;
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            new Config { DownloadPath = Path.Combine(directory, "media"), SaveUrlsOnExit = false,
                AutomaticUpdateChecks = false, CloseToTray = false, MinimizeToTray = false,
                WorkspaceMode = "Advanced", ShowWarningNotifications = false }.SaveConfig(directory);
            host = new DesktopSessionHost(directory, Array.Empty<IImageBoard>());
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && !shell.SessionRowsApplying && !Get<bool>(shell, "_rowSlicePosted"));
            // Quiesce only our empty host's publication worker, retaining its real Ready engine for Pause/Close.
            captureWasClosed = Get<bool>(host, "_captureAdmissionClosed");
            await ((Task)Call(host, "CloseProjectionAsync")!).WaitAsync(Deadline);
            original = host.Projection;
            // Own the existing single callback ticket. The production ContinueSessionRows body is unchanged;
            // natural RefreshSession/command continuations may run, but cannot advance a row slice behind us.
            Set(shell, "_rowSlicePosted", true);
            long version = original.Version + 100;
            DesktopSessionProjection seed = Frame(original, ++version, "seed", 385, 129, directory);
            Publish(host, seed);
            Call(shell, "RefreshSession");
            SliceCounts seedCounts = Drain(shell, seed);
            Require(seedCounts.SourceMultiple && seedCounts.SearchMultiple && seedCounts.Calls > 8,
                "The real shell did not slice source and search for a capture larger than 64 rows.");
            DataGrid grid = shell.FindControl<DataGrid>("ThreadsDataGrid")!;
            TextBox search = shell.FindControl<TextBox>("DownloadSearchInput")!;
            ThreadRow retained = shell.UrlList.Single(row => row.WatchId == ThreadId(1));
            ThreadRow removed = shell.UrlList.Single(row => row.WatchId == ThreadId(2));
            ThreadRow filtered = shell.UrlList.Single(row => row.WatchId == ThreadId(4));
            grid.SelectedItem = retained;
            grid.SelectedItems.Add(removed);
            grid.SelectedItems.Add(filtered);
            Set(shell, "_threadLeadId", (long?)retained.WatchId);
            shell.FocusDownloadSearch();
            await PostedAsync(DispatcherPriority.Background);
            Require(search.IsFocused, "The real search editor could not acquire fixture focus.");
            object? focus = shell.FocusManager?.GetFocusedElement();
            object menu = Call(shell, "CaptureDownloadMenu", false)
                ?? throw new InvalidOperationException("The complete-frame menu fixture has no selected lead.");
            int resets = 0;
            bool appliedSkipped = false;
            NotifyCollectionChangedEventHandler collections = (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
                appliedSkipped |= shell.UrlList.Any(row => row.Snapshot.Title.StartsWith("skipped ", StringComparison.Ordinal));
            };
            shell.UrlList.CollectionChanged += collections;
            shell.VisibleThreads.CollectionChanged += collections;
            shell.BoardsList.CollectionChanged += collections;
            shell.VisibleBoards.CollectionChanged += collections;
            foreach (ThreadRow row in shell.UrlList)
                row.PropertyChanged += (_, _) => appliedSkipped |= row.Snapshot.Title.StartsWith("skipped ", StringComparison.Ordinal);

            DesktopSessionProjection active = Frame(original, ++version, "active", 385, 129, directory);
            const string warning = "PF01 prompt warning while source/search slices are held";
            DesktopSessionProjection prompt = active with { Version = ++version, IsPaused = true,
                View = active.View with { Warnings = Array.AsReadOnly(new[] { warning }) } };
            DesktopSessionProjection skipped = Frame(original, ++version, "skipped", 410, 137, directory);
            DesktopSessionProjection latest = Frame(original, ++version, "latest", 480, 145, directory,
                omitSecond: true) with { IsPaused = true,
                    View = FrameView("latest", 480, 145, directory, omitSecond: true) with
                    { Warnings = Array.AsReadOnly(new[] { warning }) } };
            Publish(host, active);
            Call(shell, "RefreshSession");
            Require(shell.SessionRowsApplying && ReferenceEquals(Get<DesktopSessionProjection>(shell, "_applyingSessionProjection"), active),
                "The active capture completed before a controlled partial-frame boundary.");
            Require(Get<DesktopSessionProjection>(shell, "_appliedSessionProjection").Version == seed.Version,
                "A partial source capture was marked fully applied.");
            Require(Call(shell, "RevalidateDownloadMenu", menu) == null
                && ((ThreadRow[])Call(shell, "SelectedThreads")!).Length == 0
                && GetProperty<bool>(shell, "RowCommandBusy"),
                "An already-open row menu/selection can consume a partial frame.");
            int rowCommands = 0;
            MethodInfo runSelection = typeof(MainWindow).GetMethods(PrivateInstance)
                .Single(method => method.Name == "RunSelectionCommandAsync" && method.IsGenericMethodDefinition);
            Task rejected = (Task)runSelection.MakeGenericMethod(typeof(long)).Invoke(shell,
                new object[] { new[] { retained.WatchId }, (Func<long, Task>)(_ => { rowCommands++; return Task.CompletedTask; }) })!;
            Require(rejected.IsCompletedSuccessfully && rowCommands == 0 && !Get<bool>(shell, "_selectionCommandRunning"),
                "The actual selection command guard admitted work during partial application.");

            // Exercise the actual Pause button while rows cannot advance. Hold the real serializer only long
            // enough to prove its independent control admission, then await the shared control completion.
            Button pauseButton = shell.FindControl<Button>("PauseButton")!;
            Require(pauseButton.IsEnabled, "Partial row application disabled Pause.");
            SemaphoreSlim commands = Get<SemaphoreSlim>(host, "_commands");
            await commands.WaitAsync().WaitAsync(Deadline);
            Task pause;
            try
            {
                pauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pause = host.SetPausedAsync(true);
                Require(!pause.IsCompleted && host.AcceptedCommandCount == 0,
                    "Pause did not enter its independent bounded control lane.");
            }
            finally { commands.Release(); }
            await pause.WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => !Get<bool>(shell, "_applyingPreferences"));
            Require(host.Config.DownloadsPaused && shell.SessionRowsApplying,
                "Pause failed to finish independently of the held row transaction.");

            // Same-size prompt change must use the real notification/coalescer path while row advancement
            // is held. No row-count change can accidentally make this particular prompt check succeed.
            Set(shell, "_lastNotifiedProjection", active);
            Publish(host, prompt);
            Call(shell, "OnSessionChanged");
            await PostedAsync(DispatcherPriority.Background);
            Require(ReferenceEquals(Get<DesktopSessionProjection>(shell, "_applyingSessionProjection"), active)
                && shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible
                && shell.FindControl<TextBlock>("ShellActionError")!.Text == warning,
                "A same-size warning/pause capture waited for the held row/search transaction.");

            // New query and two newer captures arrive inside one transaction. Only its latest pending capture
            // and query may run next; a frame's source rows and search literal cannot be mixed mid-transaction.
            search.Text = "keep";
            Publish(host, skipped);
            Call(shell, "RefreshSession");
            Publish(host, latest);
            Call(shell, "OnSessionChanged");
            await PostedAsync(DispatcherPriority.Background);
            Require(ReferenceEquals(Get<DesktopSessionProjection>(shell, "_pendingSessionProjection"), latest)
                && ReferenceEquals(Get<DesktopSessionProjection>(shell, "_applyingSessionProjection"), active),
                "The newest pending capture replaced/mixed the active source transaction.");
            Require(shell.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible
                && shell.FindControl<TextBlock>("ShellActionError")!.Text == warning,
                "A warning waited for the held row/search transaction before becoming visible.");
            Require(pauseButton.IsEnabled && AutomationProperties.GetName(pauseButton) == Texts.T("action.resume")
                && shell.FindControl<TextBlock>("WorkspaceStatus")!.Text == Texts.T("shell.paused"),
                "Pause/status presentation waited for the held row/search transaction.");
            SliceCounts activeCounts = Drain(shell, active);
            Require(shell.UrlList.Count == 385 && shell.UrlList.All(row => row.Snapshot.Title.StartsWith("active ", StringComparison.Ordinal))
                && shell.VisibleThreads.Count == 385,
                "The active frame used a newer source capture or a query received after its start.");
            Require(ReferenceEquals(Get<DesktopSessionProjection>(shell, "_appliedSessionProjection"), active),
                "Applied version did not identify the complete source+search transaction.");
            Call(shell, "RefreshSession");
            SliceCounts latestCounts = Drain(shell, latest);
            Require(shell.UrlList.Count == 479 && shell.BoardsList.Count == 145
                && shell.UrlList.All(row => row.Snapshot.Title.StartsWith("latest ", StringComparison.Ordinal)) && !appliedSkipped,
                "A skipped pending capture was applied or the latest capture was incomplete.");
            Require(shell.VisibleThreads.Count == 240 && shell.VisibleThreads.All(row => row.Title.Contains("keep", StringComparison.Ordinal))
                && shell.VisibleBoards.Count == 73,
                "The actual bounded search pass did not apply the latest literal to the complete source.");
            Require(ReferenceEquals(shell.UrlList.Single(row => row.WatchId == retained.WatchId), retained)
                && ReferenceEquals(shell.UrlList.Single(row => row.WatchId == filtered.WatchId), filtered)
                && !shell.UrlList.Contains(removed)
                && grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], retained)
                && Get<long?>(shell, "_threadLeadId") == retained.WatchId
                && ReferenceEquals(shell.FocusManager?.GetFocusedElement(), focus) && resets == 0,
                "Reconciliation reset rows, lost retained selection/lead/focus, or kept a removed/filtered selection.");
            Require(!GetProperty<bool>(shell, "RowCommandBusy")
                && ReferenceEquals(GetProperty<ThreadRow>(shell, "LeadThread"), retained),
                "The complete frame did not restore the retained command lead.");

            search.Text = string.Empty;
            await PostedAsync(DispatcherPriority.Background);
            Call(shell, "RefreshSession");
            Drain(shell, latest);
            Require(shell.VisibleThreads.Count == 479 && grid.SelectedItems.Count == 1
                && ReferenceEquals(grid.SelectedItems[0], retained),
                "Clearing the real filter resurrected a previously excluded selection.");

            shell.UrlList.CollectionChanged -= collections;
            shell.VisibleThreads.CollectionChanged -= collections;
            shell.BoardsList.CollectionChanged -= collections;
            shell.VisibleBoards.CollectionChanged -= collections;
            foreach (int rows in new[] { 1_000, 10_000 })
            {
                DesktopSessionProjection large = Frame(original, ++version, "large", rows, 32, directory);
                await CheckPostedFrameAsync(shell, host, large, rows, retained, search);
            }
            // Return ticket ownership to the deterministic close interleaving below.
            Set(shell, "_rowSlicePosted", true);

            // Leave a real Background callback queued, then close while a large new frame is partial. The
            // independent gallery task holds actual disposal so cancellation/admission and callback fencing
            // can be observed before true close, without pretending to own a native player.
            DesktopSessionProjection closing = Frame(original, ++version, "closing", 600, 160, directory);
            Publish(host, closing);
            Call(shell, "RefreshSession");
            Require(shell.SessionRowsApplying, "Close fixture did not establish a partial frame.");
            Task rowsApplied = (Task)Call(shell, "AwaitSessionRowsAsync")!;
            int countAtClose = shell.UrlList.Count;
            Set(shell, "_rowSlicePosted", false);
            Call(shell, "PostSessionRowSlice");
            Require(Get<bool>(shell, "_rowSlicePosted"), "Close fixture has no actual queued Background callback.");
            Set(shell, "_galleryCleanupTask", releaseCleanup.Task);
            RestoreHost(host, original, captureWasClosed); restored = true;
            close = (Task)Call(shell, "CloseApplicationAsync")!;
            Require(Get<bool>(shell, "_isClosing") && !close.IsCompleted && rowsApplied.IsCompletedSuccessfully
                && host.State == DesktopSessionState.Stopping,
                "Close waited for source/search completion before accepting shutdown.");
            await PostedAsync(DispatcherPriority.Background);
            Require(!shell.SessionRowsApplying && shell.UrlList.Count == countAtClose && shell.IsVisible,
                "An old row callback mutated the closing window before actual cleanup drain.");
            bool leaseRetained = false;
            try { using ProfileLease unexpected = ProfileLease.Acquire(directory); }
            catch (IOException) { leaseRetained = true; }
            Require(leaseRetained, "Partial-frame close released its profile before actual cleanup drain.");
            bool refused = false;
            try { await host.RemoveThreadAsync(retained.WatchId).WaitAsync(Deadline); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "Partial-frame close did not close real host command admission.");
            releaseCleanup.TrySetResult();
            await close.WaitAsync(Deadline);
            Require(!shell.IsVisible && host.State == DesktopSessionState.Stopped,
                "The real shell did not finish close after its actual drain was released.");
            using (ProfileLease released = ProfileLease.Acquire(directory)) { }
            Console.WriteLine($"SMOKE shell projection reconciliation: PASS; real source/search slices={seedCounts.Calls}/{activeCounts.Calls}/{latestCounts.Calls}; >64 rows, latest pending, fixed-frame query, retained references/selection/lead/focus, no Reset, row-command fence, prompt Pause/warning/status and partial-frame close. Offline private-boundary fixture; no native-player ownership claim.");
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (host != null && original != null && !restored) RestoreHost(host, original, captureWasClosed);
            if (shell != null)
            {
                close ??= (Task)Call(shell, "CloseApplicationAsync")!;
                await close.WaitAsync(Deadline);
            }
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Texts.SetLanguage(language);
            Directory.Delete(directory, recursive: true);
        }
    }

    private readonly record struct SliceCounts(int Calls, bool SourceMultiple, bool SearchMultiple);
    private static SliceCounts Drain(MainWindow shell, DesktopSessionProjection expected)
    {
        int calls = 0;
        bool sourceMultiple = false, searchMultiple = false;
        while (shell.SessionRowsApplying)
        {
            Require(++calls <= 4096, "The real shell row/search pump did not finish within its bounded fixture steps.");
            Require(ReferenceEquals(Get<DesktopSessionProjection>(shell, "_applyingSessionProjection"), expected),
                "A different capture replaced an in-flight source/search frame.");
            sourceMultiple |= Slices(shell, "_threadReconciliation") > 1 || Slices(shell, "_boardReconciliation") > 1;
            searchMultiple |= Slices(shell, "_threadSearchReconciliation") > 1 || Slices(shell, "_boardSearchReconciliation") > 1;
            Call(shell, "ContinueSessionRows");
        }
        Require(ReferenceEquals(Get<DesktopSessionProjection>(shell, "_appliedSessionProjection"), expected),
            "The completed shell frame has the wrong epoch/version.");
        return new(calls, sourceMultiple, searchMultiple);
    }

    private static long Slices(MainWindow shell, string field)
    {
        object? plan = typeof(MainWindow).GetField(field, PrivateInstance)!.GetValue(shell);
        return plan == null ? 0 : (long)plan.GetType().GetProperty("Slices", PrivateInstance)!.GetValue(plan)!;
    }

    private static async Task CheckPostedFrameAsync(MainWindow shell, DesktopSessionHost host,
        DesktopSessionProjection frame, int rows, ThreadRow retained, TextBox search)
    {
        int answersDuringApplication = 0;
        using UiResponsivenessProbe probe = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Input),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), onAnswer: _ =>
            { if (shell.SessionRowsApplying) answersDuringApplication++; });
        probe.Start();
        await Program.WaitUntilAsync(() => probe.Samples.Count != 0);
        Set(shell, "_rowSlicePosted", false);
        Publish(host, frame);
        long started = Stopwatch.GetTimestamp();
        Call(shell, "RefreshSession");
        await Program.WaitUntilAsync(() => !shell.SessionRowsApplying
            && ReferenceEquals(Get<DesktopSessionProjection>(shell, "_appliedSessionProjection"), frame));
        UiResponsivenessSummary summary = probe.Summarize();
        Require(shell.UrlList.Count == rows && shell.VisibleThreads.Count == rows
            && ReferenceEquals(shell.UrlList.Single(row => row.WatchId == retained.WatchId), retained)
            && search.IsFocused, "A large posted shell frame lost rows, row identity or search focus.");
        Require(answersDuringApplication > 0 && summary.HardStalls == 0,
            "A large shell frame did not answer an independent UI ping during actual posted source/search work.");
        Console.WriteLine("SMOKE shell posted rows: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            rows, elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            answersDuringApplication, heartbeat = summary,
            scope = "actual Background row/search callbacks; synthetic immutable projection; managed dispatcher timing"
        }));
    }
    private static long ThreadId(int index) => 100_000L + index;
    private static DesktopSessionProjection Frame(DesktopSessionProjection original, long version, string name,
        int threads, int boards, string directory, bool omitSecond = false) => original with
        { Version = version, View = FrameView(name, threads, boards, directory, omitSecond), IsPaused = false };
    private static DesktopSessionView FrameView(string name, int threads, int boards, string directory, bool omitSecond = false)
    {
        DesktopThreadEntry[] entries = Enumerable.Range(1, threads).Where(index => !omitSecond || index != 2).Select(index =>
            new DesktopThreadEntry(ThreadId(index), new ThreadSnapshot(new("fixture", "board", index.ToString()),
                "https://fixture.invalid/board/thread/" + index, name + (index % 2 == 1 ? " keep " : " drop ") + index,
                Path.Combine(directory, "media", index.ToString()), ThreadActivity.Pending, ThreadSourceState.Active, null,
                100, name == "seed" ? 0 : 10, NoFailures, null, new DownloadCounts(0, 0)), "Offline fixture", "/board/")).ToArray();
        DesktopBoardEntry[] boardEntries = Enumerable.Range(1, boards).Select(index =>
            new DesktopBoardEntry(200_000L + index, new BoardSnapshot(new("fixture", "board" + index),
                "https://fixture.invalid/board" + index + "/", name + (index % 2 == 1 ? " keep " : " drop ") + index,
                Path.Combine(directory, "media", "board" + index), null, null), "Offline fixture", "board" + index)).ToArray();
        return new(Array.AsReadOnly(entries), Array.AsReadOnly(boardEntries), Array.AsReadOnly(Array.Empty<string>()),
            DesktopSessionState.Ready, null);
    }
    private static void Publish(DesktopSessionHost host, DesktopSessionProjection frame) => Set(host, "_published", frame);
    private static void RestoreHost(DesktopSessionHost host, DesktopSessionProjection original, bool captureClosed)
    {
        Publish(host, original);
        Set(host, "_captureAdmissionClosed", captureClosed);
    }
    private static Task PostedAsync(DispatcherPriority priority)
    {
        TaskCompletionSource posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => posted.TrySetResult(), priority);
        return posted.Task.WaitAsync(Deadline);
    }
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target)!;
    private static T GetProperty<T>(object target, string name) => (T)(target.GetType().GetProperty(name, PrivateInstance)
        ?? throw new MissingMemberException(target.GetType().FullName, name)).GetValue(target)!;
    private static void Set(object target, string name, object? value) => (target.GetType().GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);
    private static object? Call(object target, string name, params object?[] arguments) => (target.GetType().GetMethod(name, PrivateInstance)
        ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, arguments);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
