using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class SavedArchiveSearchChecks
{
    // A fresh single process proves the new contracts without repeating the
    // already accepted save/reopen acceptance pair or its presentation matrix.
    private static async Task ContractsAsync(MainWindow owner, ArchivePage page, string profile, Evidence log)
    {
        await SetupAppliedAsync(page, Board, log);
        var manager = await OpenAsync(owner, page, true, log);
        await SaveNameAsync(manager, Names[0], owner, log);
        await CloseAsync(manager, owner, log);
        byte[] savedBytes = File.ReadAllBytes(SearchFile(profile));
        string originalLayout = page.ArchiveLayoutPreference;
        try
        {
            foreach (string layout in new[] { "List", "Table" })
            {
                page.ArchiveLayoutPreference = layout;
                var cards = page.FindControl<ListBox>("ArchiveThreads")!;
                var table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
                var rows = cards.ItemsSource!.Cast<ArchiveThreadRow>().Take(2).ToArray();
                log.Action("select overlapping results", "Both marked identities remain in the saved Board result; successful Apply must clear both layouts", new { layout, ids = rows.Select(row => Key(row.Summary.Id)) });
                if (layout == "List")
                {
                    cards.SelectedItem = rows[0];
                    await Program.WaitUntilAsync(() => !page.IsBusy);
                    cards.SelectedItems!.Add(rows[1]);
                }
                else
                {
                    // The table was hidden throughout the List case. Wait for
                    // its real rows/current cell before injecting selection.
                    await SelectionChecks.WaitRow(table, rows[1]);
                    await SelectionChecks.PressAsync(owner, SelectionChecks.Row(table, rows[0]));
                    await Program.WaitUntilAsync(() => !page.IsBusy);
                    await SelectionChecks.PressAsync(owner, SelectionChecks.Row(table, rows[1]),
                        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
                }
                await Program.WaitUntilAsync(() => !page.IsBusy);
                string[] expectedSelection = rows.Select(row => Key(row.Summary.Id)).Order().ToArray();
                string[] cardSelection = cards.SelectedItems!.Cast<ArchiveThreadRow>().Select(row => Key(row.Summary.Id)).Order().ToArray();
                string[] tableSelection = table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => Key(row.Summary.Id)).Order().ToArray();
                log.Note("observed overlap selection setup", "Both controls must contain exactly the two intended overlapping identities before saved Apply", new
                { layout, expectedSelection, cardSelection, tableSelection, busy = page.IsBusy, loading = page.IsSearchLoading, reader = page.CurrentThread?.Summary.Id });
                Require(cardSelection.SequenceEqual(expectedSelection) && tableSelection.SequenceEqual(expectedSelection),
                    "Overlap fixture did not establish synchronized multiple selection.");
                Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "overlapping dirty draft", log, "The explicit saved Apply replaces this draft only after success");
                manager = await OpenAsync(owner, page, false, log); Select(manager, Names[0], log);
                await ClickAsync(Control<Button>(manager, "SavedSearchApply"), log, "Ask before applying the same saved definition over overlapping selected rows");
                await ClickAsync(Control<Button>(manager, "SavedSearchConfirm"), log, "Commit overlapping saved search");
                await WaitClosedAsync(manager, owner, page);
                AssertRows(page, BoardIds(false).Take(50).ToArray());
                Require(cards.SelectedItems!.Count == 0 && table.SelectedItems.Count == 0 && page.CurrentThread == null,
                    "Successful saved Apply restored overlapping marks or retained the previous reader.");
                Require(page.AppliedSearchSpec!.Equals(Board) && Offset(page) == 0
                    && page.FindControl<TextBox>("ArchiveSearchText")!.Text == Board.Text, "Overlapping Apply did not publish its full definition/page.");
                log.Note("overlap selection cleared", "Applied rows still contain both old identities, but no mark or reader survives", new { layout, count = cards.SelectedItems.Count, reader = page.CurrentThread?.Summary.Id });
            }
        }
        finally { page.ArchiveLayoutPreference = originalLayout; }
        await FailedAndCanceledDialogApplyAsync(owner, profile, log);
        Require(savedBytes.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Contract Apply/retry/cancel altered stored definitions.");
    }

    private static async Task FailedAndCanceledDialogApplyAsync(MainWindow owner, string profile, Evidence log)
    {
        bool fail = false;
        TaskCompletionSource? gate = null;
        var tested = new ArchivePage(async token =>
        {
            if (gate != null) await gate.Task; // Ignore cancellation to test late publication guards.
            if (fail) throw new IOException("Injected saved Apply catalog failure");
            return await ArchiveReader.OpenAsync(profile);
        }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
        var window = new Window { Width = 900, Height = 700, Content = tested, Title = "Saved Apply contract fixture" };
        SavedArchiveSearchesDialog? dialog = null;
        Task? closed = null;
        window.Show(owner);
        try
        {
            Require(await tested.ApplySearchSpecAsync(Dates), "Failure fixture could not prepare old date result.");
            var cards = tested.FindControl<ListBox>("ArchiveThreads")!;
            var table = tested.FindControl<DataGrid>("ArchiveThreadTable")!;
            cards.SelectedItem = cards.ItemsSource!.Cast<ArchiveThreadRow>().First();
            await Program.WaitUntilAsync(() => !tested.IsBusy);
            var rawText = tested.FindControl<TextBox>("ArchiveSearchText")!;
            var rawDate = tested.FindControl<ArchiveDateEditor>("ArchiveStartDate")!;
            rawText.Text = "failed saved apply raw draft";
            rawDate.RawText = "invalid raw date";
            object oldRows = cards.ItemsSource!;
            object oldSpec = tested.AppliedSearchSpec!;
            int oldOffset = Offset(tested);
            fail = true;
            (dialog, closed) = await OpenManagerAsync();
            await ClickAsync(Control<Button>(dialog, "SavedSearchApply"), log, "Actual dirty confirmation precedes an injected saved Apply failure");
            await ClickAsync(Control<Button>(dialog, "SavedSearchConfirm"), log, "Fail opening local reader before any result publication");
            await Program.WaitUntilAsync(() => Control<TextBlock>(dialog, "SavedSearchStatus").Text == Texts.T("savedSearch.applyFailed"));
            AssertOldSnapshot("Failed saved Apply");
            Require(dialog.IsVisible && !dialog.Applied, "Failed Apply closed or claimed success.");
            dialog.Close(); await closed; dialog = null;
            fail = false;
            Require(await tested.RetrySearchAsync(), "Frozen saved Apply retry failed.");
            Require(tested.AppliedSearchSpec!.Equals(Board) && rawText.Text == Board.Text && rawDate.RawText == ""
                && cards.SelectedItems!.Count == 0 && table.SelectedItems.Count == 0 && Offset(tested) == 0,
                "Retry did not publish the frozen saved Board request and clear selection.");
            AssertRows(tested, BoardIds(false).Take(50).ToArray());
            log.Note("saved Apply failure and retry", "Actual manager failure retained raw invalid editor and selected old Date snapshot; retry applied frozen Board", new { rows = Rows(tested) });

            // Cancel a second actual dialog while openReader is gated, then
            // release its deliberately cancellation-insensitive late response.
            Require(await tested.ApplySearchSpecAsync(Dates), "Cancellation fixture could not prepare Date result.");
            cards.SelectedItem = cards.ItemsSource!.Cast<ArchiveThreadRow>().First();
            await Program.WaitUntilAsync(() => !tested.IsBusy);
            rawText.Text = "canceled saved apply raw draft"; rawDate.RawText = "invalid raw date";
            oldRows = cards.ItemsSource!; oldSpec = tested.AppliedSearchSpec!; oldOffset = Offset(tested);
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (dialog, closed) = await OpenManagerAsync();
            await ClickAsync(Control<Button>(dialog, "SavedSearchApply"), log, "Confirm dirty Apply before testing its cancel action");
            await ClickAsync(Control<Button>(dialog, "SavedSearchConfirm"), log, "Hold saved Apply before reader completion");
            await Program.WaitUntilAsync(() => tested.IsSearchLoading);
            await ClickAsync(Control<Button>(dialog, "SavedSearchCancelEdit"), log, "Cancel operation closes the actual manager and cancels its pending Apply");
            await closed; dialog = null;
            gate.SetResult();
            await Program.WaitUntilAsync(() => !tested.IsSearchLoading);
            AssertOldSnapshot("Canceled saved Apply after late reader response");
            log.Note("saved Apply canceled", "Late reader ignored cancellation but could not replace raw editor, rows, selection or applied request", new { raw = rawText.Text, date = rawDate.RawText, offset = oldOffset });

            void AssertOldSnapshot(string action)
            {
                string expectedText = action.StartsWith("Failed", StringComparison.Ordinal) ? "failed saved apply raw draft" : "canceled saved apply raw draft";
                Require(ReferenceEquals(cards.ItemsSource, oldRows) && ReferenceEquals(tested.AppliedSearchSpec, oldSpec)
                    && rawText.Text == expectedText && rawDate.RawText == "invalid raw date" && Offset(tested) == oldOffset
                    && cards.SelectedItems!.Count == 1 && table.SelectedItems.Count == 1,
                    action + " changed the previous editor/result/selection snapshot.");
            }
            async Task<(SavedArchiveSearchesDialog Dialog, Task Closed)> OpenManagerAsync()
            {
                var candidate = new SavedArchiveSearchesDialog(owner.SessionHost, tested) { Width = 650, Height = 510 };
                Task completion = candidate.ShowDialog(window);
                await Program.WaitUntilAsync(() => candidate.IsVisible && Control<Button>(candidate, "SavedSearchReload").IsEnabled);
                Select(candidate, Names[0], log);
                return (candidate, completion);
            }
        }
        finally
        {
            gate?.TrySetResult();
            dialog?.CloseForLifetime();
            tested.EndLifetime();
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
