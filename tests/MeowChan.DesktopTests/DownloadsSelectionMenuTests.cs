using System.Reflection;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Diagnostics;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Semi.Avalonia.DataGrid;
using static MeowChan.DesktopTests.DownloadWindowFixture;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadsSelectionMenuTests
{
    private static readonly string[] CheckCommands = ["watch.check", "watch.retry-media"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothGesturesShowTheExecutableSubsetOfTheSelection(bool keyboard) => RunWithGrid(fixture =>
    {
        foreach ((int eligible, int selected) in new[] { (0, 9), (1, 9), (9, 9), (1, 1) })
        {
            long[] ids = Enumerable.Range(1, selected).Select(id => (long)id).ToArray();
            fixture.ShowThreads(ids.Select(id => Thread(id, status: id <= eligible
                ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), ids.Take(eligible).ToArray());
            fixture.Select(ids);
            ContextMenu menu = OpenMenu(fixture, keyboard);
            foreach (string command in CheckCommands)
            {
                MenuItem item = FindItem(menu, command);
                string label = CommandCatalog.Label(command);
                if (selected > 1) label += $" ({eligible} of {selected})";
                Assert.Equal(label, item.Header);
                Assert.Equal(label, AutomationProperties.GetName(item));
                Assert.Equal(eligible > 0, item.Command!.CanExecute(null));
                if (eligible == 0) Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(item)));
            }
            menu.Close();
            fixture.Layout();
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ActivationSkipsRemovedAndNewlyIneligibleTargetsAndNeverAddsNewOnes(bool keyboard, bool retry) => RunWithGrid(async fixture =>
    {
        long[] ids = Enumerable.Range(1, 10).Select(id => (long)id).ToArray();
        fixture.ShowThreads(ids.Select(id => Thread(id, status: id <= 3
            ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), 1, 2, 3);
        fixture.Select(ids[..9]);
        MenuItem action = FindItem(OpenMenu(fixture, keyboard), retry ? "watch.retry-media" : "watch.check");
        Assert.EndsWith("(3 of 9)", (string)action.Header!);

        // Remove the menu lead, revoke another target's eligibility, and make two other rows
        // eligible, including one that was outside the selection when the menu opened.
        fixture.ShowThreads(ids[1..].Select(id => Thread(id, status: id is 2 or 4 or 10
            ? MediaWorkStatus.NeedsIntervention : MediaWorkStatus.Excluded)).ToArray(), 2, 4, 10);
        fixture.Select(ids[1..]);

        using PerformanceDiagnostics.DiagnosticRun trace = PerformanceDiagnostics.BeginRun("downloads-menu", "fixture", "offline-rows");
        Assert.True(action.Command!.CanExecute(null));
        action.Command.Execute(null);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (fixture.WindowField<bool>("_selectionCommandRunning")) await Task.Delay(10, deadline.Token);
        string?[] accepted = trace.Snapshot().Events
            .Where(entry => entry.Phase == "host.command.admission" && entry.Outcome == "accepted")
            .Select(entry => entry.Watch).ToArray();
        string expected = retry ? "2" : new DiagnosticTarget(DiagnosticTargetKind.Thread, 2).ToString();
        Assert.Equal(new[] { expected }, accepted);
    });

    [Fact]
    public void TheOpenMenuRelocalizesTheFrozenCount() => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1), Thread(2)], 1);
        fixture.Select(1, 2);
        ContextMenu menu = OpenMenu(fixture, keyboard: true);
        MenuItem check = FindItem(menu, "watch.check");
        Assert.Equal("Check now (1 of 2)", check.Header);
        Texts.SetLanguage("de");
        fixture.Layout();
        Assert.Equal("Jetzt prüfen (1 von 2)", check.Header);
        menu.Close();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void DeleteAfterBatchExportRemovesTheOriginalSelectionAcrossProjections(ShortcutPlatform platform, KeyModifiers primary)
        => BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable testPlatform = BooruReviewTestPlatform.Enter();
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1,
                WorkspaceMode = "Advanced", Language = "en", WindowWidth = 1240, WindowHeight = 900,
                DownloadsPaused = true, SaveUrlsOnExit = false
            }.SaveConfig(profile.Path);
            string[] urls = Enumerable.Range(1000, 5).Select(WatchUrl).ToArray();
            string[] files = await SeedArchiveAsync(profile.Path, urls);
            using HttpClient client = new(new OfflineTransport());
            await using DesktopSessionHost host = new(profile.Path, [new FourChanImageBoard(client)],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(host) { Classes = { MotionPolicy.OffClass } };
            DownloadWindowFixture fixture = new(window, host);
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask.WaitAsync(Deadline);
            Assert.True(host.IsPaused);

            List<long> added = [];
            foreach (string url in urls)
                added.Add(Assert.IsType<long>((await host.AddAsync(url).WaitAsync(Deadline)).WatchId));
            RefreshRows(fixture);
            Assert.Equal(added.Order(), window.VisibleThreads.Select(row => row.WatchId).Order());
            typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, EffectiveShortcutBindings.Build(platform: platform));

            DataGrid grid = FindByAutomationId<DataGrid>(window, "ThreadsDataGrid");
            Assert.True(grid.Focus());
            Assert.True(Press(grid, Key.A, primary));
            Assert.Equal(added.Order(), window.SelectedThreadsForTests().Select(row => row.WatchId).Order());
            Assert.True(Press(grid, Key.E, primary));
            Assert.Equal(MainWindow.WorkspacePage.Archive, window.SelectedPage);
            Button close = FindByAutomationId<Button>(window.ArchivePage, "ArchiveExportClose");
            await BooruTestSupport.WaitAsync(() => close.IsEffectivelyVisible && close.IsEnabled, "export close button");
            TextBlock summary = FindByAutomationId<TextBlock>(window.ArchivePage, "ArchiveExportSummary");
            Assert.Equal(urls.Length + 1, summary.Text!.Split('\n').Length);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BooruTestSupport.WaitAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads
                && !window.SessionRowsApplying && ReferenceEquals(grid, window.FocusManager!.GetFocusedElement()),
                "restored Downloads grid focus and completed rows");
            long[] frozen = window.SelectedThreadsForTests().Select(row => row.WatchId).ToArray();
            Assert.Equal(added.Order(), frozen.Order());

            // The normal capture worker keeps publishing and executing host commands. Drive only the
            // shell's application of those frames, with the Delete continuation held between steps.
            fixture.WindowField<CoalescedUiUpdate>("_sessionRefresh").Dispose();
            using PerformanceDiagnostics.DiagnosticRun trace = PerformanceDiagnostics.BeginRun("downloads-delete", "fixture", "paused-watches");
            long sixth = await DeleteAcrossFramesAsync(fixture, grid, frozen);
            await BooruTestSupport.WaitAsync(() => !fixture.WindowField<bool>("_selectionCommandRunning"), "selection command completion");
            DesktopSessionProjection final = await host.CaptureProjectionAsync().WaitAsync(Deadline);
            RefreshRows(fixture);

            PerformanceEvent[] events = trace.Snapshot().Events;
            string CommandsFor(long id)
            {
                PerformanceEvent[] admissions = events.Where(entry => entry.Phase == "host.command.admission"
                    && entry.Watch == id.ToString()).ToArray();
                return string.Join(",", admissions.Select(entry => entry.Outcome + "/" + string.Join(",",
                    events.Where(end => end.Request == entry.Request && end.Phase == "host.command" && end.Outcome == "end")
                        .Select(end => end.Outcome))));
            }
            string Describe(IEnumerable<long> session, IEnumerable<long> visible, Func<long, string> commands) =>
                $"Frozen [{string.Join(",", frozen)}], host admission/completion [{string.Join(", ", frozen.Append(sixth)
                    .Select(id => $"{id}={commands(id)}"))}], session [{string.Join(",", session.Order())}], visible [{string.Join(",", visible.Order())}]";
            Assert.Equal(Describe([sixth], [sixth], id => id == sixth ? "" : "accepted/end"),
                Describe(final.View.Threads.Select(row => row.WatchId), grid.ItemsSource.Cast<ThreadRow>().Select(row => row.WatchId), CommandsFor));

            await using (ArchiveReader reader = await host.OpenArchiveReaderAsync(CancellationToken.None))
                Assert.Equal(urls.Length, (await reader.SelectThreadIdsAsync(new ArchiveThreadQuery(), CancellationToken.None)).Count);
            Assert.All(files, file => Assert.Equal("saved media", File.ReadAllText(file)));

            FeedbackToastContent undo = Assert.Single(window.GetVisualDescendants().OfType<FeedbackToastContent>(),
                toast => toast.ActionButton is { } action && AutomationProperties.GetName(action) == Texts.T("watch.undo"));
            Assert.Equal(Texts.T("watch.removed", urls.Length), undo.Message);
            await undo.RunActionAsync().WaitAsync(Deadline);
            DesktopSessionProjection restored = await host.CaptureProjectionAsync().WaitAsync(Deadline);
            Assert.Equal(urls.Order(), trace.Snapshot().Events.Where(entry => entry.Sequence > events[^1].Sequence
                && entry.Phase == "host.command.admission" && entry.Outcome == "accepted").Select(entry => entry.Watch!).Order());
            Assert.Equal(urls.Append(WatchUrl(1005)).Order(), restored.View.Threads.Select(row => row.Snapshot.Url).Order());
            Assert.Equal(sixth, Assert.Single(restored.View.Threads, row => row.Snapshot.Url == WatchUrl(1005)).WatchId);
        });

    private static async Task<long> DeleteAcrossFramesAsync(DownloadWindowFixture fixture, DataGrid grid, long[] frozen)
    {
        DesktopSessionHost host = fixture.Window.SessionHost;
        var commands = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        using SelectionContinuations continuations = new();
        Assert.True(await commands.WaitAsync(Deadline));
        try { continuations.Run(() => Assert.True(Press(grid, Key.Delete, KeyModifiers.None))); }
        finally { commands.Release(); }

        Action firstRemoval = await continuations.NextAsync();
        DesktopSessionProjection firstFrame = host.Projection;
        Assert.Equal(frozen[1..].Order(), firstFrame.View.Threads.Select(row => row.WatchId).Order());
        Assert.True(await commands.WaitAsync(Deadline));
        try
        {
            continuations.Run(firstRemoval);
            BeginRows(fixture, firstFrame);
        }
        finally { commands.Release(); }

        continuations.Run(await continuations.NextAsync());
        Assert.Equal(frozen[2..].Order(), host.Projection.View.Threads.Select(row => row.WatchId).Order());
        Task firstApplied = fixture.WindowField<TaskCompletionSource>("_sessionRowsApplied").Task;
        Assert.False(firstApplied.IsCompleted);
        long sixth = Assert.IsType<long>((await host.AddAsync(WatchUrl(1005)).WaitAsync(Deadline)).WatchId);
        DesktopSessionProjection secondFrame = host.Projection;
        FinishRows(fixture);
        Assert.True(firstApplied.IsCompletedSuccessfully);
        BeginRows(fixture, secondFrame);
        Assert.True(fixture.Window.SessionRowsApplying);
        continuations.Run(await continuations.NextAsync());
        FinishRows(fixture);
        return sixth;
    }

    private static void BeginRows(DownloadWindowFixture fixture, DesktopSessionProjection projection) =>
        typeof(MainWindow).GetMethod("BeginSessionRows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Window, [projection]);

    private static void FinishRows(DownloadWindowFixture fixture)
    {
        fixture.CallWindow("ContinueSessionRows");
        fixture.Layout();
        Assert.False(fixture.Window.SessionRowsApplying);
    }

    private static void RefreshRows(DownloadWindowFixture fixture)
    {
        fixture.CallWindow("RefreshSession");
        fixture.Layout();
        Assert.False(fixture.Window.SessionRowsApplying);
    }

    private static async Task<string[]> SeedArchiveAsync(string profile, string[] urls)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        List<string> files = [];
        foreach (string url in urls)
        {
            string thread = url.Split('/')[^1];
            string directory = Path.Combine(profile, "Downloads", thread);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "reader.png");
            await File.WriteAllTextAsync(file, "saved media");
            files.Add(file);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(new ResourceId("4chan", "g", thread), url,
                "Selection fixture " + thread, ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory,
                [new CapturedPost(thread, 0, PostFieldPresence.All, Html: "Saved post")]));
        }
        return files.ToArray();
    }

    private static string WatchUrl(int thread) => "https://boards.4chan.org/g/thread/" + thread;

    private static T FindByAutomationId<T>(Control root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(),
            control => ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() == id);

    private static bool Press(DataGrid grid, Key key, KeyModifiers modifiers)
    {
        KeyEventArgs input = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        grid.RaiseEvent(input);
        return input.Handled;
    }

    private sealed class SelectionContinuations : SynchronizationContext, IDisposable
    {
        private readonly Channel<Action> _pending = Channel.CreateUnbounded<Action>();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            void Resume() => Run(() => callback(state));
            if (!_pending.Writer.TryWrite(Resume)) Dispatcher.UIThread.Post(Resume);
        }

        internal void Run(Action action)
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { action(); }
            finally { SetSynchronizationContext(previous); }
        }

        internal Task<Action> NextAsync() => _pending.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        public void Dispose()
        {
            _pending.Writer.TryComplete();
            while (_pending.Reader.TryRead(out Action? resume)) Dispatcher.UIThread.Post(resume);
        }
    }

    private sealed class OfflineTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Paused selection fixture requested network access.");
    }

    private static void RunWithGrid(Func<DownloadWindowFixture, Task> exercise) =>
        Run(exercise, () => Application.Current!.Styles.Insert(1, new DataGridSemiTheme()));

    private static ContextMenu OpenMenu(DownloadWindowFixture fixture, bool keyboard)
    {
        MainWindow window = fixture.Window;
        DataGrid grid = fixture.Threads;
        Assert.True(grid.Focus());
        if (keyboard)
        {
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        }
        else
        {
            object item = grid.SelectedItems[0]!;
            grid.ScrollIntoView(item, null);
            fixture.Layout();
            DataGridRow row = Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>(),
                candidate => ReferenceEquals(candidate.DataContext, item) && candidate.IsEffectivelyVisible && candidate.Bounds.Height > 0);
            Point point = row.TranslatePoint(new Point(20, row.Bounds.Height / 2), window)!.Value;
            Visual? hit = window.InputHitTest(point) as Visual;
            Assert.Same(row, hit?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault());
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
        }
        fixture.Layout();
        ContextMenu menu = grid.ContextMenu!;
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem FindItem(ContextMenu menu, string command) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, command));
}
