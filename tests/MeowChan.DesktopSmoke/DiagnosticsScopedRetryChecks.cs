using Pointer = Avalonia.Input.Pointer;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Held callbacks and synthetic control/menu unit checks, plus an explicitly native
// row/blank pointer-and-focus check. No network or personal desktop input.
internal static class DiagnosticsScopedRetryChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static async Task RunAsync(MainWindow window)
    {
        ContentControl host = window.DiagnosticsPageHost;
        object? previous = host.Content;
        string language = Texts.Language;
        MainWindow.WorkspacePage previousPage = window.SelectedPage;
        DiagnosticsPage? page = null;
        DesktopThreadEntry Thread(long watch, string id, string source = "fixture")
        {
            DesktopThreadEntry entry = SnapshotFixtures.Thread(watch, id, "Identical displayed title");
            return entry with { Snapshot = entry.Snapshot with { Id = new(source, "b", id),
                LastCheckFailure = new FailureInfo(FailureKind.Network, "Identical failure") } };
        }
        DesktopThreadEntry first = Thread(98101, "1"), second = Thread(98102, "2"), later = Thread(98103, "3");
        DesktopSessionView View(params DesktopThreadEntry[] threads) => new(threads, Array.Empty<DesktopBoardEntry>(),
            Array.Empty<string>(), DesktopSessionState.Ready, null);
        List<DiagnosticTarget> calls = new();
        string? copied = null;
        TaskCompletionSource<CheckRequestStatus>? pending = null;
        bool admissible = true;
        DiagnosticsPage Create()
        {
            var result = new DiagnosticsPage(_ => false, text => { copied = text; return Task.CompletedTask; }, target =>
            {
                calls.Add(target);
                return pending?.Task ?? Task.FromResult(CheckRequestStatus.Scheduled);
            }, _ => admissible);
            host.Content = result;
            return result;
        }
        try
        {
            window.ShowStatus(StatusSegment.Diagnostics);
            page = Create();
            page.Refresh(View(first, second), true);
            DataGrid grid = page.FindControl<DataGrid>("DiagnosticsGrid")!;
            await Program.WaitUntilAsync(() => { window.UpdateLayout(); return grid.Bounds.Height > 40; });
            DiagnosticEntry[] entries = Entries(grid);
            // Synthetic Avalonia unit checks against realized cells (not native acceptance).
            await PressRowAsync(window, grid, entries[0], KeyModifiers.None, false);
            await PressRowAsync(window, grid, entries[1], OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control, false);
            Require(grid.SelectedItems.Count == 2, "Ctrl-click did not extend diagnostic selection.");
            await PressRowAsync(window, grid, entries[0], KeyModifiers.None, false);
            await PressRowAsync(window, grid, entries[1], KeyModifiers.Shift, false);
            Require(grid.SelectedItems.Count == 2, "Shift-click did not select the diagnostic range.");

            // A2 concrete defect: selected non-lead B must survive the Opening handler.
            grid.SelectedItem = entries[0];
            if (!grid.SelectedItems.Contains(entries[1])) grid.SelectedItems.Add(entries[1]);
            await PressRowAsync(window, grid, entries[1], KeyModifiers.None, true);
            ContextMenu menu = grid.ContextMenu!;
            Require(menu.IsOpen && ReferenceEquals(grid.SelectedItem, entries[0]), "Right click changed the existing multiselection lead.");
            // Programmatic Open may bypass Opening in Avalonia; invoke the same event
            // subscribers explicitly so this regression cannot pass by missing that boundary.
            Delegate opening = typeof(ContextMenu).GetField("Opening", Private)?.GetValue(menu) as Delegate
                ?? throw new InvalidOperationException("Update the test-only Opening event adapter for this Avalonia version.");
            opening.DynamicInvoke(menu, new CancelEventArgs());
            Menu(menu, "diagnostics.copy").Command!.Execute(null);
            Require(copied == entries[1].Details, "Opening rebuilt clicked B's copy menu from selected lead A.");
            menu.Close();
            grid.SelectedItems.Clear(); grid.SelectedItem = entries[0];
            await PressRowAsync(window, grid, entries[1], KeyModifiers.None, true);
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, entries[1]),
                "Right click on an unselected row retained unrelated marks.");
            menu.Close();
            Console.WriteLine("SMOKE diagnostics synthetic: extended selection and context/Opening unit checks passed; not OS input acceptance.");
            await CheckNativeBlankGestureAsync(window, page, grid, first);
            // Restore the unfiltered projection before the frozen-scope unit checks.
            Click(page, "DiagnosticClearFilters"); await FlushAsync();

            // Keyboard menu freezes selected/filtered scopes before later filter/row expansion.
            grid.SelectedItem = entries[0]; grid.SelectedItems.Clear(); grid.SelectedItem = entries[0];
            grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Program.WaitUntilAsync(() => menu.IsOpen);
            MenuItem frozen = Menu(menu, "diagnostics.retry-selection");
            page.Refresh(View(first, second, later), true);
            grid.SelectedItem = Entries(grid).Single(entry => entry.Target?.WatchId == later.WatchId);
            calls.Clear(); frozen.Command!.Execute(null); menu.Close();
            await IdleAsync(page);
            Require(calls.Select(target => target.WatchId).SequenceEqual(new[] { first.WatchId }), "Menu selection expanded or redirected after opening.");

            // Live removal/re-add never redirects the frozen menu to the same URL's new WatchId.
            page.Refresh(View(first, second), true); grid.SelectedItem = Entries(grid)[0];
            grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Program.WaitUntilAsync(() => menu.IsOpen);
            MenuItem stale = Menu(menu, "watch.check");
            page.Refresh(View(first with { WatchId = later.WatchId }, second), true);
            calls.Clear(); stale.Command!.Execute(null); menu.Close();
            await IdleAsync(page);
            Require(calls.Count == 0, "Removed menu watch ran or redirected to the replacement URL.");

            // Exact source filter, label collision, language switch, thread query,
            // zero results and no hidden-selection resurrection after clearing.
            DesktopThreadEntry collision = Thread(98104, "4", Texts.T("diagnostics.allSources"));
            page.Refresh(View(first, Thread(second.WatchId, "2", "other"), collision), true);
            ComboBox source = page.FindControl<ComboBox>("DiagnosticSourceFilter")!;
            source.SelectedIndex = 1 + source.ItemsSource!.Cast<object>().Skip(1).ToList()
                .FindIndex(choice => choice.ToString() == collision.Snapshot.Id.Source);
            Require(Entries(grid).Length == 1 && Entries(grid)[0].ResourceId == collision.Snapshot.Id,
                "All-sources display label collided with exact source identity.");
            grid.SelectedItem = Entries(grid)[0];
            Texts.SetLanguage(language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "en" : "de");
            page.Refresh(View(first, Thread(second.WatchId, "2", "other"), collision), true);
            Require(Entries(grid).Single().ResourceId == collision.Snapshot.Id, "Language refresh changed source identity.");
            TextBox query = page.FindControl<TextBox>("DiagnosticTextFilter")!;
            query.Text = "#1"; await FlushAsync();
            Require(Entries(grid).Length == 0 && grid.SelectedItems.Count == 0, "Filtering to zero retained hidden selection.");
            Click(page, "DiagnosticClearFilters"); await FlushAsync();
            Require(Entries(grid).Length == 3 && grid.SelectedItems.Count == 0, "Clear restored hidden diagnostic marks.");
            query.Text = "fixture /b/ #1"; await FlushAsync();
            Require(Entries(grid).Single().ResourceId == first.Snapshot.Id, "Thread query did not expose the exact resource context.");
            Click(page, "DiagnosticClearFilters"); await FlushAsync();
            await CheckAcknowledgementAsync(page);

            await CheckGroupedScopeAsync(window, page, grid, View(first, second), calls);

            // Storage retry stays its separate target-check action, never a watch batch.
            DownloadTargetWaitState storage = (DownloadTargetWaitState)Activator.CreateInstance(typeof(DownloadTargetWaitState),
                Private, null, new object[] { "/fixture/storage", "disk-full", DateTimeOffset.UtcNow }, null)!;
            int storageChecks = 0;
            page.GetTargetWaitStates = () => new[] { storage };
            page.CheckTargetsAsync = () => { storageChecks++; return Task.CompletedTask; };
            page.Refresh(View(), true);
            grid.SelectedItem = Entries(grid).Single();
            Require(!page.FindControl<Button>("RetryDiagnosticFilteredButton")!.IsEnabled
                && !page.FindControl<Button>("RetryDiagnosticSelectionButton")!.IsEnabled,
                "Storage-only projection enabled bulk watch retries.");
            calls.Clear(); Click(page, "RetryDiagnosticFilteredButton");
            Require(calls.Count == 0 && storageChecks == 0, "A watch batch invoked storage checks.");
            Click(page, "RetryDiagnosticButton"); await IdleAsync(page);
            Require(storageChecks == 1 && calls.Count == 0 && Entries(grid).Single().Kind == DiagnosticKind.Storage,
                "Explicit storage retry became a watch retry or cleared its problem row.");
            page.GetTargetWaitStates = null;

            // Real toolbar batch held in flight: duplicate invocation is blocked;
            // later rows and a changed selection cannot join it. Availability is
            // re-read between operations even if host advisory still says true.
            page.Refresh(View(first, second), true);
            calls.Clear(); pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(page, "RetryDiagnosticFilteredButton");
            Require(calls.Count == 1, "The finite batch did not start exactly one operation.");
            Click(page, "RetryDiagnosticFilteredButton");
            page.Refresh(View(first, second, later), false);
            pending.SetResult(CheckRequestStatus.Scheduled); pending = null;
            await IdleAsync(page);
            Require(calls.Count == 1 && page.FindControl<TextBlock>("DiagnosticsActionError")!.Text == Texts.T("diagnostics.batchResult", 2, 1, 0, 0, 1, 0),
                "Availability loss admitted a later operation or lost partial-result accounting.");

            // Media + check for one watch: resolve/remove the watch during media
            // await, then do not submit its check or accidentally retry a board.
            page.Refresh(View(first), true);
            first = first with { Snapshot = first.Snapshot with { MediaFailures = new Dictionary<string, FailureInfo>
                { ["file"] = new(FailureKind.Network, "Media fixture") } } };
            page.Refresh(View(first, second), true);
            TaskCompletionSource<int> mediaHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int mediaCalls = 0;
            page.RetryMediaAsync = _ => { mediaCalls++; return mediaHeld.Task; };
            page.CanRetryMedia = _ => admissible;
            calls.Clear();
            Task running = RunFrozenAsync(page, DiagnosticRetryScope.Capture(Entries(grid)));
            Require(mediaCalls == 1 && calls.Count == 0, "Media/check deduplication submitted checks before the held media operation.");
            page.Refresh(View(second, later), true);
            mediaHeld.SetResult(4);
            await running.WaitAsync(Deadline);
            Require(mediaCalls == 1 && calls.Select(target => target.WatchId).SequenceEqual(new[] { second.WatchId }),
                "A disappeared media watch still checked, or a new row joined the batch.");
            Require(page.FindControl<TextBlock>("DiagnosticsActionError")!.Text == Texts.T("diagnostics.batchResult", 2, 1, 0, 4, 1, 0),
                "Result confused watch count, accepted media files and skipped operations.");

            // Source/host eligibility changes while rows remain visible.
            page.Refresh(View(first, second), true); admissible = false; calls.Clear();
            await RunFrozenAsync(page, DiagnosticRetryScope.Capture(Entries(grid)));
            Require(calls.Count == 0 && mediaCalls == 1, "UI treated an old projection as authoritative admission.");
            admissible = true;
            page.EndLifetime(); page = Create();
            page.Refresh(View(first, second), true); grid = page.FindControl<DataGrid>("DiagnosticsGrid")!;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); calls.Clear();
            Task ending = RunFrozenAsync(page, DiagnosticRetryScope.Capture(Entries(grid)));
            string? beforeEnd = page.FindControl<TextBlock>("DiagnosticsActionError")!.Text;
            page.EndLifetime(); pending.SetException(new IOException("Ended owner fixture")); pending = null;
            await ending.WaitAsync(Deadline);
            Require(calls.Count == 1 && page.FindControl<TextBlock>("DiagnosticsActionError")!.Text == beforeEnd,
                "Ended page submitted more targets or rendered a late result.");
        }
        finally
        {
            pending?.TrySetCanceled(); page?.EndLifetime(); host.Content = previous;
            Texts.SetLanguage(language);
            typeof(MainWindow).GetMethod("NavigateWorkspace", Private)!.Invoke(window, new object[] { previousPage, true });
        }
    }

    private static async Task CheckGroupedScopeAsync(MainWindow window, DiagnosticsPage page, DataGrid grid,
        DesktopSessionView fixture, List<DiagnosticTarget> calls)
    {
        DesktopThreadEntry first = fixture.Threads[0], second = fixture.Threads[1];
        // A closed group remains in the filtered scope, and the detail follows the
        // stable entry key when a fresh snapshot replaces the selected row.
        DesktopThreadEntry withMedia = first with { Snapshot = first.Snapshot with
        {
            MediaFailures = new Dictionary<string, FailureInfo> { ["image"] = new(FailureKind.Network, "Missing image") }
        } };
        DesktopThreadEntry withRequest = second with { Snapshot = second.Snapshot with
        {
            RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 2)
        } };
        page.Refresh(fixture with { Threads = [withMedia, withRequest] }, true);
        var details = page.FindControl<TextBox>("DiagnosticDetails")!;
        var summary = page.FindControl<TextBlock>("DiagnosticFilterSummary")!;
        string GroupsLabel(string key, int count) => Texts.T(key) + " · " + Texts.Count("diagnostics.groupEntries", count);
        Button? GroupButton(string label) => grid.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("diagnostic-group-toggle") && AutomationProperties.GetName(button) == label);
        grid.SelectedItem = Entries(grid).Single(entry => entry.Target?.WatchId == first.WatchId && entry.Kind == DiagnosticKind.Check);
        Button? problemsGroup = null;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            problemsGroup = GroupButton(GroupsLabel("diagnostics.groupProblems", 3));
            return problemsGroup != null && GroupButton(GroupsLabel("diagnostics.groupActivity", 1)) != null;
        });
        using (MediaNativeInput native = new(window))
            await native.ClickButtonAsync(problemsGroup!);
        await Program.WaitUntilAsync(() => grid.SelectedItem == null && details.IsVisible);
        Require(summary.Text!.EndsWith(Texts.Count("diagnostics.retryableTargets", 2), StringComparison.Ordinal),
            "Collapsing a group changed the filtered retry scope.");
        withMedia = withMedia with { Snapshot = withMedia.Snapshot with { LastCheckFailure = new(FailureKind.Network, "Updated problem") } };
        page.Refresh(fixture with { Threads = [withMedia, withRequest] }, true);
        await FlushAsync();
        Require(grid.SelectedItem == null && details.Text!.Contains("Updated problem", StringComparison.Ordinal),
            "Collapsed refresh dropped the exact selected entry or reopened its group.");
        window.UpdateLayout();
        GroupButton(GroupsLabel("diagnostics.groupProblems", 3))!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(grid.SelectedItem is DiagnosticEntry { Summary: "Updated problem" } refreshed && refreshed.Target?.WatchId == first.WatchId,
            "Expansion did not restore the refreshed selected row.");
        page.SetProblemsOnly(true);
        page.FindControl<TextBox>("DiagnosticTextFilter")!.Text = "#1";
        await FlushAsync();
        window.UpdateLayout();
        Require(Entries(grid).Length == 2 && GroupButton(GroupsLabel("diagnostics.groupProblems", 2)) != null
            && GroupButton(GroupsLabel("diagnostics.groupActivity", 1)) == null
            && summary.Text!.EndsWith(Texts.Count("diagnostics.retryableTargets", 1), StringComparison.Ordinal),
            "The group and retry counts diverged from the filter or counted the same watch twice.");
        calls.Clear(); Click(page, "RetryDiagnosticFilteredButton"); await IdleAsync(page);
        Require(calls.Select(target => target.WatchId).SequenceEqual(new[] { first.WatchId }),
            "The filtered retry submitted a target outside the displayed count.");
        Click(page, "DiagnosticClearFilters"); await FlushAsync();
    }

    private static async Task CheckAcknowledgementAsync(DiagnosticsPage page)
    {
        SessionFeedbackStore store = new();
        SessionFeedback first = store.Record(FeedbackCategory.Error, "gallery.fixture", "one", "Gallery failure", "technical details")!;
        store.Record(FeedbackCategory.Error, "gallery.fixture", "one", "Gallery failure", "technical details");
        SessionFeedback other = store.Record(FeedbackCategory.Warning, "gallery.fixture", "two", "Other failure")!;
        page.AttachFeedback(store);
        page.FindControl<Expander>("SessionFeedbackSection")!.IsExpanded = true;
        await FlushAsync(); // Expansion/collection materialization precedes selecting its new rows.
        DataGrid grid = page.FindControl<DataGrid>("SessionFeedbackGrid")!;
        Button dismiss = page.FindControl<Button>("DismissSessionFeedbackButton")!;
        Require(!dismiss.IsEnabled, "Acknowledgement enabled without a selected entry.");
        grid.SelectedItem = store.Entries.Single(entry => entry.Id == first.Id);
        await FlushAsync();
        Require(dismiss.IsEnabled && AutomationProperties.GetName(dismiss) == Texts.T("feedback.markRead"),
            "Central acknowledgement missing localized selection action: enabled=" + dismiss.IsEnabled
            + "; selected=" + (grid.SelectedItem as SessionFeedback)?.Id + "; expected=" + first.Id
            + "; name=" + AutomationProperties.GetName(dismiss) + "; label=" + Texts.T("feedback.markRead"));
        string? details = page.FindControl<TextBox>("SessionFeedbackDetails")!.Text;
        Click(page, "DismissSessionFeedbackButton");
        Require(store.IsDismissed(first) && !store.IsDismissed(other) && store.Entries.Count == 2
            && store.Entries.Single(entry => entry.Id == first.Id).Count == 2 && grid.ItemsSource!.Cast<SessionFeedback>().Count() == 2
            && page.FindControl<TextBox>("SessionFeedbackDetails")!.Text == details && !dismiss.IsEnabled,
            "Acknowledgement cleared history/counts/details, affected another event, or left itself enabled.");
        string language = Texts.Language;
        try
        {
            foreach (string current in new[] { "en", "de" })
            {
                Texts.SetLanguage(current); page.AttachFeedback(store);
                Require(AutomationProperties.GetName(dismiss) == Texts.T("feedback.markRead")
                    && dismiss.Content is StackPanel { Children: [_, TextBlock label] } && label.Text == Texts.T("feedback.markRead") && !dismiss.IsEnabled,
                    "Acknowledgement lost localization or dismissal state after redraw.");
            }
        }
        finally { Texts.SetLanguage(language); page.AttachFeedback(store); }
        grid.SelectedItem = other;
        await FlushAsync();
        Require(dismiss.IsEnabled, "Acknowledging one event disabled a different selected event.");
        grid.SelectedItem = null;
        await FlushAsync();
        Require(!dismiss.IsEnabled, "Cleared selection left acknowledgement enabled.");
        await FlushAsync();
    }

    private static DiagnosticEntry[] Entries(DataGrid grid) => grid.ItemsSource!.Cast<DiagnosticEntry>().ToArray();
    private static MenuItem Menu(ContextMenu menu, string command) => menu.Items.OfType<MenuItem>().Single(item =>
        item.Command != null && (item.Tag as string == command || item.Header as string == CommandCatalog.Label(command)));
    private static Task RunFrozenAsync(DiagnosticsPage page, DiagnosticRetryTarget[] scope) =>
        (Task)typeof(DiagnosticsPage).GetMethod("RetryFrozenScopeAsync", Private)!.Invoke(page, new object[] { scope })!;
    private static async Task IdleAsync(DiagnosticsPage page)
    {
        await Program.WaitUntilAsync(() => !(bool)typeof(DiagnosticsPage).GetField("_retryBusy", Private)!.GetValue(page)!);
        await FlushAsync();
    }
    private static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Click(DiagnosticsPage page, string name) => page.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task PressRowAsync(MainWindow window, DataGrid grid, DiagnosticEntry entry, KeyModifiers modifiers, bool right)
    {
        grid.ScrollIntoView(entry, null);
        Point point = default;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, entry));
            row?.BringIntoView();
            if (row?.TranslatePoint(new Point(20, row.Bounds.Height / 2), window) is not Point actual) return false;
            point = actual;
            return window.InputHitTest(actual) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(row);
        });
        InputElement hit = (InputElement)window.InputHitTest(point)!;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
            new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed), modifiers));
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 1,
            new PointerPointProperties(RawInputModifiers.None, right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased), modifiers,
            right ? MouseButton.Right : MouseButton.Left));
        pointer.Capture(null);
        await FlushAsync();
    }
    private static async Task CheckNativeBlankGestureAsync(MainWindow window, DiagnosticsPage page, DataGrid grid, DesktopThreadEntry first)
    {
        // Two fixture failures + platform information fill the compact viewport. Use
        // the production filter to leave ONE row, not a grid/window height override.
        TextBox query = page.FindControl<TextBox>("DiagnosticTextFilter")!;
        query.Text = first.Snapshot.Id.Source + " /" + first.Snapshot.Id.Board + "/ #" + first.Snapshot.Id.Thread;
        await Program.WaitUntilAsync(() => Entries(grid).Length == 1 && Entries(grid)[0].Target?.WatchId == first.WatchId);
        DiagnosticEntry entry = Entries(grid).Single();
        grid.SelectedItem = entry;
        using MediaNativeInput native = new(window); // Enforces disposable desktop/Xvfb; no synthetic fallback.

        async Task FocusFilterAsync()
        {
            query.BringIntoView();
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                Point? center = query.TranslatePoint(new Point(query.Bounds.Width / 2, query.Bounds.Height / 2), window);
                return center is Point point && VisibleBounds(window, query).Contains(point)
                    && window.InputHitTest(point) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(query);
            }, () => $"diagnostic filter bounds={query.Bounds}; focus={query.IsKeyboardFocusWithin}");
            await native.ClickAsync(query);
            await Program.WaitUntilAsync(() => query.IsKeyboardFocusWithin);
        }

        await FocusFilterAsync();
        grid.ScrollIntoView(entry, null);
        grid.BringIntoView();
        Point rowPoint = default;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>()
                .SingleOrDefault(candidate => ReferenceEquals(candidate.DataContext, entry));
            DataGridCell? cell = row?.GetVisualDescendants().OfType<DataGridCell>()
                .FirstOrDefault(candidate => candidate.IsEffectivelyVisible && candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0);
            if (cell?.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window) is not Point point
                || !VisibleBounds(window, cell).Contains(point) || window.InputHitTest(point) is not Visual hit
                || !hit.GetSelfAndVisualAncestors().Contains(row!)) return false;
            rowPoint = point; return true;
        }, () => $"native diagnostic row not hit-ready; grid={grid.Bounds}; rows={Entries(grid).Length}");
        bool rowPressed = false, rowFocused = false, blankPressed = false;
        void ObserveFocus(object? sender, FocusChangedEventArgs e)
        {
            if (e.Source is Visual target && target.GetSelfAndVisualAncestors().Contains(grid)) rowFocused = true;
        }
        void ObservePress(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed || e.Source is not Visual source) return;
            DataGridRow? row = source.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
            if (ReferenceEquals(row?.DataContext, entry)) rowPressed = true;
            else if (row == null && source.GetSelfAndVisualAncestors().Contains(grid)) blankPressed = true;
        }
        grid.AddHandler(InputElement.PointerPressedEvent, ObservePress, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.GotFocusEvent, ObserveFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            await native.RightClickAsync(window, rowPoint);
            // Opening the popup may move focus onward to its menu. Observe the actual
            // grid focus transition during the press, not an incidental final owner.
            await Program.WaitUntilAsync(() => rowPressed && rowFocused && grid.ContextMenu!.IsOpen,
                () => $"rowPress={rowPressed}; gridFocusObserved={rowFocused}; menu={grid.ContextMenu!.IsOpen}");
            Require(ReferenceEquals(grid.SelectedItem, entry), "Native row context click lost its exact selected target.");
            grid.ContextMenu!.Close();
            await FocusFilterAsync(); // Prove row and blank handling without pre-focusing the grid.
            grid.BringIntoView();
            await FlushAsync();
            window.UpdateLayout();

            // The rows presenter can be sized to its realized rows on native macOS
            // while the DataGrid itself still owns the available client area. Derive
            // the gesture from the grid's clipped bounds, not from an implementation
            // detail of the presenter. This is one deterministic point, never a scan
            // over chrome or an invented Source=grid coordinate.
            Point blankPoint;
            string blankCapability;
            if (!TryGetBlankPoint(window, grid, entry, out blankPoint, out blankCapability))
            {
                // Row context input remains a native acceptance check. A platform
                // that exposes no pointer-reachable blank geometry cannot prove the
                // optional deselection gesture; the synthetic selection/context-menu
                // checks above still run and must not be hidden behind this capability.
                Console.WriteLine("SMOKE diagnostics native: row context focus passed; blank-area pointer capability unavailable ("
                    + blankCapability + "); synthetic checks retained; " + native.Backend);
                return;
            }
            Require(grid.SelectedItems.Count == 1 && query.IsKeyboardFocusWithin,
                "Native blank gesture did not start with a selected row and focus outside the grid.");
            await native.RightClickAsync(window, blankPoint);
            await Program.WaitUntilAsync(() => blankPressed && grid.SelectedItems.Count == 0 && !grid.ContextMenu!.IsOpen,
                () => $"blankPress={blankPressed}; selected={grid.SelectedItems.Count}; menu={grid.ContextMenu!.IsOpen}");
            Require(query.IsKeyboardFocusWithin, "Native blank context click stole focus from the filter.");
            Console.WriteLine("SMOKE diagnostics native: filtered single-row viewport, row context focus and blank deselection passed; " + native.Backend);
        }
        finally
        {
            grid.RemoveHandler(InputElement.PointerPressedEvent, ObservePress);
            grid.RemoveHandler(InputElement.GotFocusEvent, ObserveFocus);
            grid.ContextMenu?.Close();
        }
    }

    private static bool TryGetBlankPoint(Window window, DataGrid grid, DiagnosticEntry entry,
        out Point point, out string capability)
    {
        point = default;
        capability = "unknown";
        DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>()
            .SingleOrDefault(candidate => ReferenceEquals(candidate.DataContext, entry));
        if (row?.TranslatePoint(new Point(0, row.Bounds.Height), window) is not Point bottom)
        {
            capability = "the filtered row has no window geometry";
            return false;
        }

        Rect available = VisibleBounds(window, grid);
        double top = Math.Max(available.Top, bottom.Y);
        if (available.Width <= 0 || available.Bottom <= top)
        {
            capability = $"grid has no clipped area below the row; grid={grid.Bounds}; client={window.ClientSize}";
            return false;
        }

        Point candidate = new(available.Center.X, (top + available.Bottom) / 2);
        if (window.InputHitTest(candidate) is not Visual hit)
        {
            capability = $"grid client area is not pointer hit-testable; grid={grid.Bounds}; client={window.ClientSize}";
            return false;
        }
        Visual[] ancestors = hit.GetSelfAndVisualAncestors().ToArray();
        if (!ancestors.Contains(grid) || ancestors.OfType<DataGridRow>().Any())
        {
            capability = "grid client area is covered by a row or is outside the DataGrid hit-test path";
            return false;
        }

        point = candidate;
        capability = "available";
        return true;
    }

    private static Rect VisibleBounds(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible || control.TranslatePoint(default, window) is not Point origin) return default;
        Rect result = new Rect(origin, control.Bounds.Size).Intersect(new Rect(window.ClientSize));
        foreach (Control clip in control.GetVisualAncestors().OfType<Control>().Where(candidate => candidate.ClipToBounds))
        {
            if (clip.TranslatePoint(default, window) is not Point position) return default;
            result = result.Intersect(new Rect(position, clip.Bounds.Size));
        }
        return result;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
