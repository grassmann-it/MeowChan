using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// Standalone, opt-in entry so this disjoint fixture needs no Program/project/selector edits.
// Build the normal smoke project first; then rebuild ONLY that project with --no-restore
// -p:BuildProjectReferences=false -p:DefineConstants=DOWNLOAD_SEARCH_STARTUP_TRUTH
// -p:StartupObject=MeowChan.DesktopSmoke.DownloadSearchStartupTruthChecks .
// Run the resulting assembly with scripts/run-desktop-smoke.py --mode normal (private Xvfb).
internal static class DownloadSearchStartupTruthChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string CachedTitle = "Ältere Sitzung — truncated…";
    private static string Cache(string profile) => Path.Combine(profile, "startup-preview.json");

#if DOWNLOAD_SEARCH_STARTUP_TRUTH
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 0 || !OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            || Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE") is not { Length: > 0 } profile)
            throw new InvalidOperationException("CX03 fixture requires the disposable private-Xvfb smoke runner.");
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        Seed(profile, "truncated", live: true);
        int result = 1;
        Console.WriteLine("SMOKE SCENARIO: normal");
        int exit = AppBuilder.Configure<StartupPreviewSmokeApp>().UsePlatformDetect().WithInterFont()
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                try
                {
                    await RunAsync((MainWindow)desktop.MainWindow!);
                    result = 0;
                    Console.WriteLine("SMOKE PASS: CX03 Downloads startup truth; offline, private Xvfb, programmatic focus/input only.");
                }
                catch (Exception error) { Console.Error.WriteLine("SMOKE FAIL: CX03 " + error); }
                finally
                {
                    var app = (StartupPreviewSmokeApp)Application.Current!;
                    app.Barrier.Release.TrySetResult(); app.ReadableBarrier.Release.TrySetResult();
                    if (desktop.MainWindow is MainWindow main) await CloseAsync(main);
                    desktop.Shutdown(result);
                }
            }, DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return result == 0 ? exit : result;
    }
#endif

    private static void Seed(string profile, string cache, bool live = false)
    {
        Directory.CreateDirectory(profile);
        Require(!File.Exists(Path.Combine(profile, "settings.json")) && !File.Exists(Path.Combine(profile, "saved_urls.json")),
            "Refusing to seed an existing profile: " + profile);
        Config config = new()
        {
            DownloadPath = Path.Combine(profile, "Downloads"), DownloadsPaused = true, SaveUrlsOnExit = true,
            CloseToTray = false, MinimizeToTray = false, MonitorClipboardUrls = false,
            FirstRunSetupPending = false, FirstRunSetupVersion = 1, AutomaticUpdateChecks = false,
            Language = "en", WorkspaceMode = "Simple"
        };
        config.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        new SessionStore(profile, lease).SaveAsync(new SessionSnapshot
        {
            ThreadUrls = live ? ["https://boards.4chan.org/b/thread/710001", "https://boards.4chan.org/g/thread/710002"] : [],
            BoardUrls = live ? ["https://boards.4chan.org/g/"] : []
        }).GetAwaiter().GetResult();
        if (cache == "missing") return;
        if (cache == "malformed") { File.WriteAllText(Cache(profile), "{bad"); return; }
        StartupPreviewRow thread = new(false, "Cached source", "b", CachedTitle, 25, 12,
            "https://offline.invalid/old/thread/99", "99");
        StartupPreviewRow board = new(true, "Cached source", "old", "Previous board", 0, 0);
        StartupPreviewRow[] rows = cache switch { "empty" => [], "board-only" => [board], _ => [thread, board] };
        new StartupPreviewStore(profile, lease).Save(new(DateTimeOffset.UtcNow.AddDays(-3), 400, 12, rows));
    }

    internal static async Task RunAsync(MainWindow main)
    {
        var app = (StartupPreviewSmokeApp)Application.Current!;
        await HeldAsync(main, app.Barrier, app.Store, historical: 1);
        await CrossReadyAsync(main, app.Barrier, app.ReadableBarrier, "710001", live: true);
        await LiveAsync(main);
        string root = main.SessionHost.ProfileDirectory;
        foreach (string cache in new[] { "missing", "malformed", "empty", "board-only" })
        {
            string profile = Path.Combine(root, "cases", cache);
            Seed(profile, cache);
            StartupPreviewBarrier barrier = new(), readable = new();
            DesktopSessionHost host = app.CreateHost(profile, barrier, false, out var store);
            host.AfterCatalogReadableForTests = readable.HoldAsync;
            await using MainWindow child = new(host);
            try
            {
                child.Show(main);
                await HeldAsync(child, barrier, store, historical: 0);
                await CrossReadyAsync(child, barrier, readable, "Ä", live: false);
            }
            finally
            {
                barrier.Release.TrySetResult(); readable.Release.TrySetResult();
                await CloseAsync(child);
                await child.DisposeAsync();
            }
            Console.WriteLine("CX03 cache " + cache + " PASS: awaiting both lists, retained query/focus, authoritative zero only at Ready.");
        }
        await TerminalAsync(main, app, fail: true);
        await TerminalAsync(main, app, fail: false);
        Require(app.Transport.Requests == 0, "A fixture attempted HTTP.");
        Console.WriteLine("CX03 transport requests=0; no provider, clipboard, export or historical command target.");
    }

    private static async Task HeldAsync(MainWindow window, StartupPreviewBarrier barrier,
        StartupPreviewObservedStore store, int historical)
    {
        await barrier.Entered.Task.WaitAsync(Deadline);
        byte[]? cacheBefore = File.Exists(Cache(window.SessionHost.ProfileDirectory))
            ? File.ReadAllBytes(Cache(window.SessionHost.ProfileDirectory)) : null;
        await AppliedAsync(window);
        window.Activate();
        Require(window.SessionHost.State == DesktopSessionState.Starting && store.Loads == 0, "Startup hold escaped.");
        Require(window.HistoricThreads.Count == historical && window.UrlList.Count == 0 && window.BoardsList.Count == 0,
            "Cache changed authoritative membership.");
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            await AppliedAsync(window);
            foreach (string query in new[] { CachedTitle, "no-local-result", "Ä", "a", "  " })
            {
                window.FocusDownloadSearch();
                C<TextBox>(window, "DownloadSearchInput").Text = query;
                await AppliedAsync(window);
                foreach (int tab in new[] { 0, 1 })
                {
                    C<TabControl>(window, "DownloadsTabs").SelectedIndex = tab;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    AssertPending(window);
                }
                Require(window.HistoricThreads.Count == historical, "Literal query filtered the incomplete cache.");
            }
        }
        Texts.SetLanguage("en");
        C<TabControl>(window, "DownloadsTabs").SelectedIndex = 0;
        window.FocusDownloadSearch();
        C<TextBox>(window, "DownloadSearchInput").Text = "no-local-result";
        await AppliedAsync(window);
        C<Button>(window, "DownloadSearchClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(C<TextBlock>(window, "DownloadSearchSummary").Text!.Contains("no-local-result", StringComparison.Ordinal),
            "Collapsed pending query lost its text.");
        if (historical > 0)
        {
            C<DataGrid>(window, "ThreadsDataGrid").SelectedIndex = 0;
            Require(window.SelectedThreadsForTests().Length == 0, "Historical row became a command target.");
        }
        Require(!C<Button>(window, "AddUrlButton").IsEnabled, "Pending cache enabled Add.");
        Require(!C<Button>(window, "CopyAllUrlsButton").IsEnabled, "Pending cache enabled Copy All.");
        // Expander content can be detached from the visual tree; its leaf IsEnabled/visibility
        // does not describe command availability. Assert the real selection surface and target.
        Require(!C<StackPanel>(window, "InspectorSelection").IsVisible, "Cache exposed an inspector selection.");
        Require(typeof(MainWindow).GetMethod("GetInspectorUrl", Private)!.Invoke(window, null) == null,
            "Historical display acquired an inspector URL target.");
        DesktopSessionHost host = window.SessionHost;
        Require(!host.CanRequestCheck(new(DiagnosticTargetKind.Thread, -1)) && !host.CanRetryMissingMedia(-1),
            "Historical identity acquired command eligibility.");
        foreach (Func<Task> command in new Func<Task>[]
        {
            () => host.RequestCheckAsync(new(DiagnosticTargetKind.Thread, -1)),
            () => host.RemoveThreadAsync(-1), () => host.RemoveBoardAsync(-1), () => host.RetryMissingMediaAsync(-1)
        })
        {
            bool rejected = false;
            try { await command().WaitAsync(Deadline); }
            catch (InvalidOperationException error) when (error is not DesktopSessionBusyException) { rejected = true; }
            Require(rejected, "Non-Ready historical command passed host admission.");
        }
        Require(host.AcceptedCommandCount == 0, "Rejected historical commands retained admission.");
        if (cacheBefore != null)
            Require(cacheBefore.SequenceEqual(File.ReadAllBytes(Cache(host.ProfileDirectory))), "Query rewrote the display cache.");
        Require(store.Loads == 0 && !barrier.Release.Task.IsCompleted, "Query advanced session restore.");
        Console.WriteLine("CX03 held PASS: EN/DE/ES/FR/RU, both lists, matching/nonmatching/Unicode/short/whitespace; no totals/zero claims, historical command admission refused.");
    }

    private static void AssertPending(MainWindow window, string? terminalKey = null)
    {
        string expected = terminalKey != null ? Texts.T(terminalKey)
            : C<Border>(window, "StartupPreviewPanel").IsVisible
                ? Texts.T("shell.startupPreview") + " · " + Texts.T("shell.starting") : Texts.T("shell.starting");
        TextBlock summary = C<TextBlock>(window, "DownloadSearchSummary");
        Require(summary.Text == expected, "Pending search claims a result: " + summary.Text);
        Require(AutomationProperties.GetName(summary) == expected, "Automation summary disagrees with pending search.");
        Require(C<TextBlock>(window, "WorkspaceCounts").Text == expected, "Pending session claims complete totals.");
        Require(Equals(C<TabItem>(window, "ThreadsTab").Header, Texts.T("downloads.threads"))
            && Equals(C<TabItem>(window, "BoardsTab").Header, Texts.T("downloads.boards")), "Pending tab claims a count.");
        foreach (string name in new[] { "ThreadsEmptyState", "BoardsEmptyState" })
            Require(C<TextBlock>(window, name).Text == expected, "Pending empty/no-match claim: " + name);
        Require(C<TextBlock>(window, "BoardsEmptyState").IsVisible, "Missing historical board grid lacks an awaiting state.");
        Require(C<TextBlock>(window, "ThreadsEmptyState").IsVisible == (window.HistoricThreads.Count == 0),
            "Missing historical thread grid lacks an awaiting state.");
    }

    private static async Task CrossReadyAsync(MainWindow window, StartupPreviewBarrier barrier,
        StartupPreviewBarrier readable, string query, bool live)
    {
        C<TabControl>(window, "DownloadsTabs").SelectedIndex = 0;
        window.FocusDownloadSearch();
        TextBox input = C<TextBox>(window, "DownloadSearchInput");
        input.Text = query;
        await AppliedAsync(window);
        input.Focus(); input.SelectionStart = 0; input.SelectionEnd = 1;
        await Program.WaitUntilAsync(() => input.IsKeyboardFocusWithin);
        barrier.Release.TrySetResult();
        await readable.Entered.Task.WaitAsync(Deadline);
        await AppliedAsync(window);
        AssertPending(window);
        Require(input.Text == query && input.IsKeyboardFocusWithin, "Catalog milestone changed query/focus.");
        readable.Release.TrySetResult();
        await Read<Task>(window, "_restoreTask").WaitAsync(Deadline);
        await AppliedAsync(window);
        Require(window.SessionHost.State == DesktopSessionState.Ready && C<Button>(window, "AddUrlButton").IsEnabled,
            "Authoritative Ready was not reached: " + window.SessionHost.StartupError);
        Require(input.Text == query && input.IsKeyboardFocusWithin && input.SelectionStart == 0 && input.SelectionEnd == 1,
            "Ready changed query, input selection or focus.");
        Require(window.HistoricThreads.Count == 0 && !C<Border>(window, "StartupPreviewPanel").IsVisible
            && ReferenceEquals(C<DataGrid>(window, "ThreadsDataGrid").ItemsSource, window.VisibleThreads), "Historical grid did not retire.");
        Require(window.VisibleThreads.Count == (live ? 1 : 0) && window.VisibleBoards.Count == 0,
            "Ready did not apply the retained literal to real rows.");
        Require(C<TextBlock>(window, "DownloadSearchSummary").Text == Texts.Count("downloads.searchCount", live ? 1 : 0),
            "Ready search count is not authoritative.");
        if (live) Require(window.VisibleThreads[0].ThreadId == "710001" && window.SelectedThreadsForTests().Length == 0,
            "Historical selection leaked into restored rows.");
        else Require(C<TextBlock>(window, "ThreadsEmptyState").Text == Texts.T("downloads.searchEmpty"),
            "True Ready zero lacks no-match presentation.");
        Console.WriteLine("CX03 Ready PASS: retained query/focus/input selection, historical retirement; actual matching threads=" + window.VisibleThreads.Count);
    }

    private static async Task LiveAsync(MainWindow window)
    {
        TextBox input = C<TextBox>(window, "DownloadSearchInput");
        input.Text = "";
        await AppliedAsync(window);
        await using SyntheticShellRows rows = await SyntheticShellRows.CreateAsync(window,
            [SnapshotFixtures.Thread(920001, "920001", "Äpfel Alpha"), SnapshotFixtures.Thread(920002, "920002", "Beta")],
            [SnapshotFixtures.Board(920003, "ä")]);
        DataGrid grid = C<DataGrid>(window, "ThreadsDataGrid");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(rows.Threads[0]); grid.SelectedItems.Add(rows.Threads[1]);
        // New query arrives inside an actual multi-callback reconciliation, not an injected result set.
        bool changedDuringSlice = false;
        void ChangeQuery(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (changedDuringSlice || !window.SessionRowsApplying) return;
            changedDuringSlice = true;
            Require(window.SelectedThreadsForTests().Length == 0, "Partial frame exposed command selection.");
            input.Text = "ä";
        }
        window.VisibleThreads.CollectionChanged += ChangeQuery;
        try { input.Text = "Beta"; await rows.WaitForAppliedAsync(); await AppliedAsync(window); }
        finally { window.VisibleThreads.CollectionChanged -= ChangeQuery; }
        Console.WriteLine("CX03 stale-slice observation: changed=" + changedDuringSlice + "; query=" + input.Text
            + "; shown=" + string.Join(",", window.VisibleThreads.Select(row => row.ThreadId)));
        Require(changedDuringSlice && input.Text == "ä" && window.VisibleThreads.Count == 1
            && ReferenceEquals(window.VisibleThreads[0], rows.Threads[0]), "Stale query slice won over Unicode literal.");
        Require(window.SelectedThreadsForTests().All(row => ReferenceEquals(row, rows.Threads[0])), "Hidden target survived reconciliation.");
        input.Text = "   "; await rows.WaitForAppliedAsync(); await AppliedAsync(window);
        Require(!grid.SelectedItems.Contains(rows.Threads[1]), "Clearing restored an excluded selection.");
        input.Text = "ä"; await rows.WaitForAppliedAsync(); await AppliedAsync(window);
        C<TabControl>(window, "DownloadsTabs").SelectedIndex = 1;
        Require(window.VisibleResultCount == 1 && window.VisibleBoards[0].BoardCode == "ä", "Board literal/count diverged.");
        input.Text = "cat:ä"; await rows.WaitForAppliedAsync(); await AppliedAsync(window);
        Require(window.VisibleResultCount == 0 && window.VisibleThreads.Count == 0, "A literal acquired query-DSL semantics.");
        input.Text = "a"; input.Text = "no-local-result"; input.Text = "Ä";
        await rows.WaitForAppliedAsync(); await AppliedAsync(window);
        Require(window.VisibleThreads.Count == 1 && window.VisibleBoards.Count == 1, "Latest short query did not win.");
        C<TabControl>(window, "DownloadsTabs").SelectedIndex = 0;
        input.Text = ""; await rows.WaitForAppliedAsync(); await AppliedAsync(window);
        Console.WriteLine("CX03 live PASS: Unicode/short/whitespace/literal, stale query slice, active-list count and excluded-selection non-resurrection.");
    }

    private static async Task TerminalAsync(MainWindow owner, StartupPreviewSmokeApp app, bool fail)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "cases", fail ? "failed" : "stopping");
        Seed(profile, "truncated");
        StartupPreviewBarrier barrier = new() { HoldCancellationDrain = !fail };
        DesktopSessionHost host = app.CreateHost(profile, barrier, fail, out var store);
        await using MainWindow child = new(host);
        try
        {
            child.Show(owner);
            await HeldAsync(child, barrier, store, historical: 1);
            child.FocusDownloadSearch();
            if (fail)
            {
                barrier.Release.TrySetResult();
                await Read<Task>(child, "_restoreTask").WaitAsync(Deadline);
                await AppliedAsync(child);
                AssertPending(child, "shell.recoveryFailed");
            }
            else
            {
                child.Close();
                await barrier.Cancelled.Task.WaitAsync(Deadline);
                Require(host.State == DesktopSessionState.Stopping, "Stop hold escaped.");
                AssertPending(child, "shell.stopping");
            }
        }
        finally
        {
            barrier.Release.TrySetResult(); barrier.CancellationDrain.TrySetResult();
            await CloseAsync(child);
            await child.DisposeAsync();
        }
        Console.WriteLine("CX03 terminal " + (fail ? "failure" : "stop") + " PASS: no empty-success claim; real close/profile release.");
    }

    private static async Task AppliedAsync(MainWindow window)
    {
        // Avalonia queues TextChanged. Do not compare revision counters before the edit event
        // has even reached the shell (which would falsely accept the preceding applied query).
        TaskCompletionSource pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => pulse.TrySetResult(), DispatcherPriority.Background);
        await pulse.Task.WaitAsync(Deadline);
        await Program.WaitUntilAsync(() =>
        {
            var applied = Read<DesktopSessionProjection?>(window, "_appliedSessionProjection");
            var current = window.SessionHost.Projection;
            return !window.SessionRowsApplying && !Read<bool>(window, "_rowSlicePosted")
                && applied?.Epoch == current.Epoch && applied.Version == current.Version
                && Read<long>(window, "_appliedDownloadSearchRevision") == Read<long>(window, "_downloadSearchQueryRevision");
        });
    }

    private static async Task CloseAsync(MainWindow window)
    {
        if (!window.SessionHost.IsProfileReleased) window.Close();
        await Program.WaitUntilAsync(() => window.SessionHost.State == DesktopSessionState.Stopped
            && window.SessionHost.IsProfileReleased && !Read<bool>(window, "_shutdownRunning"));
        if (window.IsVisible) window.Close(); // real acknowledgement of failed-start preference refusal
        await Program.WaitUntilAsync(() => !window.IsVisible);
    }

    private static T Read<T>(object owner, string field) => (T)(owner.GetType().GetField(field, Private)
        ?? throw new InvalidOperationException("Missing fixture observation: " + field)).GetValue(owner)!;
    private static T C<T>(MainWindow window, string name) where T : Control => window.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing control: " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
