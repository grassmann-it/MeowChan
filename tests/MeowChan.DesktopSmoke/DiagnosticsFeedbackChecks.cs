using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Http;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// A bounded transition check: ordinary redraw, foreign selection, newer action and ended owner.
internal static class DiagnosticsFeedbackChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        ContentControl host = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
        object? previous = host.Content;
        TaskCompletionSource? pendingCopy = null;
        SessionFeedbackStore feedback = new();
        DiagnosticsPage page = new(_ => false, _ => pendingCopy?.Task ?? Task.FromException(new IOException("Copy fixture")));
        page.AttachFeedback(feedback);
        host.Content = page;
        DesktopThreadEntry first = SnapshotFixtures.Thread(9461, "123", "Feedback owner A");
        DesktopThreadEntry second = SnapshotFixtures.Thread(9462, "456", "Feedback owner B");
        first = first with { Snapshot = first.Snapshot with { LastCheckFailure = new FailureInfo(FailureKind.Network, "Failure A") } };
        second = second with { Snapshot = second.Snapshot with { LastCheckFailure = new FailureInfo(FailureKind.Network, "Failure B") } };
        DesktopSessionView view = new(new[] { first, second }, Array.Empty<DesktopBoardEntry>(), Array.Empty<string>(), DesktopSessionState.Ready, null);
        DataGrid grid = page.FindControl<DataGrid>("DiagnosticsGrid")!;
        TextBlock error = page.FindControl<TextBlock>("DiagnosticsActionError")!;
        try
        {
            window.FindControl<MenuItem>("DiagnosticsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            page.Refresh(view, true);
            await Program.WaitUntilAsync(() => { window.UpdateLayout(); return grid.Bounds.Height > 40; });
            DiagnosticEntry[] entries = grid.ItemsSource!.Cast<DiagnosticEntry>().ToArray();
            async Task SelectAsync(DiagnosticEntry entry)
            {
                grid.SelectedItem = entry;
                await Program.WaitUntilAsync(() => page.FindControl<TextBox>("DiagnosticDetails")!.Text == entry.Details);
            }
            await SelectAsync(entries[0]);
            Click(page, "CopyDiagnosticButton");
            await Program.WaitUntilAsync(() => error.Text == Texts.T("clipboard.writeFailed"));
            page.Refresh(view, true);
            Require(error.Text == Texts.T("clipboard.writeFailed"), "An unchanged diagnostic snapshot dismissed the unresolved copy failure.");
            await SelectAsync(entries[1]);
            Require(string.IsNullOrEmpty(error.Text), "A diagnostic action error appeared on another selected context.");

            await SelectAsync(entries[0]);
            pendingCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int repeats = feedback.Entries.Sum(entry => entry.Count);
            Click(page, "CopyDiagnosticButton");
            await SelectAsync(entries[1]);
            pendingCopy.SetException(new IOException("Delayed copy A"));
            await Program.WaitUntilAsync(() => feedback.Entries.Sum(entry => entry.Count) == repeats + 1);
            Require(string.IsNullOrEmpty(error.Text) && feedback.Entries.Single().Context == entries[0].Key,
                "A delayed copy failure changed B or lost A's captured identity.");

            await SelectAsync(entries[0]);
            pendingCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
            repeats = feedback.Entries.Sum(entry => entry.Count);
            Click(page, "CopyDiagnosticButton");
            Click(page, "OpenDiagnosticWatchButton");
            Require(error.Text == Texts.T("diagnostics.removed"), "The newer stale-watch result was not shown.");
            pendingCopy.SetException(new IOException("Superseded copy A"));
            await Program.WaitUntilAsync(() => feedback.Entries.Sum(entry => entry.Count) == repeats + 1);
            Require(error.Text == Texts.T("diagnostics.removed"), "An older asynchronous action replaced the newer result.");
            page.Refresh(view, true);
            Require(error.Text == Texts.T("diagnostics.removed"), "Ordinary status redraw dismissed the current stale-watch error.");

            TaskCompletionSource targets = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool targetsFinished = false;
            page.CheckTargetsAsync = async () => { try { await targets.Task; } finally { targetsFinished = true; } };
            DiagnosticEntry downloadTarget = entries[0] with { Key = "download-target:A", DownloadTargetPath = "/fixture/A" };
            grid.ItemsSource = new[] { downloadTarget, entries[1] };
            await SelectAsync(downloadTarget);
            Click(page, "RetryDiagnosticButton");
            await SelectAsync(entries[1]);
            targets.SetException(new IOException("Delayed target A"));
            await Program.WaitUntilAsync(() => targetsFinished);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(string.IsNullOrEmpty(error.Text), "A delayed target-retry failure appeared on B.");

            TaskCompletionSource ratings = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool ratingsFinished = false;
            page.ManageMediaRatingsAsync = async () => { try { await ratings.Task; } finally { ratingsFinished = true; } };
            page.Refresh(view, true);
            Click(page, "ManageMediaRatingsButton");
            page.EndLifetime();
            string? ending = error.Text;
            ratings.SetException(new IOException("Ended ratings owner"));
            await Program.WaitUntilAsync(() => ratingsFinished);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(error.Text == ending, "An ended Diagnostics page accepted a late ratings failure.");
            int endedEntries = feedback.Entries.Sum(entry => entry.Count);
            feedback.Record(FeedbackCategory.Error, "fixture.after-end", "different-owner", "New owner event");
            Require(page.FindControl<DataGrid>("SessionFeedbackGrid")!.ItemsSource!.Cast<SessionFeedback>().Sum(entry => entry.Count) == endedEntries,
                "An ended Diagnostics page retained its session listener.");
        }
        finally { page.EndLifetime(); host.Content = previous; }
        await CheckFeedbackSelectionAndMenusAsync(window, view);
        Console.WriteLine("SMOKE diagnostics feedback transitions: redraw retention, foreign-selection isolation, captured copy identity, newer-action ownership, target-retry isolation and ended ratings/listener guards passed.");
    }
    private static async Task CheckFeedbackSelectionAndMenusAsync(MainWindow window, DesktopSessionView view)
    {
        ContentControl host = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
        object? previous = host.Content;
        SessionFeedbackStore feedback = new();
        SessionFeedback feedbackA = feedback.Record(FeedbackCategory.Error, "fixture.A", "A", "Feedback A")!;
        SessionFeedback feedbackB = feedback.Record(FeedbackCategory.Error, "fixture.B", "B", "Feedback B")!;
        TaskCompletionSource? pendingCopy = null;
        TaskCompletionSource<CheckRequestStatus>? pendingRetry = null;
        bool retryFinished = false;
        string? copied = null;
        DiagnosticsPage page = new(_ => false,
            details => { copied = details; return pendingCopy?.Task ?? Task.FromException(new IOException("Menu copy fixture")); },
            async _ => { try { return await pendingRetry!.Task; } finally { retryFinished = true; } }, _ => true);
        page.AttachFeedback(feedback);
        host.Content = page;
        DataGrid grid = page.FindControl<DataGrid>("DiagnosticsGrid")!;
        DataGrid feedbackGrid = page.FindControl<DataGrid>("SessionFeedbackGrid")!;
        TextBlock error = page.FindControl<TextBlock>("DiagnosticsActionError")!;
        try
        {
            page.Refresh(view, true);
            page.FindControl<Expander>("SessionFeedbackSection")!.IsExpanded = true;
            await Program.WaitUntilAsync(() => { window.UpdateLayout(); return grid.Bounds.Height > 40 && feedbackGrid.Bounds.Height > 0; });
            DiagnosticEntry[] entries = grid.ItemsSource!.Cast<DiagnosticEntry>().ToArray();
            async Task SelectAsync(DiagnosticEntry entry)
            {
                grid.SelectedItem = entry;
                await Program.WaitUntilAsync(() => page.FindControl<TextBox>("DiagnosticDetails")!.Text == entry.Details);
            }
            async Task SelectFeedbackAsync(long id)
            {
                feedbackGrid.SelectedItem = feedbackGrid.ItemsSource!.Cast<SessionFeedback>().Single(entry => entry.Id == id);
                TextBox details = page.FindControl<TextBox>("SessionFeedbackDetails")
                    ?? throw new InvalidOperationException("The selected feedback entry has no details control.");
                await Program.WaitUntilAsync(() =>
                {
                    if ((feedbackGrid.SelectedItem as SessionFeedback)?.Id != id) return false;
                    string text = details.Text
                        ?? throw new InvalidOperationException("The selected feedback entry has no details text.");
                    return text.Contains(id == feedbackA.Id ? "Feedback A" : "Feedback B", StringComparison.Ordinal);
                });
            }
            async Task<MenuItem> OpenMenuAsync(string command)
            {
                ContextMenu menu = grid.ContextMenu!;
                // ContextMenu.Open bypasses Opening; use the actual keyboard context request
                // so the product's Opening handler builds its frozen selection commands.
                grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                await Program.WaitUntilAsync(() => menu.IsOpen);
                string expectedLabel = CommandCatalog.Label(command);
                Console.WriteLine("SMOKE diagnostics menu inventory: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    requestedCommand = command, expectedLabel, selectedKey = (grid.SelectedItem as DiagnosticEntry)?.Key,
                    menu.IsOpen, menu.ItemCount,
                    items = menu.Items.Select((entry, index) => new
                    {
                        index, itemType = entry?.GetType().FullName,
                        header = (entry as MenuItem)?.Header as string,
                        headerType = (entry as MenuItem)?.Header?.GetType().FullName,
                        commandType = (entry as MenuItem)?.Command?.GetType().FullName
                    }).ToArray()
                }));
                MenuItem[] matches = menu.Items.OfType<MenuItem>().Where(item => item.Command != null
                    && item.Header as string == expectedLabel).ToArray();
                Require(matches.Length == 1,
                    "The opened diagnostic menu must contain exactly one command named " + expectedLabel + "; found " + matches.Length + ". See menu inventory.");
                MenuItem item = matches[0];
                Require(item.IsEnabled && item.Command?.CanExecute(null) == true, "The fixture diagnostic menu command is unavailable: " + command);
                return item;
            }
            async Task InvokeMenuAsync(string command)
            {
                MenuItem item = await OpenMenuAsync(command);
                item.Command!.Execute(null);
                grid.ContextMenu!.Close();
            }

            await SelectFeedbackAsync(feedbackA.Id);
            Click(page, "CopySessionFeedbackButton");
            await Program.WaitUntilAsync(() => error.Text == Texts.T("clipboard.writeFailed"));
            page.Refresh(view, true);
            Require(error.Text == Texts.T("clipboard.writeFailed"), "A feedback copy failure was dismissed by an unchanged refresh.");
            await SelectFeedbackAsync(feedbackB.Id);
            Require(string.IsNullOrEmpty(error.Text), "A completed feedback copy failure appeared on another feedback entry.");
            await SelectFeedbackAsync(feedbackA.Id);
            Require(error.Text == Texts.T("clipboard.writeFailed"), "The matching feedback selection lost its retained copy failure.");
            Click(page, "ClearSessionFeedbackButton");
            Require(string.IsNullOrEmpty(error.Text), "Clearing session feedback left its removed entry's copy failure visible.");

            await SelectAsync(entries[0]);
            await InvokeMenuAsync("diagnostics.copy");
            await Program.WaitUntilAsync(() => error.Text == Texts.T("clipboard.writeFailed"));
            Require(copied == entries[0].Details, "The actual menu copy callback lost its frozen details.");
            page.Refresh(view, true);
            Require(error.Text == Texts.T("clipboard.writeFailed"), "Ordinary refresh dismissed a context-menu copy failure.");
            await SelectAsync(entries[1]);
            Require(string.IsNullOrEmpty(error.Text), "A completed context-menu failure appeared on another diagnostic.");
            await SelectAsync(entries[0]);
            pendingCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int repeats = feedback.Entries.Sum(entry => entry.Count);
            MenuItem frozenCopy = await OpenMenuAsync("diagnostics.copy");
            await SelectAsync(entries[1]);
            frozenCopy.Command!.Execute(null);
            grid.ContextMenu!.Close();
            Require(copied == entries[0].Details, "An already opened diagnostic menu used the later selection.");
            pendingCopy.SetException(new IOException("Frozen menu A"));
            await Program.WaitUntilAsync(() => feedback.Entries.Sum(entry => entry.Count) == repeats + 1);
            Require(string.IsNullOrEmpty(error.Text) && feedback.Entries.Single().Context == entries[0].Key,
                "A late menu copy failure changed B or lost A's captured identity.");

            await SelectAsync(entries[0]);
            pendingCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
            repeats = feedback.Entries.Sum(entry => entry.Count);
            await InvokeMenuAsync("diagnostics.copy");
            Click(page, "OpenDiagnosticWatchButton");
            pendingCopy.SetException(new IOException("Superseded menu copy"));
            await Program.WaitUntilAsync(() => feedback.Entries.Sum(entry => entry.Count) == repeats + 1);
            Require(error.Text == Texts.T("diagnostics.removed"), "A late menu copy replaced a newer toolbar result.");

            pendingRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await InvokeMenuAsync("watch.check");
            await InvokeMenuAsync("watch.show-in-downloads");
            Require(error.Text == Texts.T("diagnostics.removed"), "The actual menu OpenWatch callback failed to own its result.");
            pendingRetry.SetException(new IOException("Superseded menu retry"));
            await Program.WaitUntilAsync(() => retryFinished && page.FindControl<Button>("RetryDiagnosticButton")!.IsEnabled);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(error.Text == Texts.T("diagnostics.removed"), "A late menu retry replaced a newer menu action.");
            page.Refresh(view, true);
            Require(error.Text == Texts.T("diagnostics.removed"), "Ordinary redraw dismissed a menu OpenWatch failure.");

            retryFinished = false;
            pendingRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await InvokeMenuAsync("watch.check");
            await SelectAsync(entries[1]);
            pendingRetry.SetException(new IOException("Foreign menu retry A"));
            await Program.WaitUntilAsync(() => retryFinished && page.FindControl<Button>("RetryDiagnosticButton")!.IsEnabled);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(string.IsNullOrEmpty(error.Text), "A late menu retry appeared on another diagnostic selection.");
        }
        finally { grid.ContextMenu?.Close(); page.EndLifetime(); host.Content = previous; }
        Console.WriteLine("SMOKE diagnostics feedback menu ownership: feedback-ID redraw/switch/clear, frozen menu copy identity, retained errors and late copy/retry generation guards passed.");
    }
    private static void Click(DiagnosticsPage page, string name) => page.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
